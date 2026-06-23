using Mechanism;

namespace Orrery.Cache;

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
    int TlbMissLatency = 20
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
    Tlb? Tlb
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
                current, cfg.L3CapacityBytes, cfg.L3Ways, cfg.L3BlockBytes, cfg.L3MissLatency
            );
            current = l3;
        }

        if (cfg.L2CapacityBytes > 0) {
            l2 = new SetAssociativeCache(
                current, cfg.L2CapacityBytes, cfg.L2Ways, cfg.L2BlockBytes, cfg.L2MissLatency
            );
            current = l2;
        }

        if (cfg.CacheCapacityBytes > 0) {
            l1 = new SetAssociativeCache(
                current, cfg.CacheCapacityBytes, cfg.CacheWays, cfg.CacheBlockBytes, cfg.CacheMissLatency
            );
            current = l1;
        }

        if (cfg.TlbEntries > 0) {
            tlb = new Tlb(current, cfg.TlbEntries, cfg.TlbPageBytes, cfg.TlbMissLatency);
            current = tlb;
        }

        return new MemoryLayers(current, l1, l2, l3, tlb);
    }

    /// <summary>Drains and sums pending stall cycles from all cache and TLB levels.</summary>
    public long ConsumeAllStalls() =>
        (Cache?.ConsumePendingStalls() ?? 0) +
        (L2Cache?.ConsumePendingStalls() ?? 0) +
        (L3Cache?.ConsumePendingStalls() ?? 0) +
        (Tlb?.ConsumePendingStalls() ?? 0);
}
