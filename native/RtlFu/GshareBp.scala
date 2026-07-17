// Gshare branch predictor — the first RTL branch-predictor substitution target,
// driven through RtlFfiBranchPredictor (src/Core/Mechanism/RtlFu) via rtl_bp_shim.cpp.
//
// Deliberately mirrors Horologium's C# GsharePredictor bit-for-bit so the differential
// co-sim test can demand identical predictions on identical streams:
//   - PHT: 2^historyBits two-bit counters, initialized weakly-not-taken (1)
//   - BTB: same index space, zero-initialized, written only on taken updates
//   - index = (pc >> 2)[historyBits-1:0] XOR GHR
//   - update indexes with the pre-shift (commit-time) GHR, then shifts taken in at LSB
//     (the non-speculative path of SpeculativeGlobalHistory.Commit)
// Prediction is a combinational table read (no clock); update consumes one clock edge.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/GshareBp.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class GshareBp(historyBits: Int = 8) extends Module {
  private val entries = 1 << historyBits

  val io = IO(new Bundle {
    val predPc     = Input(UInt(32.W))
    val predTaken  = Output(Bool())
    val predTarget = Output(UInt(32.W))
    val updValid   = Input(Bool())
    val updPc      = Input(UInt(32.W))
    val updTaken   = Input(Bool())
    val updTarget  = Input(UInt(32.W))
  })

  val pht = RegInit(VecInit(Seq.fill(entries)(1.U(2.W)))) // weakly not-taken
  val ghr = RegInit(0.U(historyBits.W))
  val btb = RegInit(VecInit(Seq.fill(entries)(0.U(32.W))))

  private def index(pc: UInt): UInt = (pc >> 2)(historyBits - 1, 0) ^ ghr

  val pIdx = index(io.predPc)
  io.predTaken  := pht(pIdx) >= 2.U
  io.predTarget := btb(pIdx)

  when(io.updValid) {
    val uIdx = index(io.updPc)
    when(io.updTaken) { btb(uIdx) := io.updTarget }
    when(io.updTaken && pht(uIdx) =/= 3.U) { pht(uIdx) := pht(uIdx) + 1.U }
    when(!io.updTaken && pht(uIdx) =/= 0.U) { pht(uIdx) := pht(uIdx) - 1.U }
    ghr := Cat(ghr(historyBits - 2, 0), io.updTaken)
  }
}

/** Emits generated/GshareBp.sv. Run via generate.sh. */
object GenerateBp extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new GshareBp,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
