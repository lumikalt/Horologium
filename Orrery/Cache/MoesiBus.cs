using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Snooping coherence bus for the MOESI protocol.
/// All <see cref="MoesiCache"/> instances sharing a physical address space register here.
/// Bus transactions are synchronous — suited for direct-drive multi-hart simulation where
/// no actual parallelism exists between caches within a single kernel step.
/// </summary>
public sealed class MoesiBus : IBus {
    private readonly IMemory _backing;
    private readonly ReservationTable? _table;
    private readonly List<MoesiCache> _caches = new();
    private int _blockSize; // set from first registered cache

    public IMemory Backing => _backing;

    /// <param name="backing">Shared physical memory behind all caches on this bus.</param>
    /// <param name="table">
    /// Optional reservation table for LR/SC coherence.
    /// When set, <see cref="BusReadInvalidate"/> cancels any reservation whose granule
    /// falls within the invalidated line — covering the common write-miss and S→M upgrade
    /// paths without requiring <see cref="ReservationAwareMemory"/> in the memory stack.
    /// </param>
    public MoesiBus(IMemory backing, ReservationTable? table = null) {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
        _table = table;
    }

    public void Register(MoesiCache cache) {
        if (_caches.Count == 0) _blockSize = cache.BlockBytes;
        _caches.Add(cache);
    }

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a read miss.
    /// A peer holding M/O supplies the block into <paramref name="dest"/> and keeps it as
    /// Owned (no writeback to backing); an E holder supplies and downgrades to S. At most
    /// one such peer exists per line. S holders do not supply.
    /// </summary>
    public BusReadResponse BusRead(MoesiCache requester, ulong lineBase, Span<byte> dest) {
        var anyHeld = false;
        var supplied = false;
        foreach (MoesiCache c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            SnoopResult result = c.SnoopRead(lineBase, dest);
            anyHeld |= result != SnoopResult.Miss;
            supplied |= result is SnoopResult.Supplied or SnoopResult.SuppliedOwned;
        }

        return supplied ? BusReadResponse.SharedSupplied
            : anyHeld   ? BusReadResponse.Shared
                          : BusReadResponse.NoSharers;
    }

    /// <summary>Forces any dirty (M/O) holder — including the requester — to write the
    /// line back to backing, with no coherence state change.</summary>
    public void BusSyncToBacking(ulong lineBase) {
        foreach (MoesiCache c in _caches) c.SnoopWriteback(lineBase);
    }

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a write (upgrade to M).
    /// Dirty (M/O) holders write back then transition to I; E/S-state holders transition to I.
    /// If a <see cref="ReservationTable"/> was supplied at construction, any LR/SC reservation
    /// whose 4-byte granule falls within the invalidated cache line is also cancelled here.
    /// </summary>
    public void BusReadInvalidate(MoesiCache requester, ulong lineBase) {
        foreach (MoesiCache c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            c.SnoopInvalidate(lineBase);
        }

        _table?.InvalidateAt(lineBase, _blockSize);
    }

    /// <summary>
    /// Read-for-ownership (write miss): all peers invalidate; an M/O/E holder forwards
    /// the block into <paramref name="dest"/> instead of writing back to backing. At most
    /// one such peer exists per line. Returns true if <paramref name="dest"/> was filled.
    /// </summary>
    public bool BusReadForOwnership(MoesiCache requester, ulong lineBase, Span<byte> dest) {
        var supplied = false;
        foreach (MoesiCache c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            supplied |= c.SnoopInvalidateForward(lineBase, dest);
        }

        _table?.InvalidateAt(lineBase, _blockSize);
        return supplied;
    }

    /// <summary>
    /// Invalidates the given line in ALL registered caches, including the caller's.
    /// Used when data is written directly to backing memory via <see cref="IMemory.Load"/>,
    /// bypassing the coherence path.
    /// </summary>
    public void BusLoad(ulong lineBase) {
        foreach (MoesiCache c in _caches) c.SnoopInvalidate(lineBase);
    }

    /// <summary>
    /// Called when a cache silently upgrades an Exclusive line to Modified without
    /// issuing a BusReadInvalidate (no peers to snoop).
    /// Cancels any LR/SC reservation whose granule falls within the line so that
    /// a remote hart's reservation on the same line is invalidated even though no
    /// bus snoop was issued.
    /// </summary>
    public void BusSilentUpgrade(ulong lineBase) =>
        _table?.InvalidateAt(lineBase, _blockSize);

    /// <summary>Writes a dirty cache block to backing memory.</summary>
    public void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        _backing.Load(lineBase, block);
}