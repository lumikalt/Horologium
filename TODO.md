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
  clean exit — see the `SYS_writev` sub-bullet below), and the SimPoint/checkpoint sampling machinery
  itself has now been run against a real compiled binary too (see the `simpoint_kernel.elf` sub-bullet
  below) — including finding and fixing a real, independent `OooeTrain` crash along the way. The
  parent item still stays unchecked, though: that validation used a binary deliberately chosen so its
  measured windows are syscall-free (real emulator-state checkpointing — brk/mmap cursors, fd table,
  stdin position — is still unimplemented). It also surfaced two younger-instruction-vs-ECALL hazards
  under OoOe, both now fixed (see the two sub-bullets below). No SPEC/realistic binary with a
  non-trivial syscall footprint inside its hot path has run through this yet.
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
  - [x] Ran a non-trivial real musl binary (`TestBinaries/simpoint_kernel.c`/`.elf` — static arrays,
    no malloc, one `printf` at the very end) directly through the `Experiment.RunWithSimPointCheckpoints`
    API, the first time the profile → cluster → checkpoint → measure path touched genuinely compiled
    code. `ProfileSimPoints`/`RunWithSimPointCheckpoints` gained optional `argv`/`wordSize` parameters
    so the profiling and single-pass checkpoint-capture runs can inject a real psABI initial stack
    (`InitialStackBuilder`), not just bare-metal entry. Verified end to end in
    `Tests/RiscV64/System/RealLinkedSimPointTests.cs`: an independent functional pass records every
    ECALL's commit index, and every *selected* simulation point's warmup+measure window is asserted
    syscall-free except the two edge phases (program startup and shutdown, which always contain
    ECALLs by construction — no SimPoint parameter choice changes that). Confirms the reduced scope
    from the sub-bullet above: only those two edge phases would need full syscall-emulator-state
    checkpointing (brk/mmap cursors, fd table, stdin position) to measure correctly; every other
    point — the actual repeated compute-loop phase this sampling technique targets — measures
    correctly today. **Not done by this**: the `--simpoint-warmup` CLI flag itself
    (`Program.cs`) still isn't wired for real ELFs — it still builds a bare-metal HTIF mechanism
    with no argv/syscall handler, so `--simpoint-warmup real.elf` from the CLI does not work yet;
    only the underlying API was validated directly. See the new sub-bullet below.
  - [ ] Wire `argv` + a fresh-per-pass `LinuxSyscallEmulator` through the `--simpoint-warmup` CLI path
    in `Program.cs`, mirroring `--bench-config`'s `Func<ISyscallHandler, IMechanism>` mechanism-factory
    shape — the actual remaining step to run a real binary through SimPoint sampling from the CLI, not
    just through the `RunWithSimPointCheckpoints` API directly (see the sub-bullet above).
  - [x] Found and fixed a real, independent `OooeTrain` bug along the way, unrelated to the SimPoint
    pipeline itself (reproduced on a plain straight-through OoO run too): `LinuxSyscallEmulator`'s
    ECALL handler reads and writes guest memory through the same `IMemory` (`_capMem`) the executor
    uses for real loads/stores. `ExecResult.HasLoadAccess`/`HasStoreCapture` were derived
    unconditionally from `_capMem.HasRead`/`HasWrite`, so any ECALL that touched memory (e.g.
    `write`/`writev`'s buffer read, `fstat`/`clock_gettime`/`getrandom`'s struct write) was
    misread as owning an LQ/SQ entry it was never allocated (System-class instructions don't
    allocate one) — `_lq.At(-1)`/`_sq.At(-1)` then threw `IndexOutOfRangeException`. Root cause:
    System-class instructions are head-serialized (`ToothClass.System when rs.RobIndex !=
    _rob.HeadIndex`, same as Vector/UVE) but weren't routed through the same direct,
    non-speculative `DLayers.Accessor` path Vector/UVE already use — they went through
    `CapturingMemory`, which only *captures* writes for later Store Queue drain rather than
    applying them, silently losing syscall writes even once the crash was suppressed by an earlier,
    reverted attempt at a narrower fix. Fixed by adding System to the `DLayers.Accessor` bypass set
    alongside Vector/UVE. Two regression tests in `Tests/RiscV64/System/InitialStackTests.cs`
    (`SyscallMemoryAccess_UnderOooeTrain_DoesNotCorruptLoadStoreQueues`,
    `SyscallMemoryWrite_UnderOooeTrain_DoesNotCorruptStoreQueue`), both verified via
    revert-and-recheck to fail (the second with silently-lost data before crash-suppression was
    even attempted, then with the crash itself) without the fix.
  - [x] Fixed the younger-load-vs-ECALL memory-ordering hazard: a younger load could issue and
    execute *before* a head-serialized ECALL that writes overlapping memory, reading stale data —
    `HasPrecedingVectorStore`'s vector-store blocking set never included ECALL. Turned out to be a
    genuine, deterministically-reproducible race (not the rare/inconclusive case the earlier probe
    suggested): a new hand-encoded test with a long dependent add-chain ahead of the ECALL (to widen
    the race window) confirms a younger load races ahead and reads a poisoned pre-ECALL value without
    the fix. Fixed by adding `ITooth.MayAccessArbitraryMemory` (true only for RV32/64 `RvEcall`; CSR
    reads/writes and SRET/MRET/WFI have proper destinations and don't touch memory) and checking it
    in `HasPrecedingVectorStore` alongside the existing vector-store case — blanket, non-address-
    specific, matching the vector-store precedent's own conservatism. Regression test:
    `SyscallMemoryWrite_OrdersBeforeYoungerLoad_UnderOooeTrain`, verified via revert-and-recheck to
    fail (reads back the poison value) without the fix.
  - [x] Fixed the separate `stdin_echo64.elf`-under-`OooeTrain` register-delivery gap (its
    `SYS_write`'s count depends on `SYS_read`'s return value via `mv a2, a0`, which produced empty
    output under `OooeTrain` while passing under `SingleCycleTrain`). Root cause: `RvEcall` decodes
    with `DestinationRegister = -1` and never participates in the RAT/PRF at all, so a younger
    consumer of a0 resolved its RAT lookup to whatever produced a0 *before* the ECALL and never
    learned of the dependency — this was not a timing race like the sub-bullet above, but a structural
    gap in how ECALL's result reaches the PRF. Fixed the same way the pre-existing Zacas `amocas.d`
    register-pair high half is already handled: set `RvEcall`'s `SecondaryDestinationRegister = 10`
    (a0) so the existing, class-agnostic `HasPendingSecondaryDest` dispatch stall and
    `CommitRegisters`' secondary-dest PRF sync — previously exercised only by atomics — now cover
    ECALL's result too, with no pipeline-stage code changes needed. Regression test:
    `LinuxSyscallEmulator_InjectedStdin_EchoedBackThroughSysReadSysWrite_UnderOooeTrain`, verified via
    revert-and-recheck to fail (empty output) without the fix.
  - [x] Reference-output comparison in `BenchmarkResult.Passed` was byte-exact only, including
    trailing newline. Added `BenchmarkConfig.NormalizeTrailingWhitespace` (default `false`, so an
    existing byte-exact reference keeps today's stricter behaviour) — when set,
    `BenchmarkResult.Passed` trims trailing whitespace from both the captured output and the
    reference file before comparing, so the SPEC-style convention of reference files always ending
    in `\n` no longer alone fails a benchmark whose last write didn't. Verified via
    revert-and-recheck that the flag both closes the false-failure case and still catches a real
    content mismatch that happens to also carry a trailing newline.

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
