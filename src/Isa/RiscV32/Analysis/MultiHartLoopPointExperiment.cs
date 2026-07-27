#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32.Memory;
using RiscV32.MultiCore;

#endregion

namespace RiscV32.Analysis;

/// <summary>One representative region's own detailed-pipeline measurement.</summary>
public sealed record LoopPointRegionMeasurement(int RegionIndex, RevolutionResult[] HartResults, double Ticks);

/// <summary>
///     The result of a full LoopPoint capture-cluster-measure-extrapolate pass: the clustering itself,
///     each representative region's own measurement, and the Eq. 1 whole-run runtime estimate.
/// </summary>
public sealed record LoopPointResult(
    SimPointResult SimPoints,
    IReadOnlyList<LoopPointRegionMeasurement> RegionMeasurements,
    double EstimatedTotalTicks
);

/// <summary>
///     Everything <see cref="MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints" /> needs: one
///     <see cref="MultiHartCheckpoint" /> blob per representative region (keyed by region index, i.e.
///     <c>SimulationPoint.IntervalIndex</c>), the clustering that chose them, each region's filtered
///     instruction count (Eq. 2's inputs), and the resulting per-representative multipliers.
/// </summary>
/// <param name="RepresentativeLiveHarts">
///     Per representative region, which of the checkpoint's pre-allocated hart slots were actually
///     live (spawned and not yet halted) at that region's start — see <see cref="MultiHartKernel.IsActive" />.
///     A dormant hart's captured state is whatever <see cref="Mechanism.IMechanism.CreateArchState" />
///     produced at construction (PC 0, all-zero registers), never touched; measuring it anyway would
///     fetch and retire whatever bytes happen to sit at that stale PC as if it were real code — a real,
///     confirmed bug (a pre-spawn <c>pthread_probe.elf</c> checkpoint measured this way retired
///     hundreds of phantom instructions per never-spawned hart before this field existed).
/// </param>
public sealed record LoopPointCheckpointSet(
    SimPointResult SimPoints,
    IReadOnlyDictionary<int, byte[]> RepresentativeCheckpoints,
    IReadOnlyList<long> RegionInstructionCounts,
    IReadOnlyDictionary<int, double> Multipliers,
    ulong MemoryBaseAddress,
    int MemorySizeBytes,
    IReadOnlyDictionary<int, bool[]> RepresentativeLiveHarts
);

/// <summary>
///     LoopPoint's capture/measure orchestrator (Sabu, Patil, Heirman &amp; Carlson, HPCA 2022): composes
///     <see cref="MultiHartLoopPointProfiler" /> (region-boundary detection + BBV collection),
///     <see cref="SimPointAnalysis" /> (clustering, reused unchanged per the paper's own approach),
///     <see cref="MultiHartCheckpoint" /> (region-boundary snapshots), <see cref="MultiHartWarmupMeasureDriver" />
///     (per-representative detailed measurement), and <see cref="LoopPointRuntimeExtrapolation" />
///     (Eq. 1/2 whole-run reconstruction) into the same capture-then-measure two-phase shape
///     <c>Experiment.CaptureSimPointCheckpoints</c>/<c>MeasureSimPointCheckpoints</c> already use for
///     SimPoint.
///     <para>
///         Structurally different from the SimPoint pair in one way: SimPoint's region boundaries are
///         fixed multiples of <c>intervalSize</c>, known before the profiling pass even starts, so
///         checkpoint targets can be computed up front and captured in a cheap *second* functional pass.
///         LoopPoint's region boundaries are data-dependent (the next loop-header hit after the global
///         instruction target) — unknowable before the single profiling pass reaches them — and which
///         regions end up representative isn't known until <c>SimPointAnalysis.Analyze</c> runs over
///         the complete <see cref="MultiHartLoopPointProfiler.RegionBbvs" /> afterward. So every region
///         boundary's checkpoint is captured opportunistically during the *one* profiling pass (via
///         <see cref="MultiHartLoopPointProfiler" />'s <c>onRegionBoundary</c> hook), and only the ones
///         that turn out representative are kept. This is the simpler of the two options weighed for
///         this design (the alternative — replay a second profiling pass reproducing the same
///         boundaries to capture only representatives — saves memory on runs with very many regions,
///         at the cost of assuming pass-2 boundary reproduction is bit-identical to pass-1); at the
///         region counts this codebase's test fixtures and CLI targets actually reach, holding every
///         boundary's checkpoint in memory for the duration of one capture call is not a real cost.
///     </para>
/// </summary>
public static class MultiHartLoopPointExperiment {
    /// <param name="kernel">
    ///     A <see cref="MultiHartKernel" /> already wired for the workload under profiling — entry
    ///     point(s) set, initial register state (stack pointer, argc/argv registers, etc.) written,
    ///     not yet stepped. This method calls <see cref="MultiHartKernel.SetObserver" /> on it and
    ///     drives it with <see cref="MultiHartKernel.Run" />.
    /// </param>
    /// <param name="mechanisms">
    ///     The same mechanisms <paramref name="kernel" /> was constructed with, in hart-index order —
    ///     needed here for each hart's <see cref="Mechanism.IMechanism.Decoder" /> (loop-header/BBV
    ///     tracking) and <see cref="Mechanism.IMechanism.SyscallHandler" /> (shared handler-state
    ///     capture, if any).
    /// </param>
    /// <param name="sharedMemory">The same backing memory <paramref name="kernel" /> runs against.</param>
    /// <param name="rangeStart">Loop-header detection is restricted to <c>[rangeStart, rangeEnd)</c> — the main program image.</param>
    /// <param name="rangeEnd">See <paramref name="rangeStart" />.</param>
    /// <param name="targetGlobalInstructions">Section III-C's per-region global (all-harts) filtered-instruction target.</param>
    /// <param name="excludedRanges">
    ///     Spin-loop/sync-library ranges to exclude from both BBV weight and the region-length target
    ///     (see <c>SyncLibrarySymbols</c>).
    /// </param>
    /// <param name="profileMaxTicks">Tick budget for the profiling pass.</param>
    public static LoopPointCheckpointSet CaptureLoopPointCheckpoints(
        MultiHartKernel kernel,
        IReadOnlyList<IMechanism> mechanisms,
        ISnapshotableMemory sharedMemory,
        ulong rangeStart,
        ulong rangeEnd,
        long targetGlobalInstructions,
        IReadOnlyList<(ulong Start, ulong End)>? excludedRanges = null,
        long profileMaxTicks = 100_000_000
    ) {
        var allBoundaryCheckpoints = new Dictionary<int, byte[]>();
        var allBoundaryLiveHarts = new Dictionary<int, bool[]>();

        void SnapshotRegionStart(int regionIndex) {
            var hartStates = new IArchState[mechanisms.Count];
            var liveHarts = new bool[mechanisms.Count];
            for (var h = 0; h < mechanisms.Count; h++) {
                hartStates[h] = kernel.StateOf(h);
                liveHarts[h] = kernel.IsActive(h);
            }

            // At most one mechanism's SyscallHandler is checkpointable and every hart shares the
            // same instance (real clone()'s CLONE_FILES) — first match is the shared handler.
            ICheckpointableSyscallHandler? handler = null;
            foreach (IMechanism m in mechanisms)
                if (m.SyscallHandler is ICheckpointableSyscallHandler h) {
                    handler = h;
                    break;
                }

            using var ms = new MemoryStream();
            MultiHartCheckpoint.Save(ms, hartStates, sharedMemory, handler, (ulong)kernel.Ticks);
            allBoundaryCheckpoints[regionIndex] = ms.ToArray();
            allBoundaryLiveHarts[regionIndex] = liveHarts;
        }

        SnapshotRegionStart(0); // region 0's start is the pre-run state — no boundary fires for it.

        List<IDecoder> decoders = mechanisms.Select(m => m.Decoder).ToList();
        var profiler = new MultiHartLoopPointProfiler(
            decoders, rangeStart, rangeEnd, targetGlobalInstructions, excludedRanges,
            SnapshotRegionStart
        );
        for (var h = 0; h < mechanisms.Count; h++) kernel.SetObserver(h, profiler.HartObserver(h));

        kernel.Run(profileMaxTicks);
        profiler.Complete();

        SimPointResult sp = SimPointAnalysis.Analyze(profiler.RegionBbvs);
        IReadOnlyDictionary<int, double> multipliers =
            LoopPointRuntimeExtrapolation.ComputeMultipliers(sp, profiler.RegionInstructionCounts);

        // allBoundaryCheckpoints may hold one trailing entry beyond RegionBbvs.Count (Complete()'s
        // own CloseRegion() call fires the boundary hook once more, for a region that never actually
        // starts since the run already halted) — harmless, since no SimulationPoint's IntervalIndex
        // can reference an out-of-range region, so it's simply never looked up below.
        var representativeCheckpoints = new Dictionary<int, byte[]>();
        var representativeLiveHarts = new Dictionary<int, bool[]>();
        foreach (SimulationPoint p in sp.Points) {
            representativeCheckpoints[p.IntervalIndex] = allBoundaryCheckpoints[p.IntervalIndex];
            representativeLiveHarts[p.IntervalIndex] = allBoundaryLiveHarts[p.IntervalIndex];
        }

        return new LoopPointCheckpointSet(
            sp, representativeCheckpoints, profiler.RegionInstructionCounts, multipliers,
            sharedMemory.BaseAddress, sharedMemory.SizeBytes, representativeLiveHarts
        );
    }

    /// <param name="captured">The result of a prior <see cref="CaptureLoopPointCheckpoints" /> call.</param>
    /// <param name="hartMechanismsFactory">
    ///     Produces one fresh <see cref="Mechanism.IMechanism" /> per hart (same count and hart-id
    ///     order as the capture pass) plus the one shared <see cref="ICheckpointableSyscallHandler" />
    ///     they were built to share, if the workload has one. Called once per representative region —
    ///     mechanisms/handlers carry mutable state, so every region needs its own, bit-identically-
    ///     behaving instances.
    /// </param>
    /// <param name="detailedTrainFactory">
    ///     Builds one hart's detailed pipeline train, given that hart's fresh mechanism, the shared
    ///     memory to run on, that hart's restart PC (from the checkpoint being restored), and the
    ///     <see cref="InstructionCounter" /> the caller must wire up as that train's commit observer.
    ///     The restart PC <em>must</em> be passed as the train's own constructor <c>entryPoint</c>
    ///     argument, not left at a placeholder — detailed pipeline trains (<c>FiveStageTrain</c>,
    ///     <c>OooTrain</c>) track their own fetch-address state separately from <see cref="IArchState.Pc" />,
    ///     seeded once at construction and never re-read afterward, so <see cref="MultiHartCheckpoint.RestoreInto" />
    ///     alone (which only mutates <see cref="IArchState" />) cannot redirect fetch to the restored
    ///     PC — mirrors <c>Experiment.MeasureSimPointCheckpoints</c>' own <c>detailedTrainFactory</c>
    ///     contract, which threads <c>ArchitecturalCheckpoint.Pc</c> through the same way.
    /// </param>
    /// <param name="warmupInstructions">Global (all-harts) unmeasured warmup instructions per region, run after restore.</param>
    public static LoopPointResult MeasureLoopPointCheckpoints(
        LoopPointCheckpointSet captured,
        Func<(IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler)>
            hartMechanismsFactory,
        Func<IMechanism, IMemory, ulong, InstructionCounter, ISteppableTrain> detailedTrainFactory,
        long warmupInstructions
    ) {
        var regionMeasurements = new List<LoopPointRegionMeasurement>();
        var runtimes = new Dictionary<int, double>();

        foreach ((int regionIndex, byte[] chkBytes) in captured.RepresentativeCheckpoints) {
            using var ms = new MemoryStream(chkBytes);
            MultiHartCheckpoint chk = MultiHartCheckpoint.Load(ms);

            var mem = new FlatMemory(captured.MemorySizeBytes, captured.MemoryBaseAddress);
            (IReadOnlyList<IMechanism> mechanisms, ICheckpointableSyscallHandler? handler) = hartMechanismsFactory();
            bool[] liveHarts = captured.RepresentativeLiveHarts[regionIndex];

            // Only harts that were actually live (spawned, not yet halted) at this region's start get
            // a real detailed-pipeline train — a dormant hart's checkpointed state is untouched
            // construction-time garbage (PC 0, all-zero registers), and driving it would fetch and
            // retire whatever bytes happen to sit at that stale PC as if it were real code (see
            // LoopPointCheckpointSet.RepresentativeLiveHarts's doc comment for the confirmed repro).
            // Every hart still needs an IArchState for RestoreInto's count check, so dormant slots get
            // one straight from the mechanism, not from a train, and it's simply discarded afterward.
            var hartStates = new IArchState[mechanisms.Count];
            var counters = new List<InstructionCounter>();
            var trains = new List<ISteppableTrain>();
            for (var h = 0; h < mechanisms.Count; h++) {
                if (!liveHarts[h]) {
                    hartStates[h] = mechanisms[h].CreateArchState();
                    continue;
                }

                ulong pc = chk.PcOf(h);
                if (pc < captured.MemoryBaseAddress
                 || pc >= captured.MemoryBaseAddress + (ulong)captured.MemorySizeBytes)
                    throw new InvalidOperationException(
                        $"LoopPoint region {regionIndex}: hart {h} is marked live but its checkpointed " +
                        $"PC 0x{pc:X} falls outside the workload's mapped memory " +
                        $"[0x{captured.MemoryBaseAddress:X}, 0x{captured.MemoryBaseAddress + (ulong)captured.MemorySizeBytes:X}) "
                       +
                        "— refusing to measure a hart that would fetch garbage as code."
                    );

                var counter = new InstructionCounter();
                counters.Add(counter);
                ISteppableTrain train = detailedTrainFactory(mechanisms[h], mem, pc, counter);
                trains.Add(train);
                hartStates[h] = train.ArchState!;
            }

            chk.RestoreInto(hartStates, mem, handler);

            RevolutionResult[] results = MultiHartWarmupMeasureDriver.RunWarmupThenMeasure(
                trains, counters, warmupInstructions, captured.RegionInstructionCounts[regionIndex]
            );

            // Every hart's own tick count agrees under this round-robin lockstep interleaving;
            // Max is a safe guard against a hart that halts early rather than a real combination
            // (summing would double-count the same wall-clock ticks across harts).
            double ticks = results.Length > 0 ? results.Max(r => r.TotalTicks) : 0;
            runtimes[regionIndex] = ticks;
            regionMeasurements.Add(new LoopPointRegionMeasurement(regionIndex, results, ticks));
        }

        double totalTicks = LoopPointRuntimeExtrapolation.ExtrapolateTotalRuntime(captured.Multipliers, runtimes);
        return new LoopPointResult(captured.SimPoints, regionMeasurements, totalTicks);
    }
}