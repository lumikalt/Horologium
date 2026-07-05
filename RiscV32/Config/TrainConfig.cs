using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Cache;
using Pipeline.Ooo;

namespace RiscV32.Config;

/// <summary>
/// Cache configuration for one memory port (instruction or data).
/// null means the cache layer is disabled.
/// </summary>
public sealed record CacheHardwareConfig(
    int CapacityBytes,
    int Ways = 4,
    int BlockBytes = 32,
    int MissLatency = 10
);

/// <summary>
/// TLB configuration for one memory port.
/// null means the TLB layer is disabled.
/// </summary>
public sealed record TlbHardwareConfig(
    int Entries,
    int PageBytes = 4096,
    int MissLatency = 20
);

/// <summary>
/// Fully-serialisable description of one hardware configuration.
/// Covers both <c>FiveStageTrain</c> and <c>OooeTrain</c>; the active pipeline
/// is selected by <see cref="Pipeline"/>.
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
    int IqCapacity = 8, // per-class depth; 5 classes × 8 = 40 total slots
    int ExtraPhysRegs = 32,
    FuLatencyConfig? FuLatency = null, // null → FuLatencyConfig.Default (all 1-cycle except MulDiv=3)
    int MshrCapacity = 0,              // 0 = unlimited outstanding misses
    string? DPrefetcher = null,        // null | "next_line" | "stride"
    int DPrefetcherTableSize = 64,
    int DPrefetchLatency = 0, // cycles until a prefetched line is usable; 0 = free/instant
    string? CacheReplacementPolicy = null // null/"lru" | "srrip" | "brrip" | "drrip" | "ship" | "ship_pc" | "random" | "fifo"
) {
    [JsonIgnore] private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MemoryConfig ToIMemoryConfig() =>
        ToMemoryConfig(ICache, L2Cache, L3Cache, ITlb) with { ReplacementPolicy = ParseReplacementPolicy() };

    public MemoryConfig ToDMemoryConfig() {
        MemoryConfig mc = ToMemoryConfig(DCache, L2Cache, L3Cache, DTlb);
        PrefetcherKind kind = DPrefetcher?.ToLowerInvariant() switch {
            "next_line" => PrefetcherKind.NextLine,
            "stride"    => PrefetcherKind.Stride,
            _           => PrefetcherKind.None,
        };
        return mc with {
            Prefetcher = kind,
            PrefetcherTableSize = DPrefetcherTableSize,
            PrefetchLatency = DPrefetchLatency,
            ReplacementPolicy = ParseReplacementPolicy(),
        };
    }

    private ReplacementPolicyKind ParseReplacementPolicy() =>
        CacheReplacementPolicy?.ToLowerInvariant() switch {
            "srrip"   => ReplacementPolicyKind.Srrip,
            "brrip"   => ReplacementPolicyKind.Brrip,
            "drrip"   => ReplacementPolicyKind.Drrip,
            "ship"    => ReplacementPolicyKind.Ship,
            "ship_pc" => ReplacementPolicyKind.ShipPc,
            "random"  => ReplacementPolicyKind.Random,
            "fifo"    => ReplacementPolicyKind.Fifo,
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
            tlb?.MissLatency ?? 20
        );
}