using Orrery.Train;

namespace Orrery.Observation;

/// <summary>
///     A named signal sampled over simulation time, extracted from a Revolution's
///     periodic time-series snapshots. <see cref="Ticks" /> are relative to the start
///     of the measurement phase; <see cref="Values" /> holds one sample per snapshot.
/// </summary>
public sealed record Signal(string Name, double[] Ticks, double[] Values);

/// <summary>
///     Turns the cumulative <see cref="RevolutionResult.TimeSeries" /> snapshots into
///     plottable per-window signals for a waveform viewer.
///     <para>
///         Three kinds of signal are offered, distinguished by a suffix in the name:
///         <list type="bullet">
///             <item>
///                 <c>{gear}.{counter}</c> — the counter's per-window delta
///                 (or its raw cumulative value when requested)
///             </item>
///             <item>
///                 <c>{gear}.{metric} (windowed)</c> — a ratio computed over each window:
///                 <c>ipc</c> from <c>retired</c>/<c>cycles</c>, and <c>{x}_hit_rate</c> from any
///                 <c>{x}_hits</c>/<c>{x}_misses</c> counter pair on the same gear
///             </item>
///             <item>
///                 <c>{gear}.{dial} (cumulative)</c> — the dial's value at each snapshot,
///                 which is cumulative from run start by construction
///             </item>
///         </list>
///         Windows with no activity (zero denominator) carry the previous window's ratio
///         forward so halted gears plot as a flat line rather than a dip to zero.
///     </para>
/// </summary>
public static class SignalExtractor {
    private const string WindowedSuffix = " (windowed)";
    private const string CumulativeSuffix = " (cumulative)";

    /// <summary>
    ///     Lists every signal name extractable from the result's time series.
    ///     Returns an empty list when no time series was recorded.
    /// </summary>
    public static IReadOnlyList<string> ListSignals(RevolutionResult result) {
        if (result.TimeSeries is not { Count: > 0, } ts) return [];

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (DialBoardSnapshot snap in ts[0].Snapshots) {
            string gear = GearRelPath(snap.OwnerPath);
            foreach (string counter in snap.Counters.Keys) {
                names.Add($"{gear}.{counter}");
                if (counter.EndsWith("_hits")) {
                    string stem = counter[..^"_hits".Length];
                    if (snap.Counters.ContainsKey($"{stem}_misses"))
                        names.Add($"{gear}.{stem}_hit_rate{SignalExtractor.WindowedSuffix}");
                }
            }

            if (snap.Counters.ContainsKey("retired") && snap.Counters.ContainsKey("cycles"))
                names.Add($"{gear}.ipc{SignalExtractor.WindowedSuffix}");

            foreach (string dial in snap.Dials.Keys) names.Add($"{gear}.{dial}{SignalExtractor.CumulativeSuffix}");
        }

        return [..names,];
    }

    /// <summary>
    ///     Extracts one signal by a name obtained from <see cref="ListSignals" />.
    ///     Returns null when the result has no time series or the name is unknown.
    /// </summary>
    /// <param name="result">The revolution result to extract a time series from.</param>
    /// <param name="name">A signal name obtained from <see cref="ListSignals" />.</param>
    /// <param name="cumulativeCounters">
    ///     When true, plain counter signals keep their raw cumulative values instead
    ///     of being split into per-window deltas. Windowed and dial signals
    ///     are unaffected.
    /// </param>
    public static Signal? Extract(RevolutionResult result, string name, bool cumulativeCounters = false) {
        if (result.TimeSeries is not { Count: > 0, } ts) return null;

        double[] ticks = [..ts.Select(p => (double)p.Tick),];

        if (name.EndsWith(SignalExtractor.CumulativeSuffix)) {
            (string gear, string dial) = SplitName(name[..^SignalExtractor.CumulativeSuffix.Length]);
            double[]? values = DialSeries(ts, gear, dial);
            return values is null ? null : new Signal(name, ticks, values);
        }

        if (name.EndsWith(SignalExtractor.WindowedSuffix)) {
            (string gear, string metric) = SplitName(name[..^SignalExtractor.WindowedSuffix.Length]);
            (string numer, string[] denom) = metric switch {
                "ipc" => ("retired", (string[])["cycles",]),
                _ when metric.EndsWith("_hit_rate") =>
                    ($"{metric[..^"_hit_rate".Length]}_hits",
                     (string[])[$"{metric[..^"_hit_rate".Length]}_hits", $"{metric[..^"_hit_rate".Length]}_misses",]),
                _ => (null!, null!),
            };
            if (numer is null) return null;

            long[]? num = CounterSeries(ts, gear, numer);
            if (num is null) return null;
            var den = new long[ts.Count];
            foreach (string d in denom) {
                long[]? part = CounterSeries(ts, gear, d);
                if (part is null) return null;
                for (var i = 0; i < den.Length; i++) den[i] += part[i];
            }

            return new Signal(name, ticks, WindowedRatio(num, den));
        }

        {
            (string gear, string counter) = SplitName(name);
            long[]? values = CounterSeries(ts, gear, counter);
            if (values is null) return null;

            var samples = new double[values.Length];
            long prev = 0;
            for (var i = 0; i < values.Length; i++) {
                samples[i] = cumulativeCounters ? values[i] : values[i] - prev;
                prev = values[i];
            }

            return new Signal(name, ticks, samples);
        }
    }

    // Per-window ratio of two cumulative series. Both start implicitly at 0, so
    // the first window's delta is the first sample itself.
    private static double[] WindowedRatio(long[] num, long[] den) {
        var values = new double[num.Length];
        long prevNum = 0, prevDen = 0;
        var carried = 0.0;
        for (var i = 0; i < num.Length; i++) {
            long dDen = den[i] - prevDen;
            if (dDen != 0) carried = (num[i] - prevNum) / (double)dDen;
            values[i] = carried;
            prevNum = num[i];
            prevDen = den[i];
        }

        return values;
    }

    private static long[]? CounterSeries(IReadOnlyList<TimeSeriesPoint> ts, string gear, string counter) {
        var values = new long[ts.Count];
        for (var i = 0; i < ts.Count; i++) {
            DialBoardSnapshot? snap = FindGear(ts[i], gear);
            if (snap is null || !snap.Counters.TryGetValue(counter, out values[i])) return null;
        }

        return values;
    }

    private static double[]? DialSeries(IReadOnlyList<TimeSeriesPoint> ts, string gear, string dial) {
        var values = new double[ts.Count];
        for (var i = 0; i < ts.Count; i++) {
            DialBoardSnapshot? snap = FindGear(ts[i], gear);
            if (snap is null || !snap.Dials.TryGetValue(dial, out values[i])) return null;
        }

        return values;
    }

    private static DialBoardSnapshot? FindGear(TimeSeriesPoint point, string gear) =>
        point.Snapshots.FirstOrDefault(s => GearRelPath(s.OwnerPath) == gear);

    // Counter and dial names contain no dots, so the metric is everything after
    // the last dot and the gear path is everything before it.
    private static (string Gear, string Metric) SplitName(string name) {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? ("", name) : (name[..dot], name[(dot + 1)..]);
    }

    // Strip the train-root prefix from a gear's OwnerPath, matching the column
    // naming used by ExperimentResult: "five_stage.pipeline" → "pipeline".
    private static string GearRelPath(string ownerPath) {
        int dot = ownerPath.IndexOf('.');
        return dot < 0 ? ownerPath : ownerPath[(dot + 1)..];
    }
}