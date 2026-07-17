// RV32M multiply functional unit — the first *pipelined* RTL FU, driven through
// RtlFfiFunctionalUnit (src/Core/Mechanism/RtlFu) via rtl_fu_shim.cpp, sharing
// DivUnit's port contract (io.req = Flipped(Decoupled(op/a/b)), io.resp = Valid).
//
// Fully pipelined, 3 stages (operand capture → 33×33 multiply → half select), so
// io.req.ready is constantly high — the unit accepts a new operation every cycle
// and each result emerges exactly 3 edges after its acceptance, matching Olympia's
// MUL latency and Horologium's FuLatencyConfig default (MulDivLatency = 3). The
// FFI shim drives one operation at a time and reports cycles = 3; in-hardware
// throughput of 1 op/cycle is what OooeTrain's fully-pipelined FU model already
// assumes for the IntegerMulDiv port.
//
// Ops follow RV32M funct3 order: 0=MUL (low 32 bits), 1=MULH (high, signed×signed),
// 2=MULHSU (high, signed×unsigned), 3=MULHU (high, unsigned×unsigned). One 33×33
// signed multiplier covers all four: operands are sign- or zero-extended to 33 bits
// per op, and the result half is selected in the last stage — bit-identical to the
// C# Rv32Executor's Int128/UInt128 arithmetic.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/MulUnit.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class MulUnit extends Module {
  val io = IO(new Bundle {
    val req  = Flipped(Decoupled(new DivReq)) // same op/a/b request shape as DivUnit
    val resp = Valid(UInt(32.W))
  })

  io.req.ready := true.B // fully pipelined: accepts every cycle

  // ── Stage 1: capture operands ───────────────────────────────────────────────
  val s1Valid = RegNext(io.req.fire, false.B)
  val s1Op    = RegEnable(io.req.bits.op, io.req.fire)
  val s1A     = RegEnable(io.req.bits.a, io.req.fire)
  val s1B     = RegEnable(io.req.bits.b, io.req.fire)

  // ── Stage 2: one 33×33 signed multiply covers all four ops ─────────────────
  val aSigned = s1Op =/= 3.U // MUL/MULH/MULHSU treat rs1 as signed
  val bSigned = !s1Op(1)     // MUL/MULH treat rs2 as signed
  val aExt    = Cat(aSigned && s1A(31), s1A).asSInt
  val bExt    = Cat(bSigned && s1B(31), s1B).asSInt

  val s2Valid = RegNext(s1Valid, false.B)
  val s2Prod  = RegEnable((aExt * bExt).asUInt, s1Valid)
  val s2Low   = RegEnable(s1Op === 0.U, s1Valid)

  // ── Stage 3: select the result half ────────────────────────────────────────
  val s3Valid  = RegNext(s2Valid, false.B)
  val s3Result = RegEnable(Mux(s2Low, s2Prod(31, 0), s2Prod(63, 32)), s2Valid)

  io.resp.valid := s3Valid
  io.resp.bits  := s3Result
}

/** Emits generated/MulUnit.sv. Run via generate.sh. */
object GenerateMul extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new MulUnit,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
