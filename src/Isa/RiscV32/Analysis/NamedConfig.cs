using System.Text.Json;
using System.Text.Json.Serialization;
using RiscV32.Config;

namespace RiscV32.Analysis;

/// <summary>
///     A named hardware configuration for use in a sweep.
///     Serialises to / from JSON so sweeps can be described in files rather than code.
/// </summary>
public sealed record NamedConfig(string Name, TrainConfig Config) {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deserialises a JSON array of named configs.</summary>
    public static IReadOnlyList<NamedConfig> FromJson(string json) =>
        JsonSerializer.Deserialize<List<NamedConfig>>(json, NamedConfig.Options)
     ?? throw new JsonException("Deserialised sweep was null.");

    /// <summary>Loads a sweep from a JSON file on disk.</summary>
    public static IReadOnlyList<NamedConfig> LoadFile(string path) =>
        FromJson(File.ReadAllText(path));

    /// <summary>Serialises a list of named configs to a JSON sweep file.</summary>
    public static string ToJson(IEnumerable<NamedConfig> configs) =>
        JsonSerializer.Serialize(configs.ToList(), NamedConfig.Options);
}