using System.Text;

namespace Orrery.Observation;

/// <summary>
/// The collection of all observable metrics on a Gear.
/// <para>
/// A DialBoard owns a Gear's Counters and Dials, and exposes them
/// for querying by name. At the end of a Revolution, the Train calls
/// Snapshot() to capture a point-in-time reading of all values.
/// </para>
/// </summary>
public sealed class DialBoard {
    private readonly Dictionary<string, Counter> _counters = new();
    private readonly Dictionary<string, Dial> _dials = new();
    private readonly Dictionary<string, Histogram> _histograms = new();

    public string OwnerPath { get; }

    public DialBoard(string ownerPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPath);
        OwnerPath = ownerPath;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Registers a counter. Throws if the name is already taken.</summary>
    public Counter AddCounter(string name, string description = "") {
        if (_counters.ContainsKey(name))
            throw new InvalidOperationException(
                $"DialBoard '{OwnerPath}' already has a counter named '{name}'."
            );

        var counter = new Counter(name, description);
        _counters[name] = counter;
        return counter;
    }

    /// <summary>Registers a dial. Throws if the name is already taken.</summary>
    public Dial AddDial(string name, Func<double> expression, string description = "") {
        if (_dials.ContainsKey(name))
            throw new InvalidOperationException(
                $"DialBoard '{OwnerPath}' already has a dial named '{name}'."
            );

        var dial = new Dial(name, expression, description);
        _dials[name] = dial;
        return dial;
    }

    /// <summary>Registers a histogram. Throws if the name is already taken.</summary>
    public Histogram AddHistogram(string name, string description = "") {
        if (_histograms.ContainsKey(name))
            throw new InvalidOperationException(
                $"DialBoard '{OwnerPath}' already has a histogram named '{name}'."
            );

        var histogram = new Histogram(name, description);
        _histograms[name] = histogram;
        return histogram;
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    public Counter GetCounter(string name) =>
        _counters.TryGetValue(name, out Counter? c)
            ? c
            : throw new KeyNotFoundException(
                $"DialBoard '{OwnerPath}' has no counter named '{name}'."
            );

    public Dial GetDial(string name) =>
        _dials.TryGetValue(name, out Dial? d)
            ? d
            : throw new KeyNotFoundException(
                $"DialBoard '{OwnerPath}' has no dial named '{name}'."
            );

    public Histogram GetHistogram(string name) =>
        _histograms.TryGetValue(name, out Histogram? h)
            ? h
            : throw new KeyNotFoundException(
                $"DialBoard '{OwnerPath}' has no histogram named '{name}'."
            );

    // ── Snapshot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures a point-in-time snapshot of all counters and dials.
    /// Called by the Train at the end of a Revolution.
    /// </summary>
    public DialBoardSnapshot Snapshot() =>
        new(
            OwnerPath,
            _counters.ToDictionary(kv => kv.Key, kv => kv.Value.Value),
            _dials.ToDictionary(kv => kv.Key, kv => kv.Value.Read()),
            _histograms.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, long>)kv.Value.Buckets
                                                           .ToDictionary(b => b.Key, b => b.Value)
            )
        );

    /// <summary>Resets all counters and histograms. Called between Revolutions.</summary>
    public void Reset() {
        foreach (Counter c in _counters.Values) c.Reset();
        foreach (Histogram h in _histograms.Values) h.Reset();
    }
}

/// <summary>
/// An immutable point-in-time reading of a DialBoard.
/// Returned by the Train at the end of each Revolution.
/// </summary>
public sealed record DialBoardSnapshot(
    string OwnerPath,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, double> Dials,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Histograms
) {
    /// <summary>
    /// Returns a new snapshot whose counter and histogram values are
    /// <c>this − baseline</c>. Dials (which are rates, not totals) are
    /// kept from <c>this</c>. Used to extract measurement-phase stats
    /// from a run that included a warmup phase.
    /// </summary>
    public DialBoardSnapshot Subtract(DialBoardSnapshot baseline) =>
        new(
            OwnerPath,
            Counters.ToDictionary(
                kv => kv.Key,
                kv => kv.Value - baseline.Counters.GetValueOrDefault(kv.Key)
            ),
            Dials,
            Histograms.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, long>)kv.Value.ToDictionary(
                    b => b.Key,
                    b => b.Value - (baseline.Histograms.TryGetValue(kv.Key, out IReadOnlyDictionary<string, long>? bb)
                        ? bb.GetValueOrDefault(b.Key)
                        : 0)
                )
            )
        );

    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine($"[{OwnerPath}]");
        foreach ((string k, long v) in Counters) sb.AppendLine($"  {k} = {v}");
        foreach ((string k, double v) in Dials) sb.AppendLine($"  {k} = {v:F4}");
        foreach ((string hName, IReadOnlyDictionary<string, long> buckets) in Histograms) {
            sb.AppendLine($"  {hName}:");
            foreach ((string bk, long bv) in buckets.OrderByDescending(p => p.Value)) sb.AppendLine($"    {bk} = {bv}");
        }

        return sb.ToString();
    }
}