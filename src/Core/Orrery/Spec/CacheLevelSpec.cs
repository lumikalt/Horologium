#region

using Orrery.Cache;

#endregion

namespace Orrery.Spec;

/// <summary>
///     Geometry and replacement policy for one cache level.
///     <para>
///         <see cref="SharedAcross" /> expresses the sharing intent for the multicore assembler (Phase 3):
///         1 = private per core; N = one physical cache instance shared by N cores.
///     </para>
///     <para>
///         <see cref="InclusionPolicy" /> describes this level's relationship to the level directly
///         inside it in the same path/shared-levels list (e.g. an L2 entry's policy toward L1);
///         see <see cref="InclusionPolicyKind" />. Ignored on the innermost level, since nothing
///         sits inside it.
///     </para>
///     <para>
///         <see cref="CriticalWordLatency" /> (0 = disabled) enables critical-word-first / early
///         restart: a fresh demand miss charges the requester this latency instead of the full
///         <see cref="MissLatency" />, while the line still takes the full <see cref="MissLatency" />
///         to arrive in the background. Requires <see cref="MshrCount" /> &gt; 0.
///     </para>
///     <para>
///         <see cref="BankCount" /> splits the cache into independent banks by line address;
///         <see cref="ReadPorts" />/<see cref="WritePorts" /> (0 = unlimited) cap accesses per
///         bank per cycle, charging a 1-cycle structural-hazard stall on conflict.
///     </para>
///     <para>
///         <see cref="SectorBytes" /> (0 = disabled) splits each line into independently
///         valid/dirty sectors: a fresh miss fetches only the triggering sector, and evictions
///         write back only dirty sectors instead of the whole line. Not combinable with
///         <see cref="WbCapacity" /> &gt; 0.
///     </para>
///     <para>
///         <see cref="PolicyFactory" /> and <see cref="PrefetcherFactory" /> supply caller-built
///         instances (e.g. the RTL-backed <see cref="RtlFfiReplacementPolicy" /> /
///         <see cref="RtlFfiPrefetcher" /> from <c>native/RtlFu</c>) in place of the
///         <see cref="ReplacementPolicy" /> / <see cref="Prefetcher" /> kinds. The policy factory
///         is invoked with this level's (sets, ways); a null return falls back to the kind, so a
///         geometry-fixed RTL policy attaches only when it matches. The prefetcher factory is only
///         consulted on the innermost level (the one the pipeline drives).
///     </para>
///     <para>
///         <see cref="VictimCacheEntries" /> (0 = disabled) attaches a small fully-associative
///         FIFO buffer beside the main array that captures conflict-miss evictions instead of
///         flushing/discarding them immediately (Jouppi, ISCA 1990). A later miss that hits in
///         the buffer swaps the line back into the main array, charging
///         <see cref="VictimCacheHitLatency" /> instead of the full miss latency, with no MSHR
///         allocation. Distinct from the unrelated <c>ChooseVictim</c>/<c>InsertVictim</c>/
///         <c>VictimInserts</c> symbols (generic replacement-policy eviction selection and
///         Exclusive-inclusion-policy hand-off). Not currently combinable with
///         <see cref="SectorBytes" /> &gt; 0.
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
    int MshrCount = 0,
    CacheAccessModeKind AccessMode = CacheAccessModeKind.Parallel,
    InclusionPolicyKind InclusionPolicy = InclusionPolicyKind.Nine,
    int CriticalWordLatency = 0,
    int BankCount = 1,
    int ReadPorts = 0,
    int WritePorts = 0,
    int SectorBytes = 0,
    int VictimCacheEntries = 0,
    int VictimCacheHitLatency = 1,
    Func<int, int, IReplacementPolicy?>? PolicyFactory = null,
    Func<IPrefetcher?>? PrefetcherFactory = null
) {
    public int HitLatency => AccessMode == CacheAccessModeKind.Sequential
        ? TagLatency + DataLatency
        : Math.Max(TagLatency, DataLatency);
}