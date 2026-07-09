# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

1D and multi-dimensional streams.

- [x] 1D load/store stream setup: `ss.sta.ld.w` + `ss.end` / `ss.sta.st.w` + `ss.end`
- [x] Multi-dim stream config sequence: `ss.sta.{ld,st}.w` → `ss.app` → `ss.end`
- [ ] `ss.cfg.vec` instruction decode (full vector-delivery effect tracked separately below)
- [ ] `ss.cfg.vec` effect: vector-width element delivery from load streams.
- [x] Scalar broadcast to u-reg: `so.v.dp.w`
- [x] FP arithmetic on stream elements: add/sub/mul/div/mac (`so.a.fp.*`)
- [x] Stream loop-control branches: `sb.nc` / `sb.ndc.(dim)`
- [x] Branch-if-complete variants: `sb.c` / `sb.dc.(dim)` — opposite polarity of the existing nc/ndc branches
- [x] Integer (USG/SG) arithmetic variants of add/sub/mul/div/mac on stream elements
- [x] Additional arithmetic ops: `abs`, element-wise `min`/`max`, `inc`/`dec`; `sqrt` (FP)
- [x] Logic ops: `and`, `or`, `xor`, `not`, `nand`, `nor`; vector-vector shifts `sll`/`srl`/`sra` and scalar-register
  forms `ssll`/`ssrl`/`ssra`
- [ ] Reduction ops: `adde`/`adde.acc` (element-sum → u-reg[0]), `sadde`/`fsadde` (→ integer/FP scalar reg), `mins`/
  `maxs`
- [x] Non-word element widths: byte (`.b`), halfword (`.h`), doubleword (`.d`) for `ss.ld` and `ss.st`
- [ ] Vector register manipulation: `mv`/`mvt` (move/transpose), `mvvs`/`mvsv.(width)` (vector↔scalar), `dp.(width)` (
  duplicate scalar to all elements)
- [ ] Explicit vector load/store: `ld.(width)` / `ld.(width).s` and `st` / `st.s` (non-stream bulk memory ops)
- [x] Static dimension modifiers: `ss.app.mod` — attach a `{Target, Behavior, Displacement, Size}` modifier to a
  descriptor so the inner loop count/stride updates automatically each outer-loop iteration (enables triangular patterns
  without per-row reconfiguration)
- [ ] Static modifier E-field (Size) enforcement: cap modifier applications to E total outer-loop iterations; currently
  the modifier fires unconditionally on every inner-dim wrap and the decoded `rs3Size` register value is ignored at
  execution time
- [x] Static modifier Offset and Stride targets: implement `Target=Offset` and `Target=Stride` mutations in
  `StreamingEngine.ApplyModifiers`; currently only `Target=Size` is handled and the other two are decoded but no-op
- [x] UVE encoding alignment: decoder, executor, and all tests now match AnaBSF/riscv-isa-sim (uve branch, commit
  a048271) — `ss.sta.{ld,st}.w` funct3, `ss.app`/`ss.end` funct2, `ss.app.mod` Spike target encoding, `so.v.dp.w`
  funct7/funct3, `so.a.fp.*` (funct7>>3, funct3) table, UVE B-type branch immediate layout
- [x] Offset register (rs1) in `ss.app`/`ss.end`: rs1*ew added to stream base address; accumulated across all ss.app instructions in a config sequence and applied at ss.end time
- [ ] Indirect dimension modifiers: `ss.app.ind` / `ss.end.ind` — attach a `{Target, Behavior, StreamPointer}` modifier
  so a live stream drives the offset of another (gather/indirect access; confirmed UVE1 in ISCA 2021 paper)
- [ ] Stream suspend/resume/stop: `ss.suspend`, `ss.resume`, `ss.stop` — explicit stream lifecycle control for context
  switching and early termination
- [ ] Vector-length control: `ss.getvl` / `ss.setvl` — read and configure the active vector length for narrower-VL
  emulation and VL-aligned dimension padding
- [ ] Cache-level stream routing: `so.cfg.memx` — direct a stream to operate from L x rather than the default L2
- [ ] FP register source for scalar broadcast: `so.v.dup.fp.w ud, fs1` — the paper's SAXPY uses an FP register (fa0) not
  an integer register; `so.v.dp.w` reads from integer rs1 only

## RISC-V

- [ ] RV64 completion:
  - [ ] ELF64 loader for running RV64 binaries.
  - [ ] Sv39 page-table walker for RV64 virtual memory.
  - [ ] RV64 M extension.
  - [ ] RV64 F/D extension.
- [ ] Zfh / Zfhmin: half-precision FP.
- [ ] Zcmop: compressed may-be-operations.
- [ ] Zabha+Zacas narrower variants: amocas.b / amocas.h; amocas.d for RV32.

## Analysis

- [ ] gem5 ROI instrumentation for treesum: wire `setStats(1)`/`setStats(0)` markers into the gem5 SE
  simulation so gem5 measures the same kernel interval as Horologium's `SetStatsObserver` and the IPC
  comparison is apples-to-apples.
- [ ] treesum bypass=0 D-cache regression: measure wrong-path load counts to confirm that deeper
  wrong-path execution under 0-cycle forwarding is the source of the +138 D-cache misses and +1 479
  dispatch stall cycles observed when switching from bypass=1 to bypass=0.
- [ ] JSON-format limitations: no PC/opcode, FP register numbering, vector/UVE ops.

## Co-simulation

- [ ] Watchdog on `ReadLine` to fail cleanly on over-run instead of hanging.
- [ ] CI workflow with `HOROLOGIUM_REQUIRE_COSIM=1`.

## Out-of-Order Execution

- [ ] Store sets for memory dependence prediction: predict which loads depend on which stores to avoid unnecessary
  stalls. — Chrysos & Emer, ISCA 1998

## Cache Model Realism

- [ ] Write-back buffer (eviction buffer): dirty victims drain to the next level asynchronously from a small
  (4–8 entry) buffer instead of charging the full miss latency synchronously at eviction; stall only when the buffer
  is full or a demand miss targets a line still queued in it. Write-through counterpart: a coalescing write buffer so
  stores don't pay backing latency individually.
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

### Parallelism

- [ ] Deterministic parallel multi-hart tick: BSP-style barrier synchronization deferring every cross-hart-visible
  coherence action (snoop state transitions included) into per-hart queues drained in fixed hart order — a
  run-to-run-reproducible replacement for the racy two-phase concurrent mode.
- [ ] Parallelize the benchmark test suite across per-binary test collections; each run is fully independent.
- [ ] Background checkpoint and report serialization: write checkpoint files and result tables on a worker thread
  after a synchronous state copy.

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] L2 and L3 caches.
- [ ] Cache and virtual addressing visualization.
- [ ] Waveform/signal viewer: plot pipeline signals (IPC, cache hit rate, branch mispredictions) over simulation time.
- [ ] Vector operation visualization.
  - Gotta think of how this should be done.
- [ ] Cache management policy selection.
  - More cache settings like Ripes.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
