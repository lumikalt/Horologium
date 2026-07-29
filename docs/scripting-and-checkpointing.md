# Architecture Scripting and Checkpointing

`ScriptHost.EvaluateFileAsync(path)` compiles and evaluates a `.csx` (Roslyn C#) or `.fsx` (F# Interactive) script file
whose last expression is a `MachineSpec`. All `Pipeline.Spec`, `Orrery.Spec`, `Orrery.Cache`, `RiscV32`, and `RiscV64`
namespaces are pre-imported — no `#r` directives or `using`/`open` statements needed, so a script can construct
`Rv64Mechanism()` exactly like `Rv32Mechanism()`. The result can be passed directly to `MachineSpec.Build()`:

```csharp
// example.fsx
let pipeline = FiveStageSpec(ForwardingEnabled = true)
let l1 = CacheLevelSpec(4096, 4, 64, 10)
let cache = CacheHierarchySpec.Unified(CachePathSpec([| l1 |]))
MachineSpec(pipeline, (fun () -> Rv32Mechanism()), cache)
```

The Runner exposes this as `--script <file.csx|fsx>`. `--xlen 32|64` (default 32) selects RV32I or
RV64I for the workload *and* the mechanism factories used across every Runner mode — the default
sweep, `--simpoint`, `--trace-json`, `--elastic-record`, `--stf-record`, and `--script` (including
`--roi-start`/`--checkpoint-save`/`--checkpoint-load`); `--xlen 64` requires an ELF path (the
built-in demo program is RV32-only), except `--checkpoint-load`, which restores its own memory/PC
from the checkpoint and never touches the workload. Two script handoff patterns are supported:

**Symbol-based region-of-interest (ROI)** — name ELF symbols to bracket the measurement window. The Runner fast-forwards
functionally (single-cycle) until the start symbol's PC is committed, then restores state into the script's pipeline and
runs the detailed model until the end symbol or `--max-ticks`:

```bash
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx \
  --roi-start roi_begin --roi-end roi_end \
  --max-ticks 5000000
```

`--roi-start` requires an ELF workload with a matching symbol. `--roi-end` is optional; omit to run until `--max-ticks`.
The fast-forward phase uses `SingleCycleSpec` regardless of what the script specifies; the script's pipeline is used
only for the detailed ROI phase.

**Manual checkpoint handoff** — save and restore state across separate Runner invocations with `--checkpoint-save` and
`--checkpoint-load`:

```bash
# 1. Fast-forward 100 M instructions on a single-cycle model, save state.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/single_cycle.fsx --max-ticks 100000000 --checkpoint-save fast.chk

# 2. Resume from the checkpoint on a detailed OoO model.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx --checkpoint-load fast.chk --max-ticks 10000000
```

**`ArchitecturalCheckpoint`** (`src/Core/Mechanism/ArchitecturalCheckpoint.cs`) is the serialization layer.
`Save(path, state, memory, tick)` writes PC, privilege level, integer/FP registers, memory, and an ISA-specific blob (
CSRs, VRF, UVE scalar state via `IArchState.WriteState`) to a binary file. `SaveAsync(path, state, memory, tick)`
captures the same state synchronously (safe even if the caller keeps mutating `state`/`memory` immediately after the
call returns) but defers the file write to a worker thread, returning a `Task` the caller awaits once it has no more
overlapping work; the Runner's `--checkpoint-save` path uses it. `Load(path)` deserialises without touching live state;
`chk.RestoreInto(state, memory)` applies it. `FlatMemory` implements the `ISnapshotableMemory` interface (`BaseAddress`,
`SizeBytes`, `CopyTo`, `LoadFrom`) required by the checkpoint API. `ISteppableTrain.ArchState` (default `null`) exposes
the committed hart state after or during a run; `MachineHandle.ArchState` forwards it. ROI uses an in-memory checkpoint
internally (no file I/O); both `--checkpoint-save` and the ROI path can be combined to persist the post-ROI state.

**Instruction-count-bounded warmup/measurement.** `Train.Run`'s own warmup/measure split (and the ROI/checkpoint-load
flows above) are tick-bounded; SimPoint-style sampling needs boundaries in dynamic instruction counts instead.
`InstructionCounter` (`src/Core/Mechanism/InstructionCounter.cs`) is an `ICommitObserver` that counts commits and,
given an ascending list of target counts, fires a callback as each is crossed — enough to save one checkpoint per
simulation point in a single functional pass. `WarmupMeasureDriver.RunWarmupThenMeasure` (`src/Core/Pipeline/`) then
steps a train (built with that counter as its `CommitObserver`) through an unmeasured warmup phase, snapshots a
baseline via the new `ISteppableTrain.SnapshotDials()`, steps through the measured phase, and returns the
baseline-subtracted result via the new `FinishStepping(baseline)` overload — the stepping-API equivalent of `Run`'s
own tick-based warmup, needed because a train's lifecycle is one-shot (`Reset()` would wipe warmed-up
microarchitectural state along with the counters). Only `SingleCycleTrain`, `FiveStageTrain`, and `OooeTrain`
implement `SnapshotDials`/baseline-`FinishStepping` — the trains that already support a commit observer. Building
this surfaced a real, previously undiscovered bug: `OooeTrain`'s physical register file started zeroed at
construction and was never re-seeded from `ArchState.IntegerRegisters`, so a checkpoint restored into an OoO train
was silently invisible to execution (`Wind()` now re-seeds it; a fresh, never-restored `ArchState` seeds zeros, so
ordinary runs are unaffected).

**SimPoint-interval → checkpoint glue.** `Experiment.CaptureSimPointCheckpoints`/`MeasureSimPointCheckpoints`
(`src/Isa/RiscV32/Analysis/Experiment.cs`) compose the three pieces above into the full SimPoint sampling workflow,
split into a config-independent capture half and a config-dependent measure half. `CaptureSimPointCheckpoints`
profiles the workload (`ProfileSimPoints`) and saves one checkpoint per simulation point in a single second
functional pass (`InstructionCounter`'s target-callback mode — interval-0 targets are captured from the pristine
pre-`StepCycle` state, since the callback itself only fires after a commit, one instruction too late for a target of
exactly zero), returning a `SimPointCheckpointSet`. `MeasureSimPointCheckpoints` restores each of its checkpoints
into a fresh detailed train (built by a caller-supplied factory) and measures via `WarmupMeasureDriver`; per-point
warmup is clamped to `min(warmupInstructions, intervalStart)` so an early interval's measured window is never
shifted off the interval it represents. Per-point CPI (not IPC — SimPoint intervals are equal-length, so CPI is the
domain a weighted mean is valid in) is combined by `SimulationPoint.Weight` into a whole-program estimate.
`RunWithSimPointCheckpoints` is a thin wrapper calling both halves, kept for the single-config case.
`SimPointCheckpointSet` also carries the profiling pass's own `TotalInstructions` count (from `BbvProfiler`), so a
caller doesn't need a second, separate `ProfileSimPoints` call just to report it. The Runner exposes this as
`--simpoint-warmup <n>`, run once per `--sweep` config (skipping pipelines other than ooo/five_stage/single_cycle,
which are the only ones `ISteppableTrain.SnapshotDials`/baseline-`FinishStepping` support) — the `--sweep` loop calls
`CaptureSimPointCheckpoints` once and `MeasureSimPointCheckpoints` per config, instead of repeating the (expensive,
at SPEC-scale interval counts) profiling+capture pass for every config the way calling `RunWithSimPointCheckpoints`
in the loop used to, and the profile report reads its instruction count off that one capture instead of a second,
separate `ProfileSimPoints` call — exactly one profiling pass total, in every mode. On
`TestBinaries/simpoint_kernel.elf` (742 intervals) an 8-config sweep went from not finishing in 90 s to ~18 s.
Building the original glue surfaced a second real bug:
`Train.FinishStepping(baseline)` returned the *absolute* Escapement tick instead of ticks-since-baseline, which
`WarmupMeasureDriver`'s zero-warmup callers never noticed but inflates every per-point CPI once warmup > 0 — fixed
by recording the tick at `SnapshotDials()` and subtracting it in `FinishStepping(baseline)`.

**Validated against a real compiled binary (`TestBinaries/simpoint_kernel.c`/`.elf`).** `ProfileSimPoints` and
`RunWithSimPointCheckpoints` gained optional `argv`/`wordSize` parameters so their functional passes can inject a real
psABI initial stack (`InitialStackBuilder`) instead of only bare-metal entry — the first time the *sampling* machinery
itself has been checked directly against genuinely compiled code rather than a hand-assembled probe. This is now
wired into the CLI too: `--simpoint-argv "<args>"` opts a single `--simpoint`/`--simpoint-warmup` workload into
Linux-ABI entry (the psABI stack above, plus a `Func<IMechanism>` that builds a fresh `LinuxSyscallEmulator` on every
call — mirroring `--bench-config`'s factory shape, since the emulator carries mutable per-run state) instead of the
bare-metal HTIF entry every other mode uses; omitting the flag keeps that other-modes behavior byte-for-byte
unchanged. Captured output is discarded — this mode estimates CPI/IPC, not output; see `--bench-config` for
output-checked runs.
`Tests/RiscV64/System/RealLinkedSimPointTests.cs` uses `simpoint_kernel.elf` — static arrays (no `malloc`, so no
`brk`/`mmap`) with one `printf` at the very end — and an independent commit-trace pass to verify every *selected*
simulation point's warmup+measure window is syscall-free except the two edge phases (startup/shutdown), which always
contain ECALLs by construction. Building this
surfaced a third real, independent bug — not part of the checkpoint machinery, reproduced on a plain straight-through
`OooeTrain` run too: `LinuxSyscallEmulator`'s ECALL handler reads/writes guest memory through the same `IMemory`
`OooeTrain` uses for real loads/stores, and `ExecResult.HasLoadAccess`/`HasStoreCapture` were derived unconditionally
from that memory wrapper's flags — any memory-touching ECALL (`write`/`writev`'s buffer read, `fstat`/
`clock_gettime`/`getrandom`'s struct write) was misread as owning a load/store-queue entry it was never allocated,
corrupting `_lq`/`_sq` indexing (`IndexOutOfRangeException`). Fixed by routing System-class instructions through the
same direct, non-speculative `DLayers.Accessor` path Vector/UVE already use (they're equally head-serialized), rather
than through the deferred-write `CapturingMemory` wrapper. Two regression tests in `InitialStackTests.cs`, verified
via revert-and-recheck. This surfaced two further hazards, both since fixed. First, a younger load could issue before
a head-serialized ECALL that writes overlapping memory and read stale data — `HasPrecedingVectorStore`'s vector-store
precedent never added ECALL to its blocking set. A widened-race-window regression test (a long dependent add-chain
ahead of the ECALL, so the race is deterministic rather than luck-of-scheduling) confirmed this was a real,
reproducible bug, not the rare case an earlier probe's inconclusive pass had suggested. Fixed via
`ITooth.MayAccessArbitraryMemory` (true only for ECALL), checked alongside the vector-store case. Second, a separate,
pre-existing gap: `stdin_echo64.elf` under `OooeTrain` produced empty output, because ECALL's result is delivered via
`SideEffect` at Commit and `RvEcall` never has a `DestinationRegister`, so it never participates in the RAT/PRF at
all — a younger consumer of a0 could resolve to whatever produced a0 *before* the syscall. Fixed the same way as
Zacas `amocas.d`'s register-pair high half: `RvEcall.SecondaryDestinationRegister = 10` (a0), reusing the existing
class-agnostic `HasPendingSecondaryDest` dispatch stall and `CommitRegisters` PRF sync with no pipeline-stage changes.
Both fixes verified via revert-and-recheck in `InitialStackTests.cs`; see TODO.md.

**Full syscall-emulator-state checkpointing.** `ArchitecturalCheckpoint` only ever covered guest architectural
state (registers, memory, ISA blob) — a `LinuxSyscallEmulator`'s own mutable state (brk/mmap cursors, fd table,
stdin position, plus the deterministic clock/PRNG cursors) lives outside that and needed its own capture/restore
path so a checkpoint landing mid-syscall-emulation restores faithfully instead of resetting to a fresh handler.
`ICheckpointableSyscallHandler` (`Mechanism`) adds `WriteState(BinaryWriter)`/`ReadState(BinaryReader)`, which
`LinuxSyscallEmulator` implements: the fd table serializes path + access mode + current position per open fd
(reopened with `FileMode.Open`, never truncating, regardless of how the fd was originally created) and stdin
position is either `Seek`'d or fast-forwarded by discarding bytes depending on whether the caller's redirected
stream is seekable. `IMechanism` exposes the resolved handler as `SyscallHandler` (default `null`; `Rv32Mechanism`/
`Rv64Mechanism` read it off whichever `Rv32Executor` is currently wired in, since `Executor` is settable post-
construction). `Experiment.CaptureSimPointCheckpoints`/`MeasureSimPointCheckpoints` capture/restore this alongside
each point's `ArchitecturalCheckpoint`, in a new `SimPointCheckpointSet.SyscallStates` array (null at any index
whose mechanism has no checkpointable handler — e.g. bare-metal HTIF workloads). `Tests/RiscV32/System/
SyscallCheckpointTests.cs` proves the round-trip directly (brk continuation, a reopened fd landing at the right
position, stdin continuing past what a fresh stream over the same content already delivered, and getrandom/
clock_gettime continuing their sequence rather than repeating it — each checked against an independent reference,
not a self-consistent recomputation); `Tests/RiscV32/Analysis/SimPointCheckpointTests.cs` drives a hand-assembled
brk-extend-then-query program through `MeasureSimPointCheckpoints` itself and shows the query only sees the
extended break when the syscall state was actually restored, with a same-shape control that omits the restore and
gets the stale answer. `simpoint_kernel.elf`'s own ECALLs (`set_tid_address`/`ioctl`/`writev`/`exit_group`) turned
out to be state-inert w.r.t. every cursor tracked here — a static-array, no-`malloc` kernel, by design — so
`RealLinkedSimPointTests.cs` only proves the wiring fires against real compiled code (`SyscallStates` populated,
`MeasureSimPointCheckpoints` restores without error), not the semantic case; that's what the two unit-level tests
above are for. mmap-cursor and stdin-position serialization is exercised only at the unit level: the
`--simpoint-argv` CLI path always builds its `LinuxSyscallEmulator` with `mmapBase=0`/`input=null` (mmap disabled,
stdin always EOF) on both the capture and measure side, a separate pre-existing limitation of that specific CLI
wiring, so those two fields are currently serialized-but-unexercised there rather than validated end-to-end.

**Warm microarchitectural checkpoint (`MicroarchitecturalCheckpoint`, `OooeTrain.Drain`/`SaveMicroCheckpoint`/
`RestoreMicroCheckpoint`).** `ArchitecturalCheckpoint` above is enough to resume *correctly*, but a freshly-loaded
`OooeTrain` starts with cold caches/TLBs/branch predictor — the warmup a per-interval SimPoint sampling flow already
pays around it. This layers a second checkpoint on top that also carries the trained tables across, so a reload can
skip that warmup. It only applies at a **drained** boundary (no in-flight instructions anywhere in the pipeline) —
mirroring gem5's own `drain()`-before-`serialize()` precedent — because `RobEntry.SideEffect` is a raw
`Action<IArchState>` closure that cannot be generically serialized; at a drained boundary the ROB/issue
queues/load-store queues/decode-rename latches/exec-CDB buffers are all empty by construction, so there's nothing
closure-bearing left to capture. `OooeTrain.Drain(maxTicks)` stops admitting new fetches and steps until the
pipeline empties (or throws if it doesn't within `maxTicks`, or if the train halts first). `SaveMicroCheckpoint`
then writes an `ArchitecturalCheckpoint` plus tagged sections for each configured I/D cache (all levels), I/D TLB,
the branch predictor, and the RAS (both the speculative and committed-shadow copies) — each section is
length-prefixed and, for the branch predictor and each cache's replacement policy, additionally tagged with the
concrete type name, so a restore into a differently-configured train (missing a level, or a different
predictor/policy type) skips the mismatched section and cold-starts it instead of throwing or feeding it foreign
bytes. `RestoreMicroCheckpoint` must be called with the same `entryPoint` convention as `ArchitecturalCheckpoint`
(pass the checkpoint's PC to the constructor — `RestoreInto` only writes `ArchState.Pc`, not the pipeline's internal
fetch-PC latch). Caches/TLB/predictor gained `WriteState`/`ReadState` via the same default-no-op-then-override
pattern as `IArchState` (`IBranchPredictor`, `IReplacementPolicy`, `IValuePredictor`, `ICriticalityPredictor`);
`SetAssociativeCache`, `Tlb`, `NBitBp`, `StoreSetPredictor`, `SmbPredictor`, `RdipPrefetcher`,
`TokenPassingCriticalityPredictor` implement real bodies, as do all five `IValuePredictor` implementations
(`LvpVp`, `StrideVp`, `VtageVp`, `HybridVp`, `DynamicClassificationVp`) and eight `IReplacementPolicy`
implementations (`LruPolicy`, `FifoPolicy`, `MruPolicy`, `ClockPolicy`, `PlruPolicy`, the `RripPolicyBase`
family — `SrripPolicy`/`BrripPolicy`/`DrripPolicy` — `ShipPolicy`, and `HawkeyePolicy`) — `RandomPolicy`
(RNG-only state) and `RtlFfiReplacementPolicy` (native-owned state) cold-start by design, matching the same
FFI/RNG exclusion already established for the branch predictor side. Composed predictors (`HybridVp`,
`DynamicClassificationVp`) hold no state of their own beyond what they delegate to their two component
predictors' own `WriteState`/`ReadState`. Every `IBranchPredictor` implementation now has a real
`WriteState`/`ReadState` except `OracleBp` (trace-driven, no table), `StaticBp`'s stateless variants, and
`CbpFfiBp`/`CbpNgFfiBp`/`CbpNgCommitDrivenBp` (FFI-owned native state) — see the BP zoo section below for
the full breakdown. `FdipPrefetcher` is deliberately excluded entirely: it has no
trained table, only a lookahead FTQ that rebuilds itself within `ftqCapacity` cycles of `Wind()` regardless, so
there's nothing worth carrying over. `PhysicalRegisterFile`/`RenameMap` are deliberately not serialized: at a
drained boundary they carry no information the architectural register values (already covered by
`ArchitecturalCheckpoint`, restored into the PRF by the existing `Wind()` re-seed) don't already reconstruct.
`MSHR`/write-back-buffer/victim-buffer/in-flight-prefetch state in `SetAssociativeCache` is not serialized either —
`WriteState` throws if any of it is non-empty at save time, rather than silently dropping it, since a proper drain
leaves it empty for every config this covers today.

**The key design rule for the OoO-side predictors** (found while extending past the vertical slice): serialize
only tables keyed by PC, address, or other *content*; skip anything keyed by, or compared against, a monotonic
per-train counter (InstrId/SeqNo/commit count) — those restart at 0/1 on a freshly restored train, so a
carried-over counter value can never match again. `StoreSetPredictor`'s SSIT (PC → store-set) is safe and
serialized; its LFST (keyed by store `SeqNo`) is deliberately left cold — at a drained boundary every tracked
store has already issued, so a fresh (all-zero) LFST is the *correct* state, not an approximation, and carrying a
stale SeqNo over could stall a load's dependence prediction forever. `TokenPassingCriticalityPredictor` follows
the same rule: only its PC-indexed hysteresis table (`_cpTable`) is serialized; its ROB-slot/token/commit-counter
bookkeeping is not. `SmbPredictor` (distances are *relative* SSN deltas, not absolute SeqNos) and `RdipPrefetcher`
(everything is keyed by call-stack signature or physical address) needed no such split — both serialize wholesale.
`StrideVp`'s `_inFlight` (a per-PC renamed-but-uncommitted occurrence counter, incremented at predict/rename and
decremented at update/commit) follows the same category and is skipped — verified, not just asserted, by a
temporary instrumented build that logged any nonzero entry inside `WriteState`, producing no output across its
equivalence test. The rule has a real trap, though: `DynamicClassificationVp`'s `_armed`/`_missStreak` fields
*look* like the same kind of transient counter but aren't — `Update` (the commit-time call) never clears them,
only eviction/reclassification/a squash do, so a PC's "has this component ever predicted confidently since
classification" bit is real trained state that must round-trip, not a per-instance in-flight depth that resets
every commit. Both are serialized. Hawkeye's `_absTime`/`_absLineTime` counters look similar to the SeqNo case at
a glance but are safe to serialize wholesale for a different reason: they're self-referential (every comparison
is between values produced and restored by the same policy instance, never checked against another component's
independently-resetting counter), the same property that already let `SmbPredictor`'s relative deltas serialize
wholesale.

**The BP zoo (complete).** Every equivalence test up to this point used `NBitBp` or a stateless
`AlwaysNotTaken` predictor, neither of which carries global-history state — so before this pass, history
serialization through a *live pipeline* (as opposed to a standalone unit-level round trip) had never
actually been exercised, which is exactly the kind of thing a counter-keyed bug hides in.
`SpeculativeGlobalHistory`/`SpeculativeLocalHistory` — the shared speculative/committed history-shadow
helpers most predictors in `Mechanism.BranchPred` are built on — gained `WriteState`/`ReadState` once
(mirroring RAS/CRAS and `VtageVp`'s pair), benefiting every predictor built on them for free.

Work started scoped to three representative families (TAGE via `LTageBp`, perceptron via
`HashedPerceptronBp`, local/global-hybrid via `TournamentBp`) — a user decision to avoid a multi-session
exhaustive grind — but a follow-up ask ("do them now since it's relevant") extended it to every remaining
predictor. `LTageBp.WriteState`/`ReadState` (made `virtual`) serializes the bimodal base, tagged TAGE
tables, loop predictor, BTB, and both history shadows; per its own `CaptureHistory` doc comment, every
folded-history index is recomputed on the fly from the raw GHR, so restoring the raw register alone keeps
every derived index consistent. Every class that extends `LTageBp` or `TageScLBp` — `TageScLBp` itself
(the Statistical Corrector, also the base of eight other predictors), `BatageBp`, `BullseyeBp`,
`MultiperspectivePerceptronBp`, `LlbpBp`/`LlbpXBp`, `TeaBp`, `LvcpBp`, `RunltsBp`, `VlaTageBp` — overrides
`WriteState`/`ReadState` to call `base` first (the inherited TAGE/loop/history state) and then add only
its own layered tables. Every remaining standalone predictor also got a real implementation:
`PerceptronBp`, `CorrelatedBp`/`GselectPredictor`/`GshareBp`, `IttagePredictor` (ITTAGE — confirmed
wireable as a standalone `IBranchPredictor`, per its own doc comment, before including it), and
`ImliPredictor`. Two composed-baseline predictors needed individual attention rather than the established
pattern, exactly as flagged before starting: `BranchNetBp` holds a `TageScLBp` *field* rather than
extending it, so its `WriteState` delegates to `_baseline.WriteState` explicitly instead of calling
`base`; its per-branch CNN models are an offline-training artifact frozen at construction (the same
"shape, not state" category as `TeaBp`'s dependence chains) and are not serialized. `HypreBp`
(hyperdimensional/sparse-distributed-memory) is a genuinely different data structure — HD vectors
(a saturating-counter array plus a derived sign-bit array) rather than any table shape used elsewhere in
this codebase.

Only two predictors needed their own full pipeline equivalence test, rather than a unit-level round trip:
`LTageBp` (an alternating-parity branch pattern — `beq` on a loop counter's parity bit — a plain bimodal
counter can never learn but a history-indexed predictor can, forcing real tagged-table allocations before
the checkpoint) and `ImliPredictor` (the one standalone predictor with genuine speculative-vs-committed
*counter* semantics, distinct from the history-*register* case `LTageBp` already proves — it reuses the
same alternating-parity program, since the outer loop branch is itself a real backward taken branch
driving the IMLI counter and the inner branch's PHT index depends on it directly). Every other predictor
gets a unit-level round trip using a shared generic helper: train with a varying, biased pattern: trained
must equal restored, *and* trained must be distinguishable from a fresh cold instance (the anti-theater
check). The bias direction and iteration count aren't universal — different predictors default cold to
different directions (all-zero-weight perceptrons default "taken"; saturating counters initialized
"weakly not-taken" default the other way), and `HypreBp`'s 1024-bit HD vectors need hundreds of
repetitions to move their Hamming-distance threshold, not the two dozen every other predictor needs — both
discovered by running the anti-theater assertion and watching it fail, not by reasoning it through in
advance.

`Tests/Pipeline/MicroCheckpointTests.cs` has per-table round trips (where the type is `public` and cheap to
construct standalone) plus equivalence tests: drain a running train mid-program, save, and continue it as the
reference (`Drain` never discards in-flight work, only delays new fetches by a few cycles, so this is a faithful
continuation); separately reload the checkpoint into a fresh train and run to completion. The main D-cache/
predictor/RAS equivalence test asserts final architectural state, ticks, retired-instruction count, cache miss
count, and branch-misprediction count match exactly; cache hit count is allowed a ±1 tolerance for wrong-path
(later-squashed) memory accesses right at a branch-resolution boundary, whose exact count depends on cycle-exact
pipeline occupancy that a drained-boundary checkpoint doesn't claim to preserve — confirmed by direct tag/data/age
snapshot comparison that the restored cache table itself is bit-identical at the checkpoint instant. The
`StoreSetPredictor` equivalence test is deliberately adversarial (a store address delayed behind a long dependency
chain racing an immediately-ready load address, to reliably trigger a real memory-order violation) and needs its
own small tolerance on ticks/violation count for the same wrong-path-timing reason — but asserts `retired` matches
exactly and that the divergence stays bounded rather than scaling with remaining loop iterations, which a genuine
stale-SeqNo stall bug would do. `StoreSetPredictor` and `SmbPredictor` are `internal`, so their equivalence tests
are the only reachable verification for them (no unit-level round trip is possible from the test project).
`StrideVp` (a genuinely exercisable equivalence case — a monotonic-counter loop, the pattern its own doc comment
builds and measures against, since `LvpVp`/value-repetition predictors can never predict it), `LTageBp`, and
`ImliPredictor` get a full drain/save/restore/continue pipeline equivalence test each; every other newly-covered
predictor/policy/value-predictor gets a unit-level (optionally history/cold-baseline-checked) round trip. 55
tests total.

**Runner CLI wiring**: `--checkpoint-save-micro <path>`/`--checkpoint-load-micro <path>`, mirroring the
architectural-only `--checkpoint-save`/`--checkpoint-load`. Both require `--script` and an OoOE pipeline train —
a non-OoOE train prints a warning and skips the save (or falls back to an architectural-only restore on load)
rather than throwing. The load side has to parse the checkpoint's `Architectural.Pc`/memory geometry *before*
`spec.Build()` constructs the train (an `OooeTrain`'s fetch PC is fixed at construction, same convention as
`ArchitecturalCheckpoint`), so it reads the file into a `MemoryStream` once and replays it into
`RestoreMicroCheckpoint` rather than re-reading from disk twice. The save side drains at the simplest possible
trigger — the end of the run — since the CLI has no way to name a mid-run boundary; a run that reached program
exit (rather than being cut short by `--max-ticks`) has already halted with nothing left to drain, so that case
prints a message and skips rather than crashing on `Drain`'s `InvalidOperationException`. Manually smoke-tested
end-to-end (save mid-run, reload into a fresh train, confirm ticks/PC/register/cache-hit continuity) rather than
covered by an automated Runner-process test — no existing test in this repo spawns the Runner CLI as a
subprocess, and the underlying `Drain`/`SaveMicroCheckpoint`/`RestoreMicroCheckpoint` API this wiring calls is
already covered by the 55 tests above.

Not yet done: nothing outstanding for the Option B predictor/policy surface — every `IValuePredictor`,
`IReplacementPolicy`, and non-excluded `IBranchPredictor` implementation now has a real checkpoint. The
Runner CLI wiring above is the only remaining entry point that isn't covered by an automated test (manual
smoke-test only — see its own paragraph for why).

