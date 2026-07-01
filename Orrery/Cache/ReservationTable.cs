namespace Orrery.Cache;

/// <summary>
/// Tracks LR/SC reservations across multiple harts sharing one physical memory.
/// <para>
/// Each hart registers a reservation on LR; the reservation is consumed (or
/// cleared) on SC.  Any write from any hart that overlaps the 4-byte-aligned
/// word covering a reservation cancels it — matching the RISC-V requirement
/// that an SC.W must fail if another hart has written to the reservation set
/// between the paired LR.W and SC.W.
/// </para>
/// <para>
/// Reservation granularity is one naturally-aligned 4-byte word (the minimum
/// the spec requires for RV32A/RV64A).  Wider writes (e.g. SD, vector stores)
/// still invalidate any reservation whose granule they overlap.
/// </para>
/// </summary>
public sealed class ReservationTable {
    // hart-id → reserved physical address (4-byte-aligned granule base)
    private readonly Dictionary<int, ulong> _reservations = new();

    /// <summary>
    /// Records (or replaces) <paramref name="hartId"/>'s reservation at the
    /// 4-byte-aligned granule containing <paramref name="address"/>.
    /// </summary>
    public void Set(int hartId, ulong address) =>
        _reservations[hartId] = address & ~3UL;

    /// <summary>
    /// Attempts to consume <paramref name="hartId"/>'s reservation for
    /// <paramref name="address"/>.  Always clears the reservation (an SC
    /// releases it whether or not it succeeds).
    /// Returns <c>true</c> iff the reservation matched and the SC may proceed.
    /// </summary>
    public bool TryConsume(int hartId, ulong address) {
        bool matched = _reservations.TryGetValue(hartId, out ulong granule)
                    && granule == (address & ~3UL);
        _reservations.Remove(hartId); // always release, per RISC-V spec
        return matched;
    }

    /// <summary>
    /// Cancels every hart's reservation whose 4-byte granule overlaps the
    /// byte range <c>[address, address + bytes)</c>.
    /// Called by <see cref="ReservationAwareMemory"/> on every write.
    /// </summary>
    public void InvalidateAt(ulong address, int bytes) {
        if (_reservations.Count == 0) return;
        ulong writeEnd = address + (ulong)bytes;
        List<int>? toRemove = null;
        foreach ((int hartId, ulong granule) in _reservations) {
            ulong granuleEnd = granule + 4;
            if (address < granuleEnd && writeEnd > granule) {
                toRemove ??= new List<int>();
                toRemove.Add(hartId);
            }
        }

        if (toRemove is null) return;
        foreach (int id in toRemove) _reservations.Remove(id);
    }

    /// <summary>Number of harts currently holding a reservation.</summary>
    public int ActiveCount => _reservations.Count;
}