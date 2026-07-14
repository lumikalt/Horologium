namespace Mechanism.SmtFetchPolicies;

/// <summary>
///     ICOUNT SMT fetch policy (Tullsen et al., ISCA 1996): prioritizes harts that are moving
///     smoothly through the pipeline over harts that are clogging it, so one stalling thread
///     can't hog issue slots at the expense of ready ones.
///     <para>
///         The original ICOUNT counts each thread's in-flight instructions across
///         fetch/decode/rename/queue and issues from the threads with the fewest. SmtTrain's
///         barrel core has no such front end — fetch, decode, and execute happen atomically for
///         one instruction per issue slot — so there is no queue depth to count. This
///         implementation instead tracks an exponential moving average of each hart's recent
///         cache/TLB stall cycles as the occupancy proxy: a hart that just missed in cache is
///         exactly the one ICOUNT would have throttled, since a miss is what fills up the real
///         fetch/decode/queue stages the original policy counts.
///     </para>
/// </summary>
public sealed class IcountFetchPolicy : ISmtFetchPolicy {
    private const double DecayFactor = 0.5;

    private double[] _score = [];
    private int _tieBreakCursor;

    /// <inheritdoc />
    public void BeginCycle(int hartCount) {
        if (_score.Length != hartCount) _score = new double[hartCount];
    }

    /// <inheritdoc />
    public int SelectHart(ReadOnlySpan<bool> available) {
        int n = available.Length;
        var best = -1;
        for (var t = 0; t < n; t++) {
            int h = (_tieBreakCursor + t) % n;
            if (!available[h]) continue;
            if (best < 0 || _score[h] < _score[best]) best = h;
        }

        if (best >= 0) _tieBreakCursor = (best + 1) % n;
        return best;
    }

    /// <inheritdoc />
    public void OnIssued(int hart, long stallCycles) {
        _score[hart] = (_score[hart] * DecayFactor) + stallCycles;
    }
}
