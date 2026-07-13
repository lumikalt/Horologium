# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Cache Model Realism

- [ ] Cache-level MSHRs with hit-under-miss: move outstanding-miss tracking from the pipeline into each cache level;
  a secondary miss to a line already in flight merges into the existing MSHR entry instead of paying a second full
  miss, and the cache continues serving hits while misses are outstanding. Makes L2/L3 non-blocking too. — Kroft,
  ISCA 1981
- [ ] Sequential tag/data access mode: gem5's third timing knob alongside tag/data latency — hit latency
  = tag + data (probe tags first, then read only the matching way) instead of max(tag, data); typical for large
  lower-level caches.
- [ ] Inclusion policy per level pair: inclusive (Intel-style — lower-level eviction back-invalidates the line in
  upper levels), exclusive (AMD-style — lower levels act as victim caches for the level above), or NINE
  (non-inclusive non-exclusive, the current behavior).
- [ ] Critical-word-first / early restart: a miss fill returns the demanded word first so the load resumes after the
  leading edge while the rest of the line streams in; matters when block size is large relative to miss latency.
- [ ] Banked caches and port limits: N banks with conflict stalls on same-bank concurrent accesses; configurable
  read/write port counts (the OoO train currently has unlimited D-cache bandwidth).
- [ ] Per-sector dirty/valid bits: sectored lines so writebacks transfer only dirty sectors and fills can be partial;
  bandwidth refinement over whole-line granularity.
- [ ] Zicbom write-back semantics: wire cbo.clean/cbo.flush/cbo.inval into dirty-line state now that write-back
  caches track it (clean = writeback and keep, flush = writeback and invalidate, inval = discard without writeback).
- [ ] Victim cache: small fully-associative buffer to absorb conflict misses. — Jouppi, ISCA 1990

## Performance

- [ ] Memoization of instructions, results, and branches.
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [ ] CoreMark port (EEMBC, freely licensed): bare-metal `core_portme.c` against HTIF console + mcycle
  timer; the standard embedded core benchmark, 2–3 orders of magnitude more work than rsort.
- [ ] Embench-IoT port (Patterson et al.): the modern academic replacement for Dhrystone; designed for
  bare-metal RISC-V, fits the existing crt0/HTIF pattern.
- [ ] HTIF syscall proxy (fesvr magic-mem protocol): decode the 8-word syscall struct at tohost and
  service open/read/write/fstat/exit against the host FS, so newlib-linked binaries with file I/O run
  bare-metal (current HtifMemory only auto-ACKs).
- [ ] Linux syscall-emulation mode (gem5 SE-style): ECALL shim implementing the ~40 syscalls needed by
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
