// RV32F divide/square-root functional unit — the first RTL FU with IEEE 754 result
// AND exception-flag parity, driven through RvRtlFpExecutor (src/Isa/RiscV32/Execute)
// via rtl_fpu_shim.cpp (the flags-carrying variant of the FU shim).
//
// Deliberately mirrors Horologium's C# Rv32Executor FDIV.S/FSQRT.S semantics
// bit-for-bit (FpBin op 3 / FpSqrt), which are IEEE binary32 round-to-nearest-even
// with full subnormal support (the C# model ignores the rm field for these ops):
//   - iterative cores: 27-step restoring division of the significands; 27-step
//     digit-recurrence square root — sticky comes from the nonzero remainder, so
//     inexact detection is exact (and, by the division/sqrt exclusion-zone property,
//     equals the C# model's compare-against-double procedure)
//   - subnormal inputs are normalized (CLZ) on acceptance; subnormal outputs
//     denormalize before rounding with sticky accumulation
//   - NaN results are always the RISC-V canonical NaN 0x7FC00000
//   - flags mirror the C# quirks exactly: overflow raises OF *without* NX (the C#
//     early-returns on an infinite result); UF requires an inexact, nonzero
//     subnormal result (underflow to zero raises NX only); divide-by-zero raises
//     DZ alone; NV on any signaling NaN, 0/0, Inf/Inf, or sqrt of a negative
//
// Ops: 0 = FDIV.S (a / b), 1 = FSQRT.S (sqrt(a); b ignored). Latency is 1 cycle for
// special cases, ~30 for the iterative paths — reported honestly through the shim
// (the static FloatDivSqrtLatency default is 16, so RTL timing is a real change).
//
// scala-cli project directives live in DivUnit.scala. Regenerate
// generated/FDivSqrtUnit.sv with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class FpResp extends Bundle {
  val result = UInt(32.W)
  val flags  = UInt(5.W) // fflags: NV|DZ|OF|UF|NX
}

class FDivSqrtUnit extends Module {
  val io = IO(new Bundle {
    val req  = Flipped(Decoupled(new DivReq)) // op/a/b request shape shared with DivUnit
    val resp = Valid(new FpResp)
  })

  private val sIdle :: sDiv :: sSqrt :: sRound :: sDone :: Nil = Enum(5)
  private val state                                            = RegInit(sIdle)

  val isSqrt  = Reg(Bool())
  val resSign = Reg(Bool())
  val expR    = Reg(SInt(14.W))
  val stickyR = Reg(Bool())
  val iter    = Reg(UInt(5.W))

  val divP = Reg(UInt(25.W)) // partial remainder
  val divQ = Reg(UInt(27.W)) // quotient bits, MSB first
  val mbR  = Reg(UInt(24.W)) // divisor significand

  val sqRad = Reg(UInt(54.W)) // radicand, consumed 2 bits per step
  val sqRem = Reg(UInt(32.W))
  val sqS   = Reg(UInt(27.W)) // root bits, MSB first

  val resultR = Reg(UInt(32.W))
  val flagsR  = Reg(UInt(5.W))

  io.req.ready       := state === sIdle
  io.resp.valid      := state === sDone
  io.resp.bits.result := resultR
  io.resp.bits.flags  := flagsR

  private val NV = "b10000".U(5.W)
  private val DZ = "b01000".U(5.W)
  private val OF = "b00100".U(5.W)

  private val CanonicalNaN = "h7FC00000".U(32.W)

  // ── Unpack + subnormal normalization (combinational, used at acceptance) ────

  private def clz24(t: UInt): UInt = PriorityEncoder(Reverse(t)) // t != 0

  // Returns (sign, isZero, isInf, isNaN, isSNaN, sig24, unbiasedExp)
  private def unpack(x: UInt) = {
    val sign   = x(31)
    val e      = x(30, 23)
    val f      = x(22, 0)
    val isZero = e === 0.U && f === 0.U
    val isSub  = e === 0.U && f =/= 0.U
    val isInf  = e === 255.U && f === 0.U
    val isNaN  = e === 255.U && f =/= 0.U
    val isSNaN = isNaN && !f(22)

    val t     = Cat(0.U(1.W), f) // 24 bits, top bit clear
    val shl   = clz24(t)         // shift to place the MSB at bit 23 (f != 0)
    val sig   = Mux(isSub, (t << shl)(23, 0), Cat(1.U(1.W), f))
    val expS  = Mux(
      isSub,
      (-126).S(14.W) - Cat(0.U(8.W), shl).asSInt,
      Cat(0.U(5.W), e).asSInt - 127.S
    )
    (sign, isZero, isInf, isNaN, isSNaN, sig, expS)
  }

  // ── Accept ──────────────────────────────────────────────────────────────────

  when(state === sIdle && io.req.valid) {
    val sqrtOp                                              = io.req.bits.op(0)
    val (sa, aZero, aInf, aNaN, aSNaN, sigA, expA)          = unpack(io.req.bits.a)
    val (sb, bZero, bInf, bNaN, bSNaN, sigB, expB)          = unpack(io.req.bits.b)
    isSqrt := sqrtOp
    iter   := 0.U

    when(!sqrtOp) {
      val sign = sa ^ sb
      resSign := sign
      when(aNaN || bNaN) {
        resultR := CanonicalNaN
        flagsR  := Mux(aSNaN || bSNaN, NV, 0.U)
        state   := sDone
      }.elsewhen((aInf && bInf) || (aZero && bZero)) {
        resultR := CanonicalNaN
        flagsR  := NV
        state   := sDone
      }.elsewhen(aInf) {
        resultR := Cat(sign, "h7F800000".U(31.W))
        flagsR  := 0.U
        state   := sDone
      }.elsewhen(bInf) {
        resultR := Cat(sign, 0.U(31.W))
        flagsR  := 0.U
        state   := sDone
      }.elsewhen(bZero) {
        resultR := Cat(sign, "h7F800000".U(31.W)) // finite nonzero / 0
        flagsR  := DZ
        state   := sDone
      }.elsewhen(aZero) {
        resultR := Cat(sign, 0.U(31.W))
        flagsR  := 0.U
        state   := sDone
      }.otherwise {
        expR := expA - expB
        divP := Cat(0.U(1.W), sigA)
        divQ := 0.U
        mbR  := sigB
        state := sDiv
      }
    }.otherwise {
      resSign := false.B
      when(aNaN) {
        resultR := CanonicalNaN
        flagsR  := Mux(aSNaN, NV, 0.U)
        state   := sDone
      }.elsewhen(sa && aZero) {
        resultR := "h80000000".U // sqrt(-0) = -0, no flags
        state   := sDone
        flagsR  := 0.U
      }.elsewhen(sa) {
        resultR := CanonicalNaN // sqrt of negative (incl. -Inf)
        flagsR  := NV
        state   := sDone
      }.elsewhen(aInf) {
        resultR := "h7F800000".U
        flagsR  := 0.U
        state   := sDone
      }.elsewhen(aZero) {
        resultR := 0.U
        flagsR  := 0.U
        state   := sDone
      }.otherwise {
        val odd = expA(0)
        expR := (expA - Cat(0.U(13.W), odd.asUInt).asSInt) >> 1
        // Radicand D' = sigA << (odd ? 2 : 1), placed at the top of 54 bits so the
        // 27-step 2-bit digit recurrence yields root = sqrt(m) * 2^26 with m in [1,4).
        val dPrime = Mux(odd, Cat(sigA, 0.U(2.W)), Cat(0.U(1.W), sigA, 0.U(1.W))) // 26 bits
        sqRad := Cat(dPrime, 0.U(28.W))
        sqRem := 0.U
        sqS   := 0.U
        state := sSqrt
      }
    }
  }

  // ── Iterative cores ─────────────────────────────────────────────────────────

  when(state === sDiv) {
    val ge  = divP >= Cat(0.U(1.W), mbR)
    val sub = Mux(ge, divP - Cat(0.U(1.W), mbR), divP)
    divP := (sub << 1)(24, 0)
    divQ := Cat(divQ(25, 0), ge)
    iter := iter + 1.U
    when(iter === 26.U) {
      stickyR := sub =/= 0.U
      state   := sRound
    }
  }

  when(state === sSqrt) {
    val racc  = Cat(sqRem(29, 0), sqRad(53, 52)) // bring in the next radicand digit pair
    val trial = Cat(0.U(3.W), sqS, "b01".U(2.W)) // (S << 2) | 1
    val ge    = racc >= trial
    val nrem  = Mux(ge, racc - trial, racc)
    sqRad := (sqRad << 2)(53, 0)
    sqRem := nrem
    sqS   := Cat(sqS(25, 0), ge)
    iter  := iter + 1.U
    when(iter === 26.U) {
      stickyR := nrem =/= 0.U
      state   := sRound
    }
  }

  // ── Round (RNE) + flags ─────────────────────────────────────────────────────

  when(state === sRound) {
    val raw       = Mux(isSqrt, sqS, divQ) // 27 bits; sqrt always has bit 26 set
    val needShift = !raw(26)               // div quotient in (0.5, 1)
    val n         = Mux(needShift, (raw << 1)(26, 0), raw)
    val biased    = expR - Mux(needShift, 1.S, 0.S) + 127.S

    val m24 = n(26, 3)
    val g   = n(2)
    val r   = n(1)
    val s   = stickyR || n(0)

    // Subnormal: denormalize before rounding, folding shifted-out bits into sticky.
    val subnormal = biased <= 0.S
    val shWide    = 1.S(14.W) - biased
    val sh        = Mux(shWide > 26.S, 26.U, shWide.asUInt(4, 0))
    val ext26     = Cat(m24, g, r)
    val kept      = (ext26 >> sh)(25, 0)
    val dropped   = (ext26 & ((1.U(27.W) << sh) - 1.U)(25, 0)) =/= 0.U

    val fm = Mux(subnormal, kept(25, 2), m24)
    val fg = Mux(subnormal, kept(1), g)
    val fr = Mux(subnormal, kept(0), r)
    val fs = Mux(subnormal, s || dropped, s)

    val packed = Mux(
      subnormal,
      Cat(0.U(8.W), fm(22, 0)),
      Cat(biased.asUInt(7, 0), m24(22, 0))
    )
    val nx  = fg || fr || fs
    val up  = fg && (fr || fs || fm(0))
    val fin = packed + up.asUInt

    val overflowPre = biased >= 255.S
    val fexp        = fin(30, 23)
    val toInf       = overflowPre || fexp === 255.U
    val uf          = nx && fexp === 0.U && fin(22, 0) =/= 0.U

    resultR := Cat(resSign, Mux(toInf, "h7F800000".U(31.W), fin(30, 0)))
    // The C# model early-returns on an infinite result: overflow raises OF alone.
    flagsR := Mux(toInf, OF, Cat(0.U(3.W), uf, nx))
    state  := sDone
  }

  when(state === sDone) {
    state := sIdle
  }
}

/** Emits generated/FDivSqrtUnit.sv. Run via generate.sh. */
object GenerateFDivSqrt extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new FDivSqrtUnit,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
