using Orrery.Cache;

namespace Orrery.Spec;

/// <summary>
///     Geometry and replacement policy for one cache level.
///     <para>
///         <see cref="SharedAcross" /> expresses the sharing intent for the multicore assembler (Phase 3):
///         1 = private per core; N = one physical cache instance shared by N cores.
///     </para>
/// </summary>
public sealed record CacheLevelSpec(
    int CapacityBytes,
    int Ways = 4,
    int BlockBytes = 32,
    int MissLatency = 10,
    ReplacementPolicyKind ReplacementPolicy = ReplacementPolicyKind.Lru,
    int SharedAcross = 1,
    PrefetcherKind Prefetcher = PrefetcherKind.None,
    int PrefetcherTableSize = 64,
    int PrefetcherDepth = 8,
    int PrefetchLatency = 0,
    int TagLatency = 0,
    int DataLatency = 0,
    WritePolicyKind WritePolicy = WritePolicyKind.WriteThrough,
    WriteMissPolicyKind WriteMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
    int WbCapacity = 0,
    int MshrCount = 0
) {
    public int HitLatency => Math.Max(TagLatency, DataLatency);
}