using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Cache;
using Pipeline.Ooo;

namespace RiscV32.Config;

/// <summary>
///     DoCache configuration for one memory port (instruction or data).
///     null means the cache layer is disabled.
/// </summary>
public sealed record CacheHardwareConfig(
    int CapacityBytes,
    int Ways = 4,
    int BlockBytes = 32,
    int MissLatency = 10,
    int TagLatency = 0,
    int DataLatency = 0,
    WritePolicyKind WritePolicy = WritePolicyKind.WriteThrough,
    WriteMissPolicyKind WriteMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
    int WbCapacity = 0
);

/// <summary>
///     TLB configuration for one memory port.
///     null means the TLB layer is disabled.
/// </summary>
public sealed record TlbHardwareConfig(
    int Entries,
    int PageBytes = 4096,
    int MissLatency = 20
);

/// <summary>
///     Fully-serialisable description of one hardware configuration.
///     Covers both <c>FiveStageTrain</c> and <c>OooeTrain</c>; the active pipeline
///     is selected by <see cref="Pipeline" />.
/// </summary>
public sealed record TrainConfig(
    // ── Pipeline selector ─────────────────────────────────────────────────────
    string Pipeline = "five_stage", // "single_cycle" | "five_stage" | "superscalar" | "ooo"

    // ── FiveStageTrain parameters ─────────────────────────────────────────────
    bool ForwardingEnabled = true,
    BranchPredictorConfig? Predictor = null,
    // L1 caches (split I/D, as in real hardware)
    CacheHardwareConfig? ICache = null,
    CacheHardwareConfig? DCache = null,
    TlbHardwareConfig? ITlb = null,
    TlbHardwareConfig? DTlb = null,
    // L2 and L3 are unified (same config applied to both I and D paths)
    CacheHardwareConfig? L2Cache = null,
    CacheHardwareConfig? L3Cache = null,
    int StoreBufferCapacity = 0,

    // ── OooeTrain parameters ──────────────────────────────────────────────────
    int IssueWidth = 2,
    int RobCapacity = 32,
    int IqCapacity = 8,  // per-class depth; 5 classes × 8 = 40 total slots
    bool FlatIq = false, // true → one unified IQ with IqCount×IqCapacity slots (matches gem5 flat IQ)
    int ExtraPhysRegs = 32,
    FuLatencyConfig? FuLatency = null, // null → FuLatencyConfig.Default (all 1-cycle except MulDiv=3)
    int MshrCapacity = 0,              // 0 = unlimited outstanding misses
    string? DPrefetcher = null,        // null | "next_line" | "stride" | "stream" | "ipcp" | "pythia" | "berti" | "sms"
    int DPrefetcherTableSize = 64,
    int DPrefetcherDepth = 8, // stream buffer depth (lines ahead); ignored for other prefetchers
    int DPrefetchLatency = 0, // cycles until a prefetched line is usable; 0 = free/instant
    string? CacheReplacementPolicy
        = null, // null/"lru" | "mru" | "clock" | "srrip" | "brrip" | "drrip" | "ship" | "ship_pc" | "random" | "fifo" | "plru" | "hawkeye"
    bool EnableStoreSets = false, // Chrysos & Emer ISCA 1998 store-set memory dependence predictor
    int FdipFtqCapacity = 0, // 0 = disabled; fetch-directed I-cache prefetch (Reinman/Calder/Austin, MICRO 1999)
    bool Rdip = false, // RAS-directed I-cache prefetch (Kolli/Saidi/Wenisch, MICRO 2013)
    string? RtlCachePolicyLib
        = null, // Verilator-compiled RTL replacement policy (native/RtlFu); applies to cache levels whose
                // geometry matches the model's elaborated sets×ways, others keep CacheReplacementPolicy
    string? RtlPrefetcherLib
        = null // Verilator-compiled RTL D-cache prefetcher (native/RtlFu); overrides DPrefetcher
) {
    [JsonIgnore] private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MemoryConfig ToIMemoryConfig() =>
        ToMemoryConfig(ICache, L2Cache, L3Cache, ITlb) with {
            ReplacementPolicy = ParseReplacementPolicy(),
            PolicyFactory = MakePolicyFactory(),
        };

    public MemoryConfig ToDMemoryConfig() {
        MemoryConfig mc = ToMemoryConfig(DCache, L2Cache, L3Cache, DTlb);
        PrefetcherKind kind = DPrefetcher?.ToLowerInvariant() switch {
            "next_line" => PrefetcherKind.NextLine,
            "stride"    => PrefetcherKind.Stride,
            "stream"    => PrefetcherKind.Stream,
            "ipcp"      => PrefetcherKind.Ipcp,
            "pythia"    => PrefetcherKind.Pythia,
            "berti"     => PrefetcherKind.Berti,
            "sms"       => PrefetcherKind.Sms,
            _           => PrefetcherKind.None,
        };
        return mc with {
            Prefetcher = kind,
            PrefetcherTableSize = DPrefetcherTableSize,
            PrefetcherDepth = DPrefetcherDepth,
            PrefetchLatency = DPrefetchLatency,
            ReplacementPolicy = ParseReplacementPolicy(),
            PolicyFactory = MakePolicyFactory(),
            PrefetcherFactory = RtlPrefetcherLib is null
                ? null
                : () => new RtlFfiPrefetcher(RtlPrefetcherLib),
        };
    }

    // Each invocation loads its own verilated model (one per cache, on the worker thread
    // building the train); TryCreate returns null on geometry mismatch so non-matching
    // hierarchy levels keep the configured C# policy kind.
    private Func<int, int, IReplacementPolicy?>? MakePolicyFactory() =>
        RtlCachePolicyLib is null
            ? null
            : (sets, ways) => RtlFfiReplacementPolicy.TryCreate(RtlCachePolicyLib, sets, ways);

    private ReplacementPolicyKind ParseReplacementPolicy() =>
        CacheReplacementPolicy?.ToLowerInvariant() switch {
            "srrip"   => ReplacementPolicyKind.Srrip,
            "brrip"   => ReplacementPolicyKind.Brrip,
            "drrip"   => ReplacementPolicyKind.Drrip,
            "ship"    => ReplacementPolicyKind.Ship,
            "ship_pc" => ReplacementPolicyKind.ShipPc,
            "random"  => ReplacementPolicyKind.Random,
            "fifo"    => ReplacementPolicyKind.Fifo,
            "plru"    => ReplacementPolicyKind.Plru,
            "mru"     => ReplacementPolicyKind.Mru,
            "clock"   => ReplacementPolicyKind.Clock,
            "hawkeye" => ReplacementPolicyKind.Hawkeye,
            _         => ReplacementPolicyKind.Lru,
        };

    public string ToJson() => JsonSerializer.Serialize(this, TrainConfig.JsonOptions);

    public static TrainConfig FromJson(string json) =>
        JsonSerializer.Deserialize<TrainConfig>(json, TrainConfig.JsonOptions)
     ?? throw new JsonException("Deserialised TrainConfig was null.");

    private static MemoryConfig ToMemoryConfig(
        CacheHardwareConfig? l1,
        CacheHardwareConfig? l2,
        CacheHardwareConfig? l3,
        TlbHardwareConfig? tlb
    ) =>
        new(
            l1?.CapacityBytes ?? 0,
            l1?.Ways ?? 4,
            l1?.BlockBytes ?? 32,
            l1?.MissLatency ?? 10,
            l2?.CapacityBytes ?? 0,
            l2?.Ways ?? 8,
            l2?.BlockBytes ?? 64,
            l2?.MissLatency ?? 20,
            l3?.CapacityBytes ?? 0,
            l3?.Ways ?? 16,
            l3?.BlockBytes ?? 64,
            l3?.MissLatency ?? 50,
            tlb?.Entries ?? 0,
            tlb?.PageBytes ?? 4096,
            tlb?.MissLatency ?? 20,
            CacheTagLatency: l1?.TagLatency ?? 0,
            CacheDataLatency: l1?.DataLatency ?? 0,
            L2TagLatency: l2?.TagLatency ?? 0,
            L2DataLatency: l2?.DataLatency ?? 0,
            L3TagLatency: l3?.TagLatency ?? 0,
            L3DataLatency: l3?.DataLatency ?? 0,
            CacheWritePolicy: l1?.WritePolicy ?? WritePolicyKind.WriteThrough,
            CacheWriteMissPolicy: l1?.WriteMissPolicy ?? WriteMissPolicyKind.NoWriteAllocate,
            L2WritePolicy: l2?.WritePolicy ?? WritePolicyKind.WriteThrough,
            L2WriteMissPolicy: l2?.WriteMissPolicy ?? WriteMissPolicyKind.NoWriteAllocate,
            L3WritePolicy: l3?.WritePolicy ?? WritePolicyKind.WriteThrough,
            L3WriteMissPolicy: l3?.WriteMissPolicy ?? WriteMissPolicyKind.NoWriteAllocate,
            CacheWbCapacity: l1?.WbCapacity ?? 0,
            L2WbCapacity: l2?.WbCapacity ?? 0,
            L3WbCapacity: l3?.WbCapacity ?? 0
        );
}