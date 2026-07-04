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
/// Runs the same workload under multiple hardware configurations and returns
/// the aggregated results for comparison.
/// </summary>
public static class Experiment {
    /// <summary>
    /// Runs <paramref name="workload"/> once per entry in <paramref name="configurations"/>.
    /// Each run gets a fresh <see cref="FlatMemory"/> and a fresh predictor instance.
    /// </summary>
    /// <param name="warmupTicks">
    /// Ticks to run before starting measurement. Warms up branch predictors and caches;
    /// the returned counters and histograms reflect only the post-warmup phase.
    /// </param>
    /// <param name="snapshotInterval">
    /// Ticks between periodic time-series snapshots. Pass -1 to auto-estimate from
    /// <see cref="IWorkload.CodeSize"/> (targeting roughly 100 data points). Pass 0
    /// (default) to disable time series.
    /// </param>
    /// <param name="workload">
    /// The workload to run.
    /// </param>
    /// <param name="configurations">
    /// The hardware configurations to run under.
    /// </param>
    /// <param name="mechanismFactory">
    /// Factory called once per configuration run to produce an independent mechanism instance.
    /// </param>
    /// <param name="maxTicks">
    /// The maximum number of ticks to run for each configuration.
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
    /// Runs every combination of workload × configuration in parallel and returns one
    /// <see cref="ExperimentResult"/> per workload, preserving the input order.
    /// </summary>
    /// <param name="workloads">
    /// The labelled workloads to simulate. Each label is used as a display name in output.
    /// </param>
    /// <param name="configurations">
    /// The hardware configurations shared across all workloads.
    /// </param>
    /// <param name="mechanismFactory">
    /// Called once per (workload, config) pair. Receives the workload so callers can pass
    /// a workload-specific HTIF tohost address to the mechanism constructor.
    /// </param>
    /// <param name="maxTicks">Maximum ticks per (workload, config) run.</param>
    /// <param name="warmupTicks">Ticks before measurement starts.</param>
    /// <param name="snapshotInterval">
    /// Ticks between time-series snapshots (-1 = auto per workload, 0 = off).
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
        var memory = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(memory);
        IMemory runMemory = workload.WrapMemory(memory);
        MemoryConfig dCfg = WithMmio(config.ToDMemoryConfig(), workload);

        RevolutionResult result = config.Pipeline switch {
            "superscalar" => new SuperscalarTrain(
                mechanism, runMemory,
                workload.EntryPoint,
                config.IssueWidth,
                config.ToIMemoryConfig(),
                dCfg
            ).Run(maxTicks, warmupTicks, snapshotInterval),

            "ooo" => new OooeTrain(
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
                writeBufferCapacity: config.StoreBufferCapacity,
                mshrCapacity: config.MshrCapacity
            ).Run(maxTicks, warmupTicks, snapshotInterval),

            _ => new FiveStageTrain(
                mechanism, runMemory,
                workload.EntryPoint,
                config.ForwardingEnabled,
                config.Predictor?.Build(mechanism, workload),
                config.ToIMemoryConfig(),
                dCfg,
                config.StoreBufferCapacity
            ).Run(maxTicks, warmupTicks, snapshotInterval),
        };

        return new RunRecord(named.Name, config, result);
    }

    /// <summary>
    /// Runs <paramref name="workload"/> under a single <paramref name="config"/> with a
    /// <see cref="PEventLog"/> attached and returns the log. Superscalar returns an empty log.
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
                    mshrCapacity: cfg.MshrCapacity
                ).Run(maxTicks);
                break;
            case "superscalar": break;
            default:
                new FiveStageTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.ForwardingEnabled,
                    cfg.Predictor?.Build(mechanism, workload),
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.StoreBufferCapacity, plog
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
    /// Runs <paramref name="workload"/> functionally on the single-cycle train and
    /// writes an Olympia-compatible JSON instruction trace to <paramref name="output"/>.
    /// Returns the number of instructions written. The single-cycle train is the
    /// natural source: it retires exactly one instruction per commit, so the trace
    /// is an exact functional instruction stream (the timing model is Olympia's job).
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
}