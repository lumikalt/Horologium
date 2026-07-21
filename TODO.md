# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Cache Prefetching

- [x] STeMS: spatio-temporal memory streaming extending SMS with temporal miss-sequence recording. —
  Somogyi et al., ISCA 2009

## Analysis

- [x] Simulation state checkpoint/restore, Option B: warm microarchitectural checkpoint (caches, TLBs,
  branch predictor, RAS) layered on `ArchitecturalCheckpoint`, valid only at a drained pipeline boundary
  (`OooeTrain.Drain`) — mirroring gem5's `drain()`-before-`serialize()` precedent. `OooeTrain` only.
- [x] Extend the Option B microarchitectural checkpoint to `StoreSetPredictor`/`SmbPredictor`/
  `RdipPrefetcher`/`TokenPassingCriticalityPredictor` (one `ICriticalityPredictor` implementation)/`LvpVp`
  (one `IValuePredictor` implementation). `FdipPrefetcher` deliberately excluded — no trained table, only
  a cheap-to-rebuild lookahead FTQ. Key design rule discovered along the way: serialize only PC/address/
  content-keyed tables; skip anything keyed by or compared against a monotonic per-train counter
  (InstrId/SeqNo/commit count) — those reset to 0 on a freshly restored train, so a carried-over counter
  value can never match again (`StoreSetPredictor`'s LFST, `TokenPassingCriticalityPredictor`'s token/
  slot/commit-count state).
- [x] Extend further: the remaining `IValuePredictor` implementations (`VtageVp`, `StrideVp`, `HybridVp`,
  `DynamicClassificationVp`) and seven more `IReplacementPolicy` implementations (`FifoPolicy`,
  `MruPolicy`, `ClockPolicy`, `PlruPolicy`, `SrripPolicy`/`BrripPolicy`/`DrripPolicy`, `ShipPolicy`,
  `HawkeyePolicy`) — `RandomPolicy`/`RtlFfiReplacementPolicy` deliberately excluded (RNG-only state,
  FFI-owned native state). Found one genuine trained-state case that looked transient at first glance
  but wasn't: `DynamicClassificationVp`'s `_armed`/`_missStreak` are never cleared by `Update` (only by
  eviction/reclassification/squash), unlike a real in-flight counter — serialized, not skipped. Verified
  by direct instrumentation (not just doc-comment assertion) that `StrideVp`'s `_inFlight` genuinely is
  zero at its equivalence test's drain point, same method used for `StoreSetPredictor`'s LFST.
- [x] Wire the Option B microarchitectural checkpoint into the Runner CLI: `--checkpoint-save-micro`/
  `--checkpoint-load-micro`, mirroring the existing architectural-only `--checkpoint-save`/
  `--checkpoint-load`. Requires `--script` and an OoOE pipeline train (graceful warning + fall back to
  architectural-only restore otherwise); save drains at the simplest trigger, the end of the run — the
  CLI has no way to name a mid-run boundary. Incidentally fixed a pre-existing, unrelated bug found while
  smoke-testing this: `ScriptHost`/`FSharpScriptHost` pre-imported the stale namespace
  `Mechanism.BranchPredictModels` (renamed to `Mechanism.BranchPred` at some point), which broke every
  `--script` invocation.
- [x] The BP zoo, representative-per-family scope (user chose this over exhaustive or stopping): `LTageBp`
  (TAGE lineage — also the base of `TageScLBp`/`BullseyeBp`/`MultiperspectiveBp`/`BatageBp` via
  inheritance, so they inherit the base TAGE/loop/history round trip; their own additional layered
  tables still cold-start), `HashedPerceptronBp` (perceptron family), `TournamentBp` (local/global/chooser
  hybrid family). `SpeculativeGlobalHistory`/`SpeculativeLocalHistory` (the shared speculative/committed
  history-shadow helpers used by most predictors in this file) gained `WriteState`/`ReadState` once,
  benefiting every predictor built on them, not just these three. Only `LTageBp` got a full pipeline
  equivalence test (an alternating-parity branch pattern a bimodal counter can't learn but a
  history-indexed predictor can) — the one case that actually exercises history serialization through a
  live pipeline, since every prior equivalence test used history-free `NBitBp`/`AlwaysNotTaken`;
  `HashedPerceptronBp`/`TournamentBp` got history-populated unit-level round trips only, per the
  deliberately capped scope.
- [x] Extend BP zoo coverage to the rest of the zoo (user follow-up: "do them now since it's relevant" —
  moved from representative-per-family to exhaustive). Every `TageScLBp`-lineage subclass's own layered
  tables: `TageScLBp` (Statistical Corrector), `BatageBp` (bias table), `BullseyeBp` (HIT/H2P
  perceptrons), `MultiperspectivePerceptronBp` (five hashed-history feature tables), `LlbpBp`/`LlbpXBp`
  (RCR + context-keyed pattern storage + CTT), `TeaBp`/`LvcpBp`/`RunltsBp` (correlation tables + register/
  load-value tracking), `VlaTageBp` (Vector Loop Table). Every remaining standalone predictor:
  `PerceptronBp`, `CorrelatedBp`/`GselectPredictor`/`GshareBp`, `IttagePredictor` (ITTAGE — confirmed
  wireable as a standalone `IBranchPredictor` before including it), `ImliPredictor` (genuine
  speculative/committed counter semantics — got its own pipeline equivalence test, reusing the
  alternating-parity program, since it's the one mechanism `LTageBp`'s equivalence test doesn't
  structurally cover), `BranchNetBp` (composes a `TageScLBp` field rather than extending it — its own
  offline-trained per-branch CNNs are frozen shape, not state, same category as `TeaBp`'s `_chains`),
  `HypreBp` (hyperdimensional/SDM — a genuinely different HD-vector data structure; its round-trip test
  needed far more training reps than every other predictor to move its Hamming-distance threshold, a real
  anti-theater case caught by running the test rather than assumed). `OracleBp` (trace-driven)/`StaticBp`
  (stateless)/`CbpFfiBp`/`CbpNgFfiBp`/`CbpNgCommitDrivenBp` (FFI-owned native state) remain excluded on
  principled grounds. The BP zoo — and with it, Option B's predictor/policy coverage — is now complete.

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

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler, so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [x] Vector operation visualization.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
- [ ] More intuitive ways to visualize prefetching, cache policies, branch prediction, etc.
