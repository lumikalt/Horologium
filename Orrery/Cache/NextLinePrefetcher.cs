namespace Orrery.Cache;

/// <summary>
/// Always prefetches the cache line immediately following the current access.
/// Effective for sequential and streaming workloads; harmless but wasteful on random-access code.
/// </summary>
public sealed class NextLinePrefetcher : IPrefetcher {
    private readonly int _blockBytes;

    public NextLinePrefetcher(int blockBytes) => _blockBytes = blockBytes;

    public ulong? OnAccess(ulong pc, ulong address, bool wasHit) {
        ulong lineBase = address & ~(ulong)(_blockBytes - 1);
        return lineBase + (ulong)_blockBytes;
    }
}
