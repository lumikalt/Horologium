namespace Orrery.Observation;

/// <summary>
/// A named set of accumulating per-key counts.
/// Typical use: retired-instruction counts keyed by instruction type name.
/// </summary>
public sealed class Histogram {
    private readonly Dictionary<string, long> _buckets = new();

    public string Name { get; }
    public string Description { get; }

    public Histogram(string name, string description = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
    }

    /// <summary>Increments the count for <paramref name="key"/> by one.</summary>
    public void Observe(string key) =>
        _buckets[key] = _buckets.GetValueOrDefault(key) + 1;

    /// <summary>Point-in-time snapshot of all bucket counts.</summary>
    public IReadOnlyDictionary<string, long> Buckets => _buckets;

    internal void Reset() => _buckets.Clear();

    public override string ToString() => $"{Name} ({_buckets.Count} buckets)";
}
