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
- [ ] STT: implicit-channel protection (prediction-based and resolution-based leaks via branches/store-forwarding/
  value prediction, "implicit branches") — the full DelayExecute+STT variant, not just ExpOnly.
- [ ] STT/InvisiSpec: Futuristic-model visibility point (ROB head / "preceded only by non-squashable instructions")
  as a selectable alternative to the Spectre model — needs tracking unresolved loads and traps as additional
  squash sources, not just branches.
- [x] InvisiSpec: invisible speculative loads via a non-mutating cache peek (`IMemory.PeekRead`, default = same
  as `Read`, overridden by `SetAssociativeCache`) plus a real expose/validate transaction deferred to the shared
  Spectre-model visibility point, gating retirement only (`RobEntry.PendingUslAccess`) — a USL's data still
  reaches dependents immediately via the ordinary CDB broadcast, per the 2019 Corrigendum. Measured via
  `invisispec_exposures`/`invisispec_validations` dials; no coherence/multi-hart squash plumbing (out of scope;
  `OooTrain` has no coherence-invalidation-triggered load-squash hook today). — Yan et al., MICRO 2018 (+ 2019
  Corrigendum)
- [ ] InvisiSpec follow-up: `StepUslResolution`'s deferred real access is charged through the cache's lump-sum
  stall accumulator (the same path store-commit misses use), not a per-load in-flight countdown — so multiple
  USLs resolving in the same cycle have their miss latencies charged additively instead of MLP-overlapped like
  ordinary baseline loads. This overstates InvisiSpec's measured IPC cost vs. the paper's real dual-access
  overhead; route the deferred access through `StepExecute`'s in-flight countdown instead for a calibrated
  number.
- [ ] InvisiSpec follow-up: `BdiCache` has no `PeekRead` override, so it falls through to `IMemory`'s default
  (`=> Read(...)`), which mutates BΔI's compressed-cache segment/eviction state on a USL peek — defeats the
  non-mutation guarantee if InvisiSpec is ever combined with `L2Compression: CompressionKind.Bdi` on the same
  level.
- [ ] InvisiSpec follow-up: `SetAssociativeCache.PeekRead` skips `EnsureSectorResident`, so on a sectored cache a
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
