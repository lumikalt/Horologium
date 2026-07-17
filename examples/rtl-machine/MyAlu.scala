//> using scala 2.13.16
//> using dep org.chipsalliance::chisel:6.7.0
//> using plugin org.chipsalliance:::chisel-plugin:6.7.0
//> using options -unchecked -deprecation -language:reflectiveCalls -feature -Xcheckinit -Ymacro-annotations

// Example custom RTL ALU for Horologium's functional-unit substitution.
//
// This directory shows the user-project shape: bring your own Chisel, generate the
// SystemVerilog with ./generate.sh, verilate it with ./build.sh (which reuses the
// repo's native/RtlFu shims), and wire it into a pipeline with machine.fsx — which
// also defines the instruction→opcode mapping, so no C# changes are needed anywhere.
//
// The unit follows the standard FU port contract (rtl_fu_shim.cpp): a Decoupled
// op/a/b request and a Valid response. It is fully pipelined with a single stage —
// io.req.ready is constantly high and every result emerges one edge after its
// acceptance, so the pipeline charges 1 cycle per ALU op, same as the C# model.
//
// Opcodes are this unit's own convention (machine.fsx maps RV32I instructions onto
// them): 0=ADD, 1=SUB, 2=AND, 3=OR, 4=XOR.

package examplertl

import chisel3._
import chisel3.util._

class AluReq extends Bundle {
  val op = UInt(3.W)
  val a  = UInt(32.W)
  val b  = UInt(32.W)
}

class MyAlu extends Module {
  val io = IO(new Bundle {
    val req  = Flipped(Decoupled(new AluReq))
    val resp = Valid(UInt(32.W))
  })

  io.req.ready := true.B // fully pipelined: accepts every cycle

  private val a = io.req.bits.a
  private val b = io.req.bits.b
  private val result = MuxLookup(io.req.bits.op, 0.U(32.W))(
    Seq(
      0.U -> (a + b),
      1.U -> (a - b),
      2.U -> (a & b),
      3.U -> (a | b),
      4.U -> (a ^ b)
    )
  )

  io.resp.valid := RegNext(io.req.fire, false.B)
  io.resp.bits  := RegEnable(result, io.req.fire)
}

/** Emits generated/MyAlu.sv. Run via generate.sh. */
object GenerateAlu extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new MyAlu,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
