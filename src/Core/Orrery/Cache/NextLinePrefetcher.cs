namespace Orrery.Cache;

/// <summary>
///     Always prefetches the cache line immediately following the current access.
///     Effective for sequential and streaming workloads; harmless but wasteful on random-access code.
/// </summary>
public sealed class NextLinePrefetcher : IPrefetcher {
    private readonly int _blockBytes;

    public NextLinePrefetcher(int blockBytes) => _blockBytes = blockBytes;

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        if (targets.IsEmpty) return 0;
        ulong lineBase = address & ~(ulong)(_blockBytes - 1);
        targets[0] = lineBase + (ulong)_blockBytes;
        return 1;
    }
}