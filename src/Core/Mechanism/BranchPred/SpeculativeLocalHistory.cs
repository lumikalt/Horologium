namespace Mechanism.BranchPred;

/// <summary>
///     A table of per-branch (per-PC) local history shift registers with speculative and
///     committed copies — the local-history analogue of <see cref="SpeculativeGlobalHistory" />.
///     <para>
///         <see cref="Value" /> gives the working history for an entry, used for Predict-time
///         indexing. In an out-of-order pipeline entries are advanced speculatively at fetch
///         (<see cref="Speculate" />) and the whole table is restored from the committed shadow on a
///         flush (<see cref="Recover" />). <see cref="Commit" /> advances one entry's committed shadow
///         and trains against it (the branch's predict-time local history). A latch keeps the working
///         copy in lock-step with the committed shadow until the pipeline first speculates, so
///         in-order trains are bit-identical to a plain per-PC history table.
///     </para>
/// </summary>
internal sealed class SpeculativeLocalHistory {
    private readonly ulong[] _committed;
    private readonly ulong _mask;
    private readonly ulong[] _working;
    private bool _speculative;

    /// <param name="entries">Number of per-PC history registers.</param>
    /// <param name="bits">Width of each history register (LSB = most recent).</param>
    public SpeculativeLocalHistory(int entries, int bits) {
        _mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        _working = new ulong[entries];
        _committed = new ulong[entries];
    }

    /// <summary>Working history for entry <paramref name="idx" />, for Predict-time indexing.</summary>
    public ulong Value(int idx) => _working[idx];

    /// <summary>Folds a predicted direction into entry <paramref name="idx" /> at fetch.</summary>
    public void Speculate(int idx, bool taken) {
        _speculative = true;
        _working[idx] = ((_working[idx] << 1) | (taken ? 1UL : 0UL)) & _mask;
    }

    /// <summary>Discards all wrong-path speculation on a flush, restoring committed history.</summary>
    public void Recover() => Array.Copy(_committed, _working, _working.Length);

    /// <summary>
    ///     The working history for entry <paramref name="idx" />, captured (before this branch's
    ///     <see cref="Speculate" />) as a per-branch checkpoint for an execute-time partial squash.
    /// </summary>
    public ulong Capture(int idx) => _working[idx];

    /// <summary>
    ///     Rewinds entry <paramref name="idx" /> to a captured pre-branch value. Used when walking the
    ///     squashed branches youngest-to-oldest so each per-PC entry unwinds exactly.
    /// </summary>
    public void RestoreEntry(int idx, ulong value) => _working[idx] = value;

    /// <summary>
    ///     Restores entry <paramref name="idx" /> to a captured value and folds the redirecting branch's
    ///     resolved direction — the local-history analogue of <see cref="SpeculativeGlobalHistory.RestoreTo" />.
    /// </summary>
    public void RestoreEntryAndFold(int idx, ulong value, bool actualTaken) {
        _speculative = true;
        _working[idx] = ((value << 1) | (actualTaken ? 1UL : 0UL)) & _mask;
    }

    /// <summary>
    ///     Runs <paramref name="train" /> with entry <paramref name="idx" /> swapped to its committed
    ///     (predict-time) history, then advances that entry's committed shadow, and restores its
    ///     working value. In non-speculative mode the working value ends equal to the advanced
    ///     committed shadow (old behavior).
    /// </summary>
    public void Commit(int idx, bool taken, Action train) {
        ulong working = _working[idx];
        _working[idx] = _committed[idx];
        train();
        _committed[idx] = ((_committed[idx] << 1) | (taken ? 1UL : 0UL)) & _mask;
        _working[idx] = _speculative ? working : _committed[idx];
    }

    /// <summary>
    ///     Serializes both per-entry history shadows and the speculative latch — the local-history
    ///     analogue of <see cref="SpeculativeGlobalHistory.WriteState" />.
    /// </summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_working.Length);
        foreach (ulong v in _working) w.Write(v);
        foreach (ulong v in _committed) w.Write(v);
        w.Write(_speculative);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Entry count must match.</summary>
    public void ReadState(BinaryReader r) {
        int entries = r.ReadInt32();
        int n = Math.Min(entries, _working.Length);
        for (var i = 0; i < entries; i++) {
            ulong v = r.ReadUInt64();
            if (i < n) _working[i] = v;
        }

        for (var i = 0; i < entries; i++) {
            ulong v = r.ReadUInt64();
            if (i < n) _committed[i] = v;
        }

        _speculative = r.ReadBoolean();
    }
}