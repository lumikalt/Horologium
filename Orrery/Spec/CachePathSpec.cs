namespace Orrery.Spec;

/// <summary>
/// Per-path (I or D) private cache levels, innermost first (e.g. [L0, L1]).
/// Each level's prefetcher configuration is carried on its <see cref="CacheLevelSpec"/>.
/// </summary>
public sealed record CachePathSpec(
    IReadOnlyList<CacheLevelSpec>? Levels = null
) {
    public static readonly CachePathSpec Empty = new();
}