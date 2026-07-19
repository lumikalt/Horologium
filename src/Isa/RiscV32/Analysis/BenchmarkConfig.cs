using System.Text.Json;
using System.Text.Json.Serialization;

namespace RiscV32.Analysis;

/// <summary>
///     One benchmark in a batch run: an ELF binary, its command-line arguments, optional stdin
///     redirection, and an optional reference-output file to validate against. Mirrors
///     <see cref="NamedConfig" />'s JSON conventions so hardware sweeps and benchmark sets both
///     live in plain, hand-editable JSON files.
/// </summary>
/// <param name="Name">Display name for this benchmark.</param>
/// <param name="ElfPath">Path to the ELF binary. <c>argv[0]</c> is derived from its file name.</param>
/// <param name="Args">Command-line arguments after <c>argv[0]</c>; empty when omitted.</param>
/// <param name="StdinPath">
///     Optional file whose contents are redirected to the guest's stdin (fd 0). When omitted,
///     stdin reads as EOF immediately, matching <see cref="Syscalls.LinuxSyscallEmulator" />'s
///     default.
/// </param>
/// <param name="ExpectedOutputPath">
///     Optional reference-output file. When present, <see cref="Experiment.RunBenchmark" /> compares
///     it byte-for-byte against captured stdout/stderr; when absent, there is nothing to check —
///     see <see cref="BenchmarkResult.Checked" />.
/// </param>
/// <param name="MemorySizeBytes">Overrides the workload's auto-computed memory size when set.</param>
public sealed record BenchmarkConfig(
    string Name,
    string ElfPath,
    IReadOnlyList<string>? Args = null,
    string? StdinPath = null,
    string? ExpectedOutputPath = null,
    int? MemorySizeBytes = null
) {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deserialises a JSON array of benchmark configs.</summary>
    public static IReadOnlyList<BenchmarkConfig> FromJson(string json) =>
        JsonSerializer.Deserialize<List<BenchmarkConfig>>(json, BenchmarkConfig.Options)
     ?? throw new JsonException("Deserialised benchmark set was null.");

    /// <summary>Loads a benchmark set from a JSON file on disk.</summary>
    public static IReadOnlyList<BenchmarkConfig> LoadFile(string path) =>
        FromJson(File.ReadAllText(path));

    /// <summary>Serialises a list of benchmark configs to a JSON benchmark-set file.</summary>
    public static string ToJson(IEnumerable<BenchmarkConfig> benchmarks) =>
        JsonSerializer.Serialize(benchmarks.ToList(), BenchmarkConfig.Options);
}

/// <summary>Result of running one <see cref="BenchmarkConfig" /> to completion (or to <c>maxTicks</c>).</summary>
public sealed record BenchmarkResult(
    string Name,
    bool Halted,
    long Ticks,
    string Output,
    string? ExpectedOutput
) {
    /// <summary>True when an <see cref="ExpectedOutput" /> was supplied, so <see cref="Passed" /> means something.</summary>
    public bool Checked => ExpectedOutput is not null;

    /// <summary>
    ///     True when there was no reference output to check, or the captured output matches it
    ///     exactly (byte-for-byte — see <see cref="Experiment.RunBenchmark" /> for how both sides
    ///     are read). Check <see cref="Checked" /> to tell "nothing to verify" apart from "verified".
    /// </summary>
    public bool Passed => ExpectedOutput is null || Output == ExpectedOutput;
}
