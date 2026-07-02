using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Directory-based MOESI coherence bus.  Maintains a per-cache-line sharer directory so
/// that <see cref="IBus.BusReadInvalidate"/> and <see cref="IBus.BusLoad"/> snoop only
/// the caches that actually hold each line — O(sharers) rather than O(all-caches).
///
/// <para>
/// Directory entries are kept <em>precise</em>: caches notify the directory when they
/// voluntarily evict or invalidate a line (via <see cref="IBus.Evicted"/>), so the
/// sharer set never contains stale members and <see cref="IBus.BusRead"/> for a
/// Shared line never needs to probe peers.  Reads that hit a remote M/O/E owner are
/// filled cache-to-cache by that owner; a dirty owner keeps the line as Owned and
/// backing memory is not written.
/// </para>
///
/// <para>
/// This bus is designed for sequential multi-hart simulation
/// (<see cref="Orrery.MultiHartPipeline.Run"/>).  It cannot be wrapped by
/// <see cref="DeferredBus"/> (which is hardcoded to <see cref="MoesiBus"/>).
/// </para>
/// </summary>
public sealed class DirectoryBus : IBus {
    private readonly IMemory _backing;
    private readonly ReservationTable? _table;
    private int _blockSize;

    // Absent key → Uncached.
    // (owner≠null, sharers=null)  → owner holds M or E.
    // (owner=null, sharers≠null)  → all holders in S, backing memory clean.
    // (owner≠null, sharers≠null)  → owner holds O (dirty), sharers hold S.
    // Invariant: a Sharers set is never empty (empty sets are collapsed immediately).
    private readonly Dictionary<ulong, (MoesiCache? Owner, HashSet<MoesiCache>? Sharers)> _dir = new();

    public IMemory Backing => _backing;

    public DirectoryBus(IMemory backing, ReservationTable? table = null) {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
        _table = table;
    }

    public void Register(MoesiCache cache) {
        if (_blockSize == 0) _blockSize = cache.BlockBytes;
    }

    public BusReadResponse BusRead(MoesiCache requester, ulong lineBase, Span<byte> dest) {
        if (!_dir.TryGetValue(lineBase, out (MoesiCache? Owner, HashSet<MoesiCache>? Sharers) entry)) {
            _dir[lineBase] = (requester, null);
            return BusReadResponse.NoSharers;
        }

        MoesiCache? owner = entry.Owner;
        if (owner is null || ReferenceEquals(owner, requester)) {
            // No owner (all-S, memory clean) — or a stale owner entry for the requester
            // itself, which cannot hold the line (it just missed).
            if (entry.Sharers is { } sharers) {
                sharers.Add(requester);
                _dir[lineBase] = (null, sharers);
                return BusReadResponse.Shared;
            }

            _dir[lineBase] = (requester, null);
            return BusReadResponse.NoSharers;
        }

        // Contact the single owner (M, O, or E) for a cache-to-cache fill.
        switch (owner.SnoopRead(lineBase, dest)) {
            case SnoopResult.SuppliedOwned: {
                // M/O → O: owner keeps writeback responsibility, requester joins as S.
                HashSet<MoesiCache> sharers = entry.Sharers ?? new HashSet<MoesiCache>();
                sharers.Add(requester);
                _dir[lineBase] = (owner, sharers);
                return BusReadResponse.SharedSupplied;
            }
            case SnoopResult.Supplied:
                // E → S: clean supply, no owner remains.
                _dir[lineBase] = (null, new HashSet<MoesiCache> { owner, requester, });
                return BusReadResponse.SharedSupplied;
            case SnoopResult.Shared: {
                // Owner downgraded itself to S since we last looked (cbo.clean on an
                // O line writes back and drops to S without a bus transaction).
                HashSet<MoesiCache> sharers = entry.Sharers ?? new HashSet<MoesiCache>();
                sharers.Add(owner);
                sharers.Add(requester);
                _dir[lineBase] = (null, sharers);
                return BusReadResponse.Shared;
            }
            default: {
                // Stale owner (evicted without notifying us — shouldn't happen with the
                // Evicted protocol, but tolerate it gracefully).
                if (entry.Sharers is { } sharers) {
                    sharers.Add(requester);
                    _dir[lineBase] = (null, sharers);
                    return BusReadResponse.Shared;
                }

                _dir[lineBase] = (requester, null);
                return BusReadResponse.NoSharers;
            }
        }
    }

    public void BusReadInvalidate(MoesiCache requester, ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MoesiCache? Owner, HashSet<MoesiCache>? Sharers) entry)) {
            if (entry.Owner is { } owner && !ReferenceEquals(owner, requester))
                owner.SnoopInvalidate(lineBase); // M/O → writeback+I, E → I
            if (entry.Sharers is { } sharers)
                foreach (MoesiCache c in sharers)
                    if (!ReferenceEquals(c, requester))
                        c.SnoopInvalidate(lineBase);
        }

        _dir[lineBase] = (requester, null);
        _table?.InvalidateAt(lineBase, _blockSize);
    }

    public void BusSilentUpgrade(ulong lineBase) =>
        _table?.InvalidateAt(lineBase, _blockSize);

    public void BusLoad(ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MoesiCache? Owner, HashSet<MoesiCache>? Sharers) entry)) {
            entry.Owner?.SnoopInvalidate(lineBase);
            if (entry.Sharers is { } sharers)
                foreach (MoesiCache c in sharers)
                    c.SnoopInvalidate(lineBase);
            _dir.Remove(lineBase);
        }
    }

    public void BusSyncToBacking(ulong lineBase) {
        // Only an owner (M or O) can hold data newer than backing.
        if (_dir.TryGetValue(lineBase, out (MoesiCache? Owner, HashSet<MoesiCache>? Sharers) entry))
            entry.Owner?.SnoopWriteback(lineBase);
    }

    public void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        _backing.Load(lineBase, block);

    public void Evicted(MoesiCache source, ulong lineBase) {
        if (!_dir.TryGetValue(lineBase, out (MoesiCache? Owner, HashSet<MoesiCache>? Sharers) entry)) return;

        if (ReferenceEquals(entry.Owner, source)) {
            // A dirty owner wrote back before notifying (EvictWay/LocalInvalidate),
            // so backing is clean again; any remaining sharers are ordinary S copies.
            if (entry.Sharers is { Count: > 0, } sharers)
                _dir[lineBase] = (null, sharers);
            else
                _dir.Remove(lineBase);
            return;
        }

        if (entry.Sharers is { } members) {
            members.Remove(source);
            if (members.Count > 0) return;
            // Last S copy gone: either the line is now uncached, or only the O owner
            // remains — which the owner-only entry form represents just as well.
            if (entry.Owner is { } owner)
                _dir[lineBase] = (owner, null);
            else
                _dir.Remove(lineBase);
        }
    }
}