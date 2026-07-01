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
    private readonly List<MesiCache> _caches = new();

    public IMemory Backing => _backing;

    public MesiBus(IMemory backing) {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
    }

    internal void Register(MesiCache cache) => _caches.Add(cache);

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a read miss.
    /// M-state holders write back to backing and downgrade to S; E-state holders downgrade to S.
    /// Returns true if any peer held the line — the requester should install the line as S, not E.
    /// </summary>
    internal bool BusRead(MesiCache requester, ulong lineBase) {
        var anyHeld = false;
        foreach (var c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            anyHeld |= c.SnoopRead(lineBase);
        }
        return anyHeld;
    }

    /// <summary>
    /// Snoops all caches except <paramref name="requester"/> for a write (upgrade to M).
    /// M-state holders write back then transition to I; E/S-state holders transition to I.
    /// </summary>
    internal void BusReadInvalidate(MesiCache requester, ulong lineBase) {
        foreach (var c in _caches) {
            if (ReferenceEquals(c, requester)) continue;
            c.SnoopInvalidate(lineBase);
        }
    }

    /// <summary>
    /// Invalidates the given line in ALL registered caches, including the caller's.
    /// Used when data is written directly to backing memory via <see cref="IMemory.Load"/>,
    /// bypassing the coherence path.
    /// </summary>
    internal void BusLoad(ulong lineBase) {
        foreach (var c in _caches) c.SnoopInvalidate(lineBase);
    }

    /// <summary>Writes a dirty cache block to backing memory.</summary>
    internal void Writeback(ulong lineBase, ReadOnlySpan<byte> block) =>
        _backing.Load(lineBase, block);
}
