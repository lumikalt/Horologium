# Program Termination and Syscall Emulation

Two halt mechanisms, both stopping all three trains at the terminator instead of spinning to `maxTicks`:

- **HTIF tohost exit (first-class).** When the mechanism is given the `tohost` address (
  `new Rv32Mechanism(htifTohost)`), a word store of an odd exit code to that register is flagged by the executor with
  `ExecuteResult.RequestHalt`. The trains carry that flag through commit (the in-order pipeline via `MemWbLatch`, the
  out-of-order core via the ROB) and halt *after* the store commits — the engine terminates at the exit write itself,
  not the spin that follows. The flag is ISA-agnostic: the trains act on it without knowing about HTIF. The address is
  surfaced generically as `IWorkload.HtifTohostAddress` (the ELF's `tohost` symbol), so `Experiment.Run`/`Trace`, the
  Runner, and the Face all wire it automatically for HTIF ELFs.
- **Unconditional jump-to-self (backstop).** For non-HTIF programs, a `jal`/`jalr` whose resolved target is its own PC (
  the conventional bare-metal `j .` terminator) halts the run. Gated on `ToothClass.Branch` so a conditional spin-wait —
  which may be waiting on an interrupt — is not mistaken for a halt.
- **HTIF syscall proxy (fesvr magic-mem protocol).** `HtifMemory` now fully implements the fesvr host-side: when the
  simulated program writes an even, non-zero pointer to `tohost` (a magic-mem syscall request), `HtifMemory` reads the
  8×uint64 struct (`magic_mem[0]` = syscall number, `[1..3]` = args), dispatches `SYS_write` (64), `SYS_read` (63),
  `SYS_close` (57), `SYS_lseek` (62), `SYS_fstat` (80), `SYS_open` (1024), and `SYS_openat` (56), writes the return
  value back to `magic_mem[0]`, then ACKs `fromhost`. An optional `TextWriter? output` parameter captures `SYS_write`
  output; when null the bytes are discarded. `Rv32ElfWorkload.WrapMemory(memory, output)` forwards the writer.
- **Linux syscall-emulation mode (`LinuxSyscallEmulator`).** A gem5 SE-style ECALL interceptor. Pass
  `syscallHandler: new LinuxSyscallEmulator(workload.InitialBreak, output)` to `Rv32Mechanism`. The executor routes
  every ECALL through the handler instead of trapping: `SYS_write`/`SYS_read` operate on simulated memory; `SYS_exit`/
  `SYS_exit_group` set `RequestHalt`; `SYS_brk` manages a software heap break; `SYS_mprotect`, `SYS_rt_sigaction`,
  `SYS_rt_sigprocmask`, `SYS_set_tid_address`, `SYS_getpid`, `SYS_gettid` return benign constants; `SYS_ioctl` returns
  `−ENOTTY`; all others return `−ENOSYS`. `SYS_writev` (`Writev`) walks the `iovec` array and delegates each entry to
  `Write` — real musl/glibc buffered stdio (`printf`, `fwrite`) flushes through `writev`, not plain `write`; see the
  batch-harness note below for how this was found. `Rv32ElfWorkload.InitialBreak` exposes the page-rounded end of the
  last PT_LOAD segment as the initial break address. `ISyscallHandler` (in `Mechanism/`) defines the interface so
  alternate emulators can be plugged in.
- **`LinuxSyscallEmulator` realism: file I/O, mmap, clock/random, fcntl.** `SYS_openat`/`SYS_read`/`SYS_write`/
  `SYS_close`/`SYS_lseek` are unrestricted host passthrough (the gem5-SE/Spike-pk convention — paths open exactly as
  given, against the simulator process's own cwd; no sandboxing, since the guest binary is the user's own,
  already-compiled, locally run program). `SYS_fstat` fills a real `struct stat`/`stat64` — RV32's `fstat` syscall
  (80) is `sys_fstat64` (104-byte layout), RV64's is `sys_newfstat` (128-byte layout); genuinely different structs
  under the same syscall number, selected by the new `wordSize` constructor parameter. Both layouts, and
  `clock_gettime`'s 16-byte `struct __kernel_timespec` (identical on RV32/RV64 — RISC-V never implemented the
  legacy 32-bit-time_t syscalls), were verified by compiling field-store probes with `riscv32-none-elf-gcc`/
  `riscv64-none-elf-gcc` and reading the emitted store offsets, not reconstructed from memory. `SYS_mmap` is a bump
  allocator over a caller-supplied `[mmapBase, mmapLimit)` arena (new constructor parameters; anonymous mappings
  only, file-backed mappings eagerly read the file into the region); `SYS_munmap` never reclaims; an unconfigured
  or exhausted arena returns ENOMEM, same as real mmap under memory pressure. `SYS_clock_gettime`/`SYS_getrandom`
  are deterministic (a synthetic incrementing clock; a seeded xorshift PRNG) rather than real host time/entropy,
  matching this project's reproducibility precedent. `SYS_fcntl` returns benign success for
  `F_GETFD`/`F_SETFD`/`F_GETFL`/`F_SETFL`. stdin (fd 0) is separate from the file-I/O passthrough: an optional
  `Stream? input` constructor parameter redirects `SYS_read` on fd 0 to it (read sequentially, never rewound, EOF
  once exhausted); omitted (the default) it stays always-EOF as before. `SYS_write`/`SYS_writev`/`SYS_exit_group`
  are now validated against a real, genuinely compiled and statically-linked musl RV64 binary (see the batch-harness
  note below) — but `openat`/`read`/`close`/`lseek`/`fstat`/`mmap` are still only hand-verified struct offsets plus
  unit tests calling `Handle` directly, plus one ELF-driven round-trip (`stdin_echo64.elf`) proving an injected
  stream reaches a real guest's `SYS_read` and comes back out through `SYS_write` — none of *those* syscalls have
  been exercised by a real linked binary yet.
- **RV64 syscall-emulation wiring.** `Rv64Mechanism` now takes the same `syscallHandler: ISyscallHandler?` constructor
  parameter as `Rv32Mechanism` — `Rv64Executor : Rv32Executor` already inherited the ECALL-dispatch arm unchanged, so
  this was the only missing wire. `Rv64ElfWorkload.InitialBreak` mirrors `Rv32ElfWorkload`'s PT_LOAD-scan computation.
- **psABI initial-stack builder (`InitialStackBuilder`, ISA-agnostic, in `Mechanism/`).** Bare-metal entry (PC = ELF
  entry point, registers untouched) is enough for the hand-written assembly test fixtures, but a real compiled
  binary's C-runtime `_start` reads its command line and environment straight off the initial stack. Building
  `_start` from bare-metal isn't possible without one. `BuildInitialStack(memory, stackTop, wordSize, argv, envp,
  auxv)` writes a standard argc/argv/envp/auxv layout (string blob → 16-byte-aligned auxv array, terminated by
  `AT_NULL` → envp/argv pointer arrays, NULL-terminated → argc word) and returns the resulting SP, 16-byte aligned
  per the RISC-V calling convention. One function serves RV32 and RV64 (`wordSize` 4 or 8). `BuildStandardAuxv`
  assembles the standards-minimal auxv set for a statically-linked binary (`AT_PAGESZ`, `AT_PHDR`/`AT_PHENT`/
  `AT_PHNUM`, `AT_ENTRY`, zeroed uid/gid/hwcap/secure). `IElfWorkload.PhdrAddress`/`PhEntrySize`/`PhNum`
  (`Rv32ElfWorkload`/`Rv64ElfWorkload`, computed from the ELF header) supply the real AT_PHDR/AT_PHENT/AT_PHNUM
  values every caller now passes — placeholder zeros here are not just imprecise but a real crash: musl's own
  `_start`/`__init_tls` walks the program header table itself (found via those three auxv entries) to locate
  PT_TLS and set the thread pointer with a plain register move (no syscall involved), so a zero AT_PHDR makes
  that walk dereference address 0 and fault on any binary declaring thread-local data — confirmed with a real
  compiled `__thread`-using binary (`TestBinaries/tls_probe.c`), which crashes with the placeholder auxv and
  runs correctly with the real one (`Tests/RiscV64/System/RealLinkedBinaryTests.cs`). Verified two ways besides
  that: `InitialStackBuilderTests` asserts the exact byte layout for both word sizes directly against a
  `FlatMemory`; `Tests/RiscV64/System/InitialStackTests.cs` loads hand-assembled RV64 probes (`abi_probe64.s`,
  `stdin_echo64.s`, each with its own independent offset arithmetic) that read the stack this function built and
  echo back what they find. Callers still inject the SP manually (`ArchState.IntegerRegisters.Write(2, sp)`
  before `Run()`) — there is no `IWorkload`/`Train` wiring for it.
- **Batch benchmark harness (`BenchmarkConfig`/`Experiment.RunBenchmark`, `src/Isa/RiscV32/Analysis/`).** Ties the
  three pieces above together into a real Linux-ABI entry, instead of each being exercised only in isolation:
  `BenchmarkConfig` (JSON, mirroring `NamedConfig`'s conventions) names an ELF, its argv, an optional stdin file, an
  optional reference-output file, and an optional memory-size override. `RunBenchmark` builds the psABI stack
  (`argv[0]` derived from the ELF's file name), wires a `LinuxSyscallEmulator` with captured output and the
  benchmark's stdin file (if any), runs to completion (or `maxTicks`) on a functional `SingleCycleTrain` — this is a
  correctness/regression harness, not a timing run — and diffs captured output against the reference file
  byte-for-byte (`Encoding.Latin1` on both sides, matching the emulator's `(char)byte` capture convention, so the
  comparison is exact regardless of content) unless `BenchmarkConfig.NormalizeTrailingWhitespace` is set, in which
  case both sides are trimmed of trailing whitespace before comparing — off by default, since a stray trailing
  newline in the reference file (the common SPEC-style convention, regardless of whether the guest's last write
  emitted one) would otherwise fail a benchmark that's actually correct.
  `BenchmarkResult.Halted` (via `SingleCycleTrain.IsIdle` after `Run()`, not a tick-count heuristic — a clean
  `SYS_exit` drains the Escapement, a timeout leaves events pending) tells a timeout apart from a real exit;
  `Checked`/`Passed` tell "no reference supplied" apart from "verified and matched". Kept ISA-agnostic (takes an
  already-built `IElfWorkload` and a `mechanismFactory` the caller supplies) the same way the rest of `Experiment`
  is. The Runner exposes it as `--bench-config <path.json>`, running every benchmark under `--xlen`'s ISA and
  exiting with status 1 if any times out or fails its reference check. A crashing benchmark (e.g. one that
  dereferences a failed mmap's negative return) is caught per-benchmark and reported as `ERROR` rather than
  aborting the rest of the batch. Verified end-to-end against bare-metal SE-mode probes (`abi_probe64.elf`,
  `stdin_echo64.elf`, `mmap_probe64.elf`); the toolchain gap that previously made a real linked-libc test
  impossible is now closed (`riscv64-unknown-linux-musl-gcc` in `flake.nix`). The first attempt, a hand-written
  `hello.c` compiled `-static` and run through `--bench-config`, halted cleanly and a raw `write()` syscall
  (bypassing stdio) was captured correctly, but `printf`/`fflush` produced no output at all — root-caused to a
  missing `SYS_writev`: musl's buffered stdio flushes via `writev`, not plain `write`, and the emulator's fallback
  `ENOSYS` for unimplemented syscalls is swallowed silently by musl's stdio error path rather than surfaced. Fixed
  with `LinuxSyscallEmulator.Writev` (walks the `iovec` array, delegating each entry to the existing `Write`,
  matching real `writev`'s zero-length-skip and short-write-stops-early semantics). `TestBinaries/hello64_musl.c`/
  `.elf` — a genuinely compiled and statically-linked binary, not a hand-assembled probe — now runs its `printf`
  and returns its `argv[0]` correctly end-to-end (`Tests/RiscV64/System/RealLinkedBinaryTests`), the first real
  linked binary to complete this pipeline's entry/syscall/stdio path successfully. This validates only that
  trivial path, though — the SimPoint/checkpoint sampling pipeline itself (the actual substance of the SPEC-harness
  TODO item) has not yet been run against a real compiled binary, only against bare-metal HTIF probes; see
  TODO.md. `BenchmarkConfig.MmapArenaBytes`
  optionally sizes an anonymous-mmap arena, appended past the workload's own memory so enabling it never shifts
  where the stack or `brk`-growable region end up (both keep the exact placement they'd have with it unset);
  omitted or 0 (the default) keeps `mmap` disabled — `SYS_mmap` returns `ENOMEM`, same as before this existed. The
  arena is still a bump allocator that never reclaims (`SYS_munmap` is a no-op, per the `LinuxSyscallEmulator`
  realism note above), so a long malloc-heavy run still exhausts a finite arena and ENOMEMs mid-run, and
  `FlatMemory` is `int`-sized, putting a real multi-GB SPEC heap out of reach regardless of arena config — sizing
  the arena per benchmark is a tuning knob, not a solved problem.

Because the five-stage and out-of-order trains previously spun HTIF binaries to `maxTicks`, adding these halts also
makes the HTIF benchmark suite finish in seconds. `HtifExitTests` covers both paths across all three trains without
requiring Spike.

