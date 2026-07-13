# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Performance

- [ ] Memoization of instructions and decodings?
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [x] CoreMark port (EEMBC, freely licensed): bare-metal `core_portme.c` against HTIF console + mcycle
  timer; the standard embedded core benchmark, an order of magnitude more work than rsort at the
  committed iteration count (compile-time scalable beyond).
- [x] Embench-IoT port (Patterson et al.): the modern academic replacement for Dhrystone; designed for
  bare-metal RISC-V, fits the existing crt0/HTIF pattern.
- [x] HTIF syscall proxy (fesvr magic-mem protocol): decode the 8-word syscall struct at tohost and
  service open/read/write/fstat/exit against the host FS, so newlib-linked binaries with file I/O run
  bare-metal (current HtifMemory only auto-ACKs).
- [x] Linux syscall-emulation mode (gem5 SE-style): ECALL shim implementing the ~40 syscalls needed by
  statically linked musl binaries — unlocks MiBench and arbitrary self-built C programs.
- [ ] SPEC CPU2006/2017 harness (user-supplied install; SPEC is licensed and non-redistributable):
  RV64 + syscall emulation + SimPoint sampling — BBV profiling, clustering, checkpointed 10M-instruction
  intervals with warmup. — Sherwood et al., ASPLOS 2002 (SimPoint)

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [x] Waveform/signal viewer: plot pipeline signals (IPC, cache hit rate, branch mispredictions) over simulation time.
- [ ] Vector operation visualization.
  - Gotta think of how this should be done.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
