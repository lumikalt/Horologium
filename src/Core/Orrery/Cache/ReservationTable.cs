namespace Orrery.Cache;

/// <summary>
///     Tracks LR/SC reservations across multiple harts sharing one physical memory.
///     <para>
///         Each hart registers a reservation on LR; the reservation is consumed (or
///         cleared) on SC.  Any write from any hart that overlaps the naturally-aligned
///         granule covering a reservation cancels it — matching the RISC-V requirement
///         that an SC must fail if another hart has written to the reservation set
///         between the paired LR and SC.
///     </para>
///     <para>
///         The reservation granule is naturally aligned to the access size (4 bytes for
///         LR.W/SC.W, 8 bytes for LR.D/SC.D — the minimum the spec requires). Wider
///         writes (e.g. vector stores) still invalidate any reservation whose granule
///         they overlap.
///     </para>
/// </summary>
public sealed class ReservationTable {
    // hart-id → (reserved physical address granule base, granule size in bytes)
    private readonly Dictionary<int, (ulong Granule, int Bytes)> _reservations = new();

    /// <summary>Number of harts currently holding a reservation.</summary>
    public int ActiveCount => _reservations.Count;

    /// <summary>
    ///     Records (or replaces) <paramref name="hartId" />'s reservation at the
    ///     <paramref name="bytes" />-aligned granule containing <paramref name="address" />.
    /// </summary>
    public void Set(int hartId, ulong address, int bytes = 4) =>
        _reservations[hartId] = (address & ~(ulong)(bytes - 1), bytes);

    /// <summary>
    ///     Attempts to consume <paramref name="hartId" />'s reservation for
    ///     <paramref name="address" />.  Always clears the reservation (an SC
    ///     releases it whether or not it succeeds).
    ///     Returns <c>true</c> iff the reservation matched and the SC may proceed.
    /// </summary>
    public bool TryConsume(int hartId, ulong address, int bytes = 4) {
        bool matched = _reservations.TryGetValue(hartId, out (ulong Granule, int Bytes) r)
                    && r.Granule == (address & ~(ulong)(bytes - 1));
        _reservations.Remove(hartId); // always release, per RISC-V spec
        return matched;
    }

    /// <summary>
    ///     Cancels every hart's reservation whose granule overlaps the byte range
    ///     <c>[address, address + bytes)</c>.
    ///     Called by <see cref="ReservationAwareMemory" /> on every write.
    /// </summary>
    public void InvalidateAt(ulong address, int bytes) {
        if (_reservations.Count == 0) return;
        ulong writeEnd = address + (ulong)bytes;
        List<int>? toRemove = null;
        foreach ((int hartId, (ulong granule, int granuleBytes)) in _reservations) {
            ulong granuleEnd = granule + (ulong)granuleBytes;
            if (address < granuleEnd && writeEnd > granule) {
                toRemove ??= new List<int>();
                toRemove.Add(hartId);
            }
        }

        if (toRemove is null) return;
        foreach (int id in toRemove) _reservations.Remove(id);
    }
}