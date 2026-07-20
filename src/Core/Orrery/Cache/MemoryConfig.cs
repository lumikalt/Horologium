#region

using Mechanism;
using Orrery.Spec;

#endregion

namespace Orrery.Cache;

public enum PrefetcherKind {
    None,
    NextLine,
    Stride,
    Stream,
    Ipcp,
    Pythia,
    Berti,
    Sms,
    Bop,
    Spp,
    Ppf,
}

public enum ReplacementPolicyKind {
    Lru,
    Srrip,
    Brrip,
    Drrip,
    Ship,
    ShipPc,
    Random,
    Fifo,
    Plru,
    Mru,
    Clock,
    Hawkeye,
}

public enum WritePolicyKind { WriteThrough, WriteBack, }

public enum WriteMissPolicyKind { NoWriteAllocate, WriteAllocate, }

/// <summary>
///     Tag/data array access ordering, gem5's third timing knob alongside tag/data latency.
///     <see cref="Parallel" /> probes tag and data arrays simultaneously (hit latency =
///     max(tag, data)) — the current default, typical of small/fast L1 caches. <see cref="Sequential" />
///     probes tags first and reads only the matching way (hit latency = tag + data) — typical of
///     large lower-level caches where reading all ways in parallel would cost too much power.
/// </summary>
public enum CacheAccessModeKind { Parallel, Sequential, }

/// <summary>
///     Inclusion policy between a cache level and the level directly inside it (closer to the
///     CPU) — e.g. an L2's policy toward its L1. Ignored on the innermost level of a path, since
///     nothing sits inside it.
///     <para>
///         <see cref="Nine" /> (non-inclusive non-exclusive) is the default and matches prior
///         behavior: no coordination between levels. <see cref="Inclusive" /> (Intel-style)
///         guarantees every line resident in the inner level is also resident here — when this
///         level evicts a line it back-invalidates the inner level's copy (folding in any dirty
///         data first). <see cref="Exclusive" /> (AMD-style) guarantees a line is resident in at
///         most one of the two levels — an inner-level eviction is inserted here as a victim
///         rather than discarded, and a fill that pulls a line up into the inner level removes it
///         from here.
///     </para>
/// </summary>
public enum InclusionPolicyKind {
    Nine, Inclusive, Exclusive,
}

/// <param name="CacheCapacityBytes">0 = disabled.</param>
/// <param name="CacheWays">Associativity. Ignored when CacheCapacityBytes = 0.</param>
/// <param name="CacheBlockBytes">Cache line size. Ignored when CacheCapacityBytes = 0.</param>
/// <param name="CacheMissLatency">Extra cycles per L1 miss (penalty to access L2 or DRAM).</param>
/// <param name="L2CapacityBytes">0 = disabled.</param>
/// <param name="L2Ways">L2 associativity.</param>
/// <param name="L2BlockBytes">L2 line size.</param>
/// <param name="L2MissLatency">Extra cycles per L2 miss (penalty to access L3 or DRAM).</param>
/// <param name="L3CapacityBytes">0 = disabled.</param>
/// <param name="L3Ways">L3 associativity.</param>
/// <param name="L3BlockBytes">L3 line size.</param>
/// <param name="L3MissLatency">Extra cycles per L3 miss (penalty to access DRAM).</param>
/// <param name="TlbEntries">0 = disabled.</param>
/// <param name="TlbPageBytes">Page size in bytes. Ignored when TlbEntries = 0.</param>
/// <param name="TlbMissLatency">Extra cycles per TLB miss.</param>
/// <param name="UncacheableBase">
///     Start of a memory-mapped-I/O region that bypasses all
///     caches (0 with <see cref="UncacheableSize" /> = 0 disables it). Such a region <em>must</em>
///     be uncacheable: a device's side effects (e.g. an HTIF <c>fromhost</c> ACK written to the
///     backing below the cache) are otherwise masked by stale cached lines, hanging the run.
/// </param>
/// <param name="UncacheableSize">Size of the uncacheable MMIO region in bytes (0 = disabled).</param>
/// <param name="Prefetcher">
///     Prefetch strategy for this memory port. Ignored when no L1 cache
///     is configured. Prefetches respect the uncacheable region.
/// </param>
/// <param name="PrefetcherTableSize">
///     RPT table entries for <see cref="PrefetcherKind.Stride" /> and
///     stream-buffer count for <see cref="PrefetcherKind.Stream" />; must be a power of 2.
///     Ignored for other prefetcher kinds.
/// </param>
/// <param name="PrefetcherDepth">
///     Stream-buffer depth (lines prefetched ahead per stream) for
///     <see cref="PrefetcherKind.Stream" />. Ignored for other prefetcher kinds.
/// </param>
/// <param name="PrefetchLatency">
///     Cycles until a prefetched line is usable (0 = instant/free,
///     the idealized model). A demand hit on a line whose prefetch is still in flight pays the
///     remaining countdown instead of zero, and in-flight prefetches count against MSHR capacity.
/// </param>
/// <param name="CacheTagLatency">
///     L1 tag-array lookup cycles. Hit latency = max(CacheTagLatency, CacheDataLatency);
///     the OoO pipeline sources load result timing from this rather than FuLatencyConfig.LoadHitLatency when non-zero.
/// </param>
/// <param name="CacheDataLatency">L1 data-array read cycles (parallel with tag in the default gem5 mode).</param>
/// <param name="L2TagLatency">L2 tag-array lookup cycles.</param>
/// <param name="L2DataLatency">L2 data-array read cycles.</param>
/// <param name="L3TagLatency">L3 tag-array lookup cycles.</param>
/// <param name="L3DataLatency">L3 data-array read cycles.</param>
/// <param name="CacheAccessMode">
///     L1 tag/data access ordering. Parallel (default) computes hit latency as
///     max(CacheTagLatency, CacheDataLatency); Sequential probes tags first, then only the matching
///     way, computing hit latency as CacheTagLatency + CacheDataLatency.
/// </param>
/// <param name="L2AccessMode">L2 tag/data access ordering (same semantics as <see cref="CacheAccessMode" />).</param>
/// <param name="L3AccessMode">L3 tag/data access ordering (same semantics as <see cref="CacheAccessMode" />).</param>
/// <param name="CacheWritePolicy">
///     L1 write-hit policy: WriteThrough (store goes to backing immediately) or
///     WriteBack (store stays in cache until eviction; requires dirty tracking).
/// </param>
/// <param name="CacheWriteMissPolicy">
///     L1 write-miss policy: NoWriteAllocate (write directly to backing, no line install)
///     or WriteAllocate (install line then write into it). Write-allocate + WriteBack is the typical pairing.
/// </param>
/// <param name="L2WritePolicy">L2 write-hit policy (same semantics as L1).</param>
/// <param name="L2WriteMissPolicy">L2 write-miss policy.</param>
/// <param name="L3WritePolicy">L3 write-hit policy.</param>
/// <param name="L3WriteMissPolicy">L3 write-miss policy.</param>
/// <param name="CacheWbCapacity">L1 write-back buffer capacity in lines (0 = disabled).</param>
/// <param name="L2WbCapacity">L2 write-back buffer capacity in lines (0 = disabled).</param>
/// <param name="L3WbCapacity">L3 write-back buffer capacity in lines (0 = disabled).</param>
/// <param name="L2InclusionPolicy">L2's inclusion policy toward L1. Ignored when L2 is disabled.</param>
/// <param name="L3InclusionPolicy">L3's inclusion policy toward L2. Ignored when L3 is disabled.</param>
/// <param name="CacheCriticalWordLatency">
///     L1 critical-word-first / early-restart latency (0 = disabled). A fresh demand miss charges
///     the requester this instead of <see cref="CacheMissLatency" />, while the line still takes
///     the full miss latency to arrive in the background for later accesses. Requires
///     <see cref="CacheMshrCount" /> &gt; 0.
/// </param>
/// <param name="L2CriticalWordLatency">
///     L2 critical-word-first latency (same semantics as L1; requires
///     <see cref="L2MshrCount" /> &gt; 0).
/// </param>
/// <param name="L3CriticalWordLatency">
///     L3 critical-word-first latency (same semantics as L1; requires
///     <see cref="L3MshrCount" /> &gt; 0).
/// </param>
/// <param name="CacheBankCount">L1 bank count (1 = unbanked). See <see cref="CacheReadPorts" />.</param>
/// <param name="CacheReadPorts">
///     L1 read accesses one bank can service per cycle (0 = unlimited). A conflicting access to a
///     saturated bank pays a 1-cycle structural-hazard stall.
/// </param>
/// <param name="CacheWritePorts">
///     L1 write accesses one bank can service per cycle (0 = unlimited). See
///     <see cref="CacheReadPorts" />.
/// </param>
/// <param name="L2BankCount">L2 bank count (same semantics as L1).</param>
/// <param name="L2ReadPorts">L2 read port count per bank (same semantics as L1).</param>
/// <param name="L2WritePorts">L2 write port count per bank (same semantics as L1).</param>
/// <param name="L3BankCount">L3 bank count (same semantics as L1).</param>
/// <param name="L3ReadPorts">L3 read port count per bank (same semantics as L1).</param>
/// <param name="L3WritePorts">L3 write port count per bank (same semantics as L1).</param>
/// <param name="CacheSectorBytes">
///     L1 sector size in bytes (0 = disabled). Splits each line into independently valid/dirty
///     sectors: a fresh miss fetches only the triggering sector, and evictions write back only
///     dirty sectors. Not combinable with <see cref="CacheWbCapacity" /> &gt; 0.
/// </param>
/// <param name="L2SectorBytes">
///     L2 sector size in bytes (same semantics as L1). Not combinable with
///     <see cref="L2WbCapacity" /> &gt; 0.
/// </param>
/// <param name="L3SectorBytes">
///     L3 sector size in bytes (same semantics as L1). Not combinable with
///     <see cref="L3WbCapacity" /> &gt; 0.
/// </param>
/// <param name="CacheVictimCacheEntries">
///     L1 Jouppi victim buffer capacity in lines (0 = disabled). A small fully-associative FIFO
///     buffer beside the main array that captures conflict-miss evictions instead of
///     flushing/discarding them immediately; a later hit swaps the line back in, charging
///     <see cref="CacheVictimCacheHitLatency" /> instead of the full miss latency. Not combinable
///     with <see cref="CacheSectorBytes" /> &gt; 0.
/// </param>
/// <param name="L2VictimCacheEntries">
///     L2 victim buffer capacity (same semantics as L1). Not combinable with
///     <see cref="L2SectorBytes" /> &gt; 0.
/// </param>
/// <param name="L3VictimCacheEntries">
///     L3 victim buffer capacity (same semantics as L1). Not combinable with
///     <see cref="L3SectorBytes" /> &gt; 0.
/// </param>
/// <param name="CacheVictimCacheHitLatency">
///     Cycles charged on an L1 victim-buffer hit. Only meaningful when
///     <see cref="CacheVictimCacheEntries" /> &gt; 0.
/// </param>
/// <param name="L2VictimCacheHitLatency">
///     Cycles charged on an L2 victim-buffer hit. Only meaningful when
///     <see cref="L2VictimCacheEntries" /> &gt; 0.
/// </param>
/// <param name="L3VictimCacheHitLatency">
///     Cycles charged on an L3 victim-buffer hit. Only meaningful when
///     <see cref="L3VictimCacheEntries" /> &gt; 0.
/// </param>
/// <param name="ReplacementPolicy">
///     Cache replacement policy applied to every cache level.
///     Defaults to LRU. SRRIP is scan-resistant; DRRIP adds thrash-resistance via Set Dueling
///     (Jaleel et al., ISCA 2010). SHiP uses per-signature reuse history to predict insertion
///     RRPV: SHiP-Mem (Ship) indexes the SHCT by upper address bits; SHiP-PC (ShipPc) indexes
///     by load PC, requiring <see cref="IMemory.SetRequestPc" /> to be called before each access
///     (Wu et al., MICRO 2011). Tree-PLRU (Plru) is a hardware-friendly approximation using
///     a binary tree of bits per set; exact LRU for 2-way, approximation for wider associativity
///     (as used in Intel P6 and later designs). Hawkeye uses OPTgen to reconstruct Belady's
///     optimal decisions for the observed PC/address stream and trains a PC-indexed 3-bit
///     saturating-counter predictor; cache-friendly lines insert at RRPV=0, cache-averse at
///     RRPV=7 (Jain &amp; Lin, ISCA 2016).
/// </param>
/// <param name="PolicyFactory">
///     Optional factory for a caller-supplied <see cref="IReplacementPolicy" /> instance
///     (e.g. <see cref="RtlFfiReplacementPolicy" />), invoked per cache level with that
///     level's (sets, ways). A non-null return overrides <see cref="ReplacementPolicy" />
///     for that level; null falls back to the configured kind — so an RTL policy
///     elaborated for one geometry applies only to the matching level(s).
/// </param>
/// <param name="PrefetcherFactory">
///     Optional factory for a caller-supplied <see cref="IPrefetcher" /> instance
///     (e.g. <see cref="RtlFfiPrefetcher" />). A non-null return overrides
///     <see cref="Prefetcher" />; prefetching (and <see cref="PrefetchLatency" />)
///     is considered enabled whenever the factory is set.
/// </param>
public sealed record MemoryConfig(
    int CacheCapacityBytes = 0,
    int CacheWays = 4,
    int CacheBlockBytes = 32,
    int CacheMissLatency = 10,
    int L2CapacityBytes = 0,
    int L2Ways = 8,
    int L2BlockBytes = 64,
    int L2MissLatency = 20,
    int L3CapacityBytes = 0,
    int L3Ways = 16,
    int L3BlockBytes = 64,
    int L3MissLatency = 50,
    int TlbEntries = 0,
    int TlbPageBytes = 4096,
    int TlbMissLatency = 20,
    ulong UncacheableBase = 0,
    ulong UncacheableSize = 0,
    PrefetcherKind Prefetcher = PrefetcherKind.None,
    int PrefetcherTableSize = 64,
    int PrefetcherDepth = 8,
    int PrefetchLatency = 0,
    ReplacementPolicyKind ReplacementPolicy = ReplacementPolicyKind.Lru,
    int CacheTagLatency = 0,
    int CacheDataLatency = 0,
    int L2TagLatency = 0,
    int L2DataLatency = 0,
    int L3TagLatency = 0,
    int L3DataLatency = 0,
    CacheAccessModeKind CacheAccessMode = CacheAccessModeKind.Parallel,
    CacheAccessModeKind L2AccessMode = CacheAccessModeKind.Parallel,
    CacheAccessModeKind L3AccessMode = CacheAccessModeKind.Parallel,
    WritePolicyKind CacheWritePolicy = WritePolicyKind.WriteThrough,
    WriteMissPolicyKind CacheWriteMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
    WritePolicyKind L2WritePolicy = WritePolicyKind.WriteThrough,
    WriteMissPolicyKind L2WriteMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
    WritePolicyKind L3WritePolicy = WritePolicyKind.WriteThrough,
    WriteMissPolicyKind L3WriteMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
    int CacheWbCapacity = 0,
    int L2WbCapacity = 0,
    int L3WbCapacity = 0,
    int CacheMshrCount = 0,
    int L2MshrCount = 0,
    int L3MshrCount = 0,
    InclusionPolicyKind L2InclusionPolicy = InclusionPolicyKind.Nine,
    InclusionPolicyKind L3InclusionPolicy = InclusionPolicyKind.Nine,
    int CacheCriticalWordLatency = 0,
    int L2CriticalWordLatency = 0,
    int L3CriticalWordLatency = 0,
    int CacheBankCount = 1,
    int CacheReadPorts = 0,
    int CacheWritePorts = 0,
    int L2BankCount = 1,
    int L2ReadPorts = 0,
    int L2WritePorts = 0,
    int L3BankCount = 1,
    int L3ReadPorts = 0,
    int L3WritePorts = 0,
    int CacheSectorBytes = 0,
    int L2SectorBytes = 0,
    int L3SectorBytes = 0,
    int CacheVictimCacheEntries = 0,
    int L2VictimCacheEntries = 0,
    int L3VictimCacheEntries = 0,
    int CacheVictimCacheHitLatency = 1,
    int L2VictimCacheHitLatency = 1,
    int L3VictimCacheHitLatency = 1,
    Func<int, int, IReplacementPolicy?>? PolicyFactory = null,
    Func<IPrefetcher?>? PrefetcherFactory = null
) {
    public static readonly MemoryConfig None = new();
}

/// <summary>
///     The resolved memory access chain for one port (I or D).
///     Accessor is always non-null — it is the top of the chain.
///     Cache, L2Cache, L3Cache, and Tlb are non-null only when the corresponding layer is enabled.
///     Access order from processor: Accessor → [Tlb] → [L1 Cache] → [L2 Cache] → [L3 Cache] → backing.
/// </summary>
public sealed record MemoryLayers(
    IMemory Accessor,
    SetAssociativeCache? Cache,
    SetAssociativeCache? L2Cache,
    SetAssociativeCache? L3Cache,
    Tlb? Tlb,
    IPrefetcher? Prefetcher,
    ulong UncacheableBase,
    ulong UncacheableSize
) {
    /// <summary>
    ///     Build a layer stack: backing → [L3] → [L2] → [L1] → [TLB].
    ///     The caller always uses Accessor; stats come from Cache, L2Cache, L3Cache, and Tlb.
    /// </summary>
    public static MemoryLayers Build(IMemory backing, MemoryConfig cfg) {
        IMemory current = backing;
        SetAssociativeCache? l3 = null, l2 = null, l1 = null;
        Tlb? tlb = null;

        if (cfg.L3CapacityBytes > 0) {
            l3 = new SetAssociativeCache(
                current, cfg.L3CapacityBytes, cfg.L3Ways, cfg.L3BlockBytes, cfg.L3MissLatency,
                0, cfg.ReplacementPolicy, cfg.L3TagLatency, cfg.L3DataLatency,
                cfg.L3WritePolicy, cfg.L3WriteMissPolicy, cfg.L3WbCapacity, cfg.L3MshrCount, cfg.L3AccessMode,
                cfg.L3InclusionPolicy, cfg.L3CriticalWordLatency, cfg.L3BankCount, cfg.L3ReadPorts, cfg.L3WritePorts,
                cfg.L3SectorBytes, cfg.L3VictimCacheEntries, cfg.L3VictimCacheHitLatency,
                cfg.PolicyFactory?.Invoke(
                    cfg.L3CapacityBytes / (cfg.L3Ways * cfg.L3BlockBytes), cfg.L3Ways
                )
            );
            current = l3;
        }

        if (cfg.L2CapacityBytes > 0) {
            l2 = new SetAssociativeCache(
                current, cfg.L2CapacityBytes, cfg.L2Ways, cfg.L2BlockBytes, cfg.L2MissLatency,
                0, cfg.ReplacementPolicy, cfg.L2TagLatency, cfg.L2DataLatency,
                cfg.L2WritePolicy, cfg.L2WriteMissPolicy, cfg.L2WbCapacity, cfg.L2MshrCount, cfg.L2AccessMode,
                cfg.L2InclusionPolicy, cfg.L2CriticalWordLatency, cfg.L2BankCount, cfg.L2ReadPorts, cfg.L2WritePorts,
                cfg.L2SectorBytes, cfg.L2VictimCacheEntries, cfg.L2VictimCacheHitLatency,
                cfg.PolicyFactory?.Invoke(
                    cfg.L2CapacityBytes / (cfg.L2Ways * cfg.L2BlockBytes), cfg.L2Ways
                )
            );
            l3?.AttachInner(l2);
            current = l2;
        }

        if (cfg.CacheCapacityBytes > 0) {
            l1 = new SetAssociativeCache(
                current, cfg.CacheCapacityBytes, cfg.CacheWays, cfg.CacheBlockBytes, cfg.CacheMissLatency,
                cfg.Prefetcher != PrefetcherKind.None || cfg.PrefetcherFactory is not null
                    ? cfg.PrefetchLatency
                    : 0,
                cfg.ReplacementPolicy, cfg.CacheTagLatency, cfg.CacheDataLatency,
                cfg.CacheWritePolicy, cfg.CacheWriteMissPolicy, cfg.CacheWbCapacity, cfg.CacheMshrCount,
                cfg.CacheAccessMode, InclusionPolicyKind.Nine, cfg.CacheCriticalWordLatency,
                cfg.CacheBankCount, cfg.CacheReadPorts, cfg.CacheWritePorts, cfg.CacheSectorBytes,
                cfg.CacheVictimCacheEntries, cfg.CacheVictimCacheHitLatency,
                cfg.PolicyFactory?.Invoke(
                    cfg.CacheCapacityBytes / (cfg.CacheWays * cfg.CacheBlockBytes), cfg.CacheWays
                )
            );
            (l2 ?? l3)?.AttachInner(l1);
            current = l1;
        }

        if (cfg.TlbEntries > 0) {
            tlb = new Tlb(current, cfg.TlbEntries, cfg.TlbPageBytes, cfg.TlbMissLatency);
            current = tlb;
        }

        // Route a memory-mapped-I/O region straight to the backing, bypassing the
        // cache/TLB chain — only meaningful when something is cached above it.
        if (cfg.UncacheableSize > 0 && (l1 ?? l2 ?? l3) is not null)
            current = new UncacheableMemory(current, backing, cfg.UncacheableBase, cfg.UncacheableSize);

        // A caller-supplied prefetcher instance (e.g. RtlFfiPrefetcher) overrides the kind.
        IPrefetcher? prefetcher = l1 is not null
            ? cfg.PrefetcherFactory?.Invoke() ?? cfg.Prefetcher switch {
                PrefetcherKind.NextLine => new NextLinePrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Stride   => new StridePrefetcher(cfg.PrefetcherTableSize),
                PrefetcherKind.Stream => new StreamPrefetcher(
                    cfg.PrefetcherTableSize, cfg.PrefetcherDepth, cfg.CacheBlockBytes
                ),
                PrefetcherKind.Ipcp   => new IpcpPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Pythia => new PythiaPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Berti  => new BertiPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Sms    => new SmsPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Bop    => new BopPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Spp    => new SppPrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Ppf    => new PpfPrefetcher(cfg.CacheBlockBytes),
                _                     => null,
            }
            : null;

        return new MemoryLayers(current, l1, l2, l3, tlb, prefetcher, cfg.UncacheableBase, cfg.UncacheableSize);
    }

    /// <summary>
    ///     Build a layer stack from a <see cref="CachePathSpec" /> and optional shared levels list.
    ///     Levels are stacked from outermost (farthest from CPU) to innermost; each level uses its
    ///     own replacement policy. The first three caches in the resulting stack are surfaced as the
    ///     named <see cref="Cache" />, <see cref="L2Cache" />, and <see cref="L3Cache" /> stat fields
    ///     (innermost first); deeper levels are accessible only through the <see cref="Accessor" /> chain.
    /// </summary>
    public static MemoryLayers Build(
        IMemory backing,
        CachePathSpec path,
        IReadOnlyList<CacheLevelSpec>? sharedLevels = null,
        ulong uncacheableBase = 0,
        ulong uncacheableSize = 0
    ) {
        IReadOnlyList<CacheLevelSpec> privLevels = path.Levels ?? [];
        IReadOnlyList<CacheLevelSpec> sharedList = sharedLevels ?? [];

        // Build from outermost to innermost so each layer wraps the one below it.
        // Order: shared levels (outermost first) → private levels (outermost-private first).
        IMemory current = backing;
        var allCaches = new List<SetAssociativeCache>();
        var allSpecs = new List<CacheLevelSpec>();

        for (int i = sharedList.Count - 1; i >= 0; i--) {
            CacheLevelSpec s = sharedList[i];
            int prefLat = s.Prefetcher != PrefetcherKind.None || s.PrefetcherFactory is not null
                ? s.PrefetchLatency
                : 0;
            var cache = new SetAssociativeCache(
                current, s.CapacityBytes, s.Ways, s.BlockBytes, s.MissLatency, prefLat, s.ReplacementPolicy,
                s.TagLatency, s.DataLatency, s.WritePolicy, s.WriteMissPolicy, s.WbCapacity, s.MshrCount,
                s.AccessMode, s.InclusionPolicy, s.CriticalWordLatency, s.BankCount, s.ReadPorts, s.WritePorts,
                s.SectorBytes, s.VictimCacheEntries, s.VictimCacheHitLatency,
                s.PolicyFactory?.Invoke(s.CapacityBytes / (s.Ways * s.BlockBytes), s.Ways)
            );
            allCaches.Insert(0, cache);
            allSpecs.Insert(0, s);
            current = cache;
        }

        for (int i = privLevels.Count - 1; i >= 0; i--) {
            CacheLevelSpec s = privLevels[i];
            int prefLat = s.Prefetcher != PrefetcherKind.None || s.PrefetcherFactory is not null
                ? s.PrefetchLatency
                : 0;
            var cache = new SetAssociativeCache(
                current, s.CapacityBytes, s.Ways, s.BlockBytes, s.MissLatency, prefLat, s.ReplacementPolicy,
                s.TagLatency, s.DataLatency, s.WritePolicy, s.WriteMissPolicy, s.WbCapacity, s.MshrCount,
                s.AccessMode, s.InclusionPolicy, s.CriticalWordLatency, s.BankCount, s.ReadPorts, s.WritePorts,
                s.SectorBytes, s.VictimCacheEntries, s.VictimCacheHitLatency,
                s.PolicyFactory?.Invoke(s.CapacityBytes / (s.Ways * s.BlockBytes), s.Ways)
            );
            allCaches.Insert(0, cache);
            allSpecs.Insert(0, s);
            current = cache;
        }

        // allCaches is innermost-first; wire each level's InclusionPolicy toward its inner neighbor.
        for (var i = 1; i < allCaches.Count; i++) allCaches[i].AttachInner(allCaches[i - 1]);

        // TLB wraps the innermost cache; UncacheableMemory wraps TLB (matching MemoryConfig build order).
        Tlb? tlb = null;
        if (path.Tlb is { } tlbSpec) {
            tlb = new Tlb(current, tlbSpec.Entries, tlbSpec.PageBytes, tlbSpec.MissLatency);
            current = tlb;
        }

        if (uncacheableSize > 0 && allCaches.Count > 0)
            current = new UncacheableMemory(current, backing, uncacheableBase, uncacheableSize);

        // MemoryLayers.Prefetcher corresponds to allCaches[0] (the innermost cache = Cache).
        // TryPrefetch targets Cache, so only the innermost level's strategy is activated by the pipeline.
        // A caller-supplied prefetcher instance (e.g. RtlFfiPrefetcher) overrides the kind.
        IPrefetcher? prefetcher = allSpecs.Count > 0 ? allSpecs[0].PrefetcherFactory?.Invoke() : null;
        if (prefetcher is null && allSpecs.Count > 0 && allSpecs[0] is { Prefetcher: not PrefetcherKind.None, } s0)
            prefetcher = s0.Prefetcher switch {
                PrefetcherKind.NextLine => new NextLinePrefetcher(s0.BlockBytes),
                PrefetcherKind.Stride   => new StridePrefetcher(s0.PrefetcherTableSize),
                PrefetcherKind.Stream => new StreamPrefetcher(
                    s0.PrefetcherTableSize, s0.PrefetcherDepth, s0.BlockBytes
                ),
                PrefetcherKind.Ipcp   => new IpcpPrefetcher(s0.BlockBytes),
                PrefetcherKind.Pythia => new PythiaPrefetcher(s0.BlockBytes),
                PrefetcherKind.Berti  => new BertiPrefetcher(s0.BlockBytes),
                PrefetcherKind.Sms    => new SmsPrefetcher(s0.BlockBytes),
                PrefetcherKind.Bop    => new BopPrefetcher(s0.BlockBytes),
                PrefetcherKind.Spp    => new SppPrefetcher(s0.BlockBytes),
                PrefetcherKind.Ppf    => new PpfPrefetcher(s0.BlockBytes),
                _                     => null,
            };

        // Map the first three caches to the named MemoryLayers stat fields (innermost first).
        SetAssociativeCache? c0 = allCaches.Count > 0 ? allCaches[0] : null;
        SetAssociativeCache? c1 = allCaches.Count > 1 ? allCaches[1] : null;
        SetAssociativeCache? c2 = allCaches.Count > 2 ? allCaches[2] : null;

        return new MemoryLayers(current, c0, c1, c2, tlb, prefetcher, uncacheableBase, uncacheableSize);
    }

    /// <summary>Drains and sums pending stall cycles from all cache and TLB levels.</summary>
    public long ConsumeAllStalls() =>
        (Cache?.ConsumePendingStalls() ?? 0) +
        (L2Cache?.ConsumePendingStalls() ?? 0) +
        (L3Cache?.ConsumePendingStalls() ?? 0) +
        (Tlb?.ConsumePendingStalls() ?? 0);

    /// <summary>Advances write-back buffer drain by one entry across all cache levels.</summary>
    public void TickWb() {
        Cache?.TickWb();
        L2Cache?.TickWb();
        L3Cache?.TickWb();
    }

    /// <summary>Advances MSHR in-flight countdowns by one cycle across all cache levels.</summary>
    public void TickMshr() {
        Cache?.TickMshr();
        L2Cache?.TickMshr();
        L3Cache?.TickMshr();
    }

    /// <summary>Resets per-bank read/write port usage by one cycle across all cache levels.</summary>
    public void TickPorts() {
        Cache?.TickPorts();
        L2Cache?.TickPorts();
        L3Cache?.TickPorts();
    }

    /// <summary>
    ///     Prefetches the L1 line covering <paramref name="address" /> without any stall penalty
    ///     at install time (with <see cref="MemoryConfig.PrefetchLatency" /> &gt; 0 the line is in
    ///     flight and a demand hit pays the remaining countdown). Guards against the uncacheable
    ///     MMIO region: any prefetch that would land on (or overlap) an uncacheable line is
    ///     silently dropped, preventing re-caching of HTIF registers. No-ops when no prefetcher
    ///     is configured or no L1 is present.
    /// </summary>
    public void TryPrefetch(ulong address) {
        if (Cache is null || Prefetcher is null) return;
        if (UncacheableSize > 0) {
            ulong lineStart = address & ~(ulong)(Cache.BlockBytes - 1);
            ulong lineEnd = lineStart + (ulong)Cache.BlockBytes;
            if (lineEnd > UncacheableBase && lineStart < UncacheableBase + UncacheableSize) return;
        }

        Cache.Prefetch(address);
    }
}