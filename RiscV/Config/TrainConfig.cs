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
/// Fully-serialisable description of one FiveStageTrain hardware configuration.
/// Create with the constructor or deserialise from JSON via <see cref="FromJson"/>.
/// </summary>
public sealed record TrainConfig(
    bool ForwardingEnabled = true,
    BranchPredictorConfig? Predictor = null,
    CacheHardwareConfig? ICache = null,
    TlbHardwareConfig? ITlb = null,
    CacheHardwareConfig? DCache = null,
    TlbHardwareConfig? DTlb = null,
    int StoreBufferCapacity = 0
) {
    [JsonIgnore] private static readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MemoryConfig ToIMemoryConfig() => ToMemoryConfig(ICache, ITlb);
    public MemoryConfig ToDMemoryConfig() => ToMemoryConfig(DCache, DTlb);

    public string ToJson() => JsonSerializer.Serialize(this, TrainConfig._jsonOptions);

    public static TrainConfig FromJson(string json) =>
        JsonSerializer.Deserialize<TrainConfig>(json, TrainConfig._jsonOptions)
     ?? throw new JsonException("Deserialised TrainConfig was null.");

    private static MemoryConfig ToMemoryConfig(CacheHardwareConfig? cache, TlbHardwareConfig? tlb) =>
        new(
            cache?.CapacityBytes ?? 0,
            cache?.Ways ?? 4,
            cache?.BlockBytes ?? 32,
            cache?.MissLatency ?? 10,
            tlb?.Entries ?? 0,
            tlb?.PageBytes ?? 4096,
            tlb?.MissLatency ?? 20
        );
}