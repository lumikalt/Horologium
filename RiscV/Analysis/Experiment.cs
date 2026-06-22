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
    public static ExperimentResult Run(
        IWorkload workload,
        IEnumerable<NamedConfig> configurations,
        IMechanism mechanism,
        long maxTicks = 1_000_000,
        long warmupTicks = 0
    ) {
        var records = new List<RunRecord>();

        foreach (NamedConfig named in configurations) {
            TrainConfig config = named.Config;
            var memory = new FlatMemory(workload.MemorySize);
            workload.Load(memory);

            var train = new FiveStageTrain(
                mechanism, memory,
                entryPoint: workload.EntryPoint,
                forwardingEnabled: config.ForwardingEnabled,
                predictor: config.Predictor?.Build(),
                iMemConfig: config.ToIMemoryConfig(),
                dMemConfig: config.ToDMemoryConfig(),
                storeBufferCapacity: config.StoreBufferCapacity
            );

            RevolutionResult result = train.Run(maxTicks, warmupTicks);
            records.Add(new RunRecord(named.Name, config, result));
        }

        return new ExperimentResult(records);
    }
}
