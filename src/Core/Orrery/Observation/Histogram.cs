using System.Runtime.InteropServices;

namespace Orrery.Observation;

/// <summary>
/// A named set of accumulating per-key counts.
/// Typical use: retired-instruction counts keyed by instruction type name.
/// </summary>
public sealed class Histogram {
    // Fast path for instruction-type counting — avoids per-call string hashing.
    private readonly Dictionary<Type, long> _typeBuckets = new();

    // Legacy path for callers that key by string (and for tests).
    private readonly Dictionary<string, long> _stringBuckets = new();

    public string Name { get; }
    public string Description { get; }

    public Histogram(string name, string description = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
    }

    /// <summary>Increments the count for <paramref name="key"/> using type-identity hashing.</summary>
    public void Observe(Type key) {
        // Single hash lookup instead of GetValueOrDefault + indexer-set (two lookups);
        // runs once per retired instruction.
        ref long count = ref CollectionsMarshal.GetValueRefOrAddDefault(_typeBuckets, key, out _);
        count++;
    }

    /// <summary>Increments the count for <paramref name="key"/> by string.</summary>
    public void Observe(string key) =>
        _stringBuckets[key] = _stringBuckets.GetValueOrDefault(key) + 1;

    /// <summary>Point-in-time snapshot of all bucket counts, keyed by name.</summary>
    public IReadOnlyDictionary<string, long> Buckets {
        get {
            if (_typeBuckets.Count == 0) return _stringBuckets;
            if (_stringBuckets.Count == 0) return _typeBuckets.ToDictionary(kv => kv.Key.Name, kv => kv.Value);
            var merged = new Dictionary<string, long>(_stringBuckets);
            foreach ((Type t, long v) in _typeBuckets) merged[t.Name] = merged.GetValueOrDefault(t.Name) + v;
            return merged;
        }
    }

    internal void Reset() {
        _typeBuckets.Clear();
        _stringBuckets.Clear();
    }

    public override string ToString() => $"{Name} ({_typeBuckets.Count + _stringBuckets.Count} buckets)";
}