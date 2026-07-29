# Multi-Hart Support

How Horologium models multiple RISC-V harts, from the functional `MultiHartKernel` up through pipeline-level
coordination (`MultiHartPipeline`, `SmtTrain`). See [loop-point.md](loop-point.md) for how these building
blocks (`clone()`/`futex()`/`RequestBlock`) get exercised by LoopPoint's multi-hart sampling pipeline, and
[pipeline-trains.md](pipeline-trains.md) for `SmtTrain` itself.

## MultiHartKernel (src/Isa/RiscV32/MultiCore)

`MultiHartKernel` drives N RISC-V harts round-robin against a shared physical memory. Each call to `Step()` advances
every non-halted hart by one instruction and returns the number still active; `Run(maxTicks)` loops until all harts halt
or the tick limit is reached. Each hart has its own `IArchState` (created by `Rv32Mechanism.CreateArchState()`). Halt
detection covers EBREAK (`result.IsHalt`), HTIF tohost (`result.RequestHalt`), and the infinite-self-loop idiom (
`PC == pc && class == Branch`). The kernel operates in physical address space (no fetch translation), making it suited
for bare-metal multi-hart workloads.

Two constructors are available: `MultiHartKernel(IMemory sharedMemory, …)` gives every hart the same `IMemory` (simplest
path, used with `ReservationAwareMemory` for LR/SC); `MultiHartKernel(IMemory[] perHartMemory, …)` gives each hart its
own cache (e.g. a `MoesifCache` backed by a shared `MoesifBus`) — both instruction fetch and data access route through
the per-hart memory.

`Rv32Mechanism` now accepts optional `reservationTable` and `hartId` constructor parameters, forwarded to `Rv32Executor`
for LR/SC routing. Typical setup:

```csharp
var table   = new ReservationTable();
var guarded = new ReservationAwareMemory(flat, table);
var kernel  = new MultiHartKernel(guarded,
    new Rv32Mechanism(reservationTable: table, hartId: 0),
    new Rv32Mechanism(reservationTable: table, hartId: 1));
```

`ReservationTable` tracks per-hart LR/SC reservations. Each hart registers a reservation on `LR.W`; any write from any
hart to the same 4-byte-aligned granule cancels all overlapping reservations so a subsequent `SC.W` fails correctly.
`ReservationAwareMemory` is a thin `IMemory` wrapper whose `Write()` calls `table.InvalidateAt()` before the actual
write, ensuring cancellation fires on every store. Single-hart setups leave `ReservationTable` null and use the existing
private `_reservation` field unchanged — no API or behaviour change for existing code.

**`clone()` and dynamic hart activation.** `MultiHartKernel`'s `activeHartCount` constructor parameter pre-allocates
every
hart's `IMechanism`/`IArchState` up front but only starts the first `activeHartCount` of them running — the rest sit
*dormant* (skipped by `Step()`) until `SpawnHart` activates one. `MultiHartKernel : IHartSpawner`, and
`LinuxSyscallEmulator.Spawner` (settable post-construction, breaking the construction-order cycle between the syscall
handler and the hart driver that needs it) wires `clone()` (syscall 220) to it: the handler snapshots the parent's
`IArchState` (`IArchState.Snapshot()`), overrides `sp`/`tp`/`a0=0` per the real clone() ABI, and calls `SpawnHart` to
activate the next dormant slot. The raw RISC-V syscall ABI is `a0=flags, a1=newsp, a2=ptid, a3=tls, a4=ctid` (confirmed
by compiling and disassembling real musl 1.2.5 `__clone`); `CLONE_SETTLS`/`CLONE_PARENT_SETTID` are honored, and all
harts sharing one `LinuxSyscallEmulator` instance is required (mirrors real `CLONE_FILES`/`CLONE_VM`).
Backward-compatible
constructor overloads default `activeHartCount` to every hart starting active, so pre-existing single-shot multi-hart
setups are unaffected. `MultiHartPipeline`'s equivalent dynamic-activation support is not yet implemented — see
`TODO.md`.

**`futex()` blocking.** `LinuxSyscallEmulator` handles syscall 98 (`FUTEX_WAIT`/`FUTEX_WAKE`) by polling rather than a
wait queue: `FUTEX_WAIT` returns `-EAGAIN` immediately if the word at `uaddr` already differs from the expected value;
otherwise it returns an `ExecuteResult` with the new `RequestBlock` flag set. `MultiHartKernel.StepHart` checks
`RequestBlock` right after `Execute()` and returns without advancing PC, applying `SideEffect`, writing a register
result, or calling `OnRetire()` — so a blocked hart's `ecall` is simply re-decoded and re-executed next tick, unchanged,
until the word changes, contributing zero retired instructions or BBV samples while blocked. `FUTEX_WAKE` is a no-op
returning 0; there is no waiter-identity bookkeeping to report a real wake count from. This is sound without a wait
queue because real futex(2) callers must always re-validate the guarded condition themselves after any wait returns
(spurious wakeups are always possible), and glibc/musl's mutex/cond/barrier primitives never branch on `FUTEX_WAIT`'s
specific return value for correctness — so `-EAGAIN` on any mismatch, whether from a real wake-triggered store or any
other write, is both futex(2)-conformant and sufficient for real pthread code.

**Per-hart `gettid`/thread-exit semantics.** `ISyscallHandler.Handle` takes a `hartId` parameter (`Rv32Executor.HartId`,
the same field LR/SC routing already uses), so `gettid`/`set_tid_address` return a real per-hart value (`hartId + 1`,
never 0 — some futex-based lock implementations reserve 0 as a sentinel) instead of a hardcoded constant; `getpid`
stays constant across every hart, matching real Linux (the whole thread-group shares one pid). `SYS_exit` (this hart
only) and `SYS_exit_group` (every hart) are distinguished via a new `ExecuteResult.RequestHaltAll` flag, checked by
`MultiHartKernel.Step` alongside `RequestHalt`. `clone()`'s returned tid (`newHartId + 1`, from its `SpawnHart` slot
index) uses the same convention as `gettid()` (from `Rv32Executor.HartId`) but the two are computed independently —
they agree only if the caller constructs the mechanism occupying a given dormant slot with a matching `hartId`;
neither `LinuxSyscallEmulator` nor `MultiHartKernel` enforces this invariant, so any driver spawning harts into
pre-allocated slots must keep the two in sync itself.

**`CLONE_CHILD_CLEARTID` and the real pthread integration test.** `clone()` records `ctid` per hart (when the
`CLONE_CHILD_CLEARTID` flag bit is set) and `LinuxSyscallEmulator` writes 0 to that address on that hart's own
`SYS_exit`/`SYS_exit_group` — no explicit wake call is needed beyond the write, since a blocked poll-based `futex()`
waiter (see above) notices the value changed on its own next recheck. `Tests/RiscV64/System/PthreadProbeTests.cs` boots
a real, statically-linked musl `pthread_create`/`pthread_join` binary (`TestBinaries/pthread_probe.c`) through
`MultiHartKernel` end to end — the decisive integration proof for the `clone()`/`futex()`/`gettid` prerequisite chain.
Disassembling that exact binary is what surfaced the need for `CLONE_CHILD_CLEARTID` in the first place: real musl
passes `&__thread_list_lock` (a global lock, not the exiting thread's own tid word) as `ctid`, relying on the kernel as
a backstop to release that lock on exit, since `__pthread_exit` does not reliably call `__tl_unlock` along every exit
path. No `flake.nix` change was needed for the pthread/OpenMP toolchain — `pkgsCross.riscv64-musl`'s GCC already ships
`libgomp` and links `-pthread`/`-fopenmp` static binaries cleanly.

## MultiHartPipeline (src/Core/Pipeline/)

`MultiHartPipeline` coordinates N full pipeline trains (`ISteppableTrain`) in round-robin cycle-interleaved order — the
pipeline-train analogue of `MultiHartKernel`. Each hart owns its own train instance (and typically its own
`MoesifCache`); the coordinator advances every non-halted train by one tick per logical cycle.

`ISteppableTrain` (`src/Core/Orrery/Train/`) is a minimal interface: `BeginStepping()`, `StepCycle() → bool`, `IsIdle`,
`FinishStepping() → RevolutionResult`. All pipeline train types implement it: `SingleCycleTrain`, `FiveStageTrain`,
`SuperscalarTrain`, `OooeTrain`, `CprTrain`, `SmtTrain`, `DaeTrain`.

```csharp
var flat   = new FlatMemory(0x10000);
var bus    = new MoesifBus(flat);
var cache0 = new MoesifCache(bus, 4096, 2, 64);
var cache1 = new MoesifCache(bus, 4096, 2, 64);

var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0, entryPoint: 0x00);
var train1 = new SingleCycleTrain(new Rv32Mechanism(), cache1, entryPoint: 0x40);

RevolutionResult[] results = new MultiHartPipeline(train0, train1).Run(maxTicks: 100_000);
```

`Run` returns one `RevolutionResult` per hart. Combine with `MoesifBus(flat, table:)` +
`Rv32Mechanism(reservationTable:, hartId:)` for LR/SC atomics between pipeline trains.

**Dynamic hart activation.** `MultiHartPipeline` implements `IHartSpawner` so a real `clone()` call from
one of its harts can bring a brand-new hart onto a live `Run()` — unlike `MultiHartKernel` (a shared
`IMechanism` plus a plain `IArchState` slot per hart, so spawning just assigns into a pre-allocated dormant
slot), each hart here owns a whole `ISteppableTrain` — its own pipeline latches, PRF/rename state, and
fetch-address tracking, seeded once at construction — so there's no slot to reuse; a spawn must construct a
genuinely new train. Pass a `spawnTrainFactory` (`Func<IArchState, ISteppableTrain>`) to the constructor:
given the spawned hart's already-derived initial state (`clone()`'s snapshot with sp/tp/return-value already
patched), read its `Pc` and pass that as the new train's own `entryPoint` — the same "read PC before
construction" pattern every checkpoint-restore path in this codebase already follows, since a train's fetch
state is seeded once and never re-read from `IArchState` afterward. `SpawnHart` then copies the rest of the
spawned hart's register/ISA state into the new train's own (otherwise zero-valued) `IArchState` via
`ArchStateTransfer.CopyInto` (previously only used for SMARTS's functional↔detailed handoff), calls
`BeginStepping()`, and appends it to the round-robin set — a hart spawned mid-run starts stepping the same
tick, one tick "younger" than its parent, the same asymmetry `MultiHartKernel.SpawnHart` already documents.
Only `Run()` supports this; `SpawnHart` throws if called while a `RunConcurrent()` call is in flight, since
appending to the shared hart list would race with that method's own concurrent `Parallel.For` reads.

```csharp
var handler = new LinuxSyscallEmulator(initialBreak);
var train0 = new FiveStageTrain(new Rv32Mechanism(syscallHandler: handler, hartId: 0), mem, entryPoint: 0x00);

ISteppableTrain SpawnTrainFactory(IArchState child) =>
    new FiveStageTrain(new Rv32Mechanism(syscallHandler: handler, hartId: 1), mem, entryPoint: child.Pc);

var pipeline = new MultiHartPipeline(SpawnTrainFactory, train0);
handler.Spawner = pipeline; // clone() now spawns a real second FiveStageTrain
pipeline.Run(maxTicks: 100_000);
```

Proven with `Tests/Pipeline/MultiHartPipelineCloneTests.cs` — the same hand-assembled raw-syscall `clone()`
program `CloneTests.cs` already validates against real compiled musl `__clone` arguments for
`MultiHartKernel`, run here through a real `FiveStageTrain` instead — confirmed to fail (`HartCount` stuck
at 1, the child's state never landing) with the append reverted, then pass restored. A separate test drives
the `RunConcurrent` guard specifically: a `clone()` call from inside `RunConcurrent`'s `Parallel.For` throws
`InvalidOperationException` wrapped in an `AggregateException` (not bare, since the throw happens on a
worker thread) — asserted as that exact shape, and confirmed to fail (no exception at all) with the guard
reverted.

**A real, unrelated bug surfaced while writing this feature's own test, fixed in a follow-up session**:
`FiveStageTrain`'s `ecall` dispatch used to read the syscall number straight from architectural state,
bypassing the pipeline's hazard/forwarding path — a write to that register by the *immediately preceding*
instruction (zero-instruction gap) hadn't retired yet when `ecall` reached EX, so it read the stale value.
The fix is a register-agnostic full-pipeline drain rather than an extension of the forwarding network (see
"FiveStageTrain ecall hazard fix" below); `MultiHartPipelineCloneTests.cs` now uses the same back-to-back
`addi a7,N; ecall` sequencing as `CloneTests.cs` instead of NOP-padding around the old gap.

**This turned out to block a real use case, not just synthetic tests**: a tick-level ground-truth test
comparing a cold `pthread_probe.elf` run (via `MultiHartPipeline`'s dynamic activation) against
`MultiHartLoopPointExperiment`'s estimate, mirroring `RealLinkedLoopPointTests`'s single-hart version, hit
this exact bug on real, unpaddable compiled musl code: startup silently took the ENOSYS path on some other
syscall before ever reaching `clone()`, leaving the run permanently stuck on a `futex` wait no other hart
ever got created to clear. Now fixed and passing (`Tests/RiscV64/System/MultiHartLoopPointGroundTruthTests.cs`)
— see below.

### FiveStageTrain ecall hazard fix (src/Core/Pipeline/HazardUnit.cs, FiveStageTrain.cs)

`ecall` implicitly reads up to 7 registers (`a0`-`a5`, `a7`) straight from architectural state rather than
through any decoded `SourceRegisters` — so neither the load-use stall nor forwarding had anything to key
off. Extending the fix to "decode those 7 registers as `SourceRegisters`" only works if forwarding is
disabled: `FiveStageTrain` forwards by default, and `HazardUnit.Forward`/`ForwardingOverlay` are hardwired
to exactly 3 operand slots, so at most 3 of `ecall`'s 7 implicit reads could ever be protected regardless of
list order (concrete counter-example: `li a7,N; ecall`, where `li`/`addi` is not a load, so no load-use
stall fires either). The actual fix is register-agnostic and mirrors the immunity `OooTrain`/`CprTrain`/
`DaeTrain`/`SuperscalarTrain`/`SmtTrain` already had by construction (serialize until fully retired, not
forward): `HazardUnit`/`FiveStageTrain` now hold `ecall` in ID until both EX and MEM are empty, guaranteeing
every older instruction has reached WB. Scoped narrowly via `ITooth.MayAccessArbitraryMemory` (already true
only for `ecall`, not other `ToothClass.System` ops like CSR reads/writes, which already work correctly via
ordinary 1-register forwarding) so the drain doesn't perturb their existing cycle counts. Proven with a
permanent regression (`FiveStagePipelineTests.Pipeline_EcallImmediatelyAfterArgWrite_SeesWrittenValue_NotStaleState`
— confirmed to read the stale value with the drain reverted, pass restored) and end-to-end via the
previously-shelved `MultiHartLoopPointGroundTruthTests`: a cold `pthread_probe.elf` run through
`MultiHartPipeline`'s dynamic activation now completes (both `pthread_create` calls spawn, both threads
print, `pthread_join` unblocks), and its ground-truth tick count agrees with `MultiHartLoopPointExperiment`'s
estimate within ~6% (well inside the test's 30% tolerance, generous because this fixture is only a few
thousand instructions — far short of the paper's sampling scale).

**OoO timing note:** `OooeTrain`'s physical register file starts zeroed; `ArchState.IntegerRegisters.Write()` updates
the architectural register file but not the PRF, so register values pre-set before `Run()` are invisible to the
pipeline. For OoO MOESIF coherence tests or any test that requires non-zero initial register values, compute those
values inside the program (e.g. `lui`+`addi` sequences). Also, OoO stores commit to the cache at ROB-head (several
cycles after fetch), so a cross-hart load must be issued late enough to see the committed store — pad H1 with nops in
the decode stream before the load's source-register computation.

