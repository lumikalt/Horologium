namespace Orrery.Cache;

/// <summary>Outcome of an <see cref="IBus.BusRead" /> transaction, seen by the requester.</summary>
public enum BusReadResponse : byte {
    /// <summary>No peer holds the line — install as Exclusive, fill from backing.</summary>
    NoSharers,

    /// <summary>
    ///     Peers hold the line in plain S only (no forwarder) — fill from backing, which is
    ///     guaranteed clean, and install as Forward: the requester becomes the line's
    ///     designated forwarder.
    /// </summary>
    Shared,

    /// <summary>
    ///     A clean holder (E or F) supplied the block cache-to-cache and downgraded itself
    ///     to S — install as Forward (the forwarder role migrates to the most recent
    ///     requester). The destination span already contains the line data.
    /// </summary>
    SuppliedClean,

    /// <summary>
    ///     The dirty owner (M or O) supplied the block cache-to-cache and keeps the line as
    ///     Owned — install as plain Shared. The destination span already contains the line
    ///     data; backing is stale.
    /// </summary>
    SuppliedDirty,
}

/// <summary>Outcome of a read snoop (<see cref="MoesifCache.SnoopRead" />), seen by the bus.</summary>
public enum SnoopResult : byte {
    /// <summary>Line not present in this cache.</summary>
    Miss,

    /// <summary>Held in plain S — no data supplied (memory or the responder is responsible).</summary>
    Shared,

    /// <summary>
    ///     Held clean in E or F — data supplied cache-to-cache, holder downgraded to S
    ///     (the Forward role passes to the requester).
    /// </summary>
    Supplied,

    /// <summary>Held dirty in M or O — data supplied cache-to-cache, holder now O (retains writeback duty).</summary>
    SuppliedOwned,
}