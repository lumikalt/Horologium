namespace Orrery.Cache;

/// <summary>
/// Observes each D-cache access and optionally returns an address to prefetch.
/// Implementations must be stateless except for their own prediction tables —
/// the caller owns when and whether the suggested address is actually fetched.
/// </summary>
public interface IPrefetcher {
    /// <param name="pc">PC of the instruction that issued the access.</param>
    /// <param name="address">Effective byte address of the access.</param>
    /// <param name="wasHit">True if the demand access hit in L1.</param>
    /// <returns>Address to prefetch, or null if nothing should be prefetched.</returns>
    ulong? OnAccess(ulong pc, ulong address, bool wasHit);
}