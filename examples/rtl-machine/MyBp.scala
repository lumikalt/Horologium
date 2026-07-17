// Example custom RTL branch predictor: a 512-entry bimodal (per-PC 2-bit counters)
// with a direct-mapped BTB, following the plain rtl_bp_shim.cpp port contract —
// combinational predict at fetch, one-clock-edge update at commit.
//
// Compare with native/RtlFu/GshareBp.scala (global history) and LTageBp.scala
// (speculative-history contract) for the fancier predictor shapes.
//
// scala-cli project directives live in MyAlu.scala. Regenerate generated/MyBp.sv
// with ./generate.sh after editing.

package examplertl

import chisel3._
import chisel3.util._

class MyBp(entries: Int = 512) extends Module {
  require(isPow2(entries))

  val io = IO(new Bundle {
    val predPc     = Input(UInt(32.W))
    val predTaken  = Output(Bool())
    val predTarget = Output(UInt(32.W))
    val updValid   = Input(Bool())
    val updPc      = Input(UInt(32.W))
    val updTaken   = Input(Bool())
    val updTarget  = Input(UInt(32.W))
  })

  private val idxBits = log2Ceil(entries)

  val pht = RegInit(VecInit(Seq.fill(entries)(1.U(2.W)))) // weakly not-taken
  val btb = RegInit(VecInit(Seq.fill(entries)(0.U(32.W))))

  private def index(pc: UInt): UInt = (pc >> 2)(idxBits - 1, 0)

  private val pIdx = index(io.predPc)
  io.predTaken  := pht(pIdx) >= 2.U
  io.predTarget := btb(pIdx)

  when(io.updValid) {
    val uIdx = index(io.updPc)
    when(io.updTaken) { btb(uIdx) := io.updTarget }
    when(io.updTaken && pht(uIdx) =/= 3.U) { pht(uIdx) := pht(uIdx) + 1.U }
    when(!io.updTaken && pht(uIdx) =/= 0.U) { pht(uIdx) := pht(uIdx) - 1.U }
  }
}

/** Emits generated/MyBp.sv. Run via generate.sh. */
object GenerateExampleBp extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new MyBp,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
