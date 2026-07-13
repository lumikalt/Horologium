namespace Orrery.Spec;

/// <summary>
///     Per-path (I or D) private cache levels and optional TLB.
///     Levels are innermost first (e.g. [L0, L1]).
///     Each level's prefetcher configuration is carried on its <see cref="CacheLevelSpec" />.
///     When <see cref="Tlb" /> is non-null the TLB sits between the innermost cache and the
///     UncacheableMemory bypass layer (closest to the CPU on the normal access path).
/// </summary>
public sealed record CachePathSpec(
    IReadOnlyList<CacheLevelSpec>? Levels = null,
    TlbSpec? Tlb = null
) {
    public static readonly CachePathSpec Empty = new();
}