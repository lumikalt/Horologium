using Mechanism;
using Orrery.Train;
using RiscV.Config;
using RiscV.Memory;
using RiscV.Trains;

namespace RiscV.Analysis;

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
            var memory = new FlatMemory(workload.MemorySize);
            workload.Load(memory);

            RevolutionResult result = config.Pipeline switch {
                "superscalar" => new SuperscalarTrain(
                    mechanism, memory,
                    entryPoint: workload.EntryPoint,
                    issueWidth: config.IssueWidth
                ).Run(maxTicks, warmupTicks, resolvedInterval),

                "ooo" => new OoOETrain(
                    mechanism, memory,
                    entryPoint:    workload.EntryPoint,
                    issueWidth:    config.IssueWidth,
                    robCapacity:   config.RobCapacity,
                    iqCapacity:    config.IqCapacity,
                    extraPhysRegs: config.ExtraPhysRegs,
                    predictor:     config.Predictor?.Build()
                ).Run(maxTicks, warmupTicks, resolvedInterval),

                _ => new FiveStageTrain(
                    mechanism, memory,
                    workload.EntryPoint,
                    config.ForwardingEnabled,
                    config.Predictor?.Build(),
                    config.ToIMemoryConfig(),
                    config.ToDMemoryConfig(),
                    config.StoreBufferCapacity
                ).Run(maxTicks, warmupTicks, resolvedInterval),
            };

            records.Add(new RunRecord(named.Name, config, result));
        }

        return new ExperimentResult(records);
    }
}