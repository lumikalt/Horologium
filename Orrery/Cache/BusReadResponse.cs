namespace Orrery.Cache;

/// <summary>Outcome of an <see cref="IBus.BusRead"/> transaction, seen by the requester.</summary>
public enum BusReadResponse : byte {
    /// <summary>No peer holds the line — install as Exclusive, fill from backing.</summary>
    NoSharers,

    /// <summary>Peers hold the line in S only — install as Shared, fill from backing (clean).</summary>
    Shared,

    /// <summary>
    /// A peer supplied the block cache-to-cache (it held M, O, or E) — install as Shared,
    /// the destination span already contains the line data. Backing may be stale.
    /// </summary>
    SharedSupplied,
}

/// <summary>Outcome of a read snoop (<see cref="MoesiCache.SnoopRead"/>), seen by the bus.</summary>
public enum SnoopResult : byte {
    /// <summary>Line not present in this cache.</summary>
    Miss,

    /// <summary>Held in S — no data supplied (memory or the owner is responsible).</summary>
    Shared,

    /// <summary>Held clean in E — data supplied cache-to-cache, holder downgraded to S.</summary>
    Supplied,

    /// <summary>Held dirty in M or O — data supplied cache-to-cache, holder now O (retains writeback duty).</summary>
    SuppliedOwned,
}