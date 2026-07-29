# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.
- [ ] `vec_cv` (661 lines, `so.v.cv` conversions) has an **empty `RUN_SIMPLE`** (`void core(DataType
  src[SIZE]){}`) — there is no independent oracle to verify against at all. Matches the already-recorded
  `SPEC_NOTES.md` finding that `so.v.cv` correctness was "genuinely underspecified, never given
  attention" by the author, who also confirmed (2026-07-27) there's no forward plan for it — UVE compute
  instructions are being phased out in favor of RISC-V V's. He's fine with implementing it anyway "to
  validate", but that isn't a reference to check against, so the no-oracle hold stands.

## µops

- [ ] µop cache on `FiveStageTrain`, exploiting its real Fetch/Decode stage split (`Stages/Fetch.cs`
  → `Stages/Decode.cs`, a cycle apart) to skip Decode's stage-cycle on a hit — the one train where
  this could be a genuine, paper-faithful timing win (mirrors the paper's own Figure 1: stream-mode
  has fewer pipe stages than build-mode) rather than the ~0-cycle-impact story everywhere else.
  **Blocked for now**: user wants to confirm first whether the actually-intended follow-up is
  something bigger that also touches `OooTrain`, before scoping this.
- [x] TMA slot accounting didn't account for macro-fusion, on any of the three trains that support
  it: `SlotsIssued`/`TotalSlots` correctly counted a fused pair as 1 slot, but `slotsRetired` was
  aliased to the architectural `"retired"` counter (2 per fused pair, via `ArchInstructionCount`),
  so a fusion-heavy program made `SlotsRetired` exceed `SlotsIssued` — inflating Retiring and
  clamping Bad Speculation to zero. Fixed by adding a dedicated `td_slots_retired` counter
  (`TopDownBreakdown.SlotsRetiredCounter`/`TopDownCounters.SlotsRetired`), incremented once per
  retiring ROB/checkpoint/issue-group entry regardless of `ArchInstructionCount`, on all five
  TMA-registering trains (`SuperscalarTrain`, `OooTrain`, `CprTrain`, `SmtTrain`, `DaeTrain` — the
  last two have no fusion, so it's a 1:1 mirror of their existing `"retired"` increments).
- [x] `OooTrain`/`CprTrain` macro-fusion left gaps documented but not closed (all opt-in + opt-in,
  no correctness impact on default-off runs): (1) on both trains, `PEventLog` only ever recorded
  the primary (compare) InstrId post-Rename for a fused pair — the branch's own InstrId got a
  Fetch event and then nothing, a dangling row in the waterfall visualization. (2) On `OooTrain`
  only, the Olympia co-sim `_commitObserver`/`Rdip` hooks reported only the compare's
  `(Pc, RawEncoding)` for a fused commit, standing in for two real instructions — a trace-replay
  divergence source if fusion and co-sim are both enabled. (3) On `OooTrain` only,
  `TrainCriticality`'s critical-path source-InstrId arithmetic (`head.InstrId - 1`,
  `head.InstrId - w`) assumed InstrId contiguity, which a fused pair breaks (the branch's InstrId
  is skipped) — mis-attributed CP edges if criticality prediction and fusion were both enabled.
  Fixed by adding `FusedSecondInstrId` (the branch's own InstrId, set at Rename) to `RobEntry`/
  `CheckpointEntry`/`RenameEntry`, threaded through Dispatch to Retire/Flush: (1) mirrors every
  Retire/Flush `PEventLog` event for the branch's InstrId on both trains; (2) fires a second
  `_commitObserver`/`Rdip` call with the branch's real `(Pc, RawEncoding)` (via
  `ITooth.BranchComponent`), compare-then-branch order; (3) trains the criticality predictor a
  second time for the branch's InstrId with the same D/E/C sources as the primary (exact, not
  approximate — the fused pair shares one Dispatch/Issue/Execute/Commit timing throughout).
  `CprTrain` still has no co-sim/criticality-prediction integration, so only (1) applies there.

## Cache Prefetching

- [x] MLOP (multi-lookahead offset prefetcher): BOP generalized to score offsets at multiple lookahead depths; DPC-3
  winner. — Shakerinava et al., DPC-3 2019

## Memory System

- [x] Cache compression: base-delta-immediate (BΔI) compressed caches with variable effective capacity. — Pekhimenko
  et al., PACT 2012
- [ ] BΔI: extend compression selection to the `CacheLevelSpec`/`CachePathSpec`-driven `MemoryLayers.Build` overload
  and expose it in the Face cache-config UI — only the flat `MemoryConfig`/`TrainConfig` path supports it today.
- [ ] BΔI: a compressed level (`BdiCache`) doesn't yet participate in `OooeTrain`'s microarchitectural checkpoint
  (cold-starts on restore) or the `SingleCycleTrain`/`FiveStageTrain`/`CprTrain` PEventLog L2/L3 hit/miss counters.

## Security

- [x] Transient-execution defense modeling, first slice: STT-ExpOnly (explicit-channel-only — loads are the only
  transmitter class, no implicit-branch/prediction-based protection) on `OooTrain`, gated by a shared
  Spectre-model visibility-point tracker (all older branches resolved). A load whose address traces a taint
  root to another not-yet-visible load is held at Issue; measured via the `stt_load_issue_stalls` dial and a
  direct on/off cycle-count comparison. — Yu et al., MICRO 2019
- [x] STT: explicit-branch resolution-based implicit channel (Yu et al., MICRO 2019, §6.4.1) on `OooTrain`, gated
  by `enableSttImplicitBranches` — a mispredicted branch whose own resolution is still tainted (`RobEntry
  .SourceYrot` not yet safe on the shared `SpectreVisibilityTracker`) has its execute-time squash deferred
  (`_pendingTaintedMispredicts`/`StepSttMispredictResolution`) rather than armed immediately, so the squash's
  timing is no longer a function of tainted data; measured via `stt_mispredict_deferrals` and an on/off cycle
  comparison. Predictor training (`_predictor.Update`) and the value/bypass-mispredict squashes already fire
  only at commit — strictly later than any visibility point — so they were already safe by construction and
  untouched by this flag.
- [x] STT: prediction-based implicit channel through memory-dependence speculation (Yu et al., MICRO 2019,
  §6.4.2) on `OooTrain`, gated by `enableSttMemDepGating` — the paper: "the relevant predictor ... [must] be
  updated only by untainted data, i.e., only after the implicit branch predicate becomes untainted," which for
  memory-dependence speculation is a function of the *producing store's own address*, not the load's. The one
  genuinely open call site was `SmbPredictor.Train`'s cold-start/ongoing-seeding call in `StepComplete` (an
  ordinary forwarded load teaching the predictor a fresh SSN distance); it is now deferred
  (`_pendingSmbTraining`/`StepSmbTrainingResolution`) until the producing store's own `SourceYrot` is safe.
  Corrected mischaracterization from the previous entry: `StoreSetPredictor` has no `Train` method at all — its
  only persistent-state writer, `RecordViolation`, already fires exclusively at commit (already safe by
  construction, same as the branch predictor); `OnStoreDispatch`/`OnLoadDispatch`/`OnStoreIssued` are ephemeral
  per-SSID LFST scheduling state, not learned persistence — their channel is store-to-load-forwarding
  *resolution* timing, tracked below, not predictor training. The other three `SmbPredictor.Train`/
  `TrainNoBypass` calls all fire inside `StepCommit`, already commit-time-safe.
- [x] STT: the store-to-load-forwarding implicit branch's own resolution channel (§6.4.2/§6.5), verified already
  safe by construction (no new gating needed), same shape as the `StoreSetPredictor.Train` correction above.
  `CheckLoadViolations` (called the instant a store's address resolves) only sets `LqEntry.Violated`; the
  memory-order-violation squash itself fires exclusively in `StepCommit`'s ROB-head retire loop, unconditionally
  — commit-time-only by construction, not flag-dependently deferred (a `Debug.Assert` mirrors the one at the
  branch-mispredict site). `StoreSetPredictor.OnStoreIssued` only clears an internal LFST bookkeeping slot;
  `StoreSetStallLoad` (the actual issue-time release decision) reads `SqEntry.AddressKnown` directly, never
  `_lfst`, so `OnStoreIssued` has zero observable effect on pipeline timing. A third candidate — SMB (NoSQ)'s
  early bypass broadcast delivering a bypassed load's value ahead of its own visibility point — is not a hole:
  ExpOnly's threat model permits tainted data *propagation*, only gating *transmitters*, and the bypassed load's
  own shadow/verification execution (the real transmitter) still passes through `TryIssueSlot`'s unconditional
  STT gate. `Tests/RiscV32/Pipelines/SttStoreForwardTests.cs` demonstrates coexistence (both mechanisms fire in
  the same run without perturbing each other); the same-instance guarantee rests on the code inspection, not the
  dynamic test (confirmed: injecting the exact regression the test would need to catch still leaves it green).
- [x] STT: value-prediction training/squash gating, to round out the paper's complete DelayExecute+STT variant —
  also verified already safe by construction. `_valuePredictor.Update` and the value-misprediction squash both
  fire exclusively inside `StepCommit`, unconditionally, same as `_predictor.Update` and the memory-order
  violation above (two more `Debug.Assert`s added). Value prediction is eligible for `ToothClass.Load`, and a
  confident prediction writes the PRF at Rename — but EOLE Late Execution (the only mechanism skipping
  Issue/Execute) is restricted to `ToothClass.IntegerAlu`, so a value-predicted load always still executes for
  real through `TryIssueSlot`'s STT gate. `Tests/RiscV32/Pipelines/SttValuePredictionTests.cs` demonstrates
  coexistence with the same honest same-instance caveat as the item above.
- [x] STT/InvisiSpec: Futuristic-model visibility point (Yan et al., MICRO 2018, §V-A1, Table I; Yu et al., MICRO
  2019), selectable via `sttFuturisticModel` as an alternative to the Spectre model on the same shared tracker —
  every existing STT-ExpOnly/InvisiSpec/implicit-branch/memdep-gating call site already reads the tracker only
  through the new `IVisibilityTracker` interface, so switching models needed no change to any of them.
  `FuturisticVisibilityTracker` (`src/Core/Pipeline/Ooo/`) generalizes the Spectre tracker's branch-only FIFO to a
  per-instruction pending-source bitmask (`Trap`/`Branch`/`StoreAddr`/`Smb`/`Vp`), registered at Dispatch and
  cleared bit-by-bit as each source resolves with a "no squash" outcome — resolving early on a squash-bound
  outcome would be the security hole (a younger instruction wrongly marked safe before an older one's squash
  fires), so a bit only ever clears on the safe branch of its check, exactly like the Spectre tracker's own
  mispredicted-branch handling. Condition (i) (ROB head is unconditionally safe) is enforced by the caller —
  `OooTrain` force-resolves the current ROB head once before anything reads `IsSafe` each cycle, AND again inside
  `StepCommit`'s own retire loop for every entry examined there (the first call alone left a real gap: that loop
  can retire more than one entry per tick, and any entry only becoming head mid-tick, after an earlier retirement
  advanced it, never got force-resolved — a permanent phantom blocker for anything younger, caught by
  `SttFuturisticModelTests.DualSourceLoad...` hanging at `maxTicks` before the second call site was added). Table
  I's own "address alias between a load and an earlier store" falls out of the generalized FIFO for free — an
  older unresolved store blocks every younger instruction's safety structurally, with no separate per-load
  tracking needed. Multi-hart coherence-invalidation squashes and single-core load-load aliasing remain out of
  scope, matching every other InvisiSpec/STT slice (`OooTrain` has no coherence-invalidation-triggered squash hook
  today). `Tests/RiscV32/Pipelines/SttFuturisticModelTests.cs` proves the Futuristic model catches a squash source
  (a slow store) the Spectre model structurally cannot see in a branch-free program, and that a single load
  carrying two independent pending sources (SMB-bypass and value-prediction verification) doesn't deadlock or
  drop either.
- [x] InvisiSpec: invisible speculative loads via a non-mutating cache peek (`IMemory.PeekRead`, default = same
  as `Read`, overridden by `SetAssociativeCache`) plus a real expose/validate transaction deferred to the shared
  Spectre-model visibility point, gating retirement only (`RobEntry.PendingUslAccess`) — a USL's data still
  reaches dependents immediately via the ordinary CDB broadcast, per the 2019 Corrigendum. Measured via
  `invisispec_exposures`/`invisispec_validations` dials; no coherence/multi-hart squash plumbing (out of scope;
  `OooTrain` has no coherence-invalidation-triggered load-squash hook today). — Yan et al., MICRO 2018 (+ 2019
  Corrigendum)
- [x] InvisiSpec follow-up: `StepUslResolution`'s deferred real access now drains through its own per-entry
  countdown (`_pendingUslLatency`), the same MLP-overlapped shape ordinary load misses get from `StepExecute`'s
  `_inFlight`, instead of the cache's lump-sum stall accumulator store-commit misses use — multiple USLs
  resolving in the same cycle now overlap their miss latencies instead of charging them additively.
- [x] InvisiSpec follow-up: `IMemory.PeekRead` charged no miss latency at all (a cold miss just recursed to
  backing with no stall), so a USL's own completion and CDB broadcast happened at hit-latency regardless of
  whether the address was resident — on a dependent load chain this made InvisiSpec measure cheaper than
  baseline (confirmed: 28 cycles baseline vs. 18 on, a 2-hop pointer chase). Fixed: `SetAssociativeCache
  .PeekRead`/`BdiCache.PeekRead` now charge the same miss latency an ordinary `Read` would (`ChargeAndAllocateMshr`/
  `ChargeInFlightMshr`/`EnsureSectorResident`'s sector-miss cost, as applicable) without installing, evicting, or
  updating LRU/dirty/tag state. A missed USL now pays this latency twice — once at the speculative peek, once
  again at the deferred validation/exposure access, since the peek never installs anything for the later access
  to hit — which is not a bug but the paper's own base-design cost: Yan et al. (MICRO 2018, §VI-C) state the
  Per-Core LLC-SB extension exists specifically "to avoid a second access to main memory," confirming vanilla
  InvisiSpec (without that extension) pays main memory twice per non-forwarded USL. See the new TODO item below
  for that extension, which this simulator does not implement.
- [x] InvisiSpec follow-up: Per-Core Speculative Buffer in the LLC (Yan et al., MICRO 2018, §VI-C) — a small
  per-core buffer (`enableInvisiSpecLlcSb`, default off; capacity/hit-latency configurable) recording the line a
  USL's speculative peek touched; if that USL's own deferred access (or a different USL's) later lands on the
  same line while still resident, the access is charged a cheap buffer-hit latency instead of paying the base
  design's second full miss — closing the double-payment gap documented above. Cost-only model: the real access
  always still fires (hit/miss stats and line installation stay correct, only the charged stall is capped), so
  the paper's §VII rule against serving a squashed USL's buffered *data* to a later request doesn't apply here —
  this buffer never serves data, only influences timing. Squash cleanup still discards entries for instructions
  that never reach their deferred access, matching a real squash cancelling the outstanding fetch.
- [x] InvisiSpec follow-up: `BdiCache` has no `PeekRead` override, so it falls through to `IMemory`'s default
  (`=> Read(...)`), which mutates BΔI's compressed-cache segment/eviction state on a USL peek — defeats the
  non-mutation guarantee if InvisiSpec is ever combined with `L2Compression: CompressionKind.Bdi` on the same
  level.
- [x] InvisiSpec follow-up: `SetAssociativeCache.PeekRead` skips `EnsureSectorResident`, so on a sectored cache a
  peek can read a non-resident sector's stale bytes. Untriggered by today's tests (non-sectored configs only).

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [ ] SPEC CPU2006/2017 harness (user-supplied install; SPEC is licensed and non-redistributable):
  RV64 + syscall emulation + SimPoint sampling — BBV profiling, clustering, checkpointed 10M-instruction
  intervals with warmup. — Sherwood et al., ASPLOS 2002 (SimPoint). All supporting infrastructure is
  built and validated against real, genuinely compiled RV64 binaries: RV64 syscall wiring, a psABI
  initial-stack builder, `LinuxSyscallEmulator` (real file I/O/mmap/clock_gettime/getrandom/writev),
  checkpoint/SimPoint sampling glue (reused once per `--sweep` rather than per config, and covering full
  syscall-emulator state — brk/mmap cursors, fd table, stdin position — so a syscall inside a SimPoint
  interval's startup-/shutdown-edge phases measures correctly too), `--bench-config` batch mode, and
  `--simpoint-argv` CLI wiring for real ELFs. **Blocked on the SPEC license itself, not on remaining
  code**: missing a license currently.
