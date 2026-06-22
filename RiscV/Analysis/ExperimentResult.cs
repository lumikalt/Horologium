using System.Text;
using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using RiscV.Config;

namespace RiscV.Analysis;

/// <summary>
/// The collected result of one named simulation run.
/// </summary>
public sealed record RunRecord(string Name, TrainConfig Config, RevolutionResult Result);

/// <summary>
/// Aggregated results from an <see cref="Experiment"/> across multiple hardware configurations.
/// </summary>
public sealed class ExperimentResult {
    public IReadOnlyList<RunRecord> Runs { get; }

    internal ExperimentResult(IReadOnlyList<RunRecord> runs) => Runs = runs;

    /// <summary>
    /// Emits a CSV table with one row per run. Columns are drawn from the union of all
    /// counter/dial names across every gear snapshot in every run.
    /// </summary>
    public string ToCsv() {
        var (counterCols, dialCols) = CollectColumns();
        var sb = new StringBuilder();

        // Header
        sb.Append("name");
        foreach (string c in counterCols) sb.Append(',').Append(c);
        foreach (string d in dialCols) sb.Append(',').Append(d);
        sb.AppendLine();

        // Rows
        foreach (RunRecord run in Runs) {
            sb.Append(run.Name);
            var counters = MergeCounters(run.Result);
            var dials = MergeDials(run.Result);
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
    /// Emits a GitHub-flavoured Markdown table with the same columns as <see cref="ToCsv"/>.
    /// </summary>
    public string ToMarkdownTable() {
        var (counterCols, dialCols) = CollectColumns();
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
            var counters = MergeCounters(run.Result);
            var dials = MergeDials(run.Result);
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

    // Collect the union of all counter/dial column names across all runs and snapshots.
    private (List<string> counters, List<string> dials) CollectColumns() {
        var counters = new SortedSet<string>();
        var dials = new SortedSet<string>();
        foreach (RunRecord run in Runs) {
            foreach (DialBoardSnapshot snap in run.Result.Snapshots) {
                foreach (string k in snap.Counters.Keys) counters.Add(k);
                foreach (string k in snap.Dials.Keys) dials.Add(k);
            }
        }
        return ([..counters], [..dials]);
    }

    // Sum all counter values across every gear snapshot for a single run.
    private static Dictionary<string, long> MergeCounters(RevolutionResult result) {
        var merged = new Dictionary<string, long>();
        foreach (DialBoardSnapshot snap in result.Snapshots) {
            foreach ((string key, long value) in snap.Counters) {
                merged[key] = merged.GetValueOrDefault(key) + value;
            }
        }
        return merged;
    }

    // Last non-zero dial value wins (dials are derived — summing doesn't make sense).
    private static Dictionary<string, double> MergeDials(RevolutionResult result) {
        var merged = new Dictionary<string, double>();
        foreach (DialBoardSnapshot snap in result.Snapshots) {
            foreach ((string key, double value) in snap.Dials) {
                if (value != 0.0) merged[key] = value;
            }
        }
        return merged;
    }
}
