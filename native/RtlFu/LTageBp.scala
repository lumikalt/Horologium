// L-TAGE branch predictor — the first RTL predictor exercising the speculative-history
// port contract, driven through RtlFfiHistoryBranchPredictor (src/Core/Mechanism/RtlFu)
// via rtl_hbp_shim.cpp.
//
// Deliberately mirrors Horologium's C# LTagePredictor bit-for-bit so the differential
// co-sim test can demand identical predictions under fetch/commit/flush/partial-squash
// sequences:
//   - 4096-entry bimodal base (2-bit, init weakly-not-taken — stored biased so the
//     zero-initialized memory reads as 1) + 4 tagged tables (512 × {9b tag, 3b ctr,
//     2b u, valid}; the C# ctr=3 construction init is dead state, allocation always
//     writes ctr explicitly, so zero-init valid=0 memories match)
//   - geometric history lengths 8/13/21/34 over a 34-bit GHR; folded indices/tags
//     recomputed combinationally from the GHR exactly like the C# FoldHist
//   - longest-match provider/alt selection, RRIP-style usefulness, allocate-on-
//     mispredict in the shortest longer-history table with u=0, else decay
//   - 32-entry loop predictor overlay with trip-count confidence
//   - speculative GHR contract: predict indexes the working GHR; update trains
//     against the committed shadow and advances it; spec_update folds a predicted
//     direction at fetch; recover restores the committed shadow; history/restore
//     expose the working GHR as the checkpoint (folds derive from it, so a single
//     34-bit value is a complete history snapshot — no checkpoint RAM needed)
//
// Direction-only: targets live in the C# wrapper's BTB dictionary (the C# predictor
// uses an unbounded Dictionary, which RTL cannot mirror).
//
// State is held in Mems (combinational read) so the generated Verilog stays compact.
//
// scala-cli project directives live in DivUnit.scala. Regenerate generated/LTageBp.sv
// with ./generate.sh after editing.

package rtlfu

import chisel3._
import chisel3.util._

class LTageBp extends Module {
  private val NumTables      = 4
  private val TableIndexBits = 9
  private val BaseIndexBits  = 12
  private val TagWidth       = 9
  private val MaxHist        = 34
  private val LoopIndexBits  = 5
  private val LoopTagWidth   = 10
  private val LoopConfidence = 4
  private val HistLengths    = Seq(8, 13, 21, 34)

  val io = IO(new Bundle {
    val predPc    = Input(UInt(64.W))
    val predTaken = Output(Bool())

    val updValid = Input(Bool())
    val updPc    = Input(UInt(64.W))
    val updTaken = Input(Bool())

    val specValid = Input(Bool())
    val specTaken = Input(Bool())

    val recoverValid = Input(Bool())

    val histOut   = Output(UInt(64.W))
    val restValid = Input(Bool())
    val restHist  = Input(UInt(64.W))
    val restTaken = Input(Bool())
  })

  class LoopEntry extends Bundle {
    val tag         = UInt(LoopTagWidth.W)
    val learnedIter = UInt(16.W)
    val currentIter = UInt(16.W)
    val confCount   = UInt(8.W)
    val confident   = Bool()
  }

  val ghr          = RegInit(0.U(MaxHist.W)) // working (speculative) history
  val committedGhr = RegInit(0.U(MaxHist.W)) // architectural shadow, advanced at update
  val specMode     = RegInit(false.B)        // latches once speculative updates arrive

  val baseMem = Mem(1 << BaseIndexBits, UInt(2.W)) // stores ctr ^ 1 → zero-init reads as 1
  val tagMems = Seq.fill(NumTables)(Mem(1 << TableIndexBits, UInt(TagWidth.W)))
  val ctrMems = Seq.fill(NumTables)(Mem(1 << TableIndexBits, UInt(3.W)))
  val uMems   = Seq.fill(NumTables)(Mem(1 << TableIndexBits, UInt(2.W)))
  val vMems   = Seq.fill(NumTables)(Mem(1 << TableIndexBits, Bool()))
  val loopMem = Mem(1 << LoopIndexBits, new LoopEntry)

  // ── Fold / index / tag helpers (mirror the C# FoldHist/TageIdx/TageTag) ─────

  private def fold(hist: UInt, histLen: Int, outBits: Int): UInt = {
    val masked = hist(histLen - 1, 0)
    (0 until histLen by outBits)
      .map { sh =>
        val hi = math.min(sh + outBits, histLen) - 1
        masked(hi, sh).pad(outBits)
      }
      .reduce(_ ^ _)
  }

  private def tageIdx(pc: UInt, hist: UInt, t: Int): UInt =
    ((pc >> 2)(TableIndexBits - 1, 0) ^ fold(hist, HistLengths(t), TableIndexBits))(TableIndexBits - 1, 0)

  private def tageTag(pc: UInt, hist: UInt, t: Int): UInt = {
    val f1 = fold(hist, HistLengths(t), TagWidth)
    val f2 = fold(hist, HistLengths(t) - 1, TagWidth - 1)
    ((pc >> 2)(TagWidth - 1, 0) ^ f1 ^ (f2 << 1).pad(TagWidth)(TagWidth - 1, 0))(TagWidth - 1, 0)
  }

  private def baseIdx(pc: UInt): UInt = (pc >> 2)(BaseIndexBits - 1, 0)
  private def baseCtr(pc: UInt): UInt = baseMem(baseIdx(pc)) ^ 1.U(2.W)

  private def loopIdx(pc: UInt): UInt = (pc >> 2)(LoopIndexBits - 1, 0)

  private def loopTag(pc: UInt): UInt = {
    val x = pc >> 2
    (x ^ (x >> LoopIndexBits))(LoopTagWidth - 1, 0)
  }

  // Scan tables shortest→longest: provider = longest match, alt = previous best.
  // provNum encodes provider+1 (0 = base). Mirrors the C# TageLookup fold.
  private def tageLookup(pc: UInt, hist: UInt): (UInt, Bool, Bool, Vec[UInt]) = {
    val idx  = VecInit((0 until NumTables).map(t => tageIdx(pc, hist, t)))
    val base = baseCtr(pc) >= 2.U
    var prov: UInt  = 0.U(3.W)
    var provP: Bool = base
    var altP: Bool  = base
    for (t <- 0 until NumTables) {
      val hit  = vMems(t)(idx(t)) && tagMems(t)(idx(t)) === tageTag(pc, hist, t)
      val pred = ctrMems(t)(idx(t)) >= 4.U
      altP = Mux(hit, provP, altP)
      provP = Mux(hit, pred, provP)
      prov = Mux(hit, (t + 1).U(3.W), prov)
    }
    (prov, provP, altP, idx)
  }

  // ── Predict (combinational, no state change) ────────────────────────────────

  private val (_, predProvPred, _, _) = tageLookup(io.predPc, ghr)
  private val ple                     = loopMem(loopIdx(io.predPc))
  private val loopHit                 = ple.tag === loopTag(io.predPc) && ple.confident
  io.predTaken := Mux(loopHit, ple.currentIter < ple.learnedIter, predProvPred)

  io.histOut := ghr

  // ── Update (trains against the committed shadow; one clock edge) ────────────

  private val (uProv, uProvPred, uAltPred, uIdx) = tageLookup(io.updPc, committedGhr)
  private val mispredict                         = uProvPred =/= io.updTaken

  // Allocation: shortest longer-history table with u == 0; else decay u in that range.
  private val eligible = VecInit(
    (0 until NumTables).map(t => (t + 1).U > uProv && uMems(t)(uIdx(t)) === 0.U)
  )
  private val anyAlloc = eligible.asUInt.orR
  private val allocOH  = PriorityEncoderOH(eligible.asUInt)

  when(io.updValid) {
    for (t <- 0 until NumTables) {
      val isProvider = uProv === (t + 1).U
      val ctr        = ctrMems(t)(uIdx(t))
      val u          = uMems(t)(uIdx(t))
      when(isProvider) {
        ctrMems(t).write(
          uIdx(t), Mux(io.updTaken, Mux(ctr === 7.U, 7.U, ctr + 1.U), Mux(ctr === 0.U, 0.U, ctr - 1.U))
        )
        when(uProvPred =/= uAltPred) {
          uMems(t).write(
            uIdx(t),
            Mux(uProvPred === io.updTaken, Mux(u === 3.U, 3.U, u + 1.U), Mux(u === 0.U, 0.U, u - 1.U))
          )
        }
      }.elsewhen(mispredict && anyAlloc && allocOH(t)) {
        tagMems(t).write(uIdx(t), tageTag(io.updPc, committedGhr, t))
        ctrMems(t).write(uIdx(t), Mux(io.updTaken, 4.U, 3.U)) // weakly in correct direction
        uMems(t).write(uIdx(t), 0.U)
        vMems(t).write(uIdx(t), true.B)
      }.elsewhen(mispredict && !anyAlloc && (t + 1).U > uProv && u > 0.U) {
        uMems(t).write(uIdx(t), u - 1.U)
      }
    }

    when(uProv === 0.U) {
      val b = baseCtr(io.updPc)
      val n = Mux(io.updTaken, Mux(b === 3.U, 3.U, b + 1.U), Mux(b === 0.U, 0.U, b - 1.U))
      baseMem.write(baseIdx(io.updPc), n ^ 1.U(2.W))
    }

    // ── Loop predictor (mirrors the C# UpdateLoop cases exactly) ──────────────
    val li       = loopIdx(io.updPc)
    val lt       = loopTag(io.updPc)
    val le       = loopMem(li)
    val tagMatch = le.tag === lt

    val fresh = Wire(new LoopEntry) // zeroed replacement entry with the new tag
    fresh.tag         := lt
    fresh.learnedIter := 0.U
    fresh.currentIter := 0.U
    fresh.confCount   := 0.U
    fresh.confident   := false.B

    val next = WireDefault(le)
    when(tagMatch) {
      when(io.updTaken) {
        next.currentIter := le.currentIter + 1.U
      }.otherwise {
        when(!le.confident) {
          when(le.learnedIter === 0.U || le.learnedIter === le.currentIter) {
            next.learnedIter := le.currentIter
            val cc = Mux(le.confCount === 255.U, le.confCount, le.confCount + 1.U)
            next.confCount := cc
            next.confident := cc >= LoopConfidence.U
          }.otherwise {
            next := fresh // inconsistent trip count — restart tracking at this slot
          }
        }.elsewhen(le.currentIter =/= le.learnedIter) {
          next.confident   := false.B
          next.confCount   := 0.U
          next.learnedIter := le.currentIter
        }
        next.currentIter := 0.U
      }
      loopMem.write(li, next)
    }.elsewhen(io.updTaken) {
      val start = WireDefault(fresh) // first taken sighting at this slot: start tracking
      start.currentIter := 1.U
      loopMem.write(li, start)
    }

    // ── History ───────────────────────────────────────────────────────────────
    val newCommitted = ((committedGhr << 1) | io.updTaken.asUInt)(MaxHist - 1, 0)
    committedGhr := newCommitted
    ghr          := Mux(specMode, ghr, newCommitted)
  }

  when(io.specValid) {
    ghr      := ((ghr << 1) | io.specTaken.asUInt)(MaxHist - 1, 0)
    specMode := true.B
  }

  when(io.recoverValid) {
    ghr := committedGhr
  }

  when(io.restValid) {
    ghr      := ((io.restHist << 1) | io.restTaken.asUInt)(MaxHist - 1, 0)
    specMode := true.B
  }
}

/** Emits generated/LTageBp.sv. Run via generate.sh. */
object GenerateLTage extends App {
  _root_.circt.stage.ChiselStage.emitSystemVerilogFile(
    new LTageBp,
    Array("--target-dir", "generated"),
    Array("--lowering-options=disallowLocalVariables,disallowPackedArrays", "--strip-debug-info")
  )
}
