// DRRIP cache replacement policy (Jaleel et al., ISCA 2010) — the second RTL
// replacement-policy substitution target, driven through RtlFfiReplacementPolicy
// (src/Core/Orrery/Cache) via rtl_rp_shim.cpp, sharing SrripRp's port contract.
//
// Deliberately mirrors Horologium's C# DrripPolicy bit-for-bit so the differential
// co-sim test can demand identical victims, RRPV metadata, and (indirectly, through
// follower-set insertions) an identical PSEL trajectory on identical streams:
//   - hit promotion and victim selection are the shared RRIP base (see SrripRp.scala)
//   - Set Dueling: sets [0, sdmSets) permanently follow SRRIP, [sdmSets, 2*sdmSets)
//     permanently follow BRRIP, the rest follow whichever the PSEL counter favors;
//     sdmSets is clamped to max(1, min(param, sets/4)) exactly like the C#
//   - a miss (install) in an SDM set votes against its policy: SRRIP SDM → PSEL++,
//     BRRIP SDM → PSEL--; followers use SRRIP while PSEL < threshold (= max/2 + 1)
//   - PSEL initializes to threshold - 1 (SRRIP winning)
//   - BRRIP inserts distant (RRPV 3) except every 1/bimodalDenominator-th install,
//     which inserts long (RRPV 2); the shared bimodal counter increments only on
//     BRRIP-path installs, mirroring the C# _bimodalCounter
//
// Unlike SRRIP, DRRIP carries *global* cross-set state (PSEL, bimodal counter) —
// the point of this unit is exercising such state through the unchanged port
// contract. Geometry is fixed at elaboration; the committed default (64 sets ×
// 4 ways → 16 SDM sets per policy) matches an 8 KiB / 4-way / 32 B-block cache.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/DrripRp.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class DrripRp(
    sets: Int = 64,
    ways: Int = 4,
    m: Int = 2,
    sdmSetsParam: Int = 32,
    pselBits: Int = 10,
    bimodalDenominator: Int = 32
) extends Module {
  private val maxRrpv       = (1 << m) - 1
  private val sdmSets       = math.max(1, math.min(sdmSetsParam, sets / 4))
  private val pselMax       = (1 << pselBits) - 1
  private val pselThreshold = pselMax / 2 + 1

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

  val rrpv       = RegInit(VecInit(Seq.fill(sets)(VecInit(Seq.fill(ways)(maxRrpv.U(m.W))))))
  val psel       = RegInit((pselThreshold - 1).U(pselBits.W)) // start with SRRIP winning
  val bimodalCtr = RegInit(0.U(log2Ceil(bimodalDenominator + 1).W))

  // ── Victim selection + aging (shared RRIP base, as in SrripRp) ──────────────

  val row    = rrpv(io.victimSet)
  val maxVal = row.reduce((a, b) => Mux(a >= b, a, b))
  val delta  = maxRrpv.U - maxVal
  io.victimWay := PriorityEncoder(VecInit(row.map(_ === maxVal)))
  when(io.victimValid && delta =/= 0.U) {
    for (w <- 0 until ways) rrpv(io.victimSet)(w) := row(w) + delta
  }

  when(io.hitValid) { rrpv(io.hitSet)(io.hitWay) := 0.U }

  // ── Install: Set Dueling insertion policy ───────────────────────────────────

  when(io.instValid) {
    val isSdmSrrip = io.instSet < sdmSets.U
    val isSdmBrrip = !isSdmSrrip && io.instSet < (2 * sdmSets).U
    val useSrrip   = Mux(isSdmSrrip, true.B, Mux(isSdmBrrip, false.B, psel < pselThreshold.U))

    when(isSdmSrrip && psel =/= pselMax.U) { psel := psel + 1.U } // SRRIP SDM missed → vote BRRIP
    when(isSdmBrrip && psel =/= 0.U) { psel := psel - 1.U }       // BRRIP SDM missed → vote SRRIP

    val brripLong = bimodalCtr + 1.U >= bimodalDenominator.U
    when(!useSrrip) { bimodalCtr := Mux(brripLong, 0.U, bimodalCtr + 1.U) }

    rrpv(io.instSet)(io.instWay) := Mux(
      useSrrip || brripLong,
      (maxRrpv - 1).U, // long re-reference
      maxRrpv.U        // distant (most BRRIP inserts)
    )
  }

  io.metaRrpv := rrpv(io.metaSet)(io.metaWay)
}

/** Emits generated/DrripRp.sv. Run via generate.sh. */
object GenerateDrrip extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new DrripRp,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
