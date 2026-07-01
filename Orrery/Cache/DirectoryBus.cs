using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Directory-based MESI coherence bus.  Maintains a per-cache-line sharer directory so
/// that <see cref="IBus.BusReadInvalidate"/> and <see cref="IBus.BusLoad"/> snoop only
/// the caches that actually hold each line — O(sharers) rather than O(all-caches).
///
/// <para>
/// Directory entries are kept <em>precise</em>: caches notify the directory when they
/// voluntarily evict or invalidate a line (via <see cref="IBus.Evicted"/>), so the
/// sharer set never contains stale members and <see cref="IBus.BusRead"/> for a
/// Shared line never needs to probe peers.
/// </para>
///
/// <para>
/// This bus is designed for sequential multi-hart simulation
/// (<see cref="Orrery.MultiHartPipeline.Run"/>).  It cannot be wrapped by
/// <see cref="DeferredBus"/> (which is hardcoded to <see cref="MesiBus"/>).
/// </para>
/// </summary>
public sealed class DirectoryBus : IBus {
    private readonly IMemory _backing;
    private readonly ReservationTable? _table;
    private int _blockSize;

    // Absent key → Uncached.  (owner≠null, sharers=null) → Exclusive(owner).
    // (owner=null, sharers≠null) → Shared(sharers).
    // Invariant: Shared set is never empty (empty sets are removed immediately).
    private readonly Dictionary<ulong, (MesiCache? Owner, HashSet<MesiCache>? Sharers)> _dir = new();

    public IMemory Backing => _backing;

    public DirectoryBus(IMemory backing, ReservationTable? table = null) {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
        _table = table;
    }

    public void Register(MesiCache cache) {
        if (_blockSize == 0) _blockSize = cache.BlockBytes;
    }

    public bool BusRead(MesiCache requester, ulong lineBase) {
        if (!_dir.TryGetValue(lineBase, out (MesiCache? Owner, HashSet<MesiCache>? Sharers) entry)) {
            _dir[lineBase] = (requester, null);
            return false;
        }

        if (entry.Sharers is { } sharers) {
            // Precise: sharers set is accurate — no need to probe.
            sharers.Add(requester);
            return true;
        }

        MesiCache? owner = entry.Owner;
        if (owner is null || ReferenceEquals(owner, requester)) {
            _dir[lineBase] = (requester, null);
            return false;
        }

        // Exclusive: contact the single owner.
        bool held = owner.SnoopRead(lineBase); // M/E → S (writeback if M)
        if (held) {
            _dir[lineBase] = (null, new HashSet<MesiCache> { owner, requester, });
            return true;
        }

        // Stale exclusive (owner evicted without notifying us — shouldn't happen
        // with the Evicted protocol, but tolerate it gracefully).
        _dir[lineBase] = (requester, null);
        return false;
    }

    public void BusReadInvalidate(MesiCache requester, ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MesiCache? Owner, HashSet<MesiCache>? Sharers) entry)) {
            if (entry.Sharers is { } sharers) {
                foreach (MesiCache c in sharers)
                    if (!ReferenceEquals(c, requester))
                        c.SnoopInvalidate(lineBase);
            }
            else if (entry.Owner is { } owner && !ReferenceEquals(owner, requester)) {
                owner.SnoopInvalidate(lineBase); // M → writeback+I, E/S → I
            }
        }

        _dir[lineBase] = (requester, null);
        _table?.InvalidateAt(lineBase, _blockSize);
    }

    public void BusSilentUpgrade(ulong lineBase) =>
        _table?.InvalidateAt(lineBase, _blockSize);

    public void BusLoad(ulong lineBase) {
        if (_dir.TryGetValue(lineBase, out (MesiCache? Owner, HashSet<MesiCache>? Sharers) entry)) {
            if (entry.Sharers is { } sharers)
                foreach (MesiCache c in sharers)
                    c.SnoopInvalidate(lineBase);
            else
                entry.Owner?.SnoopInvalidate(lineBase);
            _dir.Remove(lineBase);
        }
    }

    public void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        _backing.Load(lineBase, block);

    public void Evicted(MesiCache source, ulong lineBase) {
        if (!_dir.TryGetValue(lineBase, out (MesiCache? Owner, HashSet<MesiCache>? Sharers) entry)) return;

        if (entry.Sharers is { } sharers) {
            sharers.Remove(source);
            if (sharers.Count == 0) _dir.Remove(lineBase);
        }
        else if (ReferenceEquals(entry.Owner, source)) { _dir.Remove(lineBase); }
    }
}