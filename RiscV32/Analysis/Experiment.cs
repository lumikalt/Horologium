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
    /// <param name="mechanism">
    /// The mechanism to use.
    /// </param>
    /// <param name="maxTicks">
    /// The maximum number of ticks to run for each configuration.
    /// </param>
    public static ExperimentResult Run(
        IWorkload workload,
        IEnumerable<NamedConfig> configurations,
        IMechanism mechanism,
        long maxTicks = 1_000_000,
        long warmupTicks = 0,
        long snapshotInterval = 0
    ) {
        long resolvedInterval = snapshotInterval == -1
            ? Math.Max(10, workload.CodeSize / 200)
            : snapshotInterval;

        var records = new List<RunRecord>();

        foreach (NamedConfig named in configurations) {
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
                ).Run(maxTicks, warmupTicks, resolvedInterval),

                "ooo" => new OooeTrain(
                    mechanism, runMemory,
                    workload.EntryPoint,
                    config.IssueWidth,
                    config.RobCapacity,
                    config.IqCapacity,
                    config.ExtraPhysRegs,
                    config.Predictor?.Build(),
                    config.ToIMemoryConfig(),
                    dCfg,
                    config.FuLatency
                ).Run(maxTicks, warmupTicks, resolvedInterval),

                _ => new FiveStageTrain(
                    mechanism, runMemory,
                    workload.EntryPoint,
                    config.ForwardingEnabled,
                    config.Predictor?.Build(),
                    config.ToIMemoryConfig(),
                    dCfg,
                    config.StoreBufferCapacity
                ).Run(maxTicks, warmupTicks, resolvedInterval),
            };

            records.Add(new RunRecord(named.Name, config, result));
        }

        return new ExperimentResult(records);
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
                    cfg.Predictor?.Build(),
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.FuLatency, plog
                ).Run(maxTicks);
                break;
            case "superscalar": break;
            default:
                new FiveStageTrain(
                    mechanism, runMemory, workload.EntryPoint,
                    cfg.ForwardingEnabled,
                    cfg.Predictor?.Build(),
                    cfg.ToIMemoryConfig(), dCfg,
                    cfg.StoreBufferCapacity, plog
                ).Run(maxTicks);
                break;
        }

        return plog;
    }

    // The HTIF tohost/fromhost registers are memory-mapped I/O and must bypass the
    // cache: HtifMemory's auto-ACK writes fromhost to the backing below the cache,
    // so a cached copy goes stale and the printstr poll loop spins forever. They
    // are two adjacent 8-byte registers (tohost at the symbol, fromhost at +8).
    private static MemoryConfig WithMmio(MemoryConfig dCfg, IWorkload workload) =>
        workload.HtifTohostAddress is ulong tohost
            ? dCfg with { UncacheableBase = tohost, UncacheableSize = 16 }
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