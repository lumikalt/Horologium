using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32.Config;
using RiscV32.Memory;
using RiscV32.Trace;

namespace RiscV32.Analysis;

/// <summary>
///     Runs the same workload under multiple hardware configurations and returns
///     the aggregated results for comparison.
/// </summary>
public static class Experiment {
    /// <summary>
    ///     Runs <paramref name="workload" /> once per entry in <paramref name="configurations" />.
    ///     Each run gets a fresh <see cref="FlatMemory" /> and a fresh predictor instance.
    /// </summary>
    /// <param name="warmupTicks">
    ///     Ticks to run before starting measurement. Warms up branch predictors and caches;
    ///     the returned counters and histograms reflect only the post-warmup phase.
    /// </param>
    /// <param name="snapshotInterval">
    ///     Ticks between periodic time-series snapshots. Pass -1 to auto-estimate from
    ///     <see cref="IWorkload.CodeSize" /> (targeting roughly 100 data points). Pass 0
    ///     (default) to disable time series.
    /// </param>
    /// <param name="workload">
    ///     The workload to run.
    /// </param>
    /// <param name="configurations">
    ///     The hardware configurations to run under.
    /// </param>
    /// <param name="mechanismFactory">
    ///     Factory called once per configuration run to produce an independent mechanism instance.
    /// </param>
    /// <param name="maxTicks">
    ///     The maximum number of ticks to run for each configuration.
    /// </param>
    public static ExperimentResult Run(
        IWorkload workload,
        IEnumerable<NamedConfig> configurations,
        Func<IMechanism> mechanismFactory,
        long maxTicks = 1_000_000,
        long warmupTicks = 0,
        long snapshotInterval = 0
    ) {
        long resolvedInterval = ResolveInterval(snapshotInterval, workload);
        List<NamedConfig> configs = configurations.ToList();
        var records = new RunRecord[configs.Count];

        Parallel.For(
            0, configs.Count, i =>
                records[i] = RunOne(workload, mechanismFactory(), configs[i], maxTicks, warmupTicks, resolvedInterval)
        );

        return new ExperimentResult(records.ToList());
    }

    /// <summary>
    ///     Runs every combination of workload × configuration in parallel and returns one
    ///     <see cref="ExperimentResult" /> per workload, preserving the input order.
    /// </summary>
    /// <param name="workloads">
    ///     The labelled workloads to simulate. Each label is used as a display name in output.
    /// </param>
    /// <param name="configurations">
    ///     The hardware configurations shared across all workloads.
    /// </param>
    /// <param name="mechanismFactory">
    ///     Called once per (workload, config) pair. Receives the workload so callers can pass
    ///     a workload-specific HTIF tohost address to the mechanism constructor.
    /// </param>
    /// <param name="maxTicks">Maximum ticks per (workload, config) run.</param>
    /// <param name="warmupTicks">Ticks before measurement starts.</param>
    /// <param name="snapshotInterval">
    ///     Ticks between time-series snapshots (-1 = auto per workload, 0 = off).
    /// </param>
    public static IReadOnlyList<(string Label, ExperimentResult Result)> RunMany(
        IEnumerable<(string Label, IWorkload Workload)> workloads,
        IEnumerable<NamedConfig> configurations,
        Func<IWorkload, IMechanism> mechanismFactory,
        long maxTicks = 1_000_000,
        long warmupTicks = 0,
        long snapshotInterval = 0
    ) {
        List<(string Label, IWorkload Workload)> wl = workloads.ToList();
        List<NamedConfig> configs = configurations.ToList();
        int c = configs.Count;
        var records = new RunRecord[wl.Count * c];

        Parallel.For(
            0, wl.Count * c, idx => {
                int wi = idx / c;
                int ci = idx % c;
                IWorkload workload = wl[wi].Workload;
                long resolvedInterval = ResolveInterval(snapshotInterval, workload);
                records[idx] = RunOne(
                    workload, mechanismFactory(workload), configs[ci], maxTicks, warmupTicks, resolvedInterval
                );
            }
        );

        var results = new (string Label, ExperimentResult Result)[wl.Count];
        for (var wi = 0; wi < wl.Count; wi++) {
            var wlRecords = new RunRecord[c];
            for (var ci = 0; ci < c; ci++) wlRecords[ci] = records[wi * c + ci];
            results[wi] = (wl[wi].Label, new ExperimentResult(wlRecords));
        }

        return results;
    }

    private static long ResolveInterval(long snapshotInterval, IWorkload workload) =>
        snapshotInterval == -1 ? Math.Max(10, workload.CodeSize / 200) : snapshotInterval;

    private static RunRecord RunOne(
        IWorkload workload,
        IMechanism mechanism,
        NamedConfig named,
        long maxTicks,
        long warmupTicks,
        long snapshotInterval
    ) {
        TrainConfig config = named.Config;
        // RTL functional units are named per config (rtl_div_lib etc.); wrap this run's
        // mechanism here, where both are in hand — one verilated model set per run/thread.
        if (mechanism is Rv32Mechanism rv32) config.ApplyRtlUnits(rv32);
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        IMemory runMemory = workload.WrapMemory(memory);
        MemoryConfig dCfg = WithMmio(config.ToDMemoryConfig(), workload);

        RevolutionResult result;
        if (config.Pipeline == "ooo") {
            // For HTIF benchmark workloads that have setStats(), attach an observer to
            // measure kernel-only IPC (excluding startup and the sprintf teardown that
            // inflates instruction count vs. the Linux-ABI gem5 binary).
            SetStatsObserver? setStatsObs = null;
            OooeTrain? trainRef = null;
            if (workload is Rv32ElfWorkload elfWorkload &&
                elfWorkload.TryFindSymbol("setStats", out ulong setStatsPc))
                // ReSharper disable once AccessToModifiedClosure
                setStatsObs = new SetStatsObserver(setStatsPc, () => trainRef!.SnapshotPipeline());

            trainRef = new OooeTrain(
                mechanism, runMemory,
                workload.EntryPoint,
                config.IssueWidth,
                config.RobCapacity,
                config.IqCapacity,
                config.ExtraPhysRegs,
                config.Predictor?.Build(mechanism, workload),
                config.ToIMemoryConfig(),
                dCfg,
                config.FuLatency,
                commitObserver: setStatsObs,
                writeBufferCapacity: config.StoreBufferCapacity,
                mshrCapacity: config.MshrCapacity,
                flatIq: config.FlatIq,
                enableStoreSets: config.EnableStoreSets,
                fdipFtqCapacity: config.FdipFtqCapacity,
                rdip: config.Rdip
            );

            result = trainRef.Run(maxTicks, warmupTicks, snapshotInterval);

            if (setStatsObs?.KernelDelta is { } kernelSnap) result = result with { Snapshots = [kernelSnap,], };
        }
        else if (config.Pipeline == "cpr") {
            // Checkpoint Processing and Recovery train: shares the OoO knobs it understands
            // (width, IQ, physical registers, predictor, caches, FU latencies); checkpoint
            // geometry and CFP stay at their constructor defaults. Store sets stay at the
            // CprTrain default (enabled) rather than following config.EnableStoreSets:
            // CPR's violation recovery re-executes the whole checkpoint, so without
            // memory-dependence learning the same load re-violates forever (a livelock the
            // OoO train cannot have — its violation path re-executes from the load itself).
            result = new CprTrain(
                mechanism, runMemory,
                workload.EntryPoint,
                config.IssueWidth,
                config.IqCapacity,
                config.ExtraPhysRegs,
                predictor: config.Predictor?.Build(mechanism, workload),
                iMemConfig: config.ToIMemoryConfig(),
                dMemConfig: dCfg,
                fuLatency: config.FuLatency
            ).Run(maxTicks, warmupTicks, snapshotInterval);
        }
        else {
            result = config.Pipeline switch {
                "superscalar" => new SuperscalarTrain(
                    mechanism, runMemory,
                    workload.EntryPoint,
                    config.IssueWidth,
                    config.ToIMemoryConfig(),
                    dCfg,
                    config.Predictor?.Build(mechanism, workload),
                    fuLatency: config.FuLatency
                ).Run(maxTicks, warmupTicks, snapshotInterval),

                "dae" => new DaeTrain(
                    mechanism, runMemory,
                    workload.EntryPoint,
                    config.DaeLaneQueueDepth,
                    config.ToIMemoryConfig(),
                    dCfg
                ).Run(maxTicks, warmupTicks, snapshotInterval),

                _ => new FiveStageTrain(
                    mechanism, runMemory,
                    workload.EntryPoint,
                    config.ForwardingEnabled,
                    config.Predictor?.Build(mechanism, workload),
                    config.ToIMemoryConfig(),
                    dCfg,
                    config.StoreBufferCapacity,
                    fdipFtqCapacity: config.FdipFtqCapacity,
                    rdip: config.Rdip
                ).Run(maxTicks, warmupTicks, snapshotInterval),
            };
        }

        return new RunRecord(named.Name, config, result);
    }

    /// <summary>
    ///     Runs <paramref name="workload" /> under a single <paramref name="config" /> with a
    ///     <see cref="PEventLog" /> attached and returns the log.
    /// </summary>
    public static PEventLog Trace(
        IWorkload workload,
        NamedConfig config,
        IMechanism mechanism,
        long maxTicks = 10_000
    ) {
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        IMemory runMemory = workload.WrapMemory(memory);
        var plog = new PEventLog();
        TrainConfig cfg = config.Config;
        MemoryConfig dCfg = WithMmio(cfg.ToDMemoryConfig(), workload);
        switch (cfg.Pipeline) {
            case "ooo":
                new OooeTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.IssueWidth, cfg.RobCapacity, cfg.IqCapacity, cfg.ExtraPhysRegs,
                    cfg.Predictor?.Build(mechanism, workload),
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.FuLatency, plog,
                    writeBufferCapacity: cfg.StoreBufferCapacity,
                    mshrCapacity: cfg.MshrCapacity,
                    flatIq: cfg.FlatIq,
                    fdipFtqCapacity: cfg.FdipFtqCapacity,
                    rdip: cfg.Rdip
                ).Run(maxTicks);
                break;
            case "superscalar":
                new SuperscalarTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.IssueWidth,
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.Predictor?.Build(mechanism, workload),
                    plog,
                    cfg.FuLatency
                ).Run(maxTicks);
                break;
            case "cpr":
                new CprTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.IssueWidth, cfg.IqCapacity, cfg.ExtraPhysRegs,
                    predictor: cfg.Predictor?.Build(mechanism, workload),
                    iMemConfig: cfg.ToIMemoryConfig(), dMemConfig: dCfg,
                    fuLatency: cfg.FuLatency,
                    pEventLog: plog
                ).Run(maxTicks);
                break;
            case "dae":
                new DaeTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.DaeLaneQueueDepth,
                    cfg.ToIMemoryConfig(), dCfg,
                    plog
                ).Run(maxTicks);
                break;
            default:
                new FiveStageTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.ForwardingEnabled,
                    cfg.Predictor?.Build(mechanism, workload),
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.StoreBufferCapacity, plog,
                    fdipFtqCapacity: cfg.FdipFtqCapacity,
                    rdip: cfg.Rdip
                ).Run(maxTicks);
                break;
        }

        return plog;
    }

    // Memory-mapped I/O must bypass caches: device side effects (e.g. HtifMemory's
    // fromhost auto-ACK, or a UART device's TX/RX state) are invisible to the cache,
    // so a cached copy goes stale and poll loops spin forever.
    private static MemoryConfig WithMmio(MemoryConfig dCfg, IWorkload workload) =>
        workload.MmioRegion is { } r
            ? dCfg with { UncacheableBase = r.Base, UncacheableSize = r.Size, }
            : dCfg;

    /// <summary>
    ///     SimPoint phase analysis (Sherwood et al., ASPLOS 2002): runs
    ///     <paramref name="workload" /> once on a functional <c>SingleCycleTrain</c> with a
    ///     <see cref="BbvProfiler" /> attached, then clusters the interval basic-block
    ///     vectors into phases and picks representative simulation points.
    /// </summary>
    public static (SimPointResult Result, BbvProfiler Profiler) ProfileSimPoints(
        IWorkload workload,
        IMechanism mechanism,
        long intervalSize,
        long maxTicks = 100_000_000,
        int dimensions = 15,
        int maxK = 10,
        int seed = 42
    ) {
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);

        var profiler = new BbvProfiler(mechanism.Decoder, intervalSize);
        new SingleCycleTrain(mechanism, workload.WrapMemory(memory), workload.EntryPoint, commitObserver: profiler)
           .Run(maxTicks);
        profiler.Complete();

        return (SimPointAnalysis.Analyze(profiler.Intervals, dimensions, maxK, seed: seed), profiler);
    }

    /// <summary>
    ///     Full SimPoint-sampled estimation for a detailed pipeline: profile
    ///     <paramref name="workload" /> (<see cref="ProfileSimPoints" />), save one checkpoint per
    ///     simulation point in a single second functional pass, then restore each checkpoint into a
    ///     fresh detailed train (built by <paramref name="detailedTrainFactory" />) and measure
    ///     <paramref name="intervalSize" /> instructions after <paramref name="warmupInstructions" />
    ///     of unmeasured warmup (<see cref="WarmupMeasureDriver" />). Per-point CPIs are combined into
    ///     a whole-program estimate by <see cref="SimulationPoint.Weight" /> — all intervals are the
    ///     same length, so the correct combination is a weighted mean of CPI (not of IPC).
    /// </summary>
    /// <param name="workload">The workload to profile and measure.</param>
    /// <param name="mechanismFactory">
    ///     Produces a fresh <see cref="IMechanism" /> instance. Called once per functional pass and
    ///     once per simulation point — mechanisms can carry state (CSRs etc.), so every pass needs
    ///     its own, bit-identically-behaving instance.
    /// </param>
    /// <param name="detailedTrainFactory">
    ///     Builds the detailed pipeline train for one simulation point, given a fresh mechanism, the
    ///     wrapped memory to run on, the checkpoint's restart PC (to use as the train's entry point —
    ///     required so the train's own fetch-PC state starts at the restored PC, not the workload's
    ///     original entry point), and the <see cref="InstructionCounter" /> the caller must wire up as
    ///     the train's commit observer. Must return a train that supports
    ///     <see cref="ISteppableTrain.SnapshotDials" />/baseline-<see cref="ISteppableTrain.FinishStepping(System.Collections.Generic.IReadOnlyList{DialBoardSnapshot})" />
    ///     — currently <c>SingleCycleTrain</c>, <c>FiveStageTrain</c>, <c>OooeTrain</c>.
    /// </param>
    /// <param name="intervalSize">Instructions per SimPoint interval (also the per-point measured length).</param>
    /// <param name="warmupInstructions">
    ///     Unmeasured warmup instructions run before each point's measured interval, restarting from
    ///     <c>intervalStart − warmupInstructions</c> (clamped to 0).
    /// </param>
    public static SimPointCheckpointResult RunWithSimPointCheckpoints(
        IWorkload workload,
        Func<IMechanism> mechanismFactory,
        Func<IMechanism, IMemory, ulong, InstructionCounter, ISteppableTrain> detailedTrainFactory,
        long intervalSize,
        long warmupInstructions,
        long profileMaxTicks = 100_000_000,
        int dimensions = 15,
        int maxK = 10,
        int seed = 42
    ) {
        (SimPointResult sp, _) = ProfileSimPoints(workload, mechanismFactory(), intervalSize, profileMaxTicks, dimensions, maxK, seed);

        // Checkpoint target per point (clamped so warmup never reaches before instruction 0),
        // sorted ascending for InstructionCounter — `order` maps sorted position back to the
        // point's index so each checkpoint lands in the right slot.
        var targets = sp.Points.Select(p => Math.Max(0L, p.IntervalIndex * intervalSize - warmupInstructions)).ToList();
        int[] order = [..Enumerable.Range(0, sp.Points.Count).OrderBy(i => targets[i]),];
        List<long> sortedTargets = [..order.Select(i => targets[i]),];

        // Single second functional pass: save every point's checkpoint without re-running the
        // workload from scratch per point. Targets of exactly 0 (interval 0 with warmup clamped
        // away) need the pristine pre-step state — InstructionCounter's callback only fires after
        // a commit, which would already be one instruction past the true start — so those are
        // captured directly from the wound-but-not-yet-stepped train, before any StepCycle call.
        var checkpoints = new byte[sp.Points.Count][];
        if (sp.Points.Count > 0) {
            var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
            workload.Load(mem);
            IMemory runMem = workload.WrapMemory(mem);

            var zeroPointIndices = new List<int>();
            var positiveOrder = new List<int>();
            var positiveTargets = new List<long>();
            for (var si = 0; si < sortedTargets.Count; si++)
                if (sortedTargets[si] == 0) zeroPointIndices.Add(order[si]);
                else {
                    positiveOrder.Add(order[si]);
                    positiveTargets.Add(sortedTargets[si]);
                }

            SingleCycleTrain? captureTrainRef = null;
            var counter = new InstructionCounter(
                positiveTargets,
                sortedIdx => {
                    int pointIdx = positiveOrder[sortedIdx];
                    using var ms = new MemoryStream();
                    ArchitecturalCheckpoint.Save(
                        ms, captureTrainRef!.ArchState, mem, (ulong)captureTrainRef.CurrentTick
                    );
                    checkpoints[pointIdx] = ms.ToArray();
                }
            );

            captureTrainRef = new SingleCycleTrain(mechanismFactory(), runMem, workload.EntryPoint, commitObserver: counter);
            captureTrainRef.BeginStepping();

            foreach (int pointIdx in zeroPointIndices) {
                using var ms = new MemoryStream();
                ArchitecturalCheckpoint.Save(ms, captureTrainRef.ArchState, mem, 0);
                checkpoints[pointIdx] = ms.ToArray();
            }

            if (positiveTargets.Count > 0) {
                long maxTarget = positiveTargets[^1];
                while (counter.Count < maxTarget && captureTrainRef.StepCycle()) { }
            }

            captureTrainRef.FinishStepping();

            for (var i = 0; i < checkpoints.Length; i++)
                if (checkpoints[i] is null)
                    throw new InvalidOperationException(
                        $"Simulation point {i} (interval {sp.Points[i].IntervalIndex}) needs " +
                        $"{targets[i]:N0} instructions, but the workload halted at {counter.Count:N0}."
                    );
        }

        // Restore each checkpoint into a fresh detailed train and measure.
        var pointResults = new List<SimPointPointResult>();
        for (var i = 0; i < sp.Points.Count; i++) {
            SimulationPoint point = sp.Points[i];
            ArchitecturalCheckpoint chk;
            using (var ms = new MemoryStream(checkpoints[i])) chk = ArchitecturalCheckpoint.Load(ms);

            var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
            workload.Load(mem);
            IMemory runMem = workload.WrapMemory(mem);

            var counter = new InstructionCounter();
            IMechanism mechanism = mechanismFactory();
            ISteppableTrain detailedTrain = detailedTrainFactory(mechanism, runMem, chk.Pc, counter);
            chk.RestoreInto(detailedTrain.ArchState!, mem);

            // The checkpoint target is intervalStart − actualWarmup (clamped so it never precedes
            // instruction 0); actualWarmup must match here too, or an early interval's measured
            // window would be shifted past the interval it's supposed to represent (e.g. interval
            // 0 with warmupInstructions > 0 would measure [warmup, warmup+intervalSize) instead of
            // [0, intervalSize)).
            long intervalStart = point.IntervalIndex * intervalSize;
            long actualWarmup = Math.Min(warmupInstructions, intervalStart);

            RevolutionResult rev = WarmupMeasureDriver.RunWarmupThenMeasure(
                detailedTrain, counter, actualWarmup, intervalSize
            );
            long measured = Math.Max(0, counter.Count - actualWarmup);
            pointResults.Add(new SimPointPointResult(point, measured, rev));
        }

        double cpi = pointResults.Sum(r => r.Point.Weight * r.Cpi);
        return new SimPointCheckpointResult(sp, pointResults, cpi);
    }

    /// <summary>
    ///     Runs <paramref name="workload" /> functionally on the single-cycle train and
    ///     writes an Olympia-compatible JSON instruction trace to <paramref name="output" />.
    ///     Returns the number of instructions written. The single-cycle train is the
    ///     natural source: it retires exactly one instruction per commit, so the trace
    ///     is an exact functional instruction stream (the timing model is Olympia's job).
    /// </summary>
    public static int WriteOlympiaTrace(
        IWorkload workload,
        IMechanism mechanism,
        TextWriter output,
        long maxTicks = 10_000_000
    ) {
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        var tracing = new TracingMemory(workload.WrapMemory(memory));

        using var writer = new OlympiaJsonTraceWriter(mechanism.Decoder, tracing, output);
        new SingleCycleTrain(mechanism, tracing, workload.EntryPoint, commitObserver: writer).Run(maxTicks);
        return writer.Count;
    }

    /// <summary>
    ///     Records a Horologium elastic DDG trace (HELF binary format) by running
    ///     <paramref name="workload" /> once on a <c>SingleCycleTrain</c> and observing
    ///     each committed instruction.
    /// </summary>
    public static int WriteElasticTrace(
        IWorkload workload,
        IMechanism mechanism,
        Stream output,
        long maxTicks = 10_000_000
    ) {
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        var tracing = new TracingMemory(workload.WrapMemory(memory));

        using var writer = new ElasticTraceWriter(mechanism.Decoder, tracing, output);
        new SingleCycleTrain(mechanism, tracing, workload.EntryPoint, commitObserver: writer).Run(maxTicks);
        return writer.Count;
    }

    /// <summary>
    ///     Records an STF binary trace by running <paramref name="workload" /> once on a
    ///     <c>SingleCycleTrain</c> and observing each committed instruction.
    /// </summary>
    public static int WriteStfTrace(
        IWorkload workload,
        IMechanism mechanism,
        Stream output,
        long maxTicks = 10_000_000
    ) {
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        var tracing = new TracingMemory(workload.WrapMemory(memory));

        using var writer = new StfTraceWriter(mechanism.Decoder, tracing, output, workload.EntryPoint);
        new SingleCycleTrain(mechanism, tracing, workload.EntryPoint, commitObserver: writer).Run(maxTicks);
        return writer.Count;
    }
}