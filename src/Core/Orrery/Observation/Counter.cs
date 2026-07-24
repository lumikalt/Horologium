namespace Orrery.Observation;

/// <summary>
///     A monotonically increasing integer statistic.
///     Counters may only be incremented — never decremented or reset mid-revolution.
///     This constraint makes them safe to snapshot at any time.
/// </summary>
public sealed class Counter {
    public Counter(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    private string Name { get; }

    /// <summary>The current value of this counter.</summary>
    public long Value { get; private set; }

    /// <summary>Increments the counter by 1.</summary>
    public void Increment() => Value++;

    /// <summary>Increments the counter by a given amount.</summary>
    public void IncrementBy(long amount) {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                $"Counter '{Name}' cannot be decremented. Amount must be non-negative."
            );
        Value += amount;
    }

    /// <summary>
    ///     Resets the counter to zero. Only valid between Revolutions —
    ///     the Train calls this during its Reset pass, never mid-run.
    /// </summary>
    internal void Reset() => Value = 0;

    public override string ToString() => $"{Name} = {Value}";
}