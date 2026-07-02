namespace Orrery.Cache;

public enum MoesiState : byte {
    Invalid,
    Shared,
    Exclusive,
    Modified,

    /// <summary>
    /// Dirty but shared (MOESI). Set when a Modified line is snooped by a remote read:
    /// the holder supplies the data cache-to-cache and keeps writeback responsibility
    /// instead of writing back to backing memory. Backing is stale while an Owned copy
    /// exists; the owner writes back on eviction, invalidation, or snoop-invalidate.
    /// </summary>
    Owned,
}