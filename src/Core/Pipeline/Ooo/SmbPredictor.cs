namespace Pipeline.Ooo;

/// <summary>
///     Speculative Memory Bypassing distance predictor (NoSQ — Sha, Martin &amp; Roth,
///     MICRO 2006; the load-store dependence-prediction idea traces to Tyson &amp; Austin,
///     MICRO 1997). PC-indexed table: predicts the SSN distance from a load back to the
///     store that produced its value, gated by a saturating confidence counter so a stale
///     one-shot pairing doesn't cause repeated mispredicts.
///     <para>
///         v1 scope (see README.md): path-insensitive (PC only, no history register),
///         full-word/zero-offset bypass only — the caller is responsible for checking that the
///         predicted producing store's static width matches the load's before trusting a
///         prediction, since this predictor only tracks distance/confidence.
///     </para>
/// </summary>
internal sealed class SmbPredictor {
    private readonly byte[] _confidence; // saturating counter per slot; bypass gated at >= _threshold
    private readonly ulong[] _distance;  // predicted SSN delta (load.SeqNo - producingStore.SeqNo)
    private readonly ulong[] _pc;        // PC that owns the slot; valid only when _valid[slot]
    private readonly int _tableMask;
    private readonly byte _threshold;
    private readonly bool[] _valid;

    public SmbPredictor(int tableSize = 1024, byte threshold = 2, byte maxConfidence = 7) {
        _distance = new ulong[tableSize];
        _confidence = new byte[tableSize];
        _pc = new ulong[tableSize];
        _valid = new bool[tableSize];
        _tableMask = tableSize - 1;
        _threshold = threshold;
        MaxConfidence = maxConfidence;
    }

    private byte MaxConfidence { get; }

    private int Idx(ulong pc) => (int)((pc >> 2) & (uint)_tableMask);

    /// <summary>Returns true (with the predicted SSN distance) only above the confidence threshold.</summary>
    public bool TryPredict(ulong pc, out ulong distance) {
        int idx = Idx(pc);
        if (_valid[idx] && _pc[idx] == pc && _confidence[idx] >= _threshold) {
            distance = _distance[idx];
            return true;
        }

        distance = 0;
        return false;
    }

    /// <summary>
    ///     Reinforce (or newly seed) the predicted distance for <paramref name="pc" />. Called
    ///     both to confirm a correct bypass and to retrain after a mispredict with the freshly
    ///     discovered correct distance.
    /// </summary>
    public void Train(ulong pc, ulong observedDistance) {
        int idx = Idx(pc);
        if (_valid[idx] && _pc[idx] == pc && _distance[idx] == observedDistance) {
            if (_confidence[idx] < MaxConfidence) _confidence[idx]++;
        }
        else {
            _pc[idx] = pc;
            _distance[idx] = observedDistance;
            _confidence[idx] = 1;
            _valid[idx] = true;
        }
    }

    /// <summary>
    ///     Called when verification shows the load's value did not come from any in-flight
    ///     store at all. Decays confidence without touching the distance field, so a
    ///     transiently-absent producer doesn't discard an otherwise-stable distance.
    /// </summary>
    public void TrainNoBypass(ulong pc) {
        int idx = Idx(pc);
        if (!_valid[idx] || _pc[idx] != pc) return;
        _confidence[idx] = (byte)Math.Max(0, _confidence[idx] - 1);
    }

    /// <summary>
    ///     Serializes this predictor's table for a microarchitectural checkpoint. Safe to carry
    ///     over wholesale — unlike <see cref="StoreSetPredictor" />'s LFST, <see cref="_distance" />
    ///     stores a relative SSN <em>delta</em> (load minus producing-store), not an absolute
    ///     SeqNo, so it stays meaningful across a train restart that resets SeqNo numbering.
    /// </summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_distance.Length);
        foreach (ulong d in _distance) w.Write(d);
        foreach (byte c in _confidence) w.Write(c);
        foreach (ulong pc in _pc) w.Write(pc);
        foreach (bool v in _valid) w.Write(v);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table size must match.</summary>
    public void ReadState(BinaryReader r) {
        int size = r.ReadInt32();
        int n = Math.Min(size, _distance.Length);
        for (var i = 0; i < size; i++) {
            ulong d = r.ReadUInt64();
            if (i < n) _distance[i] = d;
        }

        for (var i = 0; i < size; i++) {
            byte c = r.ReadByte();
            if (i < n) _confidence[i] = c;
        }

        for (var i = 0; i < size; i++) {
            ulong pc = r.ReadUInt64();
            if (i < n) _pc[i] = pc;
        }

        for (var i = 0; i < size; i++) {
            bool v = r.ReadBoolean();
            if (i < n) _valid[i] = v;
        }
    }
}