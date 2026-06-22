using Mechanism;

namespace Orrery.Cache;

/// <param name="CacheCapacityBytes">0 = disabled.</param>
/// <param name="CacheWays">Associativity. Ignored when CacheCapacityBytes = 0.</param>
/// <param name="CacheBlockBytes">Cache line size. Ignored when CacheCapacityBytes = 0.</param>
/// <param name="CacheMissLatency">Extra cycles per cache miss.</param>
/// <param name="TlbEntries">0 = disabled.</param>
/// <param name="TlbPageBytes">Page size in bytes. Ignored when TlbEntries = 0.</param>
/// <param name="TlbMissLatency">Extra cycles per TLB miss.</param>
public sealed record MemoryConfig(
    int CacheCapacityBytes = 0,
    int CacheWays = 4,
    int CacheBlockBytes = 32,
    int CacheMissLatency = 10,
    int TlbEntries = 0,
    int TlbPageBytes = 4096,
    int TlbMissLatency = 20
) {
    public static readonly MemoryConfig None = new();
}

/// <summary>
/// The resolved memory access chain for one port (I or D).
/// Accessor is always non-null — it is the top of the chain.
/// Cache and Tlb are non-null only when the corresponding layer is enabled.
/// </summary>
public sealed record MemoryLayers(
    IMemory Accessor,
    SetAssociativeCache? Cache,
    Tlb? Tlb
) {
    /// <summary>
    /// Build a layer stack: backing → [cache] → [tlb].
    /// The caller always uses Accessor; stats come from Cache and Tlb.
    /// </summary>
    public static MemoryLayers Build(IMemory backing, MemoryConfig cfg) {
        IMemory current = backing;
        SetAssociativeCache? cache = null;
        Tlb? tlb = null;

        if (cfg.CacheCapacityBytes > 0) {
            cache = new SetAssociativeCache(
                current, cfg.CacheCapacityBytes, cfg.CacheWays,
                cfg.CacheBlockBytes, cfg.CacheMissLatency
            );
            current = cache;
        }

        if (cfg.TlbEntries > 0) {
            tlb = new Tlb(current, cfg.TlbEntries, cfg.TlbPageBytes, cfg.TlbMissLatency);
            current = tlb;
        }

        return new MemoryLayers(current, cache, tlb);
    }
}