namespace Orrery.Cache;

/// <summary>
///     Observes each D-cache access and writes zero or more addresses to prefetch into
///     <c>="targets"</c>. Implementations must be stateless except for their own
///     prediction tables — the caller owns when and whether the suggested addresses are
///     actually fetched.
/// </summary>
public interface IPrefetcher {
    /// <param name="pc">PC of the instruction that issued the access.</param>
    /// <param name="address">Effective byte address of the access.</param>
    /// <param name="wasHit">True if the demand access hit in L1.</param>
    /// <param name="targets">Caller-provided buffer; prefetch addresses are written here.</param>
    /// <returns>Number of addresses written to <paramref name="targets" /> (0 = no prefetch).</returns>
    int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets);
}