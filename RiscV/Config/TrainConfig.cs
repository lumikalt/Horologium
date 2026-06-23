using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Cache;

namespace RiscV.Config;

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
    string Pipeline = "five_stage", // "five_stage" | "superscalar" | "ooo"

    // ── FiveStageTrain parameters ─────────────────────────────────────────────
    bool ForwardingEnabled = true,
    BranchPredictorConfig? Predictor = null,
    // L1 caches (per-port, split I/D)
    CacheHardwareConfig? ICache = null,
    CacheHardwareConfig? DCache = null,
    TlbHardwareConfig? ITlb = null,
    TlbHardwareConfig? DTlb = null,
    // L2 and L3 (per-port; unified L2/L3 would require a shared cache object)
    CacheHardwareConfig? IL2Cache = null,
    CacheHardwareConfig? DL2Cache = null,
    CacheHardwareConfig? IL3Cache = null,
    CacheHardwareConfig? DL3Cache = null,
    int StoreBufferCapacity = 0,

    // ── OooeTrain parameters ──────────────────────────────────────────────────
    int IssueWidth = 2,
    int RobCapacity = 32,
    int IqCapacity = 16,
    int ExtraPhysRegs = 32
) {
    [JsonIgnore] private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MemoryConfig ToIMemoryConfig() => ToMemoryConfig(ICache, IL2Cache, IL3Cache, ITlb);
    public MemoryConfig ToDMemoryConfig() => ToMemoryConfig(DCache, DL2Cache, DL3Cache, DTlb);

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
