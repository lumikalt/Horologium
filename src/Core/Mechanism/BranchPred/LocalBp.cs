namespace Mechanism.BranchPred;

/// <summary>
///     Local predictor with a single counter per PC.
/// </summary>
public sealed class NBitBp : IBranchPredictor {
    private readonly ulong[] _btb;
    private readonly byte[] _counters;
    private readonly int _satMax;
    private readonly int _satThreshold;

    /// <summary>
    ///     Constructs a predictor with the given number of states and BTB size.
    /// </summary>
    /// <param name="bits">
    ///     Number of states.
    /// </param>
    /// <param name="tableSize">
    ///     Entries in the BTB.
    /// </param>
    public NBitBp(int bits = 2, int tableSize = 1024) {
        _satMax = (1 << bits) - 1;
        _satThreshold = 1 << (bits - 1); // taken if counter >= satThreshold
        _counters = new byte[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, (byte)(_satThreshold - 1)); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int idx = Index(pc);
        bool taken = _counters[idx] >= _satThreshold;
        return new BranchPrediction(taken, taken ? _btb[idx] : pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        // Only taken outcomes carry a real target; a not-taken outcome's "next ip" is just the
        // fallthrough address, and recording it here would clobber the BTB entry a subsequent
        // taken prediction at the same PC relies on.
        if (taken) _btb[idx] = actualTarget;
        switch (taken) {
            case true when _counters[idx] < _satMax: _counters[idx]++; break;
            case false when _counters[idx] > 0:      _counters[idx]--; break;
        }
    }

    /// <inheritdoc />
    public void WriteState(BinaryWriter w) {
        w.Write(_counters.Length);
        w.Write(_counters);
        foreach (ulong target in _btb) w.Write(target);
    }

    /// <inheritdoc />
    public void ReadState(BinaryReader r) {
        int count = r.ReadInt32();
        int n = Math.Min(count, _counters.Length);
        byte[] counters = r.ReadBytes(count);
        Array.Copy(counters, _counters, n);
        for (var i = 0; i < count; i++) {
            ulong target = r.ReadUInt64();
            if (i < _btb.Length) _btb[i] = target;
        }
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_counters.Length);
}