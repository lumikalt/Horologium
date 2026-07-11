# gem5 O3CPU vs OooeTrain — divergence analysis & change plan

Investigation of the "heavy" H/G ratio gaps in `docs/gem5-comparison.md`
(`--structurally-matched`: bypass=0, mem=10ns). Grounded in gem5 `stats.txt`
under `m5out-compare/` plus live Horologium runs. **Planning only — no code
changes made.**

## Where the gaps actually come from

Two independent root causes explain every heavy divergence. Neither is the
"structural simulation difference" the current doc gestures at.

### 1. memcpy / vvadd — store-buffer depth, not caches (calibration knob)

The comparison sweep pins `store_buffer_capacity: 2`. That is the entire gap
for the store-streaming kernels. Measured (16 KB D$, miss_latency 10):

| workload | ways | sb | Horo IPC | cache_miss_stalls | note |
|----------|------|----|----------|-------------------|------|
| memcpy   | 4    | 2  | 0.321    | 27 430            | comparison default |
| memcpy   | 8    | 2  | 0.321    | 27 430            | **ways has zero effect** |
| memcpy   | 8    | 8  | 1.580    | 10                | |
| memcpy   | 4    | 8  | 1.580    | 10                | **sb is the only lever** |
| vvadd    | 4    | 2  | 1.072    | 970               | comparison default |
| vvadd    | 4    | 8  | 1.863    | 10                | |

gem5 references: memcpy 0.727 (@30ns) / 1.086 (@10ns); vvadd 1.454 (@30ns).

With a depth-2 store buffer, committed store-misses back up and stall commit
(`wb_absorbed_stalls` 12 460 → 39 880 when depth goes to 8, i.e. stores get
absorbed instead of stalling). gem5 sustains far more outstanding stores
(SQ=32 + 4 cache MSHRs + write buffer). The `assoc=8` vs `ways=4` asymmetry
noted in the current doc is a **red herring** — it changes nothing here.

Caveat: `sb=8` overshoots (Horologium ends up *faster* than gem5), so this is a
knob to *calibrate*, not simply maximise. The fair target is whatever depth
reproduces gem5's effective store-handling, not an arbitrary bump.

### 2. treesum / towers — control-flow speculation (two real modeling gaps)

treesum: Horologium 499 branch mispredicts → 525 flushes → 13 345 cyc (IPC
0.865). gem5: **70** committed mispredicts (70 conditional, **0 return**, 2
call) → 6 943 cyc (IPC 1.66). Matched instruction streams (Δinsts +0.1%). The
~455 extra flushes are the whole 2× gap.

**The current doc's claim that gem5 records ~499 treesum mispredicts is wrong
(it's 70).** That error is what led the prior analysis to dismiss the predictor
/ control-flow angle. gem5's RAS is checkpointed → 0 committed return
mispredicts; gem5 does not direction-predict unconditional branches → 2 call
mispredicts.

Two compounding Horologium gaps produce the excess:

- **(a) Unconditional direct branches are routed through the direction
  predictor.** `FetchHint` (`src/Core/Mechanism/FetchHint.cs`) exposes only
  `IsBranch / IsCall / IsReturn / BranchTarget` — there is no
  conditional/unconditional bit. In `OooeTrain` fetch (~line 1377) every
  non-return branch, including `jal`/`j`, is sent to `_predictor.Predict`. A
  predictor that does not special-case unconditionals mispredicts every call:
  `true_oracle` yields **1537** mispredicts on treesum, identical to
  `always_not_taken`, because it never forces the call taken-to-target. `LTage`
  masks this by learning targets BTB-style, but the fetch model is still
  fragile and wastes prediction bandwidth on always-taken control flow.

- **(b) The RAS is not checkpointed/restored on flush.**
  `ReturnAddressStack` documents this explicitly ("not checkpointed — a flush
  leaves the stack transiently wrong"). Wrong-path fetch after a mispredict
  (amplified by (a)) pushes/pops the RAS, and the corruption is never repaired,
  so subsequent correct-path returns mispredict — cascading into more flushes.

towers (recursive Hanoi) shows the same signature: 45 vs 30 mispredicts, plus a
memory-dependence component (96 vs 15 violations) already addressed by the
opt-in store-set predictor.

Note: a clean config-only split of the 499 into return-share vs conditional-
share is **not achievable** — every predictor swap changes the wrong-path fetch
pattern, which changes RAS corruption (that entanglement is itself the finding).
So "RAS checkpointing closes the gap" is the leading hypothesis, not an
established quantity; confirm the return share with a diagnostic per-type
counter before claiming closure.

### 3. pchase — DRAM-latency knob, not a defect

Horologium faster (H/G 1.38) purely because HtifMemory ≈ 10-cycle miss vs gem5
SimpleMemory 30 ns. This is the `--mem-lat-ns` axis, already documented. **No
change required.**

## Plan of changes (in priority order)

**A. Recalibrate `store_buffer_capacity` in the comparison harness.**
Config-only. `scripts/gem5-compare.sh` currently hard-codes `2`. Determine the
depth that fairly matches gem5's store path (SQ=32 + 4 MSHRs + write buffer) and
update the sweep + `docs/gem5-comparison.md`. Expected: memcpy and vvadd move
from H/G ≈ 0.30 / 0.74 to ≈ 1.0. This is a calibration fix, not a core defect.

**B. Distinguish unconditional direct branches in fetch.** Core change. Add an
`IsConditional` (or `IsUnconditional`) flag to `FetchHint`; populate it in each
decoder's `GetFetchHint`. In `OooeTrain` (and `FiveStageTrain`) fetch, force
unconditional direct jumps/calls to predicted-taken → `BranchTarget`, and
consult the direction predictor only for conditional branches. Mirrors gem5,
which never direction-predicts unconditionals.

**C. Checkpoint/restore the RAS across flushes.** Core change. Snapshot the RAS
top-of-stack pointer (and enough state to reconstruct the popped entry) at each
predicted branch; restore on `StepFlush`. gem5 carries RAS state in the branch
predictor history and rolls it back on squash. Do **B and C together** — B
reduces wrong-path fetches, C stops those that remain from corrupting the RAS —
then re-measure treesum/towers mispredicts by type to confirm the closure
rather than asserting it.

**D. Correct `docs/gem5-comparison.md`.** The treesum section states gem5 gets
~499 mispredicts and that predictor/control-flow differences don't explain the
gap. Replace with the measured 70 (0 return) and the RAS/unconditional findings
above. Do this alongside B/C so the doc reflects the fixed behaviour.

**E. pchase — no change.** Keep it out of "must change"; annotate as the
DRAM-latency axis if not already clear.

## Confidence

- A (store buffer): **high** — measured, config-only, reproducible.
- B/C (control flow): **high on mechanism, unquantified on closure** — root
  causes confirmed by code inspection and predictor-swap behaviour; the exact
  return-vs-conditional split needs a diagnostic counter before the gap-closure
  magnitude is claimed.
- D/E: bookkeeping.

## Implementation outcome (measured)

All four items were implemented. Results (`--structurally-matched`, store_buffer=8):

- **A — store buffer 2 → 8** (matches gem5 `write_buffers=8`, confirmed in `config.ini`).
  memcpy 0.302 → 1.433, vvadd 0.740 → 1.315, rsort 1.139 → 1.233. Clean win; the memory
  kernels now sit above gem5 for the same reason pchase does (faster HtifMemory).
- **B — unconditional-jump handling** (`FetchHint.IsUnconditional`; fetch resolves direct
  `jal`/`j` to target). Correct and faithful; eliminates the pathology where predictors
  that don't special-case unconditionals mispredict every call (`true_oracle` on treesum
  went from 1537 to correct).
- **C — RAS checkpoint/restore** (architectural shadow in `OooeTrain`, restored on flush).
  Correct and faithful.
- **B + C together removed only ~4 of treesum's 499 mispredicts.** The RAS hypothesis in
  this plan was **wrong**: the excess is *conditional* mispredicts, not returns. Confirmed
  by code: `LTageBranchPrediction.Ghr` is updated only in `Update()` at commit, so the
  global history is stale across the ROB window. The real treesum lever is **speculative
  branch-history update + squash recovery** — a change to the `IBranchPredictor` contract
  and every predictor. Not implemented here; logged in `IDEAS.md`. treesum stays at 0.478.
- **D — doc correction** applied to `docs/gem5-comparison.md` (the gem5-gets-499 error and
  the associativity/DRAM misattribution for memcpy).

Lesson (kept for the next investigation): measure the return-vs-conditional split before
attributing a mispredict gap to the RAS. B/C are still worth keeping — they are strictly
more faithful to gem5 — but they were not the treesum lever.

## Follow-up: speculative branch history (implemented)

The real treesum lever from the IDEAS backlog. `LTageBranchPrediction` updated the global
history register only at commit, so TAGE lookups indexed stale history across the ROB
window. Fixed by keeping a speculative `Ghr` advanced at fetch against an architectural
`_committedGhr` shadow that trains the tables and restores `Ghr` on flush; exposed as
`SpeculativeHistoryUpdate`/`RecoverSpeculativeHistory` on `IBranchPredictor` (default
no-ops) and wired into `OooeTrain` only. Bit-identical for in-order pipelines via a
`_speculative` latch; covers the whole TAGE family (all share the base `Ghr`).

Measured (structurally-matched): mispredicts fell broadly (treesum 495 → 322, towers
41 → 21, below gem5's 30). IPC: **median 0.797 → 1.051**, gcd 0.865 → 0.906, towers
0.721 → 0.744; rest flat. treesum slipped 0.478 → 0.459 — the mispredict win drives deeper
speculation and lifts memory-order violations 26 → 245; store sets remove them and recover
IPC to ~0.84. The residual treesum gap (322 vs 70) is predictor *structure*: gem5's
local-history predictor suits the recursive null-check. Logged as a new IDEAS item.
