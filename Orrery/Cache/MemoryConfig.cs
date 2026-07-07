using Mechanism;
using Orrery.Spec;

namespace Orrery.Cache;

public enum PrefetcherKind {
    None, NextLine, Stride,
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
/// <param name="UncacheableBase">Start of a memory-mapped-I/O region that bypasses all
/// caches (0 with <see cref="UncacheableSize"/> = 0 disables it). Such a region <em>must</em>
/// be uncacheable: a device's side effects (e.g. an HTIF <c>fromhost</c> ACK written to the
/// backing below the cache) are otherwise masked by stale cached lines, hanging the run.</param>
/// <param name="UncacheableSize">Size of the uncacheable MMIO region in bytes (0 = disabled).</param>
/// <param name="Prefetcher">Prefetch strategy for this memory port. Ignored when no L1 cache
/// is configured. Prefetches respect the uncacheable region.</param>
/// <param name="PrefetcherTableSize">RPT table entries for <see cref="PrefetcherKind.Stride"/>;
/// must be a power of 2. Ignored for other prefetcher kinds.</param>
/// <param name="PrefetchLatency">Cycles until a prefetched line is usable (0 = instant/free,
/// the idealized model). A demand hit on a line whose prefetch is still in flight pays the
/// remaining countdown instead of zero, and in-flight prefetches count against MSHR capacity.</param>
/// <param name="CacheTagLatency">L1 tag-array lookup cycles. Hit latency = max(CacheTagLatency, CacheDataLatency);
/// the OoO pipeline sources load result timing from this rather than FuLatencyConfig.LoadHitLatency when non-zero.</param>
/// <param name="CacheDataLatency">L1 data-array read cycles (parallel with tag in the default gem5 mode).</param>
/// <param name="L2TagLatency">L2 tag-array lookup cycles.</param>
/// <param name="L2DataLatency">L2 data-array read cycles.</param>
/// <param name="L3TagLatency">L3 tag-array lookup cycles.</param>
/// <param name="L3DataLatency">L3 data-array read cycles.</param>
/// <param name="ReplacementPolicy">Cache replacement policy applied to every cache level.
/// Defaults to LRU. SRRIP is scan-resistant; DRRIP adds thrash-resistance via Set Dueling
/// (Jaleel et al., ISCA 2010). SHiP uses per-signature reuse history to predict insertion
/// RRPV: SHiP-Mem (Ship) indexes the SHCT by upper address bits; SHiP-PC (ShipPc) indexes
/// by load PC, requiring <see cref="IMemory.SetRequestPc"/> to be called before each access
/// (Wu et al., MICRO 2011). Tree-PLRU (Plru) is a hardware-friendly approximation using
/// a binary tree of bits per set; exact LRU for 2-way, approximation for wider associativity
/// (as used in Intel P6 and later designs). Hawkeye uses OPTgen to reconstruct Belady's
/// optimal decisions for the observed PC/address stream and trains a PC-indexed 3-bit
/// saturating-counter predictor; cache-friendly lines insert at RRPV=0, cache-averse at
/// RRPV=7 (Jain &amp; Lin, ISCA 2016).</param>
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
    int PrefetchLatency = 0,
    ReplacementPolicyKind ReplacementPolicy = ReplacementPolicyKind.Lru,
    int CacheTagLatency = 0,
    int CacheDataLatency = 0,
    int L2TagLatency = 0,
    int L2DataLatency = 0,
    int L3TagLatency = 0,
    int L3DataLatency = 0
) {
    public static readonly MemoryConfig None = new();
}

/// <summary>
/// The resolved memory access chain for one port (I or D).
/// Accessor is always non-null — it is the top of the chain.
/// Cache, L2Cache, L3Cache, and Tlb are non-null only when the corresponding layer is enabled.
/// Access order from processor: Accessor → [Tlb] → [L1 Cache] → [L2 Cache] → [L3 Cache] → backing.
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
    /// Build a layer stack: backing → [L3] → [L2] → [L1] → [TLB].
    /// The caller always uses Accessor; stats come from Cache, L2Cache, L3Cache, and Tlb.
    /// </summary>
    public static MemoryLayers Build(IMemory backing, MemoryConfig cfg) {
        IMemory current = backing;
        SetAssociativeCache? l3 = null, l2 = null, l1 = null;
        Tlb? tlb = null;

        if (cfg.L3CapacityBytes > 0) {
            l3 = new SetAssociativeCache(
                current, cfg.L3CapacityBytes, cfg.L3Ways, cfg.L3BlockBytes, cfg.L3MissLatency,
                0, cfg.ReplacementPolicy, cfg.L3TagLatency, cfg.L3DataLatency
            );
            current = l3;
        }

        if (cfg.L2CapacityBytes > 0) {
            l2 = new SetAssociativeCache(
                current, cfg.L2CapacityBytes, cfg.L2Ways, cfg.L2BlockBytes, cfg.L2MissLatency,
                0, cfg.ReplacementPolicy, cfg.L2TagLatency, cfg.L2DataLatency
            );
            current = l2;
        }

        if (cfg.CacheCapacityBytes > 0) {
            l1 = new SetAssociativeCache(
                current, cfg.CacheCapacityBytes, cfg.CacheWays, cfg.CacheBlockBytes, cfg.CacheMissLatency,
                cfg.Prefetcher != PrefetcherKind.None ? cfg.PrefetchLatency : 0,
                cfg.ReplacementPolicy, cfg.CacheTagLatency, cfg.CacheDataLatency
            );
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

        IPrefetcher? prefetcher = l1 is not null
            ? cfg.Prefetcher switch {
                PrefetcherKind.NextLine => new NextLinePrefetcher(cfg.CacheBlockBytes),
                PrefetcherKind.Stride   => new StridePrefetcher(cfg.PrefetcherTableSize),
                _                       => null,
            }
            : null;

        return new MemoryLayers(current, l1, l2, l3, tlb, prefetcher, cfg.UncacheableBase, cfg.UncacheableSize);
    }

    /// <summary>
    /// Build a layer stack from a <see cref="CachePathSpec"/> and optional shared levels list.
    /// Levels are stacked from outermost (farthest from CPU) to innermost; each level uses its
    /// own replacement policy. The first three caches in the resulting stack are surfaced as the
    /// named <see cref="Cache"/>, <see cref="L2Cache"/>, and <see cref="L3Cache"/> stat fields
    /// (innermost first); deeper levels are accessible only through the <see cref="Accessor"/> chain.
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
            int prefLat = s.Prefetcher != PrefetcherKind.None ? s.PrefetchLatency : 0;
            var cache = new SetAssociativeCache(
                current, s.CapacityBytes, s.Ways, s.BlockBytes, s.MissLatency, prefLat, s.ReplacementPolicy,
                s.TagLatency, s.DataLatency
            );
            allCaches.Insert(0, cache);
            allSpecs.Insert(0, s);
            current = cache;
        }

        for (int i = privLevels.Count - 1; i >= 0; i--) {
            CacheLevelSpec s = privLevels[i];
            int prefLat = s.Prefetcher != PrefetcherKind.None ? s.PrefetchLatency : 0;
            var cache = new SetAssociativeCache(
                current, s.CapacityBytes, s.Ways, s.BlockBytes, s.MissLatency, prefLat, s.ReplacementPolicy,
                s.TagLatency, s.DataLatency
            );
            allCaches.Insert(0, cache);
            allSpecs.Insert(0, s);
            current = cache;
        }

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
        IPrefetcher? prefetcher = null;
        if (allSpecs.Count > 0 && allSpecs[0] is { Prefetcher: not PrefetcherKind.None, } s0)
            prefetcher = s0.Prefetcher switch {
                PrefetcherKind.NextLine => new NextLinePrefetcher(s0.BlockBytes),
                PrefetcherKind.Stride   => new StridePrefetcher(s0.PrefetcherTableSize),
                _                       => null,
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

    /// <summary>
    /// Prefetches the L1 line covering <paramref name="address"/> without any stall penalty
    /// at install time (with <see cref="MemoryConfig.PrefetchLatency"/> &gt; 0 the line is in
    /// flight and a demand hit pays the remaining countdown). Guards against the uncacheable
    /// MMIO region: any prefetch that would land on (or overlap) an uncacheable line is
    /// silently dropped, preventing re-caching of HTIF registers. No-ops when no prefetcher
    /// is configured or no L1 is present.
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