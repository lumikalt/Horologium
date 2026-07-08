using System.Numerics;

namespace Orrery.Cache;

/// <summary>
/// Reference Prediction Table (RPT) stride prefetcher. Tracks (lastAddress, stride,
/// confidence) per PC; issues a prefetch at address+stride once the stride is seen
/// twice (confidence ≥ 2 on a 0–3 saturating counter).
/// </summary>
public sealed class StridePrefetcher : IPrefetcher {
    private struct RptEntry {
        public ulong LastAddr;
        public long Stride;
        public int Confidence;   // 0–3; prefetch when ≥ 2
        public bool Initialized; // false on first access → skip stride computation
    }

    private readonly RptEntry[] _table;
    private readonly int _mask;

    public StridePrefetcher(int tableSize = 64) {
        if (!BitOperations.IsPow2(tableSize))
            throw new ArgumentException("tableSize must be a power of 2.", nameof(tableSize));
        _table = new RptEntry[tableSize];
        _mask = tableSize - 1;
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        int idx = (int)(pc >> 2) & _mask;
        ref RptEntry e = ref _table[idx];

        if (!e.Initialized) {
            // First access from this PC: record address, don't compute a stride from the
            // zero-initialized LastAddr (that would create a spurious span-of-address-space stride).
            e.LastAddr = address;
            e.Initialized = true;
            return 0;
        }

        long stride = (long)address - (long)e.LastAddr;
        if (stride == e.Stride) {
            if (e.Confidence < 3) e.Confidence++;
        }
        else {
            e.Stride = stride;
            if (e.Confidence > 0) e.Confidence--;
        }

        e.LastAddr = address;

        if (e.Confidence >= 2 && e.Stride != 0 && !targets.IsEmpty) {
            targets[0] = (ulong)((long)address + e.Stride);
            return 1;
        }
        return 0;
    }
}