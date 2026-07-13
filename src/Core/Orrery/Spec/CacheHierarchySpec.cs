using Mechanism;
using Orrery.Cache;

namespace Orrery.Spec;

/// <summary>
///     Structural description of a core's full cache hierarchy.
///     <para>
///         Private levels are stored per named path in <see cref="Paths" />;
///         <see cref="SharedLevels" /> describes unified levels above all paths — L2, L3, … —
///         innermost first. Use the <see cref="I" /> and <see cref="D" /> constants for the
///         conventional split I/D names, or any string for additional paths (e.g. "vector").
///         Each level's <see cref="CacheLevelSpec.SharedAcross" /> records sharing intent for
///         the multicore assembler (Phase 3).
///     </para>
/// </summary>
public sealed record CacheHierarchySpec(
    IReadOnlyDictionary<string, CachePathSpec>? Paths = null,
    IReadOnlyList<CacheLevelSpec>? SharedLevels = null
) {
    public const string I = "I";
    public const string D = "D";

    public static readonly CacheHierarchySpec Empty = new();

    // ── Factories ─────────────────────────────────────────────────────────────

    public static CacheHierarchySpec SplitId(
        CachePathSpec iPath,
        CachePathSpec dPath,
        IReadOnlyList<CacheLevelSpec>? sharedLevels = null
    ) => new(
        new Dictionary<string, CachePathSpec> { [CacheHierarchySpec.I] = iPath, [CacheHierarchySpec.D] = dPath, },
        sharedLevels
    );

    /// <summary>Unified L1: both I and D paths use the same cache spec (same geometry, separate instance per call to Build).</summary>
    public static CacheHierarchySpec Unified(
        CachePathSpec path,
        IReadOnlyList<CacheLevelSpec>? sharedLevels = null
    ) => new(
        new Dictionary<string, CachePathSpec> { [CacheHierarchySpec.I] = path, [CacheHierarchySpec.D] = path, },
        sharedLevels
    );

    public static CacheHierarchySpec WithPath(
        string name,
        CachePathSpec path,
        IReadOnlyList<CacheLevelSpec>? sharedLevels = null
    ) => new(new Dictionary<string, CachePathSpec> { [name] = path, }, sharedLevels);

    // ── Build ─────────────────────────────────────────────────────────────────

    public MemoryLayers Build(IMemory backing, string pathName, ulong uncacheableBase = 0, ulong uncacheableSize = 0) {
        CachePathSpec path = Paths?.GetValueOrDefault(pathName) ?? CachePathSpec.Empty;
        return MemoryLayers.Build(backing, path, SharedLevels, uncacheableBase, uncacheableSize);
    }

    public MemoryLayers BuildILayers(IMemory backing, ulong uncacheableBase = 0, ulong uncacheableSize = 0)
        => Build(backing, CacheHierarchySpec.I, uncacheableBase, uncacheableSize);

    public MemoryLayers BuildDLayers(IMemory backing, ulong uncacheableBase = 0, ulong uncacheableSize = 0)
        => Build(backing, CacheHierarchySpec.D, uncacheableBase, uncacheableSize);
}