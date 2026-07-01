using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Cache coherence bus protocol. Implemented by <see cref="MesiBus"/> and
/// (in two-phase concurrent mode) by <see cref="DeferredBus"/>.
/// </summary>
public interface IBus {
    /// <summary>Shared physical memory behind all caches on this bus.</summary>
    IMemory Backing { get; }

    /// <summary>Registers a cache with this bus. Called from <see cref="MesiCache"/> constructors.</summary>
    void Register(MesiCache cache);

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a read miss.
    /// Returns true if any peer held the line (requester should install as S, not E).
    /// </summary>
    bool BusRead(MesiCache requester, ulong lineBase);

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a write (upgrade to M).
    /// M-state holders write back; E/S-state holders transition to I.
    /// Also cancels any LR/SC reservation whose granule falls within the line.
    /// </summary>
    void BusReadInvalidate(MesiCache requester, ulong lineBase);

    /// <summary>
    /// Called when a cache silently upgrades an Exclusive line to Modified without
    /// issuing a BusReadInvalidate. Cancels any LR/SC reservation on the line.
    /// </summary>
    void BusSilentUpgrade(ulong lineBase);

    /// <summary>
    /// Invalidates the given line in ALL registered caches, including the caller's.
    /// Used when data is written directly to backing memory via <see cref="IMemory.Load"/>.
    /// </summary>
    void BusLoad(ulong lineBase);

    /// <summary>Writes a dirty cache block to backing memory.</summary>
    void Writeback(ulong lineBase, ReadOnlySpan<byte> block);

    /// <summary>
    /// Called when a cache <em>voluntarily</em> evicts or invalidates a line — i.e. not
    /// in response to a snoop.  Allows directory-based buses to maintain precise per-line
    /// sharer sets so future bus transactions are targeted rather than broadcast.
    /// <para>
    /// Called from: cache eviction (<c>EvictWay</c>), CBO-initiated local invalidation
    /// (<c>LocalInvalidate</c> — cbo.flush / cbo.inval). <b>Not</b> called from snoop
    /// handlers (<c>SnoopRead</c>, <c>SnoopInvalidate</c>) — the directory bus already
    /// updates its state when it issues those snoops.
    /// </para>
    /// No-op on snooping buses (<see cref="MesiBus"/>, <see cref="DeferredBus"/>).
    /// </summary>
    void Evicted(MesiCache source, ulong lineBase) { }
}