#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32.Analysis;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.System;

/// <summary>
///     Tick-level ground truth for <see cref="MultiHartLoopPointExperiment" />'s estimate, the
///     multi-hart sibling of <see cref="RealLinkedLoopPointTests" />: a cold, cycle-accurate run of
///     the real, genuinely multi-threaded <c>pthread_probe.elf</c> (real <c>clone()</c>/<c>futex()</c>)
///     from t=0 through <see cref="MultiHartPipeline" />'s dynamic hart activation, compared against
///     <see cref="MultiHartLoopPointExperiment" />'s profile/cluster/checkpoint/measure/extrapolate
///     estimate for the same binary.
///     <para>
///         This is the test <see cref="Tests.RiscV64.Analysis.MultiHartLoopPointExperimentRealElfTests" />'s
///         own class doc comment names as out of reach — it required <c>MultiHartPipeline</c> dynamic
///         hart activation (a real <c>clone()</c> spawning a hart onto a live detailed-pipeline run,
///         committed separately) plus a fix to a real, independently-discovered bug: <c>FiveStageTrain</c>'s
///         <c>ecall</c> dispatch used to read its implicit <c>a0</c>-<c>a5</c>/<c>a7</c> arguments
///         straight from architectural state with no decoded operand for <c>HazardUnit</c> to stall or
///         forward on, so a still-in-flight register write immediately before an <c>ecall</c> (zero
///         instruction gap) was invisible to it — see
///         <see
///             cref="Tests.RiscV32.Pipelines.FiveStagePipelineTests.Pipeline_EcallImmediatelyAfterArgWrite_SeesWrittenValue_NotStaleState" />
///         for the isolated repro and fix. Before that fix, a cold run of this exact binary never
///         reached its first real <c>clone()</c> call at all: musl's own startup sequence tripped the
///         same hazard on some other syscall first, silently took the ENOSYS path, and left hart 0
///         spinning forever on a <c>futex</c> no other hart would ever be created to clear.
///     </para>
///     <para>
///         Tolerance is generous (30% relative) because this fixture is tiny (a few thousand
///         instructions total, not the paper's multi-hundred-million-instruction target) — LoopPoint's
///         representative-region sampling and multiplier extrapolation are approximate by design, and
///         a handful of regions gives less averaging-out of per-region variance than a real workload
///         would. Empirically, a real run of this exact test measured 6124 ground-truth ticks against
///         a 6504-tick estimate (~6.2% relative error) — 30% leaves headroom for that variance to move
///         under unrelated changes without making this test load-bearing on a specific tick count.
///     </para>
/// </summary>
public class MultiHartLoopPointGroundTruthTests {
    private const int WordSize = 8;
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");

    private static (Rv64ElfWorkload Workload, ulong MmapBase, ulong MmapLimit) MakeWorkload() {
        var workload = new Rv64ElfWorkload(PthreadProbeElf, 16 * 1024 * 1024);
        ulong mmapBase = workload.BaseAddress + 8UL * 1024 * 1024;
        ulong mmapLimit = workload.BaseAddress + 14UL * 1024 * 1024;
        return (workload, mmapBase, mmapLimit);
    }

    // Cold, cycle-accurate ground truth: MultiHartPipeline starting with exactly the main hart live,
    // the other two spawned dynamically by real clone() calls during the run — the same mechanism
    // MultiHartPipelineCloneTests proves against a hand-assembled program, exercised here end-to-end
    // against a real compiled binary's actual startup sequence.
    private static long RunColdGroundTruth(
        Rv64ElfWorkload workload,
        ulong mmapBase,
        ulong mmapLimit,
        out string output
    ) {
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, MultiHartLoopPointGroundTruthTests.WordSize, ["pthread_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(
            workload.InitialBreak, sw, MultiHartLoopPointGroundTruthTests.WordSize, mmapBase, mmapLimit
        );
        var mech0 = new Rv64Mechanism(syscallHandler: handler, hartId: 0);
        var train0 = new FiveStageTrain(mech0, mem, workload.EntryPoint);
        train0.ArchState.IntegerRegisters.Write(2, sp);

        var nextHartId = 1;

        ISteppableTrain SpawnTrainFactory(IArchState initialState) {
            var mech = new Rv64Mechanism(syscallHandler: handler, hartId: nextHartId++);
            return new FiveStageTrain(mech, mem, initialState.Pc);
        }

        var pipeline = new MultiHartPipeline(SpawnTrainFactory, train0);
        handler.Spawner = pipeline;

        RevolutionResult[] results = pipeline.Run(5_000_000);
        output = sw.ToString();
        // Wall-clock ticks for the whole cold run: every hart is stepped in the same outer tick loop
        // (see MultiHartPipeline.Run), and exit_group (RequestHaltAll) halts every hart at the same
        // final tick, so the slowest-finishing hart's own tick count is the system's ground truth.
        return results.Max(r => r.TotalTicks);
    }

    private static LoopPointResult EstimateViaLoopPoint(Rv64ElfWorkload workload, ulong mmapBase, ulong mmapLimit) {
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, MultiHartLoopPointGroundTruthTests.WordSize, ["pthread_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        var handler = new LinuxSyscallEmulator(
            workload.InitialBreak, new StringWriter(), MultiHartLoopPointGroundTruthTests.WordSize, mmapBase, mmapLimit
        );
        IMechanism[] mechanisms = [
            new Rv64Mechanism(syscallHandler: handler, hartId: 0),
            new Rv64Mechanism(syscallHandler: handler, hartId: 1),
            new Rv64Mechanism(syscallHandler: handler, hartId: 2),
        ];
        var kernel = new MultiHartKernel(mem, 1, mechanisms);
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        IReadOnlyList<(ulong Start, ulong End)> excludedRanges = SyncLibrarySymbols.ExcludedRanges(workload);
        ulong rangeEnd = workload.BaseAddress + (ulong)workload.CodeSize + 0x10000;

        // Small enough that representative regions span the 3-hart steady state, not just the
        // pre-spawn single-hart prefix — mirrors MultiHartLoopPointExperimentRealElfTests' own choice.
        LoopPointCheckpointSet captured = MultiHartLoopPointExperiment.CaptureLoopPointCheckpoints(
            kernel, mechanisms, mem, workload.BaseAddress, rangeEnd, 60,
            excludedRanges, 2_000_000
        );

        (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) FreshMechanisms() {
            var freshHandler = new LinuxSyscallEmulator(
                workload.InitialBreak, TextWriter.Null, MultiHartLoopPointGroundTruthTests.WordSize, mmapBase, mmapLimit
            );
            Rv64Mechanism[] freshMechanisms = [
                new(syscallHandler: freshHandler, hartId: 0),
                new(syscallHandler: freshHandler, hartId: 1),
                new(syscallHandler: freshHandler, hartId: 2),
            ];
            return (freshMechanisms, freshHandler);
        }

        ISteppableTrain DetailedTrainFactory(
            IMechanism m,
            IMemory runMem,
            ulong restartPc,
            InstructionCounter counter
        ) =>
            new FiveStageTrain(m, runMem, restartPc, commitObserver: counter);

        return MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
            captured, FreshMechanisms, DetailedTrainFactory, 10
        );
    }

    [Fact]
    public void ColdMultiHartPipelineRun_MatchesLoopPointEstimate_WithinTolerance() {
        (Rv64ElfWorkload groundTruthWorkload, ulong gtMmapBase, ulong gtMmapLimit) = MakeWorkload();
        long groundTruthTicks = RunColdGroundTruth(groundTruthWorkload, gtMmapBase, gtMmapLimit, out string output);

        // The cold run must actually complete the real workload, not just produce some tick count —
        // otherwise a broken run silently "agreeing" with a broken estimate would pass for the wrong
        // reason (see feedback_test_independent_reference in project memory).
        Assert.Contains("thread 1 running\n", output);
        Assert.Contains("thread 2 running\n", output);
        Assert.EndsWith("done\n", output);
        Assert.True(groundTruthTicks > 0);

        (Rv64ElfWorkload loopPointWorkload, ulong lpMmapBase, ulong lpMmapLimit) = MakeWorkload();
        LoopPointResult result = EstimateViaLoopPoint(loopPointWorkload, lpMmapBase, lpMmapLimit);

        Assert.True(result.EstimatedTotalTicks > 0);

        double relativeError = Math.Abs(result.EstimatedTotalTicks - groundTruthTicks) / groundTruthTicks;
        Assert.True(
            relativeError <= 0.30,
            $"LoopPoint estimate {result.EstimatedTotalTicks:F1} ticks vs. ground truth {groundTruthTicks} " +
            $"ticks: {relativeError:P1} relative error, expected <= 30%."
        );
    }
}