namespace Mechanism.BranchPredictModels;

/// <summary>
/// A table of per-branch (per-PC) local history shift registers with speculative and
/// committed copies — the local-history analogue of <see cref="SpeculativeGlobalHistory"/>.
/// <para>
/// <see cref="Value"/> gives the working history for an entry, used for Predict-time
/// indexing. In an out-of-order pipeline entries are advanced speculatively at fetch
/// (<see cref="Speculate"/>) and the whole table is restored from the committed shadow on a
/// flush (<see cref="Recover"/>). <see cref="Commit"/> advances one entry's committed shadow
/// and trains against it (the branch's predict-time local history). A latch keeps the working
/// copy in lock-step with the committed shadow until the pipeline first speculates, so
/// in-order trains are bit-identical to a plain per-PC history table.
/// </para>
/// </summary>
internal sealed class SpeculativeLocalHistory {
    private readonly ulong _mask;
    private readonly ulong[] _working;
    private readonly ulong[] _committed;
    private bool _speculative;

    /// <param name="entries">Number of per-PC history registers.</param>
    /// <param name="bits">Width of each history register (LSB = most recent).</param>
    public SpeculativeLocalHistory(int entries, int bits) {
        _mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        _working = new ulong[entries];
        _committed = new ulong[entries];
    }

    /// <summary>Working history for entry <paramref name="idx"/>, for Predict-time indexing.</summary>
    public ulong Value(int idx) => _working[idx];

    /// <summary>Folds a predicted direction into entry <paramref name="idx"/> at fetch.</summary>
    public void Speculate(int idx, bool taken) {
        _speculative = true;
        _working[idx] = ((_working[idx] << 1) | (taken ? 1UL : 0UL)) & _mask;
    }

    /// <summary>Discards all wrong-path speculation on a flush, restoring committed history.</summary>
    public void Recover() => Array.Copy(_committed, _working, _working.Length);

    /// <summary>
    /// Runs <paramref name="train"/> with entry <paramref name="idx"/> swapped to its committed
    /// (predict-time) history, then advances that entry's committed shadow and restores its
    /// working value. In non-speculative mode the working value ends equal to the advanced
    /// committed shadow (old behaviour).
    /// </summary>
    public void Commit(int idx, bool taken, Action train) {
        ulong working = _working[idx];
        _working[idx] = _committed[idx];
        train();
        _committed[idx] = ((_committed[idx] << 1) | (taken ? 1UL : 0UL)) & _mask;
        _working[idx] = _speculative ? working : _committed[idx];
    }
}