namespace Orrery.Observation;

/// <summary>
/// The collection of all observable metrics on a Gear.
///
/// A DialBoard owns a Gear's Counters and Dials, and exposes them
/// for querying by name. At the end of a Revolution, the Train calls
/// Snapshot() to capture a point-in-time reading of all values.
/// </summary>
public sealed class DialBoard {
    private readonly Dictionary<string, Counter> _counters = new();
    private readonly Dictionary<string, Dial> _dials = new();

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

    public IReadOnlyDictionary<string, Counter> Counters => _counters;
    public IReadOnlyDictionary<string, Dial> Dials => _dials;

    // ── Snapshot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures a point-in-time snapshot of all counters and dials.
    /// Called by the Train at the end of a Revolution.
    /// </summary>
    public DialBoardSnapshot Snapshot() =>
        new(
            OwnerPath,
            _counters.ToDictionary(kv => kv.Key, kv => kv.Value.Value),
            _dials.ToDictionary(kv => kv.Key, kv => kv.Value.Read())
        );

    /// <summary>Resets all counters to zero. Called between Revolutions.</summary>
    public void Reset() => _counters.Values.ToList().ForEach(c => c.Reset());
}

/// <summary>
/// An immutable point-in-time reading of a DialBoard.
/// Returned by the Train at the end of each Revolution.
/// </summary>
public sealed record DialBoardSnapshot(
    string OwnerPath,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, double> Dials
) {
    public override string ToString() {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{OwnerPath}]");
        foreach ((string k, long v) in Counters) sb.AppendLine($"  {k} = {v}");
        foreach ((string k, double v) in Dials) sb.AppendLine($"  {k} = {v:F4}");
        return sb.ToString();
    }
}