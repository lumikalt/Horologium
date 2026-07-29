# Out-of-Order Execution (OooeTrain)

Deep-dive on `OooeTrain`'s feature set — Tomasulo-style superscalar OoO with physical register renaming,
ROB-based in-order commit, and a unified issue queue. See [pipeline-trains.md](pipeline-trains.md) for the other
trains and [checkpoint-processing.md](checkpoint-processing.md) for `CprTrain`, the ROB-free sibling that shares
several of these mechanisms (macro/micro-fusion, store address/data decomposition).

`OooeTrain` is a superscalar out-of-order pipeline using Tomasulo's algorithm. Physical register renaming, ROB-based
in-order commit, and a unified issue queue. Macro-fusion (same opt-in `Rv32Mechanism(enableMacroFusion: true)` flag
  and SLT+branch idiom as `SuperscalarTrain`, see [pipeline-trains.md](pipeline-trains.md)) fuses at Rename, before RAT allocation, into a single ROB+IQ
  entry — unlike the in-order train, an OoO consumer can't read a producer's value until the CDB broadcasts it, so
  only collapsing to one entry (not just co-issuing) removes the latency; `ITooth.ArchInstructionCount` keeps
  `_retiredCounter`/instret counting both original instructions, and `ITooth.BranchComponent` redirects branch-
  predictor training to the fused pair's real branch PC (the fused Tooth's own `Pc` is the compare half). The branch
  half's own InstrId (assigned at Fetch, before fusion collapses it into one ROB entry) is preserved as
  `RobEntry.FusedSecondInstrId`, threaded from Rename through to Retire/Flush: `PEventLog` records a matching
  Retire/Flush event for it instead of leaving a Fetch-only row dangling in the waterfall visualization; the Olympia
  co-sim `_commitObserver`/`Rdip` hooks fire a second time with its real `(Pc, RawEncoding)` so a fused commit reports
  as the two architectural instructions it is, not one; and `TrainCriticality` trains the critical-path predictor a
  second time for it with the same D/E/C sources as the primary (the pair shares one Dispatch/Issue/Execute/Commit
  timing throughout, so this is exact, not approximate) — otherwise its dropped InstrId, silently skipped in the ROB's
  InstrId sequence, would leave stale/unrelated token-table state for a later entry's `InstrId - 1`/`InstrId - w`
  source lookup to read. `CprTrain` mirrors only the `PEventLog` half of this (via `CheckpointEntry.FusedSecondInstrId`)
  since it has no co-sim or criticality-prediction integration. The benefit
  doesn't show on a dense independent-pairs stream (fetch/rename/issue/commit share one width parameter, so
  throughput there is fetch-bound regardless of fusion) — it shows under ROB pressure: a fused pair costs one ROB
  entry instead of two, so more independent work survives in the shadow of a stalled ROB head (e.g. a cache-miss
  load) before dispatch stalls on ROB-full. **Load+ALU micro-fusion** (`RvMacroFuser`'s second pattern,
  `RvFusedLoadAlu`, same `enableMacroFusion` flag) fuses a load immediately followed by an ALU op that both reads
  and overwrites the load's own destination register (e.g. `lw t0,0(a0); addi t0,t0,4`) into a single ROB+IQ entry,
  `ToothClass.Load`-classed so every existing Load-keyed mechanism (LQ allocation, forwarding, STT/InvisiSpec
  gating, cache-hit/miss latency selection) applies unchanged. Safe without a general liveness analysis because the
  ALU op's destination is provably the same architectural register as the load's, and fusion only ever triggers on
  the actual adjacent decoded/dispatched stream — unlike compare+branch, this can't collapse execute latency (a
  load's cache-hit/miss latency is charged after `Execute()` as a visibility-delay countdown, not modeled inside it;
  fusing eliminates only the extra Issue→wakeup→re-issue round-trip the ALU consumer would otherwise pay), so it's
  lower-impact than compare+branch fusion even in principle. Tracked separately from compare+branch fusion via a
  distinct `micro_fusions` counter (compare+branch keeps `macro_fusions`).
  load) before dispatch stalls on ROB-full. Functional units are configurable per class (`FuLatencyConfig`): each
  class (integer ALU, multiplier/divider, pipelined FP, FP divide/sqrt, load-store, branch, system) has an independent
  issue-port count and execution latency; multi-cycle results flow through a countdown-based in-flight buffer before CDB
  broadcast. Default latencies: integer ALU 1 cycle, integer mul/div 3, pipelined FP 4 (
  add/sub/mul/fma/compare/convert), FP div/sqrt 16, load-store 1. Dedicated `LoadQueue` and `StoreQueue` circular
  buffers track in-flight speculative loads and stores independently of the ROB. Loads and stores share a monotonic
  sequence number at dispatch so program order can be determined across queues without ROB-index wrap. Loads issue
  speculatively without waiting for older stores; store-to-load forwarding supplies the correct value when the store has
  already executed, and a memory-order violation squash (flush + re-execute from the load's PC) recovers when the store
  resolved after the load. A `mem_order_violations` counter tracks re-executions. A **store-set predictor** (
  `StoreSetPredictor`, enable with `enableStoreSets: true`) avoids unnecessary squashes by predicting, at dispatch,
  which stores a load depends on: a Store-Set Identifier Table (SSIT, 1024 entries, PC-indexed) maps instructions to
  store-set IDs; a Last Fetched Store Table (LFST, 1024 entries) tracks the most-recently-dispatched store per set;
  loads stall at issue until their predicted store's address is known; violations train the predictor via four
  SSID-merge rules (Chrysos &amp; Emer, ISCA 1998). **Store address/data decomposition** (opt-in via
  `enableEarlyStoreAddress`) lets that address become known before the store's data operand does, instead of both
  only ever resolving together via the store's single atomic Issue/Execute: `StepEarlyStoreAddressResolution` scans
  in-flight store IQ entries each cycle and, once the address operand alone is ready, calls a new
  `IExecutor.TryComputeStoreAddress` hook to set `SqEntry.Address`/`AddressKnown` early — releasing this store-set
  stall, `HasUnresolvedPrecedingStore` (`FuLatencyConfig.ConservativeLoads`), and forwarding-candidate address
  matching sooner when a store's data operand has a slower producer chain than its address. This requires a genuine
  `SqEntry.DataKnown` split (`AddressKnown` can now be true well before `Value`/`Width` are valid) —
  `TryForwardFromStore`/`FindForwardingProducerSeqNo` gate on both. Because the store's own retire-gating
  Issue/Execute event and `RobEntry.IsComplete` are untouched, this needed no second IQ entry or dual-completion
  tracking — deliberately lighter than the textbook STA/STD µop-decomposition shape. Because the SSIT carries no address information, one genuine
  conflict permanently merges every future dynamic instance of that load/store PC pair — costly for recursive/generic
  functions that reuse one PC pair across many independent addresses. Both tables are cleared every 4096 load
  dispatches (`clearPeriod`) to bound that cost; the value was chosen by sweeping the full gem5-compare benchmark
  suite (see `docs/gem5-comparison.md`). A **speculative memory bypassing predictor** (NoSQ, `SmbPredictor`,
  enable with `enableSmbBypass: true`) lets a load short-circuit straight to an early result instead of waiting
  for its own execution: a PC-indexed, confidence-gated table predicts the SSN distance (shared LQ/SQ dispatch
  sequence number) back to the producing store, and if a store with that exact distance is live in the SQ at
  dispatch with a static access width matching the load's, the load is marked `Bypassed` and gets an early PRF
  write + CDB broadcast the moment that store's value resolves — without needing anyone's address. The load's
  own shadow execution still runs the ordinary pipeline afterward and is the sole thing that gates its commit;
  a mismatch marks it `BypassMispredicted`, triggering the same flush + retrain recovery as an ordinary
  memory-order violation. Ordinary (non-bypassed) loads that forward from a live store also train the
  predictor, so the very first prediction for a PC doesn't have to wait for a successful bypass to seed it.
  v1 scope reductions (Sha, Martin &amp; Roth, MICRO 2006; Tyson &amp; Austin, MICRO 1997): the predictor is
  path-insensitive (PC-indexed only, no history register); bypass is full-word/zero-offset only (the predicted
  producer's static width must equal the load's, so no address is ever needed to trust a prediction); and the
  `LoadQueue` is retained unconditionally as the verification backstop rather than eliminated, exactly as the
  papers themselves treat LQ elimination as an optional, performance-neutral extension. A **value predictor** (pass
  an `IValuePredictor` via the `valuePredictor` constructor parameter; `null` disables the feature entirely) predicts
  the destination register value of eligible instructions (`IntegerAlu`, `IntegerMulDiv`, `Load`, `FloatingPoint`,
  `FloatDivSqrt`, `System` — i.e. any single-register-destination result that lands in the PRF; excludes `Atomic`,
  whose secondary destination bypasses the PRF, and `Vector`, which isn't renamed) at rename, writes it
  speculatively into the PRF and marks it ready immediately — so dependent instructions issue and execute without
  waiting for the real producer — while the producing instruction still executes for real in the background. Two
  predictors are provided: `LvpVp` (Lipasti &amp; Shen, "Exceeding the Dataflow Limit via Value Prediction",
  MICRO 1996 — the LVPT scheme), a tagless, PC-indexed table of last-seen values; and `VtageVp` (Perais &amp;
  Seznec, "Practical Data Value Speculation for Future High-end Processors", HPCA 2014), which adapts the ITTAGE
  indirect-branch predictor to value prediction — a tagless `LvpVp` base component backed by six tagged
  components indexed by a hash of the PC and a geometrically increasing number of global-branch-history bits (2, 4,
  8, 16, 32, 64), so it can predict back-to-back occurrences of an instruction in a tight loop with no same-cycle
  critical dependency, unlike local-value-history predictors. Both are gated by a `ForwardProbabilisticCounter`
  (Perais &amp; Seznec §5, after Riley &amp; Zilles, HPCA 2006): a 3-bit saturating counter whose forward
  transitions are only taken probabilistically, mimicking a much wider counter at a fraction of the storage, and
  which hard-resets to 0 on any misprediction; a prediction is used only once fully saturated. Recovery mirrors
  `SmbPredictor`'s: no selective reissue, no execution-time repair path — a mismatch discovered when the real
  result completes (`StepComplete`) is squashed with a full re-fetch the moment the mispredicted instruction reaches
  the ROB head (`StepCommit`), the paper's central finding that squash-at-commit performs within noise of an
  idealized selective-reissue implementation once FPC accuracy exceeds ~99.5%. `VtageVp` keeps its own
  speculative/committed global-history shadow (independent of whichever `IBranchPredictor` is configured), advanced
  at fetch and rewound via checkpoint/restore on both a full flush and an execute-time partial squash — so its
  index survives ordinary branch mispredictions exactly, not just approximately. **EOLE Late Execution** (Perais
  &amp; Seznec, "EOLE: Paving the Way for an Effective Implementation of Value Prediction", ISCA 2014; enable with
  `enableEoleLateExec: true`, requires a `valuePredictor`) removes confidently value-predicted single-cycle ALU
  ops from the OoO scheduler entirely: instead of entering the IQ, they are marked complete at Dispatch (their
  predicted value is already the live PRF value) and their real computation is deferred to an in-order
  verify-at-Commit step reusing the exact same `ExecuteOne`/squash-at-commit machinery as ordinary value
  prediction, just relocated from `StepComplete` to `StepCommit` — so it costs no new recovery path, only a
  narrower window for OoO issue-port contention to matter, letting a narrow-issue machine approach a wider one's
  performance on ALU-heavy code (the paper's own headline result). **EOLE Early Execution** (same paper §3.2;
  enable with `enableEoleEarlyExec: true`, no `valuePredictor` required — operand readiness at rename is
  provenance-agnostic) computes a single-cycle `IntegerAlu` instruction immediately in `StepRename`, in-order,
  whenever both its source registers are already ready — an immediate, an already-committed value, or a value
  prediction all count, exactly as the paper specifies ("operands are never read from the PRF" in their hardware;
  Horologium's PRF already stores all three provenances, so reading it is the equivalent simplification). The one
  detail that came directly from the paper rather than Horologium's own structure: the paper found chaining
  Early-Execution results *within the same rename cycle* ("more than a single [ALU] stage") "highly inefficient"
  and settled on a 1-deep design where only the *previous* cycle's Early-Execution results may feed a new one.
  `StepRename`'s `_eeWrittenThisTick` set enforces exactly that cap (cleared every tick), which matters because
  `StepRename` can drain a multi-tick decode-queue backlog in one call — without the cap, a stalled dependency
  chain would collapse implausibly in a single tick the moment the backlog cleared. An Early-Executed
  instruction's result needs no separate commit-time verification (unlike Late Execution): the only way one of
  its operands could be wrong is if it came from a value prediction, and in-order commit guarantees that
  prediction's own squash-at-commit path (if it mispredicts) flushes the Early-Executed consumer before it ever
  reaches the ROB head. Both bypass `StepIssue`/`StepExecute` entirely, so the Top-Down (Yasin, ISPASS 2014)
  Level-2 Core/Memory Bound split — the only TMA/CPI-stack accounting that reads execute-stage traffic rather
  than dispatch-stage slot counts or ROB-head completeness — folds each tick's EOLE-bypassed count in at the
  end of `StepDispatch` so a narrow-issue machine leaning on EOLE isn't misreported as execution-stalled.
  VTAGE's `TryPredict`/`Update` calls take an explicit per-instruction `ValueHistoryCheckpoint`, captured once at
  that instruction's own Fetch and threaded through Rename/Commit, rather than each method separately reading
  whichever of the predictor's live-speculative or committed-shadow history register it used to read — the
  latter let heavy squash/refetch churn drift the two apart, aliasing a confidently-wrong prediction onto a
  slot training could never reach to correct (a permanent livelock, not just a missed opportunity).
  **`StrideVp`** is a computational value predictor (Sazeides & Smith's taxonomy, as summarized in
  Perais & Seznec, HPCA 2014 §2) complementary to LVP/VTAGE's value-repetition approach: it tracks a static
  instruction's last value and the constant stride between successive occurrences, predicting `lastValue +
  stride`, so a monotonically incrementing register (which never repeats a value, and so never lets
  LVP/VTAGE's confidence saturate) still predicts trivially. Confidence is a 4-state FSM (`Init`/`Transient`/
  `Steady`/`NoPred`) requiring two consecutive matching strides to reach `Steady` before predicting -- a
  "2-delta"-style confidence gate, not a reproduction of any specific historical stride predictor's exact
  mechanism. It also tracks an in-flight speculative depth per PC -- how many renamed-but-uncommitted
  occurrences of that PC are still unresolved, confident prediction or not -- so a tight loop with several
  genuinely overlapping iterations (renamed well ahead of commit under a competent branch predictor) predicts
  `lastCommittedValue + stride * (depth + 1)` rather than a single un-scaled stride step; measured directly in
  Horologium's own OoOE pipeline, the latter mispredicted roughly 44% of the time once overlap was allowed to
  develop. Counting every `TryPredict` call toward the depth (not just confident ones) closes a residual
  undercount in the warmup and post-squash windows, where earlier, still-in-flight occurrences of the same PC
  hadn't yet reached `Steady` when renamed but still owed a matching commit -- the 84 residual mispredicts left
  by the depth-scaling fix above dropped to 0 on the same loop once this was fixed. The depth counter is reset
  on any squash (a discarded occurrence has no commit to decrement it, so it would otherwise leak upward).
  **`HybridVp`** composes any context-based and computational `IValuePredictor` (e.g.
  `VtageVp` + `StrideVp`) per the paper's own §7.1.2 combination rule: a lone confident
  component's prediction is used as-is; two confident components that agree are used; two that disagree
  suppress the prediction entirely; both are trained at every retire regardless of which one predicted. Not
  modeled: the paper's further optimization of feeding one component's speculative prediction to the other to
  resolve back-to-back same-PC occurrences within a single cycle -- Horologium's pipeline only calls
  `TryPredict`/`Update` once per instruction, at Rename/Commit, so there's no equivalent intra-cycle chaining
  to hook into. **`DynamicClassificationVp`** (Rychlik et al., CMuART-1998-01, §3.2.3 "Efficient
  Dynamic Scheme") is the alternative to always-query-both: each PC is assigned, after a 3-value learning
  window, to *at most one* of the two components -- equal consecutive deltas (including zero) route to the
  computational component, anything else routes to the context component, folding the paper's 3-predictor
  split (Popular Last Value / Stride+ / FCM) onto Horologium's 2-component hybrid since VTAGE's own tagless
  LVP base already subsumes Popular Last Value. A classified PC whose component stops predicting confidently
  after having predicted at least once is evicted -- permanently to Don't Predict if it was on the context
  (FCM-role) component, or back to Unclassified to relearn if it was on the computational one -- after
  `evictThreshold` (default 2) consecutive non-confident `TryPredict` calls, resetting on the next confident
  one. That threshold is still an adapted proxy for the paper's confidence-reaches-zero trigger, since
  `IValuePredictor` exposes no raw confidence value to distinguish "low but nonzero" from "zero" -- but it no
  longer drops a context-classified PC (whose whole premise is ~95%, not 100%, accuracy) to permanent Don't
  Predict on its first ordinary miss. A **critical-path
  predictor** (`TokenPassingCriticalityPredictor`,
  enable with `enableCriticalityPrediction: true`) biases `StepIssue` to prefer predicted-critical
  instructions when several ready instructions compete for the same functional-unit/port slot. Each
  instruction is modeled as a 3-node dependence graph (dispatch/execute/commit); the pipeline resolves,
  at commit, which of seven edge types (ROB-stall, branch-redirect, last-arriving-operand producer, etc.)
  fed each node, and a token-passing predictor plants a token at a seed instruction's execute node,
  propagates it forward along those edges, and trains a 16K-entry PC-indexed hysteresis table on whether
  the token survives `500 + robCapacity` commits (Fields, Rubin &amp; Bodík, "Focusing Processor Policies
  via Critical-Path Prediction", ISCA 2001). Purely a scheduling-priority hint — disabled by default and,
  when enabled, never changes committed architectural results, only issue order among already-ready
  instructions. **Runahead execution** (enable with `enableRunahead: true`, budget via `runaheadBudget`,
  default 200) pre-executes past a full-window stall to generate prefetches (Mutlu et al., "Runahead
  Execution: An Alternative to Very Large Instruction Windows for Out-of-order Processors", HPCA 2003;
  Naithani, Roelandts &amp; Eeckhout, "Precise Runahead Execution", HPCA 2020). A literal port of either
  paper's release-and-refetch or elastic-ROB-release mechanism doesn't fit: `ReorderBuffer` retires
  strictly from the head with no out-of-order release, and dispatch is already unconditionally stalled
  the instant the ROB is full — there is no free ROB/IQ capacity to run inside during the stall the way
  PRE's target microarchitecture has. What Horologium builds instead is a self-contained shadow execution
  lane, entered only when dispatch is stalled behind a full ROB whose head is an incomplete load. Because
  real commit and real dispatch are already frozen for the whole stall, the shadow lane can safely draw
  fresh physical registers from the same live `RenameMap` free list with zero collision risk (nothing else
  is renaming during the stall) and undo everything on exit by restoring a `RenameMapSnapshot` of just the
  RAT — no ROB/IQ/LQ/SQ involvement needed. Since `SetAssociativeCache.Read()` installs data functionally
  and immediately on every call (hit or miss; miss cost is a separately-accounted stall-cycle count, not
  an async fill), no runahead cache or INV-bit array is needed either: the only genuinely unavailable value
  during an episode is the blocking load's own not-yet-written physical register and anything that
  transitively reads it, tracked by a small tainted-physical-register set seeded at entry from every
  not-ready live RAT mapping. A tainted source blocks a shadow load/store's real memory access entirely —
  broadened from the papers' narrower "tainted address" framing because identifying which specific source
  is the address is not exposed generically by `ITooth` and would require ISA-specific operand-ordering
  knowledge, which the pipeline/ISA isolation boundary forbids; the broadened rule is strictly more
  conservative (it may occasionally forgo a safe prefetch, never a correctness risk, since nothing shadow-
  computed is ever committed). Shadow stores write only into a scratch dictionary keyed by exact
  `(address, bytes)`; shadow loads check that dictionary first, then fall through to the real
  `DLayers.Accessor` — which is what actually warms the real cache for the real pipeline to find hot once
  it resumes. v1 scope reductions: the shadow stream is scalar-only (`IntegerAlu`, `IntegerMulDiv`, `Load`,
  `Store`, `Branch`, `ConditionalBranch` — anything else, including `Vector`/`Uve`, exits the episode
  cleanly rather than modeling side effects); shadow branches call `IBranchPredictor.Predict()` for direction but
  never train predictor history or touch the RAS; runahead is disabled whenever fetch is not effectively
  bare-metal (active Sv32 paging resolves a non-identity physical address for the fetch PC); and shadow
  throughput is capped at `issueWidth` instructions per real cycle, bounded per-episode by
  `runaheadBudget`. The LQ, SQ, and RAS are never touched by any runahead code path. **Vector Runahead**
  (enable with `enableVectorRunahead: true`, lane width via `runaheadVectorWidth`, default 8; requires
  `enableRunahead`) extends the shadow lane to chase long dependent (pointer-chasing) gather/scatter
  chains instead of exiting the instant the real blocking load resolves (Naithani, Ainsworth, Jones &amp;
  Eeckhout, "Vector Runahead", ISCA 2021). A PC-indexed, direct-mapped stride table (`LastAddr`/`Stride`/
  2-bit saturating `Confidence`/learned `Terminator`, sized like `StridePrefetcher`'s RPT) is trained only
  from the real (non-shadow) demand-load stream at the existing prefetcher hook. Once a load's own PC
  reaches saturated confidence, the shadow lane replicates its own instruction stream `runaheadVectorWidth`-
  wide instead of stepping one iteration at a time, and that vectorized state propagates through dependent
  arithmetic and indirect loads exactly the way `_runaheadTainted` already propagates the invalid-bit —
  membership in `_runaheadVectorized` is the paper's vectorize-bit, membership in `_runaheadTainted` is its
  invalid-bit. Per the paper's termination-condition change (innovation #1), the shadow lane keeps running
  past the point a scalar-only episode would exit as long as a chain is actively vectorizing, stopping only
  when the chain loops back to its own origin PC (backfilling the learned terminator) or reaches a
  previously-learned terminator. A chain-origin load whose address operand is itself tainted — typically
  because the front end has renamed several loop iterations ahead of a stalled dispatch, not because it
  truly depends on the stalled load's value — can still vectorize directly off the trained `LastAddr`/
  `Stride` sequence rather than requiring the live operand; this RPT-driven bypass is what lets Vector
  Runahead chase a simple strided loop-induction address in practice. As with the scalar feature, no real
  state is ever put at risk: the real `RiscV32.VectorRegisterFile` and RVV gather/scatter encodings are
  never touched, since a shadow episode must stay perfectly discardable and there is no physical VRF or
  vector rename table to safely draw scratch registers from the way scalar runahead draws from the live
  integer `RenameMap` free list. Vectorization is instead pure N-wide replication of the existing scalar
  shadow body against new scratch, physical-register-indexed lane state (`_runaheadVectorLanes`,
  `_runaheadVectorized`), discarded on exit exactly like `_runaheadTainted`/`_runaheadStoreBuffer` already
  are. **Vector unrolling** (§III-G of the paper, bounded by `runaheadUnrollLength`, default 8) extends a
  chain past its first termination point: instead of ending the moment the shadow lane loops back to the
  chain's origin PC or reaches the learned terminator, `TerminateOrUnroll` issues another
  `runaheadVectorWidth`-wide round from the same origin (advancing a round base address by
  `runaheadVectorWidth × Stride` each time) until `runaheadUnrollLength` total rounds have run, matching
  the paper's default of U=8 rounds of N=8 lanes (64 scalar-equivalent iterations) before falling back to
  normal shadow stepping. A `_runaheadCappedOrigins` set records which origin PCs have spent their round
  budget so a later revisit of the same PC in the same episode does not silently restart a fresh chain.
  Physical-register pressure from many rounds is handled by immediate free-on-rename reclamation
  (`FreeShadowRename`) rather than the paper's VRAT plus in-order register-deallocation queue: because the
  shadow lane issues strictly one instruction at a time along a single PC (never the paper's overlapped,
  out-of-program-order pipelined issue), any shadow instruction that could still read a register's old value
  has, by construction, already executed before the instruction that redefines it, so a physical register
  can be freed the instant its architectural register is renamed again — an RDQ's ordering guarantee for
  free, with no queue needed. **Vector pipelining** (the paper's P overlapped in-flight rounds, bounded by
  `runaheadPipelineDepth`, default 1) decouples round issuance from the shadow PC's single-PC loop-body
  walk: `PipelineRoundsThisVisit` computes `min(P, U − roundsSoFar)` — how many of the remaining unroll
  rounds to pack into the *next* origin-load vectorization event — so a single visit to the chain origin
  produces an N×rounds-wide lane array instead of a fixed N-wide one, needing only `⌈U/P⌉` total loop-body
  walks to reach `U` total rounds rather than `U` separate ones. No VRAT is needed the way the paper's fixed
  512-bit AVX vector registers require one: because "vectorization" here is already pure scratch lane-array
  replication (see above), a vectorized physical register's lane array is naturally width-generic —
  `VectorizeByReplay` propagates whatever width the origin produced (`srcLanes.Length`, not a fixed field),
  so ALU/indirect-load propagation through the dependent chain automatically carries P× the width with zero
  extra bookkeeping. `runaheadPipelineDepth: 1` is defined as exactly today's pre-pipelining behavior (every
  origin visit packs 1 round, matching `TerminateOrUnroll`'s old per-visit increment byte-for-byte) so the
  knob is purely additive. This is a real timing effect, not a counter relabeling: `SetAssociativeCache` has
  finite MSHR capacity (`MshrCount`, per-cycle countdown via `TickMshr`, capacity-stall charging via
  `ChargeAndAllocateMshr` when every slot is busy — `MshrCapacityStalls`), and shadow-lane reads share the
  same cache instance and `_pendingStalls` accumulator as real loads, so packing many rounds' worth of
  misses into one instant (no `TickMshr` ticks between them, unlike misses spread across separate
  loop-body-walk rounds) measurably contends for a small MSHR table — the *direction* of the paper's own
  §VI-B/Fig. 11 MSHR-count sensitivity (an under-provisioned MSHR table limits how much a deep pipeline
  depth can help) shows up here too, as higher `MshrCapacityStalls` under a tight `CacheMshrCount`, and
  vanishes (contention stays negligible) once the table is sized ≥ N×P. In principle a chain-bound episode
  (one that `UpdateChainTermination` keeps open past the point the originally-blocking real load already
  resolved, purely to keep vectorizing) *should* let a shorter, pipelined walk resume real dispatch/commit
  sooner than a serial one needing `U` separate walks — the episode's real-cycle length is `max` of the
  load's own resolution time and the chain's; only the second term is pipeline-depth-dependent, and it
  shrinks with `P`. In measurement, though, this potential saving was never isolable from two confounds
  that dominate it in every synthetic single-pass-loop configuration tried here: MSHR contention (above)
  and a more severe effect — deep `P` packs far more simultaneous speculative reads through the shared
  cache than a serial round would, and on these small test programs that measurably *raised* real demand-
  side `dcache_misses` (over an order of magnitude in one configuration) rather than lowering it, i.e. the
  extra speculative volume evicted or otherwise disturbed data the real stream still needed sooner than the
  far-future addresses it fetched — cache pollution, not the paper's assumed clean-prefetch benefit.
  Net effect measured across every configuration tried up to that point: pipelining came out
  neutral-to-worse on real `cycles`/`dcache_misses`, never demonstrably better.

  **Follow-up investigation, resolved:** disentangling MSHR contention and cache pollution from the
  in-principle episode-shortening benefit required a program where `U` rounds' reach never overshoots
  real future demand and an MSHR table large enough (`CacheMshrCount ≥ N×P`) to keep contention at
  zero — once both confounds are eliminated by construction, `runaheadPipelineDepth` **does** shrink
  real cycles, monotonically (measured: P=1 2565, P=2 2506, P=4 2492, P=8 2487 cycles on a 200-iteration,
  one-cache-line-stride program). But the deeper finding, found only by adding the `enableRunahead: false`
  baseline that earlier comparisons omitted, is less flattering than "pipelining helps": on this same
  program, turning Vector Runahead **off entirely** measured 1311 cycles — faster than *every*
  runahead-enabled configuration, including the best-case fully-pipelined one. The reason is structural,
  not confound-related: `StepRename` freezes real rename for the entire duration `_runaheadActive` is
  true (a chain-bound episode extends that freeze past the point the real blocking load resolves, for as
  long as the chain keeps unrolling), which blocks further iterations from even entering the ROB window
  during the episode — but this program's ROB (8 entries, 3 instructions/iteration) is already deep
  enough that plain OoO execution extracts most of the available memory-level parallelism for free,
  without any speculative help. Runahead's rename freeze is pure overhead here; deeper `P` only shrinks
  how long that freeze lasts (recovering part of the self-inflicted cost), it never converts Vector
  Runahead into a net win on a pattern the ROB alone can already parallelize. So the TODO's question
  ("why does pipelining measure neutral-to-worse") resolves to: it usually isn't really about pipelining
  at all — it's Vector Runahead itself being net-negative on ROB-parallelizable streaming patterns, with
  `P` only ever modulating the size of that self-inflicted loss. Locked in by
  `VectorRunaheadPipelineTests.PipelineDepthP_RecoversPartOfRunaheadsOwnOverhead_ButNeverBeatsRunaheadOff`.
  Also worth remembering: this comparison is itself sensitive to `runaheadBudget`/`extraPhysRegs` — under
  small values (400/32) sized for shorter chains, the same 200-iteration program's P=1-vs-P=8 comparison
  *inverts* (P=8 measured worse: 3009 vs. P=1's 1808 cycles). Checked, not asserted on a guess: at P=1
  under the small budget, `runahead_episodes` explodes to 645 (vs. 9 in the well-provisioned regime) while
  total `runahead_instructions` stays tiny (226) — nearly every episode aborts on `!_rat.HasFree` almost
  immediately, so P=1 does barely any speculative work and ends up cheap almost by accident (close to the
  1311-cycle runahead-off floor). At P=8 under the same small budget, episodes stay low (13) but
  `runahead_vector_lane_accesses` is high (576 = 9 chains × 64 lanes) — most episodes *do* complete a full
  unrolled chain before something forces a restart, so P=8 pays for several complete, largely redundant
  re-vectorizations of overlapping address ranges across those restarts. A fourth, distinct mechanism from
  MSHR contention, reach-overshoot, and the ROB-MLP finding above: tight budgets change how much redundant
  speculative work survives per episode, with opposite-signed effect for small vs. large P.
  `TryVectorizeShadowStep`'s untainted chain-origin path now tracks `_runaheadRoundBaseAddr` explicitly
  across visits, mirroring `TryVectorizeTaintedLoad`, instead of re-deriving each round's lane addresses
  from `mem.LastReadAddress` (which only advances one real loop iteration's stride per visit and made
  later rounds' lane ranges overlap almost entirely with earlier ones). This closes a real divergence
  between the two sibling paths, but empirically it produced no measurable behavioral difference in this
  model — real scalar demand coverage and shadow-lane stepping already reach these addresses on their own,
  so the overlap it fixes was never the actual bottleneck; kept for correctness and consistency, not a
  measured performance gain.

  v1-v3 scope reductions: no per-lane divergence/masking (an invalid lane is simply
  marked tainted rather than modeled with a real predicate mask); fixed lane width, not tied to the real
  `VLEN=128` architectural setting; and only one live chain is tracked at a time. Control-flow
  speculation follows gem5: direct unconditional jumps (`jal`/`j`,
  flagged by `FetchHint.IsUnconditional`) are resolved straight to their statically known target at fetch instead of
  being routed through the direction predictor; every direct branch (conditional included) takes its taken-target from
  the decode hint (`FetchHint.BranchTarget`) rather than a possibly-cold predictor BTB, so a stale/aliased BTB entry can
  never send speculative fetch to a null address (only indirect branches use the predictor's target, and a cold indirect
  target falls through); the `ReturnAddressStack` is checkpointed against an architectural shadow (advanced only when a
  call/return retires) that restores it on every flush, so wrong-path push/pop corruption does not leak into later
  return predictions; and history-based predictors keep a **speculative global history** advanced at fetch (
  `IBranchPredictor.SpeculativeHistoryUpdate`) and restored on flush (`RecoverSpeculativeHistory`) against an
  architectural committed-history shadow, so TAGE/LTage lookups index up-to-date history across the ROB window rather
  than stale commit-time history (the in-order trains are unaffected — they retain commit-time history bit-for-bit).
  Branch mispredicts are resolved at **execute** rather than commit, matching gem5's `iew`: a branch that resolves off
  its predicted path before reaching the ROB head triggers a **partial squash** that redirects fetch immediately,
  discarding only the younger in-flight instructions while the branch and everything older stay live and commit
  normally (a full flush is reserved for traps, load-order violations, and a mispredict that is already the ROB head).
  Exact recovery of speculative predictor state uses a per-branch history checkpoint captured at fetch (
  `IBranchPredictor.CaptureHistory`/`RestoreHistory`, covering the shared global- and local-history helpers and the
  whole TAGE family) plus a `ReturnAddressStack` rebuilt from the committed shadow and a replay of the surviving
  in-flight calls/returns; a branch younger than an older in-flight halt or trap is left to the commit-time path since
  that older entry will redirect first. Memory-level parallelism is modelled on both sides: each missed load carries the
  miss penalty in its own in-flight countdown so independent misses overlap (load-side MLP); a bounded write buffer (
  `writeBufferCapacity` parameter, default 0) absorbs committed store write-miss penalties asynchronously so the
  pipeline is not frozen while the write bus drains (store-side MLP). The D-cache is write-through / no-write-allocate,
  so writes reach memory the instant they are issued and write-buffer occupancy is a pure bus-latency model with no
  forwarding implications. TSO fences are modeled: a FENCE whose predecessor set contains W and successor set contains
  R (`ITooth.IsStoreLoadFence`, including FENCE.TSO) issues only at the ROB head once the write buffer has fully
  drained, and younger loads may not issue while it is in the ROB — closing the store→load window, the only reordering
  the train performs; all other fence flavours are timing no-ops because TSO already provides their ordering. The
  `OooeTrain` public API is a thin wrapper; `OoOPipelineCore` is the single-Gear implementation. A `StreamingEngine` (
  `src/Core/Orrery/Streaming/StreamingEngine.cs`) is embedded in every `OoOPipelineCore`: it manages up to 8
  independently configured affine memory streams (`StreamDescriptor` in `src/Core/Mechanism/`: base address, element
  width in bytes, element count, byte stride), each backed by a prefetch buffer of configurable depth. The engine's
  `Step()` is called unconditionally every pipeline cycle so streams prefetch ahead of consumption; streams are
  architectural state and survive pipeline flushes. UVE (Unlimited Vector Extension) instructions consume streams in the
  OoO pipeline: `ToothClass.Uve` ops are head-serialized (like `Vector`); the pipeline injects load-stream elements into
  `IUveScalars` before calling the executor; Issue stalls when a required load stream has no buffered element;
  `ExecuteResult.StreamConfig` carries `ss.ld.w` descriptors for `StreamingEngine.Configure`.
  **STT-ExpOnly** (enable with `enableSttExpOnly: true`) is the first slice of transient-execution defense
  modeling: **Speculative Taint Tracking**'s explicit-channel-only variant (Yu, Yan, Khyzha, Morrison, Torrellas
  &amp; Fletcher, "Speculative Taint Tracking (STT)", MICRO 2019) — only loads are treated as transmitters, no
  implicit-branch/prediction-based protection. A shared Spectre-model visibility-point tracker
  (`Pipeline.Ooo.SpectreVisibilityTracker`, Yan, Choi, Skarlatos, Morrison, Fletcher &amp; Torrellas,
  "InvisiSpec", MICRO 2018, Table 1) maintains the InstrId of the oldest unresolved in-flight branch as a FIFO
  of dispatched branches, drained as `StepComplete` resolves each one — an instruction is "safe" once no older
  branch is still unresolved. At Dispatch, each destination register's Youngest Root of Taint (`RobEntry.SourceYrot`
  / `PhysicalRegisterFile.Yrot`/`SetYrot`) is computed from its source operands' existing Yrot, with a Load/Atomic
  additionally rooting taint at its own destination (its fetched data isn't visible yet either). A load whose
  address operands carry a taint root that hasn't reached the visibility point is held at Issue — the classic
  `y = mem[mem[x]]` pointer-chase gadget is delayed until the branch that precedes it resolves, even when
  correctly predicted, which is the real, measurable IPC cost the paper's DelayExecute+STT-ExpOnly configuration
  reports. Held cycles are counted by the `stt_load_issue_stalls` dial.
  The **Futuristic visibility-point model** (Yan et al., MICRO 2018, §V-A1, Table I; Yu et al., MICRO 2019) is
  a selectable alternative to the Spectre model above, enabled with `sttFuturisticModel: true` on the same shared
  tracker — every STT-ExpOnly/InvisiSpec/implicit-branch/memdep-gating call site reads the tracker only through
  the `IVisibilityTracker` interface, so switching models changes none of them. An instruction is safe once it
  either (i) is at the ROB head, or (ii) is "speculative non-squashable" — preceded only by instructions that
  individually cannot be squashed by any of Table I's events. `Pipeline.Ooo.FuturisticVisibilityTracker`
  generalizes the Spectre tracker's branch-only FIFO into a per-instruction pending-source bitmask (`Trap`,
  `Branch`, `StoreAddr`, `Smb`, `Vp`), registered at Dispatch and cleared bit-by-bit only on each source's
  "no squash" outcome — a squash-bound outcome leaves its bit set until the squash actually fires, exactly like
  the Spectre tracker's own mispredicted-branch handling, so an instruction that turns out to squash something is
  never mistaken for safe beforehand. Condition (i) is enforced by the caller: `OooTrain` force-resolves the
  current ROB head once per cycle before anything reads `IsSafe`, and again for every entry `StepCommit`'s own
  retire loop examines (needed because that loop can retire more than one entry per tick). Table I's
  load-store/load-load aliasing risk falls out of the generalized FIFO for free — an older unresolved store
  blocks every younger instruction structurally, no separate per-load tracking needed. Multi-hart
  coherence-invalidation squashes and single-core load-load aliasing remain out of scope, matching every other
  InvisiSpec/STT slice below.
  **STT implicit-branch protection** (enable with `enableSttImplicitBranches: true`) closes the resolution-based
  implicit channel through explicit branches (§6.4.1): a mispredicted branch whose own resolution is still
  tainted (`RobEntry.SourceYrot` not yet safe) would otherwise squash younger wrong-path instructions the
  instant it resolves, making the squash's *timing* itself a function of tainted data. With this flag, such a
  branch is queued (`_pendingTaintedMispredicts`) and re-checked every cycle (`StepSttMispredictResolution`);
  the branch keeps executing/resolving normally — only this observable squash effect is delayed — until its
  taint clears, matching the paper's own framing ("STT lets the instructions execute, and only increases the
  latency of recovering from a tainted branch misprediction"). Predictor training (`_predictor.Update`) and the
  value-prediction/SMB-bypass mispredict squashes already fire only at commit — strictly later than any
  visibility point — so they were already safe by construction before this flag existed, and are untouched by
  it. Counted by the `stt_mispredict_deferrals` dial; composes with `enableSttExpOnly` (both flags together are
  the paper's actual "DelayExecute+STT" main proposal, not either flag alone).
  **STT memory-dependence predictor-training gate** (enable with `enableSttMemDepGating: true`) closes the
  prediction-based implicit channel from §6.4.2 ("Implicit branch with prediction"): the paper requires "the
  relevant predictor ... [to] be updated only by untainted data, i.e., only after the implicit branch predicate
  becomes untainted" — for memory-dependence speculation that predicate is a function of the *producing store's
  own address*, not the load's. `SmbPredictor.Train`'s cold-start/ongoing-seeding call (an ordinary forwarded
  load teaching the predictor a fresh SSN distance) is the one call site not already commit-time-safe; when the
  producing store's own `SourceYrot` isn't safe yet, the update is queued (`_pendingSmbTraining`) and re-checked
  every cycle (`StepSmbTrainingResolution`) rather than applied immediately. `StoreSetPredictor` has no `Train`
  method at all — its only persistent-state writer, `RecordViolation`, already fires exclusively at commit
  (already safe by construction); `OnStoreDispatch`/`OnLoadDispatch`/`OnStoreIssued` are ephemeral per-SSID LFST
  scheduling state, not learned persistence, so they aren't a training channel (their own resolution-timing
  channel is a separate, still-open TODO item). The other three `SmbPredictor.Train`/`TrainNoBypass` calls all
  fire inside `StepCommit`, already safe. Counted by the `stt_memdep_training_deferrals` dial.
  **The store-to-load-forwarding implicit branch's own resolution channel (§6.4.2/§6.5)** and
  **value-prediction training/squash gating** were both verified already safe by construction — no new gating
  needed, the same conclusion as `StoreSetPredictor.Train` above. The memory-order-violation squash and the
  value-misprediction squash both fire exclusively inside `StepCommit`'s ROB-head retire loop, unconditionally
  (two `Debug.Assert`s mirror the STT-implicit-branch one); `StoreSetPredictor.OnStoreIssued` only clears
  internal LFST bookkeeping with zero observable pipeline effect (the actual issue-time release reads
  `SqEntry.AddressKnown` directly). SMB (NoSQ)'s early bypass and value prediction's Rename-time write both let
  a load's *value* reach dependents ahead of its own visibility point — permitted under ExpOnly's threat model,
  which only gates *transmitters* (a load's own real memory access), never propagation — and in both cases the
  load's own real access still passes through `TryIssueSlot`'s unconditional STT gate (EOLE Late Execution, the
  only mechanism that skips it, is restricted to `ToothClass.IntegerAlu`). `Tests/RiscV32/Pipelines
  /SttStoreForwardTests.cs` and `SttValuePredictionTests.cs` demonstrate coexistence (both mechanisms fire
  together without perturbing each other's outcome); the same-instance guarantee itself rests on the code
  inspection above, not on those dynamic tests — confirmed by deliberately injecting the exact regression each
  test would need to catch and observing both stay green regardless.
  **InvisiSpec** (enable with `enableInvisiSpec: true`; Yan, Choi, Skarlatos, Morrison, Fletcher &amp; Torrellas,
  MICRO 2018, + 2019 Corrigendum) reuses the same shared `SpectreVisibilityTracker` for the cache-hierarchy
  counterpart: every scalar load speculatively peeks its data at Execute (`IMemory.PeekRead`, a non-mutating
  read — no LRU/fill/tag-install side effects, overridden by `SetAssociativeCache`/`BdiCache`, default `=> Read`
  elsewhere) rather than doing a real access. A peek still charges the same miss latency an ordinary `Read` would
  (via the same MSHR/sector-miss accounting), so a USL's own completion timing matches a real access even though
  its cache-state side effects are deferred. The load's real access (an *exposure* if no older load/fence was in
  the ROB at its own Execute time, else a *validation*, per the paper's TSO rule) is deferred to the load's own
  visibility point and gates retirement only (`RobEntry.PendingUslAccess`) — the peeked value still reaches
  dependents immediately via the ordinary CDB broadcast once the peek resolves, which is the 2019 Corrigendum's
  critical fix (the original paper's text suggested delaying value visibility too, which the corrigendum
  retracts). `StepUslResolution` runs each cycle between `StepComplete` and `StepCommit`, firing the deferred
  access once `SpectreVisibilityTracker.IsSafe` reports the USL's own visibility point has cleared, and draining
  any resulting miss latency through its own per-entry countdown (`_pendingUslLatency`) — the same
  MLP-overlapped shape ordinary load misses get from `StepExecute`'s `_inFlight`, instead of the cache's lump-sum
  stall accumulator store-commit misses use, so multiple USLs resolving in the same cycle overlap their miss
  latencies instead of charging them additively; a squash or flush this same cycle discards any still-pending USL
  instead of letting it fire. Counted by the `invisispec_exposures`/`invisispec_validations` dials. A wrong-path
  (squashed) load never installs anything in the real cache under InvisiSpec, unlike this simulator's undefended
  baseline — proven directly by a test comparing the same program on/off, not merely asserted.
  A USL that misses pays its miss latency **twice** by default — once at the speculative peek, once again at the
  deferred validation/exposure access, since the peek never installs anything for the later access to hit. This
  is not a bug: Yan et al. state (§VI-C) that their optional Per-Core LLC-SB extension exists specifically "to
  avoid a second access to main memory," confirming the paper's own *base* design (without that extension) pays
  main memory twice per non-forwarded USL — the dual-access cost this section's own name refers to.
  The optional **Per-Core Speculative Buffer in the LLC** (§VI-C) closes that gap: `enableInvisiSpecLlcSb: true`
  (default off, so the base-design cost model above stays the selectable default) records the line each USL's
  peek touched in a small per-core buffer (`llcSbCapacity`, default 16, FIFO eviction); if that USL's own
  deferred access — or a different USL's — later lands on a line still resident there, the access is charged a
  cheap buffer-hit latency (`llcSbHitLatency`, default 1) instead of paying the full miss again. This is a
  cost-only model: the real access always still fires for real (hit/miss stats and line installation stay
  correct; only the *charged stall* is capped on a hit), so the paper's §VII rule against ever serving a squashed
  USL's buffered *data* to a later, unrelated request doesn't apply — this buffer never serves data, only shapes
  timing. Squash cleanup (`StepFlush`/`StepPartialSquash`) discards entries for instructions that never reach
  their deferred access, matching a real squash cancelling the outstanding speculative fetch. Counted by
  `invisispec_llc_sb_hits`. No coherence/multi-hart squash plumbing (`OooTrain` has no coherence-invalidation-
  triggered load-squash hook today).

  `BdiCache` now has a `PeekRead` override (non-mutating: a hit reads the resident compressed block with no
  LRU/segment update, a miss recurses to backing rather than decompressing/filling), and
  `SetAssociativeCache.PeekRead` correctly checks sector residency (`_sectorValid`) before returning resident-line
  bytes, falling through to backing for an unfetched sector — the sectored-cache gap was a real bug (peeking an
  unfetched sector previously returned stale/zero bytes instead of the backing value), not merely a documented
  limitation, fixed and covered by discriminating tests in `BdiCacheTests`/`CacheTests`. Neither path is exercised
  by a wired config combining InvisiSpec with BΔI or sectored caches today, so this closes latent correctness gaps
  rather than validating an integrated configuration.

