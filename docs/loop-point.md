# LoopPoint: Multi-Hart Sampling

The region-marker half of LoopPoint (Sabu, Patil, Heirman & Carlson, HPCA 2022) — the multi-hart counterpart to
[SimPoint](sampling-methodologies.md) — plus the `RequestBlock` (blocking-syscall) support this needed across
every pipeline train, and the checkpoint/warmup/measure/extrapolate machinery built on top of it.

## Loop-header detection (Pipeline/LoopPointAnalysis)

The region-marker half of LoopPoint (Sabu, Patil, Heirman & Carlson, HPCA 2022) — the multi-hart counterpart to
[SimPoint](sampling-methodologies.md), staged as a sequence of independently-actionable prerequisites in `TODO.md` (thread pointer/PT_TLS,
`clone()`, `futex()`, per-hart `gettid`/thread-exit, and an OpenMP/pthreads toolchain fixture are done; the
methodology's own analysis pieces are still landing one at a time). `LoopHeaderTracker` is a standalone
`ICommitObserver`, structurally a sibling to `BbvProfiler` rather than an extension of it (not yet wired into its
interval slicing), that identifies a loop header the same way `BbvProfiler` identifies a block boundary — from the
committed PC stream's own discontinuities, without a real control-flow graph. A candidate is a backward transfer
(target ≤ source) that is both direct and not a call, via `IDecoder.GetFetchHint`'s `BranchTarget.HasValue && !IsCall`
— the same ISA-agnostic pre-decode hint branch predictors already use for RAS/indirect handling. Both halves of that
check are independently necessary: excluding indirect transfers (JALR — returns, virtual calls, computed gotos)
rules out a `ret` landing at a lower address than its own call site; excluding calls separately rules out a direct
`jal ra, target` to a function placed, in link order, before its caller. Real loop back-edges are essentially
always direct, non-call transfers (a conditional branch, or a compiler-emitted unconditional jump for a `goto`-style
loop), which is what makes this cut viable without a real dominator analysis — confirmed by a discriminating test
with a helper function placed *after* its caller's loop, so its `ret` lands backward every iteration, right beside
a genuine loop back-edge. `count` in the paper's `(PC, count)` markers is the number of times the backward edge has
been *taken* to reach that header, not the total iteration count — an N-iteration loop's first entry is a
fall-through from the code before it, not a discontinuity, and so isn't observable in a single streaming pass; the
final marker for such a loop reads `(header, N-1)`. `rangeStart`/`rangeEnd` scope detection to the loaded program's
own address space (a sanity bound) — they do not by themselves separate user code from statically-linked library
code sharing the same segment; that separation is `excludedRanges`, an optional constructor parameter checked
against the header's own PC only (not the backward edge's source), mirroring the paper's exclusion of
synchronization-library busy-waiting from loop-based work counting while still executing that code normally.
`IElfWorkload.EnumerateSymbols()` exposes every named, non-zero-size `.symtab` entry, and
`SyncLibrarySymbols.ExcludedRanges` turns a name-prefix list (`__tl_`/`__vm_`/`__wait`/`__lock`/`pthread_`/`sem_`/
`gomp_`/etc., a musl/libpthread/libgomp internal-symbol guess verified against `pthread_probe.elf`'s own compiled
symbol table, not assumed) into the `[Start, End)` ranges `LoopHeaderTracker` consumes. Each piece is unit-tested on
its own — symbol enumeration, prefix classification against the real ELF, and range-suppression against a synthetic
two-loop fixture — and proven end-to-end under real multi-hart contention: `SpinLoopFilteringEndToEndTests` runs
`pthread_probe.elf` through `MultiHartKernel` (3 harts, real `clone()`/`futex()`/thread-list-lock contention from
two concurrent `pthread_create`/`pthread_join` pairs); without exclusion, real markers land inside `__tl_lock`'s
etc. ranges (89/61/56 markers per hart, 22/3/6 of them inside excluded ranges); with the same ranges wired in,
exactly and only those markers disappear. `hello64_musl.elf` (single-threaded) could never show this — an
uncontended lock's retry loop is never taken backward — which is why the real proof needed a genuinely
multi-threaded fixture, only available once `MultiHartKernel` gained per-hart commit-observer wiring below.

The paper's next step, "flow-control" (restricting thread forward progress during profiling so no thread races
ahead of another), needed no new code here: it exists in the paper to correct skew from a real, non-deterministic
host OS scheduler running Pin instrumentation — an artifact `MultiHartKernel.Step()`/`MultiHartPipeline.Run()`
cannot produce, since both already advance every non-halted hart by exactly one instruction/cycle per call,
deterministically. `RunConcurrent`'s `Parallel.For` is the one mode with real host-thread parallelism, but each
tick is still a hard barrier — no hart can complete two cycles before another completes its first. Confirmed
(not just argued) with a test reading through independent architectural state: two harts each running an
infinite counting loop stay bit-for-bit in lockstep after every tick, both under `MultiHartKernel.Step()` and
under `MultiHartPipeline.RunConcurrent`.

`MultiHartLoopPointProfiler` (same file) is the rest of the profiling pass: one `LoopHeaderTracker` +
`BbvProfiler` pair per hart, coordinated through a shared global instruction counter and region-boundary
logic. `MultiHartKernel` gained per-hart commit-observer wiring (`SetObserver`) to support it — it had none
before, despite being LoopPoint's own profiling-pass driver ("we want the timing model to control thread
progress... not PinPlay" only applies to the later detailed-timing pass; the profiling pass is exactly
this deterministic functional driver). A region closes at the next loop-header hit, on *any* hart ("we do
not restrict specific threads to indicate loop boundaries"), after a global counter — incremented once per
non-excluded commit on any hart, mirroring spin-loop filtering's "not work done" — reaches
`targetGlobalInstructions` (the paper's "approximately N × 100M global (all-threads) instructions"). At that
instant every hart's accumulated interval is cut via `BbvProfiler`'s new public `CutInterval()` (refactored
out of its existing fixed-instruction-count auto-cut so an externally-triggered cut still correctly splits
an open block and continues its remainder into the next region — `Complete()`'s one-shot-flush semantics
would instead lose that continuity, caught by a focused test before it could silently corrupt every
subsequent region's BBV), per-thread-normalized to a fixed integer total, namespaced into a disjoint key
range (`(hartId << 48) | pc`, since real addresses never use the top 16 bits) so identical-binary threads'
identical PCs never collide, and concatenated into one region vector — feeding `SimPointAnalysis.Analyze`
completely unchanged. Namespacing keeps regions with different active-hart-counts from colluding across
threads' dimensions; each thread's own within-region block proportions survive concatenation exactly
(per-thread normalization first), while a region's combined magnitude naturally varies with how many harts
were active in it — `Project()` divides by that region's own total, not a cross-region constant, so this
doesn't need to be, and isn't, uniform across regions.
`MultiHartLoopPointProfilerRealElfTests` runs the whole coordinator on `pthread_probe.elf`, spanning regions
with genuinely varying active-hart-counts (single-threaded startup/teardown vs. 3-hart steady state), and
feeds the result straight into `SimPointAnalysis.Analyze` — the test that actually exercises spin-loop
filtering's BBV/work-target exclusion under real contention (the marker-suppression proof above used a bare
`LoopHeaderTracker`, not this coordinator).

`MultiHartCheckpoint` (`Mechanism/MultiHartCheckpoint.cs`) and `MultiHartWarmupMeasureDriver`
(`Pipeline/MultiHartWarmupMeasureDriver.cs`) are LoopPoint's checkpoint/warmup/measure half —
`ArchitecturalCheckpoint`/`WarmupMeasureDriver`'s multi-hart counterparts. A checkpoint is N harts'
architectural state plus **one** shared-memory blob and **one** optional shared syscall-handler blob, not N
of either: threads share memory and an fd table/brk/mmap cursor by real `clone()`'s
`CLONE_VM|CLONE_FILES`, and every established multi-hart config already wires one shared handler across
every hart — duplicating either N-fold would let per-hart copies drift independently on restore, which
real shared state never does. `MultiHartWarmupMeasureDriver` bounds its warmup/measure phases by a global
(summed-across-harts) instruction count, matching `MultiHartLoopPointProfiler`'s own accounting, and
round-robins one `StepCycle()` per hart per tick like `MultiHartPipeline.Run`. `LinuxSyscallEmulator`
gained `_childCleartid` serialization — previously excluded with a comment naming this exact item as the
reason; without it a restored hart's later exit clears the wrong (or no) `ctid` address, reproducing the
`__thread_list_lock` hang class `pthread_probe.elf` originally surfaced.
Proven with a cold-baseline comparison, not just warm-equals-restored (which would pass even with a
broken `RestoreInto` — see the checkpoint-roundtrip-theater lesson from earlier checkpoint work): captured
mid-run on the functional `MultiHartKernel`, restored into fresh `FiveStageTrain`s driven by
`MultiHartPipeline`, and compared against a `FiveStageTrain`-only cold run of the same program to
completion — two harts with different iteration counts to different addresses, so a swapped-hart restore
bug would produce a visibly wrong final value rather than coincidentally matching. A second test drives a
real, stateful `LinuxSyscallEmulator` (brk moved, `_childCleartid` set via `clone()`) through the
checkpoint's shared-handler-blob path specifically. Measuring a genuinely *blocking* multi-threaded
region (a real futex wait) under detailed pipeline timing needed `ExecuteResult.RequestBlock` support in
the detailed trains — now done in all six (`FiveStageTrain`, `OooTrain`, `CprTrain`, `DaeTrain`,
`SuperscalarTrain`, `SmtTrain`; see below). This does not by itself make multi-hart LoopPoint measurement
work end-to-end — see the residual items in `TODO.md`.

**`RequestBlock` support in `FiveStageTrain` (Pipeline/Stages/Execute.cs, WriteBack.cs, FiveStageTrain.cs).**
`FiveStageTrain` doesn't serialize `ecall` — no stall/hazard treatment at all — so by the time a syscall's
`ExecuteResult.RequestBlock` is discovered (at the EX→MEM boundary), younger instructions may already be
fetched/decoded/in EX behind it. A still-blocked syscall can't simply be retried in place the way
`MultiHartKernel.StepHart` does (return without advancing `state.Pc`) — that only works there because
functional stepping is one instruction at a time, with nothing younger in flight to worry about. The fix
mirrors the pipeline's existing halt/trap/return-from-trap handling exactly: `RequestBlock` is added to
the same three-round squash pattern (`MemWbLatch` gained a `RequestBlock` field, copied from
`ExecuteResult` in `MemoryStage.Cycle()`) — round 1 (EX→MEM boundary) and round 2 (MEM→WB boundary) each
squash EX and flush ID the moment a `RequestBlock` result is seen at that boundary, exactly like the
existing `IsHalt`/`HasTrap`/`IsReturnFromTrap` checks; round 3 fires once the blocked instruction actually
reaches `WritebackStage`, which — instead of retiring it, applying its `SideEffect`, writing a register,
or advancing `state.Pc` — sets a new `BlockRedirect` property (mirroring `TrapRedirect`) to the
instruction's *own* `Pc`, which `FiveStageTrain.RunCycle` reads to flush Fetch back to that same address.
The blocked instruction is thus re-fetched, re-decoded, and re-executed every cycle until a later
re-execution of the same `ecall` finally returns `RequestBlock: false`, at which point it commits
normally and younger instructions proceed — the same outcome `MultiHartKernel`'s retry achieves
functionally, reconstructed here across a pipeline with instructions genuinely in flight.
`Tests/Pipeline/FiveStageRequestBlockGuardTests.cs` proves both directions with a stub handler that blocks
a fixed number of calls before clearing: the blocked `ecall`'s own PC is retired (committed) exactly
once despite the handler being invoked several times, the instruction immediately after it doesn't run
until the block actually clears, and — confirmed by deliberately reverting the fix — both assertions fail
without the squash logic in place. A second test proves an ecall that never clears never retires or lets
anything past it, within a bounded tick budget (it would otherwise spin forever, which is the *correct*
behavior — a real futex wait blocks indefinitely too — but a test needs a bound regardless). Both of
those use a single hart with a self-clearing stub handler, which proves retry-in-place and resume-on-
clear but not the thing `RequestBlock` actually exists for: a hart's block clearing because *another*
hart wrote the word it's waiting on. A third test, `BlockedEcall_ResumesWhenAnotherHartClearsTheSharedFutexWord`,
closes that gap: two `FiveStageTrain`s share one `FlatMemory` under `MultiHartPipeline`; hart 0's handler
re-reads a shared word fresh on every retry (never caches it) and blocks while it's zero; hart 1 — a
plain, unrelated `Rv32Mechanism` with no blocking handler at all — writes that word via its own `sw` and
halts. Only the real cross-hart hand-off through shared memory makes hart 0's next instruction retire.
Confirmed discriminating by temporarily making the handler cache its first read instead of re-reading
memory: the test failed as expected, then passed again once the caching was reverted.

**`RequestBlock` support in `OooTrain` (Pipeline/OooTrain.cs, Pipeline/Ooo/ReorderBuffer.cs).**
`OooTrain`'s ROB-based commit/squash machinery needed a genuinely different translation from
`FiveStageTrain`'s, not a mechanical copy: `ecall` (a `ToothClass.System` op) is already head-serialized
at issue (it only issues once it's the ROB head), and `StepComplete`'s existing `!r.RegValue.HasValue`
guard already skips broadcasting a register value onto the CDB for a blocked result, the same way it
does for a trap — so the only new work is at commit. Rather than mirror the Trap/Halt/ReturnFromTrap
case (which calls `_rob.Retire()` and defers a `_pendingRollback*` rename rollback for later), the fix
follows the *load-violation* precedent instead: the blocked entry is left sitting at the ROB head — never
retired — so `StepFlush`'s existing `InOrder().Reverse()` walk-back (which restores the RAT and frees the
physical register for every entry still in the ROB) un-renames it for free, exactly like a re-executed
violated load. `RobEntry` and the internal `ExecResult` record both gained a `RequestBlock` field
(threaded through `ExecuteOne`'s return value and `StepComplete`'s CDB-broadcast copy, right alongside
the existing `RequestHalt` field); `RobEntry.Clear()` resets it so a reused ROB slot never inherits stale
state. A new `StepCommit` case does no `CommitRegisters`, no retired-count increment, and no
`State.OnRetire()` (this entry never contributed committed work), then calls `SetFlush(head.Pc)` — the
same "re-fetch from my own PC" recovery the load-violation and value-misprediction cases already use.
`Tests/Pipeline/OooRequestBlockGuardTests.cs` mirrors all three `FiveStageTrain` tests verbatim at the
black-box level (same stub-handler retry-in-place/never-clears tests, plus the cross-hart
`MultiHartPipeline`/shared-`FlatMemory` composition test) — all three passed on the first attempt, and
all three were confirmed to fail once the new `StepCommit` case was temporarily removed, then to pass
again once it was restored. The full non-benchmark suite stayed green throughout (4398 passing), showing
the new field/case didn't disturb value prediction, EOLE, runahead, store-set, or critical-path-prediction
logic sharing the same commit loop.

**`RequestBlock` support in `CprTrain` (Pipeline/CprTrain.cs).**
`CprTrain` has no ROB at all — Akkary/Rajwar/Srinivasan-style checkpoint-epoch tracking instead
(`Ooo/CheckpointList.cs`), where instructions commit in program-order bulk batches per checkpoint and
recovery means discarding checkpoints back to a target and refetching from that checkpoint's own
`RestartPc`. The fix is structurally the same *load-violation* shape `OooTrain` used, made exact by a
fact already true here: syscalls are always `IsSerialized` (`ToothClass.System`), which forces a fresh
checkpoint both immediately before and immediately after one — so a still-blocked `ecall` is always the
sole entry of its own single-instruction checkpoint. `StepComplete` gained a `RequestBlock` check (right
before the entry would otherwise be marked complete and broadcast onto the CDB) that calls the existing
`ScheduleRecovery(entry.InstrId, entry.CheckpointSeq, 0, false)` — the exact same helper a memory-order
violation already uses — which discards this checkpoint (and any later ones) and refetches precisely its
own Pc. No new persistent field was needed on `CheckpointEntry`: the check and the recovery both happen
inline in the same `StepComplete` pass, so nothing needs to survive past that point. `ExecResult` gained
a `RequestBlock` field threaded through `ExecuteOne`'s return, alongside the existing `RequestHalt`.
`Tests/Pipeline/CprRequestBlockGuardTests.cs` mirrors the `OooTrain` tests' shape (two single-hart
stub-handler tests plus the cross-hart `MultiHartPipeline`/shared-`FlatMemory` composition test), reading
dial-board `recoveries`/`retired` counters in place of a commit observer (`CprTrain`'s constructors don't
accept one). All three were confirmed to fail once the new `StepComplete` branch was temporarily removed,
then to pass again once it was restored. The full suite stayed green at 4401.

Writing the cross-hart test surfaced a genuine, separate, pre-existing bug, since fixed: `CprTrain`'s
physical register file was never seeded from `ArchState.IntegerRegisters` at construction — only during
`ApplyFullFlush`'s post-trap resync, whose seeding loop was otherwise never called at `Wind()`/startup
time. A pre-`Run()` register write (originally used to preset the second hart's store-address register in
the cross-hart test, the same thing a checkpoint restore does) was silently invisible to the instruction
that read it, sourcing 0 instead. Confirmed decisively with an isolated repro (an addi-synthesized
register worked identically otherwise; the pre-set version didn't) — this is the same bug class already
fixed for `OooTrain` (`Wind()` seeds the PRF from `ArchState.IntegerRegisters`, commit `832f1ab`), never
ported to `CprTrain`'s separate PRF/RAT. Fixed by mirroring the identical `OooTrain.Wind()` shape;
`Tests/RiscV32/Pipelines/CprTrainTests.PreRunRegisterWrite_IsVisibleToExecution` proves it (confirmed to
fail without the fix, pass with it), and the cross-hart `CprRequestBlockGuardTests` test now presets the
register via `ArchState.IntegerRegisters.Write` directly — the same shape as the `OooTrain` test — rather
than working around the bug, giving a second confirmation under real multi-hart interleaving.

**`RequestBlock` support in `DaeTrain` (Pipeline/DaeTrain.cs).**
The only one of the four detailed trains done so far needing **no rollback at all** — a genuinely simpler
case than `FiveStageTrain`/`OooTrain`/`CprTrain`. `ecall` is barrier-class (`IsBarrierClass` — everything
except `IntegerAlu`/`IntegerMulDiv`/`Load`/`Store`), and `ExecuteBarrier` only ever runs once both the
Access and Execute lanes are fully drained (`_accessQueue.Count == 0 && _executeQueue.Count == 0`); the
front end never dispatches a new instruction while a barrier is staged. That means nothing younger can
ever be in flight by the time a barrier executes, and its own undo-log entries were just cleared
immediately beforehand (bounding undo-log growth, since a drained barrier point is a permanent
checkpoint no future trap can roll back past). A still-blocked barrier is therefore a pure retry-in-place:
a new check right after `Execute()` — before the retired-count increment, `SideEffect`, register write, or
Pc advance — sets both `State.Pc` and `_fetchPc` back to the barrier's own Pc when `RequestBlock` is true,
so it's re-decoded and re-dispatched as a fresh instruction the next time `TryDispatchOne` runs.
`Tests/Pipeline/DaeRequestBlockGuardTests.cs` mirrors the other three trains' tests (two single-hart
stub-handler tests plus the cross-hart `MultiHartPipeline`/shared-`FlatMemory` composition test) — the
cross-hart test needed no PRF-seeding workaround, since `DaeTrain` reads and writes
`ArchState.IntegerRegisters` directly with no separate physical register file (confirmed by a direct
`ArchState.IntegerRegisters.Write` before `Run()` working correctly, unlike `CprTrain` before its fix
above). All three tests were confirmed to fail once the new check was temporarily removed, then to pass
again once it was restored. The full suite stayed green at 4405.

**`RequestBlock` support in `SuperscalarTrain` (Pipeline/SuperscalarTrain.cs).**
The simplest fix of the five done so far. `StepIssue` has no pipeline latch chain at all — it executes
each instruction functionally at issue, synchronously, one at a time, with only a plain fetch queue of
not-yet-executed entries ahead of the current one (branches are predicted at fetch but nothing there has
executed yet, so those entries are wrong-path candidates only, never in-flight executed work). A
still-blocked instruction is therefore never even dequeued: a new check right after `Execute()` — before
the dequeue, retired-count increment, or per-class issue-slot bookkeeping — just `break`s the issue loop
for this cycle, leaving the blocked instruction exactly where it is at the fetch-queue head. The next
`StepIssue` call simply re-peeks and re-executes the same head from scratch. No `FlushFrontend`/PC
redirect is needed at all: nothing has been dequeued or committed, and — since `ecall` isn't control flow
— the queue entries sitting behind it are already the correct post-clear path (unlike a branch, where a
wrong-path successor would need flushing). The leftover issue slot is deliberately left unclassified in
the TMA slot-accounting block below (neither `frontendStarved` nor `_tdRefillPending` is set), so it
falls into the Backend Bound residual rather than being mislabeled as a fetch-side stall.
`Tests/Pipeline/SuperscalarRequestBlockGuardTests.cs` mirrors the other trains' tests (two single-hart
stub-handler tests plus the cross-hart `MultiHartPipeline`/shared-`FlatMemory` composition test) — no
PRF-seeding workaround needed, since `SuperscalarTrain` has no separate physical register file either
(only a readiness-cycle scoreboard, not a value store). All three tests were confirmed to fail once the
new check was temporarily removed, then to pass again once it was restored. The full suite stayed green
at 4408.

**`RequestBlock` support in `SmtTrain` (Pipeline/SmtTrain.cs) — closes out all six detailed trains.**
`SmtTrain` is structurally different from the other five: it's genuinely multi-hart *within a single
train* — N harts sharing one core's fetch/issue bandwidth, interleaved cycle-by-cycle via a pluggable
`ISmtFetchPolicy` — rather than one train per hart. Each hart executes exactly one instruction fully to
completion (fetch → decode → execute → SideEffect → register write → Pc update) per turn, with no
cross-cycle in-flight state at all — the same completion model as `SingleCycleTrain`. That model is what
makes the fix simple *and* automatically hart-scoped: a check right after `Execute()` — before the
retired-count increment — returns `true` on `RequestBlock`, cutting only this hart's slot for the rest of
the current cycle (identical to how a halt/trap/branch already does), while leaving `ctx.ArchState.Pc`
untouched (already this instruction's own Pc, so no redirect logic is needed) and every other hart's
disjoint `HartContext` completely unaffected. No rollback, no squash machinery, nothing beyond this one
check. `Tests/Pipeline/SmtRequestBlockGuardTests.cs` mirrors the other trains' single-hart tests, but its
cross-hart test is necessarily shaped differently: rather than `MultiHartPipeline` wrapping two separate
trains, it's ONE `SmtTrain` constructed with two internal harts (`SmtTrain.StateOf(hartId)` exposes each
hart's register state) sharing one `FlatMemory`. This proves hart-scoping empirically, not just by
construction: hart 1 completes its `addi`/`sw`/`ebreak` *while* hart 0 is blocked every cycle, and only
hart 1's write unblocks hart 0 — a fix that accidentally starved the sibling hart would have failed this
test, not just missed a retry. All three tests were confirmed to fail once the new check was temporarily
removed, then to pass again once it was restored. The full suite stayed green at 4410.

`SmtTrain` getting `RequestBlock` support is not itself a step toward LoopPoint measurement — SMT models a
shared core with interleaved threads, the wrong microarchitecture for what LoopPoint measures (recorded
earlier in this project's own OoOE investigation). It closes the feature out for correctness/completeness
across all six detailed trains, not because LoopPoint will ever drive an `SmtTrain` measurement.

**`MultiHartWarmupMeasureDriver` stall detection (Pipeline/MultiHartWarmupMeasureDriver.cs).** With all six
trains now retrying in place on `RequestBlock` rather than halting, a hart permanently spinning on a wait no
one will ever clear (e.g. the waking hart already halted) never returns `false` from `StepCycle()` — the
old bare `while (GlobalCount() < target && StepAllActive()) { }` loop had no way to notice and would spin
forever. Fixed by folding both the warmup and measure loops into one `RunUntil(target)` local function that
also bails out after `StallTickLimit` (100,000) consecutive ticks with zero global instruction-count
progress across every hart — generous enough that no legitimate detailed-pipeline stall (cache misses,
etc.) could ever trip it, since `stalledTicks` resets the moment *any* hart retires *anything*, machine-
wide. `Tests/Pipeline/MultiHartWarmupMeasureDriverDeadlockTests.cs` proves it's genuinely load-bearing: a
hart blocked forever on an `ecall` whose only possible waker halts almost immediately times out at 15s with
the fix reverted, completes in well under a second with it restored.

This closes the first of the two residuals that stood between "all six trains have `RequestBlock`" and
working multi-hart LoopPoint measurement — but not without leaving a known gap of its own: `RunUntil`
returns identically whether it stopped because the target was reached, every hart halted first, or the
stall limit fired, and the caller can't yet tell those apart. Closing the second residual (below) turned
out not to need resolving this directly — the concrete risk it was guarding against (a hart driven from
invalid state polluting the global count) is instead caught by `MeasureLoopPointCheckpoints`'s own
fail-loud out-of-range-PC guard. A three-outcome `RunUntil` return remains a real, smaller gap (a
genuinely-live hart deadlocked against another live hart in the same region would still silently truncate
rather than throw), tracked in `TODO.md`.

**Real multi-hart `--looppoint` measurement (Apps/Runner/Program.cs, Analysis/MultiHartLoopPointExperiment).**
The CLI's `--looppoint` wiring built exactly one `IMechanism`/`MultiHartKernel`, with zero dormant hart
slots and `LinuxSyscallEmulator.Spawner` never assigned — `clone()` threw the instant a real multi-threaded
ELF called `pthread_create`. Fixed with a new `--looppoint-max-harts <n>` flag (default 1, so every
existing single-hart invocation behaves identically): it pre-allocates `n` mechanisms, each built with its
own `hartId` (mirroring `PthreadProbeTests`' reference construction — `clone()`'s returned tid is the
`SpawnHart` slot index, which only agrees with a later `gettid()` if the slot's own mechanism carries the
matching id), activates only hart 0, wires `handler.Spawner = kernel`, and carves an mmap arena out of
workload memory (for `pthread_create`'s thread-stack mmaps) only when `n > 1`.

Running this against a real `pthread_create`/`pthread_join` binary (`pthread_probe.elf`) surfaced a second,
more serious bug — found empirically, not by reasoning about it in advance: a region-boundary checkpoint
taken before a hart is ever `clone()`d (or after it halts) captures that hart's untouched construction-time
state (PC 0, all-zero registers). `MeasureLoopPointCheckpoints` restored and drove a full detailed-pipeline
train for it regardless, fetching and retiring whatever bytes happened to sit at PC 0 as real code —
measured directly: 924 phantom-retired instructions per never-spawned hart on this fixture's own pre-run
checkpoint, polluting the exact global instruction count `MultiHartWarmupMeasureDriver` uses to bound the
measurement window. Fixed with a new `LoopPointCheckpointSet.RepresentativeLiveHarts` field, captured per
region from a new `MultiHartKernel.IsActive` (not dormant and not halted); `MeasureLoopPointCheckpoints` now
builds a real detailed-pipeline train only for harts live at that region's start, and throws if a hart
marked live still has a checkpointed PC outside the workload's mapped memory rather than silently measuring
garbage. Proven with real end-to-end tests against `pthread_probe.elf`
(`Tests/RiscV64/Analysis/MultiHartLoopPointExperimentRealElfTests.cs`) — reverting the liveness filter and
PC guard *together* and confirming the failure is the intended one: `HartResults.Length` genuinely comes
back wrong (`Assert.Single` fails, with the dial board showing the exact 924-phantom-retirement bug again),
not the PC guard's exception masquerading as the length check (the guard alone already throws on this
fixture's dormant-hart PC 0x0 before the length assertion is ever reached, so reverting only one of the two
doesn't isolate the other). A separate synthetic hand-built-checkpoint test isolates the PC guard alone,
confirmed to fail with just that guard reverted.

**Update: the tick-level ground truth this section originally lacked now exists.** It needed
`MultiHartPipeline` dynamic hart activation (a live detailed-pipeline run gaining a hart via a real
`clone()` call) plus a fix to a real `FiveStageTrain` `ecall` hazard that blocked a cold `pthread_probe.elf`
run from ever reaching its first `clone()` call — both landed in a follow-up session (see "FiveStageTrain
ecall hazard fix" above). `Tests/RiscV64/System/MultiHartLoopPointGroundTruthTests.cs` now runs
`pthread_probe.elf` cold from t=0 through `MultiHartPipeline` and compares its ground-truth tick count
against `MultiHartLoopPointExperiment`'s profile/cluster/checkpoint/measure/extrapolate estimate for the
same binary — agreement within ~6% relative error, well inside the test's 30% tolerance.

The PC guard also only catches an out-of-range PC — it doesn't catch a live hart genuinely deadlocked
against another live hart within the measured window, which still runs through `MultiHartWarmupMeasureDriver`'s
stall-limit bail-out and would silently feed a truncated tick count into Eq. 1/2 rather than throwing;
tracked as its own `TODO.md` item rather than folded into this one.

**Update: `RunUntil`'s three-outcome ambiguity is closed.** `MultiHartWarmupMeasureDriver` gained a
`RunOutcome` enum (`TargetReached`/`AllHartsHalted`/`StallLimitHit`); `RunUntil` and
`RunWarmupThenMeasure` now return it instead of leaving every caller to infer what happened from the tick
count alone. `MeasureLoopPointCheckpoints` throws only on `StallLimitHit` — not `AllHartsHalted`, which is
legitimate (a region near the workload's end can finish mid-window with real, if fewer, ticks) — since only
a genuine stall's recorded ticks are contaminated with up to `StallTickLimit` phantom spin ticks charged
with zero retirement. Proven with a new synthetic checkpoint test mirroring the existing PC-guard one, but
swapping the out-of-range PC for a live hart whose `ecall` a `NeverClearingHandler` blocks forever —
confirmed to fail (no exception; the stall silently produces a real but garbage `RevolutionResult`) with
the throw temporarily removed, then pass restored. The real-ELF LoopPoint suites
(`MultiHartLoopPointExperimentRealElfTests`, `MultiHartLoopPointGroundTruthTests`) were re-run with the
throw in place to confirm no well-behaved `pthread_probe.elf` region now spuriously throws.

**`RequestBlock` support in `SingleCycleTrain` (Pipeline/SingleCycleTrain.cs).** The seventh and last train
identified as needing this. It shares `SmtTrain`/`MultiHartKernel`'s "one instruction fully completes per
call" model — no pipeline latches at all — making this the simplest translation of the seven:
`ExecuteOneCycle` charges the cycle as usual (real hardware time genuinely passes even on a blocked retry)
but, right after that, skips writeback/retire/PC-advance entirely and reschedules the same instruction,
mirroring `MultiHartKernel.StepHart`'s functional retry-in-place at this train's own cycle-accurate
granularity. No squash-and-refetch machinery needed — a single-instruction-at-a-time train has no younger
in-flight state to squash in the first place. `Tests/Pipeline/SingleCycleRequestBlockGuardTests.cs` mirrors
the other six trains' tests (2 single-hart stub-handler + 1 cross-hart `MultiHartPipeline` composition
test); all 3 confirmed to fail with the fix removed — not a crash, but silently wrong forward progress
(e.g. a blocked ecall's handler called once instead of the expected four times) — then pass restored. This
closes the bug class for every functional (non-timing) driver path that runs through this train too
(SMARTS's fast-forward pass, plain single-hart bare-metal runs), not just the six detailed pipeline trains.

**Runtime extrapolation + `--looppoint` CLI (Pipeline/LoopPointRuntimeExtrapolation,
Analysis/MultiHartLoopPointExperiment).**
`LoopPointRuntimeExtrapolation` implements the paper's Eq. 1/2: `ComputeMultipliers` takes a `SimPointResult`
(computed from `MultiHartLoopPointProfiler.RegionBbvs`) plus `RegionInstructionCounts` (the new property
feeding it — each region's global filtered instruction count, same index order as `RegionBbvs`) and returns,
per representative region, the ratio of its cluster's total filtered instructions to its own — deliberately
not `SimulationPoint.Weight`, which assumes every interval is the same length (true for SimPoint's fixed
intervals, false for LoopPoint's data-dependent regions). `ExtrapolateTotalRuntime` then sums each
representative's own measured runtime, multiplier-weighted, into one whole-run estimate.

`MultiHartLoopPointExperiment` (`src/Isa/RiscV32/Analysis/`) is the orchestrator gluing all of the above
together, in the same capture-then-measure two-phase shape as `Experiment.CaptureSimPointCheckpoints`/
`MeasureSimPointCheckpoints`, but structurally different in one way: SimPoint's checkpoint targets are known
before profiling starts (fixed multiples of `intervalSize`), so a cheap second pass captures only the
pre-computed representative points. LoopPoint's region boundaries are data-dependent — unknowable until the
profiling pass actually reaches them — and which regions turn out representative isn't known until
clustering runs over the *complete* pass. So `CaptureLoopPointCheckpoints` captures every region boundary's
`MultiHartCheckpoint` opportunistically during the single profiling pass, via a new `onRegionBoundary` hook
on `MultiHartLoopPointProfiler` (fires after each region closes, with the about-to-start region's index —
region 0's checkpoint, the pre-run state, is the caller's own responsibility since no boundary fires for
it), then discards every non-representative one once `SimPointAnalysis.Analyze` picks representatives.
`MeasureLoopPointCheckpoints` restores each kept checkpoint onto fresh detailed trains and drives them with
`MultiHartWarmupMeasureDriver`, then feeds the per-representative tick counts into
`LoopPointRuntimeExtrapolation`.

**A real, once-shipped bug found and fixed along the way**: detailed pipeline trains (`FiveStageTrain`,
confirmed; `OooTrain` has the identical shape) track their fetch address in a field completely separate from
`IArchState.Pc` — `FetchStage.Pc`, seeded once from the train's constructor `entryPoint` argument and never
re-read afterward. `MultiHartCheckpoint.RestoreInto` (like `ArchitecturalCheckpoint.RestoreInto`) only
mutates `IArchState`, so restoring a checkpoint into an already-constructed train with the wrong
`entryPoint` doesn't just fail to fetch from the right address — it livelocks silently, fetching from
wherever the train happened to be constructed to start, forever, with zero commits and zero errors.
`Experiment.MeasureSimPointCheckpoints` already avoided this (it threads `ArchitecturalCheckpoint.Pc`
through as the detailed train's `entryPoint`); `MultiHartLoopPointExperiment`'s first draft didn't, since
`MultiHartCheckpoint` had no public per-hart PC accessor. Fixed by adding `MultiHartCheckpoint.PcOf(hartId)`
and threading it through `MeasureLoopPointCheckpoints`'s `detailedTrainFactory` callback (now `Func<IMechanism,
IMemory, ulong, InstructionCounter, ISteppableTrain>`, PC included, mirroring `Experiment`'s own
`detailedTrainFactory` shape exactly). Caught by a genuinely discriminating test — not "did it produce some
ticks" (a wrongly-defaulted hart fetching another hart's still-valid, self-terminating loop code would pass
that trivially), but "did each hart's detailed train actually restart fetch inside *that hart's own* address
range" — confirmed to fail under the reverted bug and pass with the fix before being kept.

**Validated against a real compiled binary.** `simpoint_kernel.elf` through `--looppoint 20000
--looppoint-warmup 2000 --looppoint-argv ""` estimates 4,736,068 total ticks; a full, cold `FiveStageTrain`
run of the same binary to completion measures 4,738,700 — a 0.06% difference, well beyond wiring-level
proof that the whole capture→cluster→measure→extrapolate pipeline is numerically sound end-to-end, not just
"runs without throwing."

`--looppoint`/`--looppoint-warmup`/`--looppoint-argv` mirror `--simpoint`/`--simpoint-warmup`/
`--simpoint-argv`'s shape but require `--looppoint-argv` unconditionally (no bare-metal HTIF fallback —
region-boundary detection and spin-loop exclusion need a real ELF's symbol table); `--looppoint-max-harts`
(default 1) pre-allocates real multi-hart `clone()` support, described above. Measurement is hardcoded to
the `five_stage` pipeline (no `--sweep` support yet) — `MultiHartCheckpoint` restore + `MultiHartWarmupMeasureDriver`
have only been proven against `FiveStageTrain`.

