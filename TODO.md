# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Performance

- [x] Value prediction: predict ALU/load results to break dependence chains; commit only if the prediction is correct. —
  Lipasti & Shen, MICRO 1996 (LVPT); Perais & Seznec, HPCA 2014 (VTAGE + FPC confidence)
- [x] EOLE: late in-order ALU execution atop value prediction, to shrink the OoO issue width without losing
  performance. — Perais & Seznec, ISCA 2014 (EOLE)
- [x] Early Execution: the front-end half of EOLE — single-cycle ALU ops with immediate/predicted operands
  execute in-order, in parallel with Rename, bypassing the OoO scheduler entirely. — Perais & Seznec, ISCA 2014
  (EOLE)
- [x] TMA/CPI-stack accounting for EOLE Early/Late Execution: reconcile Top-Down slot/cycle accounting with
  instructions that bypass `StepIssue`/`StepExecute` entirely. — Yasin, ISPASS 2014 (TMA)
- [x] Widen value-prediction eligibility beyond scalar ALU/load to MulDiv, floating point (pipelined and
  div/sqrt), and CSR reads (System). — Lipasti & Shen, MICRO 1996; Perais & Seznec, HPCA 2014
- [x] Add a computational (stride-family) predictor component to hybridize with VTAGE. — Perais & Seznec,
  HPCA 2014
- [x] Dynamic component selection for hybrid value predictors: assign each PC to at most one component
  (context-based or computational) instead of always querying both, for space efficiency. — Rychlik et al.,
  CMuART-1998-01
- [ ] Tighten the stride predictor's in-flight speculative-depth tracking to hold through warmup and
  post-squash recovery, closing the residual undercount measured in each of those windows.
- [ ] Evict-on-consecutive-misprediction threshold for dynamic component selection, closer to Rychlik et
  al.'s confidence-zero trigger than the current evict-on-first-non-confident-prediction rule.

## Cache Prefetching

- [x] Best-Offset Prefetcher (BOP): offset-selection tournament with timeliness scoring; the DPC-2 winner. — Michaud,
  HPCA 2016
- [x] Signature Path Prefetcher (SPP): compressed access-pattern signatures with path-confidence lookahead. — Kim et
  al., MICRO 2016
- [x] Perceptron Prefetch Filter (PPF): a learned filter on top of SPP that suppresses low-value prefetches by
  perceptron vote over signature/confidence/delta features. — Bhatia et al., ISCA 2019

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [ ] SPEC CPU2006/2017 harness (user-supplied install; SPEC is licensed and non-redistributable):
  RV64 + syscall emulation + SimPoint sampling — BBV profiling, clustering, checkpointed 10M-instruction
  intervals with warmup. — Sherwood et al., ASPLOS 2002 (SimPoint). All infrastructure below is done,
  and the toolchain gap that blocked end-to-end validation is now closed
  (`riscv64-unknown-linux-musl-gcc` is in `flake.nix`): a real, genuinely compiled and statically-linked
  musl RV64 binary now runs correctly end-to-end through `--bench-config` (argv, `printf`-based stdio,
  clean exit — see the `SYS_writev` sub-bullet below). The parent item still stays unchecked, though:
  what's validated so far is only the trivial entry/syscall/stdio path on a hello-world binary — the
  actual substance of this item (BBV profiling, clustering, checkpointed intervals with warmup) has
  never been run against a real compiled binary, only bare-metal HTIF probes. See the open sub-bullet
  below for the next real validation step.
  - [x] RV64 ECALL/syscall-handler wiring (`Rv64Mechanism`) and `Rv64ElfWorkload.InitialBreak`.
  - [x] ISA-agnostic psABI initial-stack builder (argc/argv/envp/auxv) so a real compiled `_start`
    can run, not just bare-metal entry — `InitialStackBuilder`.
  - [x] `LinuxSyscallEmulator` realism: real host file I/O (openat/read/write/close/lseek/fstat),
    an anonymous-mmap bump allocator, deterministic clock_gettime/getrandom, and fcntl — works on
    both RV32 and RV64 (`wordSize` selects the `fstat` struct layout, verified against real
    toolchain codegen). Not yet validated against a real linked glibc/musl binary.
  - [x] Instruction-count-triggered checkpoint hook (`InstructionCounter`) and an
    instruction-count-bounded warmup-then-measure driver for the detailed pipeline
    (`WarmupMeasureDriver`, `ISteppableTrain.SnapshotDials`/baseline-`FinishStepping`) — verified
    independent of clustering (checkpoint-at-K-then-run-N−K matches straight-through-to-N on OoO
    bit-for-bit). Found and fixed a real bug along the way: `ArchitecturalCheckpoint.RestoreInto`
    was silently invisible to `OooeTrain`'s physical register file.
  - [x] SimPoint-interval → checkpoint glue (`Experiment.RunWithSimPointCheckpoints`): profiles,
    saves one checkpoint per simulation point in a single functional pass (per-point warmup
    clamped to the interval's own start), restores each into a fresh detailed train, and
    weight-combines per-point CPI (not IPC — intervals are equal length, so CPI is the correct
    domain) into a whole-program estimate. Wired into the Runner CLI as `--simpoint-warmup <n>`,
    run once per `--sweep` config (ooo/five_stage/single_cycle only). Found and fixed a second
    tick-accounting bug along the way: `Train.FinishStepping(baseline)` returned the absolute
    Escapement tick instead of ticks-since-baseline, which only mattered once warmup > 0.
  - [ ] Reuse one profiling + checkpoint-capture pass across the whole `--sweep`, instead of
    `RunWithSimPointCheckpoints` repeating both per config — matters at SPEC-scale interval
    counts, not at today's small test/demo sizes.
  - [x] Runner CLI: `--xlen 32|64` selects RV32/RV64 workload + mechanism across every mode
    (default sweep, `--simpoint`, `--trace-json`, `--elastic-record`, `--stf-record`, `--script`
    incl. `--roi-start`/`--checkpoint-save`/`--checkpoint-load`); `.csx`/`.fsx` scripts can
    construct `Rv64Mechanism` directly (RiscV64 pre-imported like RiscV32).
  - [x] Runner benchmark-config/batch-mode concept (`BenchmarkConfig`/`Experiment.RunBenchmark`,
    `--bench-config <path.json>`): ELF + argv (via `InitialStackBuilder`, wired into production for
    the first time) + stdin redirection (new — `LinuxSyscallEmulator` previously stubbed fd 0 as
    always-EOF) + captured stdout/stderr + exact reference-output diffing, functional single-cycle
    only. Verified end-to-end against bare-metal SE-mode probes only (`abi_probe64.elf`,
    `stdin_echo64.elf`) — no real linked-libc/SPEC binary has run through it (see parent item).
  - [x] `BenchmarkConfig.MmapArenaBytes` wires a caller-sized mmap arena through to
    `LinuxSyscallEmulator` (appended past the workload's own memory, so stack/`brk` placement is
    unchanged whether or not it's set), and a crashing benchmark (e.g. one that dereferences a
    failed mmap's negative return) now reports as `ERROR` and lets the rest of the `--bench-config`
    batch continue instead of aborting it. Residual limits, not chased further: the arena never
    reclaims (`SYS_munmap` is a no-op) so a long malloc-heavy run still exhausts a finite arena and
    ENOMEMs mid-run — arena sizing is a per-benchmark tuning knob, not a solved problem — and
    `FlatMemory` is `int`-sized, so a real multi-GB SPEC heap is out of reach regardless of arena
    config.
  - [x] `riscv64-unknown-linux-musl-gcc` added to `flake.nix`'s dev shell — a real, statically-linking
    libc toolchain (glibc's `pkgsCross.riscv64` cross toolchain, already used in-tree for
    OpenSBI/the Linux kernel, fails `-static` linking here without extra plumbing; musl doesn't).
    First real test: a hand-written `hello.c` compiled `-static` and run through `--bench-config`
    halts cleanly (`SYS_exit`), and a raw `write(1, ...)` syscall (bypassing stdio) is captured
    correctly — but `printf`, even followed by an explicit `fflush(stdout)`, produced no captured
    output at all (root-caused and fixed below).
  - [x] Root-caused musl's buffered-stdio silent-output bug: `printf`/`fwrite`'s flush path calls
    `SYS_writev` (66), not plain `write` (64) — unimplemented in `LinuxSyscallEmulator`, it fell
    through to the `ENOSYS` default, which musl's stdio layer swallows as a write error instead of
    surfacing it, so the symptom was "no output" rather than a crash. Found via a temporary
    syscall-tracing wrapper (`ISyscallHandler` decorator logging every ECALL's number/args),
    deleted once syscall 66 was identified. Fixed with `LinuxSyscallEmulator.Writev`: walks the
    `iovec` array (2 `wordSize`-wide fields/entry) and delegates each entry to the existing
    `Write`, skipping zero-length entries and stopping early on a short write (matching real
    `writev` semantics). Covered by 3 unit tests (`SyscallRealismTests.Writev_*`: concatenation
    order, zero-length-iovec skip, real-file fd) and a new end-to-end fixture —
    `TestBinaries/hello64_musl.c`/`.elf`, a genuinely compiled and statically-linked (not
    hand-assembled) binary, exercised by `Tests/RiscV64/System/RealLinkedBinaryTests` — the first
    real linked binary to run correctly through this pipeline's entry/syscall/stdio path
    end-to-end. Verified by revert-and-recheck: all 4 new tests fail with `ENOSYS`/empty output
    without the fix. This validates only the trivial entry+syscall+stdio path (argv, a handful of
    syscalls, one `printf`, clean exit) — not the SimPoint/checkpoint sampling pipeline itself,
    which has still never seen a real binary (see the sub-bullet below).
  - [ ] Run a non-trivial real musl binary (real file I/O, a loop, `malloc` under actual pressure —
    not just hello-world) through `--simpoint-warmup`, not merely `--bench-config`. This is the
    first time the profile → cluster → checkpoint → measure path would touch genuinely compiled
    code rather than a bare-metal HTIF probe or a single straight-through printf, and it's the most
    likely place for the next silently-swallowed syscall gap (wider syscall surface, `SYS_munmap`
    still a no-op against a heap under real churn) to surface.
  - [ ] Reference-output comparison in `BenchmarkResult.Passed` is byte-exact, including trailing
    newline — real reference files almost always end in `\n`. Consider a
    trailing-whitespace-normalized compare mode.

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler, so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [ ] Vector operation visualization.
  - Need to think of how this should be done.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
