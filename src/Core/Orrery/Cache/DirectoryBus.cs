#region

using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Directory-based MOESIF coherence bus.  Maintains a per-cache-line sharer directory so
///     that <see cref="IBus.BusReadInvalidate" /> and <see cref="IBus.BusLoad" /> snoop only
///     the caches that actually hold each line — O(sharers) rather than O(all-caches).
///     <para>
///         Directory entries are kept <em>precise</em>: caches notify the directory when they
///         voluntarily evict or invalidate a line (via <see cref="IBus.Evicted" />), so the
///         sharer set never contains stale members and <see cref="IBus.BusRead" /> for a
///         forwarder-less Shared line never needs to probe peers.  Reads that hit a remote
///         M/O/E/F responder are filled cache-to-cache by that responder; a dirty owner keeps
///         the line as Owned (backing memory is not written), while a clean supplier passes
///         the Forward role to the requester.
///     </para>
///     <para>
///         This bus is designed for sequential multi-hart simulation
///         (<c>MultiHartPipeline.Run</c>).  It cannot be wrapped by
///         <see cref="DeferredBus" /> (which is hardcoded to <see cref="MoesifBus" />).
///     </para>
/// </summary>
public sealed class DirectoryBus : IBus {
    // Absent key → Uncached.  Owner is the line's designated responder — the single
    // cache holding it in M, E, O, or F — and Sharers are plain S holders.
    // (owner≠null, sharers=null)  → owner holds M, E, or F; no other copies.
    // (owner=null, sharers≠null)  → all holders in plain S (forwarder was lost, e.g.
    //                               evicted); backing memory clean.
    // (owner≠null, sharers≠null)  → owner holds O (dirty) or F (clean), sharers hold S.
    // Invariant: a Sharers set is never empty (empty sets are collapsed immediately).
    private readonly Dictionary<ulong, (MoesifCache? Owner, HashSet<MoesifCache>? Sharers)> _dir = new();
    private readonly ReservationTable? _table;
    private int _blockSize;

    public DirectoryBus(IMemory backing, ReservationTable? table = null) {
        ArgumentNullException.ThrowIfNull(backing);
        Backing = backing;
        _table = table;
    }

    public IMemory Backing { get; }

    public void Register(MoesifCache cache) {
        if (_blockSize == 0) _blockSize = cache.BlockBytes;
    }

    public BusReadResponse BusRead(MoesifCache requester, ulong lineBase, Span<byte> dest) {
        if (!_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry)) {
            _dir[lineBase] = (requester, null);
            return BusReadResponse.NoSharers;
        }

        MoesifCache? owner = entry.Owner;
        if (owner is null || ReferenceEquals(owner, requester)) {
            // No responder (all plain-S, memory clean) — or a stale owner entry for the
            // requester itself, which cannot hold the line (it just missed). The
            // requester fills from backing and becomes the new forwarder.
            HashSet<MoesifCache>? sharers = entry.Sharers;
            sharers?.Remove(requester);
            if (sharers is { Count: > 0, }) {
                _dir[lineBase] = (requester, sharers);
                return BusReadResponse.Shared;
            }

            _dir[lineBase] = (requester, null);
            return BusReadResponse.NoSharers;
        }

        // Contact the single responder (M, O, E, or F) for a cache-to-cache fill.
        switch (owner.SnoopRead(lineBase, dest)) {
            case SnoopResult.SuppliedOwned: {
                // M/O → O: owner keeps writeback responsibility, requester joins as S.
                HashSet<MoesifCache> sharers = entry.Sharers ?? new HashSet<MoesifCache>();
                sharers.Add(requester);
                _dir[lineBase] = (owner, sharers);
                return BusReadResponse.SuppliedDirty;
            }
            case SnoopResult.Supplied: {
                // E/F → S: clean supply; the Forward role migrates to the requester.
                HashSet<MoesifCache> sharers = entry.Sharers ?? new HashSet<MoesifCache>();
                sharers.Add(owner);
                _dir[lineBase] = (requester, sharers);
                return BusReadResponse.SuppliedClean;
            }
            case SnoopResult.Shared: {
                // Owner downgraded itself to plain S since we last looked (cbo.clean on
                // an O line writes back and drops to S without a bus transaction).
                // Memory is clean; the requester fills from it and becomes the forwarder.
                HashSet<MoesifCache> sharers = entry.Sharers ?? new HashSet<MoesifCache>();
                sharers.Add(owner);
                _dir[lineBase] = (requester, sharers);
                return BusReadResponse.Shared;
            }
            default: {
                // Stale owner (evicted without notifying us — shouldn't happen with the
                // Evicted protocol, but tolerate it gracefully).
                if (entry.Sharers is { Count: > 0, } sharers) {
                    _dir[lineBase] = (requester, sharers);
                    return BusReadResponse.Shared;
                }

                _dir[lineBase] = (requester, null);
                return BusReadResponse.NoSharers;
            }
        }
    }

    public void BusReadInvalidate(MoesifCache requester, ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry)) {
            if (entry.Owner is { } owner && !ReferenceEquals(owner, requester))
                owner.SnoopInvalidate(lineBase); // M/O → writeback+I, E → I
            if (entry.Sharers is { } sharers)
                foreach (MoesifCache c in sharers)
                    if (!ReferenceEquals(c, requester))
                        c.SnoopInvalidate(lineBase);
        }

        _dir[lineBase] = (requester, null);
        _table?.InvalidateAt(lineBase, _blockSize);
    }

    public bool BusReadForOwnership(MoesifCache requester, ulong lineBase, Span<byte> dest) {
        var supplied = false;
        if (_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry)) {
            // Only the owner (M/O/E) can forward; plain S sharers just invalidate.
            if (entry.Owner is { } owner && !ReferenceEquals(owner, requester))
                supplied = owner.SnoopInvalidateForward(lineBase, dest);
            if (entry.Sharers is { } sharers)
                foreach (MoesifCache c in sharers)
                    if (!ReferenceEquals(c, requester))
                        c.SnoopInvalidate(lineBase);
        }

        _dir[lineBase] = (requester, null);
        _table?.InvalidateAt(lineBase, _blockSize);
        return supplied;
    }

    public void BusSilentUpgrade(ulong lineBase) =>
        _table?.InvalidateAt(lineBase, _blockSize);

    public void BusLoad(ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry)) {
            entry.Owner?.SnoopInvalidate(lineBase);
            if (entry.Sharers is { } sharers)
                foreach (MoesifCache c in sharers)
                    c.SnoopInvalidate(lineBase);
            _dir.Remove(lineBase);
        }
    }

    public void BusSyncToBacking(ulong lineBase) {
        // Only an owner (M or O) can hold data newer than backing.
        if (_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry))
            entry.Owner?.SnoopWriteback(lineBase);
    }

    public void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        Backing.Load(lineBase, block);

    public void Evicted(MoesifCache source, ulong lineBase) {
        if (!_dir.TryGetValue(lineBase, out (MoesifCache? Owner, HashSet<MoesifCache>? Sharers) entry)) return;

        if (ReferenceEquals(entry.Owner, source)) {
            // The responder is gone: a dirty owner wrote back before notifying
            // (EvictWay/LocalInvalidate); a clean forwarder simply dropped its line.
            // Either way backing is now clean; remaining sharers are ordinary S copies
            // and the next reader becomes the new forwarder.
            if (entry.Sharers is { Count: > 0, } sharers)
                _dir[lineBase] = (null, sharers);
            else
                _dir.Remove(lineBase);
            return;
        }

        if (entry.Sharers is { } members) {
            members.Remove(source);
            if (members.Count > 0) return;
            // Last S copy gone: either the line is now uncached, or only the O/F
            // responder remains — which the owner-only entry form represents just as well.
            if (entry.Owner is { } owner)
                _dir[lineBase] = (owner, null);
            else
                _dir.Remove(lineBase);
        }
    }
}