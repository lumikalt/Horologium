// SRRIP cache replacement policy — the first RTL replacement-policy substitution
// target, driven through RtlFfiReplacementPolicy (src/Core/Orrery/Cache) via
// rtl_rp_shim.cpp.
//
// Deliberately mirrors Horologium's C# SrripPolicy (RripPolicyBase, m=2) bit-for-bit
// so the differential co-sim test can demand identical victims on identical streams:
//   - per-way 2-bit RRPVs initialized to 3 (invalid ways treated as distant)
//   - hit promotion RRPV ← 0 (RRIP-HP)
//   - install at RRPV = 2 ("long re-reference")
//   - victim = lowest-indexed way with maximal RRPV; all RRPVs in the set age by
//     (3 − max) on the same operation — the unrolled form of the C# increment-and-
//     retry loop, which returns the first way to reach 3
// Geometry is fixed at elaboration; io_cfgSets/io_cfgWays expose it so the C# side
// can validate against the cache it is attached to. The committed default (64 sets ×
// 4 ways) matches an 8 KiB / 4-way / 32 B-block cache.
//
// The victim way is read combinationally; the aging write-back, hit promotion, and
// install each consume one clock edge. The shim never overlaps two operations.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/SrripRp.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class SrripRp(sets: Int = 64, ways: Int = 4, m: Int = 2) extends Module {
  private val maxRrpv = (1 << m) - 1

  val io = IO(new Bundle {
    val cfgSets = Output(UInt(32.W))
    val cfgWays = Output(UInt(32.W))

    val hitValid = Input(Bool())
    val hitSet   = Input(UInt(log2Ceil(sets).W))
    val hitWay   = Input(UInt(log2Ceil(ways).W))

    val instValid = Input(Bool())
    val instSet   = Input(UInt(log2Ceil(sets).W))
    val instWay   = Input(UInt(log2Ceil(ways).W))

    val victimValid = Input(Bool())
    val victimSet   = Input(UInt(log2Ceil(sets).W))
    val victimWay   = Output(UInt(log2Ceil(ways).W))

    val metaSet  = Input(UInt(log2Ceil(sets).W))
    val metaWay  = Input(UInt(log2Ceil(ways).W))
    val metaRrpv = Output(UInt(m.W))
  })

  io.cfgSets := sets.U
  io.cfgWays := ways.U

  val rrpv = RegInit(VecInit(Seq.fill(sets)(VecInit(Seq.fill(ways)(maxRrpv.U(m.W))))))

  val row    = rrpv(io.victimSet)
  val maxVal = row.reduce((a, b) => Mux(a >= b, a, b))
  val delta  = maxRrpv.U - maxVal
  io.victimWay := PriorityEncoder(VecInit(row.map(_ === maxVal)))
  when(io.victimValid && delta =/= 0.U) {
    for (w <- 0 until ways) rrpv(io.victimSet)(w) := row(w) + delta
  }

  when(io.hitValid) { rrpv(io.hitSet)(io.hitWay) := 0.U }
  when(io.instValid) { rrpv(io.instSet)(io.instWay) := (maxRrpv - 1).U }

  io.metaRrpv := rrpv(io.metaSet)(io.metaWay)
}

/** Emits generated/SrripRp.sv. Run via generate.sh. */
object GenerateRp extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new SrripRp,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
