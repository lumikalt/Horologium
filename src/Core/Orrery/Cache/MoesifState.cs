namespace Orrery.Cache;

public enum MoesifState : byte {
    Invalid,
    Shared,
    Exclusive,
    Modified,

    /// <summary>
    /// Dirty but shared (MOESIF). Set when a Modified line is snooped by a remote read:
    /// the holder supplies the data cache-to-cache and keeps writeback responsibility
    /// instead of writing back to backing memory. Backing is stale while an Owned copy
    /// exists; the owner writes back on eviction, invalidation, or snoop-invalidate.
    /// </summary>
    Owned,

    /// <summary>
    /// Clean but designated forwarder (MESIF). At most one sharer of a clean line holds
    /// F and answers read misses cache-to-cache while the remaining sharers hold plain S;
    /// Forward migrates to the most recent requester on each supply. Unlike Owned, backing
    /// memory is current, so eviction or invalidation drops the line without a writeback.
    /// A line never has both an Owned and a Forward copy: F exists only while the line is
    /// clean, O only while it is dirty.
    /// </summary>
    Forward,
}