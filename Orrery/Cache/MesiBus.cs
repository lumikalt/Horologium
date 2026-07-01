using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Snooping coherence bus for the MESI protocol.
/// All <see cref="MesiCache"/> instances sharing a physical address space register here.
/// Bus transactions are synchronous — suited for direct-drive multi-hart simulation where
/// no actual parallelism exists between caches within a single kernel step.
/// </summary>
public sealed class MesiBus {
    private readonly IMemory _backing;
    private readonly ReservationTable? _table;
    private readonly List<MesiCache> _caches = new();
    private int _blockSize; // set from first registered cache

    public IMemory Backing => _backing;

    /// <param name="backing">Shared physical memory behind all caches on this bus.</param>
    /// <param name="table">
    /// Optional reservation table for LR/SC coherence.
    /// When set, <see cref="BusReadInvalidate"/> cancels any reservation whose granule
    /// falls within the invalidated line — covering the common write-miss and S→M upgrade
    /// paths without requiring <see cref="ReservationAwareMemory"/> in the memory stack.
    /// </param>
    public MesiBus(IMemory backing, ReservationTable? table = null) {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
        _table = table;
    }

    internal void Register(MesiCache cache) {
        if (_caches.Count == 0) _blockSize = cache.BlockBytes;
        _caches.Add(cache);
    }

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a read miss.
    /// M-state holders write back to backing and downgrade to S; E-state holders downgrade to S.
    /// Returns true if any peer held the line — the requester should install the line as S, not E.
    /// </summary>
    internal bool BusRead(MesiCache requester, ulong lineBase) {
        var anyHeld = false;
        foreach (MesiCache c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            anyHeld |= c.SnoopRead(lineBase);
        }

        return anyHeld;
    }

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a write (upgrade to M).
    /// M-state holders write back then transition to I; E/S-state holders transition to I.
    /// If a <see cref="ReservationTable"/> was supplied at construction, any LR/SC reservation
    /// whose 4-byte granule falls within the invalidated cache line is also cancelled here.
    /// </summary>
    internal void BusReadInvalidate(MesiCache requester, ulong lineBase) {
        foreach (MesiCache c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            c.SnoopInvalidate(lineBase);
        }

        _table?.InvalidateAt(lineBase, _blockSize);
    }

    /// <summary>
    /// Invalidates the given line in ALL registered caches, including the caller's.
    /// Used when data is written directly to backing memory via <see cref="IMemory.Load"/>,
    /// bypassing the coherence path.
    /// </summary>
    internal void BusLoad(ulong lineBase) {
        foreach (MesiCache c in _caches) c.SnoopInvalidate(lineBase);
    }

    /// <summary>
    /// Called when a cache silently upgrades an Exclusive line to Modified without
    /// issuing a BusReadInvalidate (no peers to snoop).
    /// Cancels any LR/SC reservation whose granule falls within the line so that
    /// a remote hart's reservation on the same line is invalidated even though no
    /// bus snoop was issued.
    /// </summary>
    internal void BusSilentUpgrade(ulong lineBase) =>
        _table?.InvalidateAt(lineBase, _blockSize);

    /// <summary>Writes a dirty cache block to backing memory.</summary>
    internal void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        _backing.Load(lineBase, block);
}