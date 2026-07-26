#region

using Mechanism;
using Orrery.Observation;
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
///     <see cref="MultiHartLoopPointExperiment" /> against the same real, statically-linked binary
///     <see cref="RealLinkedSimPointTests" /> uses (<c>simpoint_kernel.elf</c>) — a single-hart,
///     compute-heavy kernel with syscalls only at startup (musl's <c>_start</c> bookkeeping) and
///     shutdown (the final <c>printf</c>/<c>exit_group</c>), never inside the compute loop (proven
///     directly in <see cref="RealLinkedSimPointTests.EcallsOnlyOccurAtStartupAndShutdown_NotInsideTheComputeLoop" />).
///     <para>
///         LoopPoint's own machinery (<see cref="MultiHartLoopPointProfiler" />, <c>MultiHartCheckpoint</c>,
///         <c>MultiHartWarmupMeasureDriver</c>) is multi-hart by construction, but nothing about it
///         requires more than one hart — this binary drives it through a real ELF end-to-end with
///         hart count degenerating to 1, exercising every step a <c>--looppoint</c> CLI flag would
///         need (profile → cluster → checkpoint → restore → measure → extrapolate → a real ticks
///         total) without the toolchain risk of a new multi-hart pthread fixture (see
///         <c>MultiHartLoopPointExperimentTests</c>'s class doc comment for why that risk was
///         avoided). None of this binary's ECALLs are blocking syscalls (no <c>futex</c> anywhere in
///         a single-threaded static-array kernel), so <c>FiveStageTrain</c>'s <c>RequestBlock</c>
///         guard can never fire here regardless of which regions end up representative.
///     </para>
/// </summary>
public class RealLinkedLoopPointTests {
    private const int WordSize = 8;
    private static readonly string[] Argv = ["simpoint_kernel.elf",];
    private static string ElfPath => Path.Combine(AppContext.BaseDirectory, "simpoint_kernel.elf");

    private static Rv64ElfWorkload MakeWorkload() => new(ElfPath, 8 * 1024 * 1024);

    [Fact]
    public void CaptureClusterMeasureExtrapolate_OnRealBinary_DegeneratesGracefullyToOneHart() {
        Rv64ElfWorkload workload = MakeWorkload();
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, WordSize, Argv, [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        var handler = new LinuxSyscallEmulator(workload.InitialBreak, TextWriter.Null, WordSize);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        IReadOnlyList<IMechanism> mechanisms = [mech,];
        var kernel = new MultiHartKernel(mem, mech);
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        IReadOnlyList<(ulong Start, ulong End)> excludedRanges = SyncLibrarySymbols.ExcludedRanges(workload);
        ulong rangeEnd = workload.BaseAddress + (ulong)workload.CodeSize + 0x10000;
        // Sized to keep both halves of this test cheap: capture snapshots the *entire* shared-memory
        // image (workload.MemorySize, 8 MiB here) at every candidate region boundary, since which
        // region ends up representative isn't known until clustering runs over the whole pass (see
        // MultiHartLoopPointExperiment's class doc comment) — too fine an interval means too many
        // boundaries and too much snapshot churn. But each representative region is then measured on
        // FiveStageTrain (cycle-accurate, far slower per instruction than the functional profiling
        // pass), so too coarse an interval makes that measurement slow instead. 100_000 against this
        // ~3.7M-instruction kernel lands in the tens-of-regions range: cheap to snapshot, cheap to
        // measure per representative.
        const long targetGlobalInstructions = 100_000;

        LoopPointCheckpointSet captured = MultiHartLoopPointExperiment.CaptureLoopPointCheckpoints(
            kernel, mechanisms, mem, workload.BaseAddress, rangeEnd, targetGlobalInstructions, excludedRanges,
            profileMaxTicks: 8_000_000
        );

        Assert.True(captured.SimPoints.IntervalCount >= 2, "expected multiple regions across the compute loop");
        Assert.Equal(captured.SimPoints.Points.Count, captured.RepresentativeCheckpoints.Count);
        Assert.Equal(captured.SimPoints.Points.Count, captured.Multipliers.Count);
        Assert.All(captured.Multipliers.Values, m => Assert.True(m >= 1.0));

        double reconstructed = captured.Multipliers.Sum(kv => kv.Value * captured.RegionInstructionCounts[kv.Key]);
        Assert.Equal(captured.RegionInstructionCounts.Sum(), reconstructed, 1e-6);

        (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) MechanismsFactory() {
            var freshHandler = new LinuxSyscallEmulator(workload.InitialBreak, TextWriter.Null, WordSize);
            var freshMech = new Rv64Mechanism(syscallHandler: freshHandler);
            return ([freshMech,], freshHandler);
        }

        ISteppableTrain DetailedTrainFactory(IMechanism m, IMemory runMem, ulong restartPc, InstructionCounter counter) =>
            new FiveStageTrain(m, runMem, restartPc, commitObserver: counter);

        LoopPointResult result = MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
            captured, MechanismsFactory, DetailedTrainFactory, warmupInstructions: 2_000
        );

        Assert.Equal(captured.SimPoints.Points.Count, result.RegionMeasurements.Count);
        Assert.True(result.EstimatedTotalTicks > 0);
        foreach (LoopPointRegionMeasurement region in result.RegionMeasurements) {
            Assert.Single(region.HartResults);
            RevolutionResult hartResult = region.HartResults[0];
            Assert.True(hartResult.TotalTicks > 0);

            // Discriminating check: retired count must land near the measured window (warmup +
            // region's own instruction count), not zero — proves fetch actually redirected to the
            // restored PC rather than livelocking at whatever address the train's constructor
            // entryPoint happened to default to (see MultiHartLoopPointExperimentTests' doc comment
            // on this exact, once-shipped bug).
            DialBoardSnapshot? pipeline = hartResult.Find("five_stage.pipeline");
            Assert.NotNull(pipeline);
            Assert.True(pipeline.Counters["retired"] > 0, "expected genuine forward progress, not a stuck-fetch livelock");
        }
    }
}
