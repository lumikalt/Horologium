//> using scala 2.13.16
//> using dep org.chipsalliance::chisel:6.7.0
//> using plugin org.chipsalliance:::chisel-plugin:6.7.0
//> using options -unchecked -deprecation -language:reflectiveCalls -feature -Xcheckinit -Ymacro-annotations

// RV32M divide/remainder functional unit — the first RTL substitution target
// (see docs in native/RtlFu/README.md and RtlBackedExecutor in src/Core/Mechanism).
//
// Sequential restoring divider with early termination: iterates one quotient bit
// per cycle starting from the highest set bit of |dividend|, so latency is
// data-dependent — 1 cycle for the RISC-V special cases (divide-by-zero, signed
// overflow, zero dividend), significant-bits(|dividend|) + 1 cycles otherwise.
// The Verilator shim (rtl_fu_shim.cpp) reports the observed cycle count to the
// pipeline, which uses it as the instruction's FU latency.
//
// Regenerate generated/DivUnit.sv with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

/** Request: op 0=DIV, 1=DIVU, 2=REM, 3=REMU (op(0)=unsigned, op(1)=remainder). */
class DivReq extends Bundle {
  val op = UInt(2.W)
  val a  = UInt(32.W)
  val b  = UInt(32.W)
}

class DivUnit extends Module {
  val io = IO(new Bundle {
    val req  = Flipped(Decoupled(new DivReq))
    val resp = Valid(UInt(32.W))
  })

  val sIdle :: sRun :: sDone :: Nil = Enum(3)
  val state = RegInit(sIdle)

  val isRem  = Reg(Bool())
  val negQ   = Reg(Bool()) // negate quotient on completion (signed, sign(a) != sign(b))
  val negR   = Reg(Bool()) // negate remainder on completion (signed, a negative)
  val ua     = Reg(UInt(32.W)) // |dividend|
  val ub     = Reg(UInt(32.W)) // |divisor|
  val idx    = Reg(UInt(5.W))  // bit position being processed, counts down to 0
  val rem    = Reg(UInt(32.W)) // partial remainder; invariant rem < ub
  val quot   = Reg(UInt(32.W))
  val result = Reg(UInt(32.W))

  io.req.ready  := state === sIdle
  io.resp.valid := state === sDone
  io.resp.bits  := result

  when(state === sIdle && io.req.valid) {
    val a      = io.req.bits.a
    val b      = io.req.bits.b
    val signed = !io.req.bits.op(0)
    val rm     = io.req.bits.op(1)
    val aNeg   = signed && a(31)
    val bNeg   = signed && b(31)
    val absA   = Mux(aNeg, 0.U(32.W) - a, a)
    val absB   = Mux(bNeg, 0.U(32.W) - b, b)

    isRem := rm
    negQ  := aNeg ^ bNeg
    negR  := aNeg

    // RISC-V M special cases resolve in a single cycle.
    when(b === 0.U) {
      result := Mux(rm, a, "hFFFFFFFF".U) // q = -1, r = dividend
      state  := sDone
    }.elsewhen(signed && a === "h80000000".U && b === "hFFFFFFFF".U) {
      result := Mux(rm, 0.U, "h80000000".U) // signed overflow: q = INT_MIN, r = 0
      state  := sDone
    }.elsewhen(absA === 0.U) {
      result := 0.U
      state  := sDone
    }.otherwise {
      ua   := absA
      ub   := absB
      rem  := 0.U
      quot := 0.U
      idx  := 31.U - PriorityEncoder(Reverse(absA)) // highest set bit: skip leading zeros
      state := sRun
    }
  }

  when(state === sRun) {
    val shifted = Cat(rem, ua(idx)) // 33 bits; < 2*ub since rem < ub
    val ge      = shifted >= ub
    val newRem  = Mux(ge, shifted - ub, shifted)(31, 0)
    val newQuot = quot | (ge.asUInt << idx)
    rem  := newRem
    quot := newQuot
    when(idx === 0.U) {
      val qFinal = Mux(negQ, 0.U(32.W) - newQuot, newQuot)
      val rFinal = Mux(negR, 0.U(32.W) - newRem, newRem)
      result := Mux(isRem, rFinal, qFinal)
      state  := sDone
    }.otherwise {
      idx := idx - 1.U
    }
  }

  when(state === sDone) {
    state := sIdle
  }
}

/** Emits generated/DivUnit.sv. Run via generate.sh. */
object Generate extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new DivUnit,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
