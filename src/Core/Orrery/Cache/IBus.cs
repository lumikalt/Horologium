#region

using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Cache coherence bus protocol. Implemented by <see cref="MoesifBus" /> and
///     (in two-phase concurrent mode) by <see cref="DeferredBus" />.
/// </summary>
public interface IBus {
    /// <summary>Shared physical memory behind all caches on this bus.</summary>
    IMemory Backing { get; }

    /// <summary>
    ///     Cache line size in bytes, taken from the first registered <see cref="MoesifCache" />.
    ///     0 if no cache has registered yet (e.g. every hart on this bus is uncached) — callers
    ///     must treat 0 as "no coherence traffic possible" rather than a valid block size.
    /// </summary>
    int BlockBytes { get; }

    /// <summary>Registers a cache with this bus. Called from <see cref="MoesifCache" /> constructors.</summary>
    void Register(MoesifCache cache);

    /// <summary>
    ///     Snoops all caches except <paramref name="requester" /> for a read miss.
    ///     A peer holding the line in M, O, E, or F supplies the block cache-to-cache into
    ///     <paramref name="dest" />: M/O holders keep the dirty line as Owned (backing is not
    ///     written), E/F holders drop to S and the Forward role passes to the requester.
    ///     The response tells the requester which state to install (E, F, or S) and whether
    ///     <paramref name="dest" /> was filled.
    /// </summary>
    BusReadResponse BusRead(MoesifCache requester, ulong lineBase, Span<byte> dest);

    /// <summary>
    ///     Forces any dirty (M/O) holder of the line to write its block to backing memory,
    ///     without changing coherence state or the directory. Used before reading backing
    ///     directly (block-boundary-crossing accesses), where cache-to-cache supply does
    ///     not apply and backing must be current.
    /// </summary>
    void BusSyncToBacking(ulong lineBase);

    /// <summary>
    ///     Snoops all caches except <paramref name="requester" /> for an S/O→M upgrade or a
    ///     block-boundary-crossing write. Dirty (M/O) holders write back; all holders
    ///     transition to I. Also cancels any LR/SC reservation whose granule falls within
    ///     the line. For write misses use <see cref="BusReadForOwnership" /> instead.
    ///     <paramref name="requester" /> is null for a write that does not originate from any
    ///     <see cref="MoesifCache" /> (an uncached hart writing directly to the bus via
    ///     <see cref="BusCoherentMemory" />) — every cache is then snooped, since none is "self".
    /// </summary>
    void BusReadInvalidate(MoesifCache? requester, ulong lineBase);

    /// <summary>
    ///     Read-for-ownership (write miss): snoops all caches except <paramref name="requester" />.
    ///     All holders transition to I; an M/O/E/F holder forwards the block into
    ///     <paramref name="dest" /> instead of writing back — the requester installs the line
    ///     as Modified, so its copy becomes authoritative. Also cancels any LR/SC reservation
    ///     on the line. Returns true if <paramref name="dest" /> was filled; false means no
    ///     holder could supply and the requester must fill from backing (which is then
    ///     guaranteed current for this line, since stale backing implies an M/O holder).
    /// </summary>
    bool BusReadForOwnership(MoesifCache requester, ulong lineBase, Span<byte> dest);

    /// <summary>
    ///     Called when a cache silently upgrades an Exclusive line to Modified without
    ///     issuing a BusReadInvalidate. Cancels any LR/SC reservation on the line.
    /// </summary>
    void BusSilentUpgrade(ulong lineBase);

    /// <summary>
    ///     Invalidates the given line in ALL registered caches, including the caller's.
    ///     Used when data is written directly to backing memory via <see cref="IMemory.Load" />.
    /// </summary>
    void BusLoad(ulong lineBase);

    /// <summary>Writes a dirty cache block to backing memory.</summary>
    void Writeback(ulong lineBase, ReadOnlySpan<byte> block);

    /// <summary>
    ///     Called when a cache <em>voluntarily</em> evicts or invalidates a line — i.e. not
    ///     in response to a snoop.  Allows directory-based buses to maintain precise per-line
    ///     sharer sets so future bus transactions are targeted rather than broadcast.
    ///     <para>
    ///         Called from: cache eviction (<c>EvictWay</c>), CBO-initiated local invalidation
    ///         (<c>LocalInvalidate</c> — cbo.flush / cbo.inval). <b>Not</b> called from snoop
    ///         handlers (<c>SnoopRead</c>, <c>SnoopInvalidate</c>) — the directory bus already
    ///         updates its state when it issues those snoops.
    ///     </para>
    ///     No-op on snooping buses (<see cref="MoesifBus" />, <see cref="DeferredBus" />).
    /// </summary>
    void Evicted(MoesifCache source, ulong lineBase) { }
}