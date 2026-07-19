using System.Text;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32.Config;

namespace RiscV32.Analysis;

/// <summary>
///     The collected result of one named simulation run.
/// </summary>
public sealed record RunRecord(string Name, TrainConfig Config, RevolutionResult Result);

/// <summary>
///     One SimPoint simulation point measured on the detailed pipeline: its representative
///     interval/cluster/weight (<see cref="SimulationPoint" />), the actual number of instructions
///     measured (may be less than the requested interval size if the workload halted mid-interval),
///     and the baseline-subtracted <see cref="RevolutionResult" /> for that measurement.
/// </summary>
public sealed record SimPointPointResult(SimulationPoint Point, long MeasuredInstructions, RevolutionResult Revolution) {
    /// <summary>Cycles per instruction over the measured interval.</summary>
    public double Cpi => MeasuredInstructions > 0 ? (double)Revolution.TotalTicks / MeasuredInstructions : double.NaN;
}

/// <summary>
///     Result of <see cref="Experiment.RunWithSimPointCheckpoints" />: the SimPoint phase analysis,
///     the per-point detailed measurement, and the weighted whole-program CPI estimate.
/// </summary>
public sealed record SimPointCheckpointResult(
    SimPointResult SimPoints,
    IReadOnlyList<SimPointPointResult> PointResults,
    double EstimatedCpi
) {
    /// <summary>Whole-program IPC estimate (1 / <see cref="EstimatedCpi" />).</summary>
    public double EstimatedIpc => 1.0 / EstimatedCpi;
}

/// <summary>
///     Aggregated results from an <see cref="Experiment" /> across multiple hardware configurations.
/// </summary>
public sealed class ExperimentResult {
    internal ExperimentResult(IReadOnlyList<RunRecord> runs) => Runs = runs;
    public IReadOnlyList<RunRecord> Runs { get; }

    /// <summary>
    ///     Emits a CSV table with one row per run. Columns are drawn from the union of all
    ///     counter/dial names across every gear snapshot in every run.
    /// </summary>
    public string ToCsv() {
        (List<string> counterCols, List<string> dialCols) = CollectColumns();
        var sb = new StringBuilder();

        // Header
        sb.Append("name");
        foreach (string c in counterCols) sb.Append(',').Append(c);
        foreach (string d in dialCols) sb.Append(',').Append(d);
        sb.AppendLine();

        // Rows
        foreach (RunRecord run in Runs) {
            sb.Append(run.Name);
            Dictionary<string, long> counters = MergeCounters(run.Result);
            Dictionary<string, double> dials = MergeDials(run.Result);
            foreach (string c in counterCols) {
                sb.Append(',');
                if (counters.TryGetValue(c, out long v)) sb.Append(v);
            }

            foreach (string d in dialCols) {
                sb.Append(',');
                if (dials.TryGetValue(d, out double v)) sb.Append(v.ToString("G6"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Emits a GitHub-flavoured Markdown table with the same columns as <see cref="ToCsv" />.
    /// </summary>
    public string ToMarkdownTable() {
        (List<string> counterCols, List<string> dialCols) = CollectColumns();
        var sb = new StringBuilder();

        // Header row
        sb.Append("| name");
        foreach (string c in counterCols) sb.Append(" | ").Append(c);
        foreach (string d in dialCols) sb.Append(" | ").Append(d);
        sb.AppendLine(" |");

        // Separator row
        sb.Append("|---");
        foreach (string _ in counterCols) sb.Append("|---");
        foreach (string _ in dialCols) sb.Append("|---");
        sb.AppendLine("|");

        // Data rows
        foreach (RunRecord run in Runs) {
            sb.Append("| ").Append(run.Name);
            Dictionary<string, long> counters = MergeCounters(run.Result);
            Dictionary<string, double> dials = MergeDials(run.Result);
            foreach (string c in counterCols) {
                sb.Append(" | ");
                if (counters.TryGetValue(c, out long v)) sb.Append(v);
            }

            foreach (string d in dialCols) {
                sb.Append(" | ");
                if (dials.TryGetValue(d, out double v)) sb.Append(v.ToString("G6"));
            }

            sb.AppendLine(" |");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Emits a CSV table with one row per (run, time-series point). Returns an empty
    ///     string if no run has time-series data.
    ///     Columns: name, tick, then the same counter/dial columns as <see cref="ToCsv" />.
    /// </summary>
    public string ToTimeSeriesCsv() {
        // Only include runs that have time series.
        List<RunRecord> runsWithTs = Runs.Where(r => r.Result.TimeSeries is { Count: > 0, }).ToList();
        if (runsWithTs.Count == 0) return string.Empty;

        (List<string> counterCols, List<string> dialCols) = CollectColumns();
        var sb = new StringBuilder();

        // Header
        sb.Append("name,tick");
        foreach (string c in counterCols) sb.Append(',').Append(c);
        foreach (string d in dialCols) sb.Append(',').Append(d);
        sb.AppendLine();

        foreach (RunRecord run in runsWithTs)
        foreach (TimeSeriesPoint point in run.Result.TimeSeries!) {
            sb.Append(run.Name).Append(',').Append(point.Tick);
            Dictionary<string, long> counters = MergeCounters(point.Snapshots);
            Dictionary<string, double> dials = MergeDials(point.Snapshots);
            foreach (string c in counterCols) {
                sb.Append(',');
                if (counters.TryGetValue(c, out long v)) sb.Append(v);
            }

            foreach (string d in dialCols) {
                sb.Append(',');
                if (dials.TryGetValue(d, out double v)) sb.Append(v.ToString("G6"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Collect the union of all counter/dial column names across all runs and snapshots.
    // Column names are qualified as "{gearRelPath}.{metric}" to avoid collisions when
    // multiple gears share a metric name (e.g. hits, misses on different cache levels).
    private (List<string> counters, List<string> dials) CollectColumns() {
        var counters = new SortedSet<string>();
        var dials = new SortedSet<string>();
        foreach (RunRecord run in Runs)
        foreach (DialBoardSnapshot snap in run.Result.Snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach (string k in snap.Counters.Keys) counters.Add($"{prefix}.{k}");
            foreach (string k in snap.Dials.Keys) dials.Add($"{prefix}.{k}");
        }

        return ([..counters,], [..dials,]);
    }

    // Counter values are keyed as "{gearRelPath}.{metric}" to match CollectColumns.
    private static Dictionary<string, long> MergeCounters(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, long>();
        foreach (DialBoardSnapshot snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, long value) in snap.Counters) {
                var col = $"{prefix}.{key}";
                merged[col] = merged.GetValueOrDefault(col) + value;
            }
        }

        return merged;
    }

    private static Dictionary<string, long> MergeCounters(RevolutionResult result) =>
        MergeCounters(result.Snapshots);

    // Last non-zero dial value wins (dials are derived — summing doesn't make sense).
    private static Dictionary<string, double> MergeDials(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, double>();
        foreach (DialBoardSnapshot snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, double value) in snap.Dials)
                if (value != 0.0)
                    merged[$"{prefix}.{key}"] = value;
        }

        return merged;
    }

    private static Dictionary<string, double> MergeDials(RevolutionResult result) =>
        MergeDials(result.Snapshots);

    // Strip the train-root prefix from a gear's OwnerPath so columns stay short.
    // "five_stage.pipeline" → "pipeline", "five_stage.l2" → "l2".
    private static string GearRelPath(string ownerPath) {
        int dot = ownerPath.IndexOf('.');
        return dot < 0 ? ownerPath : ownerPath[(dot + 1)..];
    }
}