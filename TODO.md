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
- [ ] Extend the Option B microarchitectural checkpoint to `StoreSetPredictor`/`IValuePredictor`/
  `ICriticalityPredictor`/`SmbPredictor`/`FdipPrefetcher`/`RdipPrefetcher` (currently cold-start on
  restore via the default no-op `WriteState`/`ReadState`), and to more `IBranchPredictor`/
  `IReplacementPolicy` implementations beyond `NBitBp`/`LruPolicy`.
- [ ] Wire the Option B microarchitectural checkpoint into the Runner CLI (a `--checkpoint-micro`-style
  flag alongside the existing architectural-only `--checkpoint-save`/`--checkpoint-load`).

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
