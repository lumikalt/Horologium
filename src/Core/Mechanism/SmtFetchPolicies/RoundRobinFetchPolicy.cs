namespace Mechanism.SmtFetchPolicies;

/// <summary>
///     Round-robin SMT fetch policy: issue slots are distributed to harts in rotating order, and
///     the starting hart advances by one every cycle so long-run throughput is fair regardless
///     of how <c>issueWidth</c> divides the hart count. This was <c>SmtTrain</c>'s only policy
///     before <see cref="ISmtFetchPolicy" /> was introduced, and remains the default.
/// </summary>
public sealed class RoundRobinFetchPolicy : ISmtFetchPolicy {
    private int _cursor;
    private int _cycleStartHart;

    /// <inheritdoc />
    public void BeginCycle(int hartCount) {
        _cursor = _cycleStartHart;
        if (hartCount > 0) _cycleStartHart = (_cycleStartHart + 1) % hartCount;
    }

    /// <inheritdoc />
    public int SelectHart(ReadOnlySpan<bool> available) {
        int n = available.Length;
        for (var t = 0; t < n; t++) {
            int h = (_cursor + t) % n;
            if (!available[h]) continue;
            _cursor = (h + 1) % n;
            return h;
        }

        return -1;
    }

    /// <inheritdoc />
    public void OnIssued(int hart, long stallCycles) { }
}
