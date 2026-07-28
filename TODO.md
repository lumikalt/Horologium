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
- [ ] `OooTrain`/`CprTrain` macro-fusion leaves gaps documented but not closed (all opt-in +
  opt-in, no correctness impact on today's default-off runs): (1) on both trains, `PEventLog` only
  ever records the primary (compare) InstrId post-Rename for a fused pair — the branch's own
  InstrId gets a Fetch event and then nothing, a dangling row in the waterfall visualization
  (confirmed non-crashing: `PEventLog` has no completeness invariant). (2) On `OooTrain` only, the
  Olympia co-sim `_commitObserver`/`Rdip` hooks report only the compare's `(Pc, RawEncoding)` for a
  fused commit, standing in for two real instructions — a trace-replay divergence source if fusion
  and co-sim are ever both enabled together (`CprTrain` has no such hooks). (3) On `OooTrain` only,
  `TrainCriticality`'s critical-path source-InstrId arithmetic (`head.InstrId - 1`,
  `head.InstrId - w`) assumes InstrId contiguity, which a fused pair breaks (the branch's InstrId
  is skipped) — mis-attributes CP edges if criticality prediction and fusion are both enabled
  (`CprTrain` has no criticality-prediction integration).

## Cache Prefetching

- [ ] MLOP (multi-lookahead offset prefetcher): BOP generalized to score offsets at multiple lookahead depths; DPC-3
  winner. — Shakerinava et al., DPC-3 2019

## Memory System

- [ ] Cache compression: base-delta-immediate (BΔI) compressed caches with variable effective capacity. — Pekhimenko
  et al., PACT 2012

## Security

- [ ] Transient-execution defense modeling: invisible speculative loads (InvisiSpec) and speculative taint tracking
  (STT); measure the IPC cost of each defense on the OoO train. — Yan et al., MICRO 2018; Yu et al., MICRO 2019

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
