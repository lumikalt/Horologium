namespace Orrery.Cache;

/// <summary>
///     FIFO replacement: evicts the oldest-installed block, regardless of subsequent hits.
///     Maintains a per-set circular pointer; RecordInstall advances it; RecordHit is a no-op.
///     GetMetadata returns a pseudo-age compatible with the LRU convention: 0 = most recently
///     installed, ways−1 = next to be evicted.
/// </summary>
public sealed class FifoPolicy : IReplacementPolicy {
    private readonly int[] _ptr; // per-set next-evict pointer
    private readonly int _ways;

    public FifoPolicy(int sets, int ways) {
        _ways = ways;
        _ptr = new int[sets];
    }

    public void RecordHit(int set, int way) { } // hits have no effect in FIFO

    public int ChooseVictim(int set) => _ptr[set];

    public void RecordInstall(int set, int way) =>
        _ptr[set] = (way + 1) % _ways;

    // Pseudo-age: 0 = just installed (MRU), ways−1 = next victim (oldest).
    public int GetMetadata(int set, int way) =>
        _ways - 1 - (way - _ptr[set] + _ways) % _ways;

    /// <inheritdoc />
    public void WriteState(BinaryWriter w) {
        w.Write(_ptr.Length);
        foreach (int p in _ptr) w.Write(p);
    }

    /// <inheritdoc />
    public void ReadState(BinaryReader r) {
        int sets = r.ReadInt32();
        int n = Math.Min(sets, _ptr.Length);
        for (var s = 0; s < sets; s++) {
            int p = r.ReadInt32();
            if (s < n) _ptr[s] = p;
        }
    }
}