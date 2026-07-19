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
/// <param name="MmapArenaBytes">
///     Size of the anonymous-mmap bump-allocation arena passed to
///     <see cref="Syscalls.LinuxSyscallEmulator" />. Null or 0 (the default) disables <c>mmap</c>
///     entirely (<c>SYS_mmap</c> returns <c>ENOMEM</c>) — a malloc that falls back to <c>brk</c>
///     when that happens still works, but a malloc-heavy binary that mmaps large allocations
///     directly will fail. When set, <see cref="Experiment.RunBenchmark" /> appends this many bytes
///     onto the workload's memory and dedicates that region to the arena — it never overlaps the
///     stack or the <c>brk</c>-growable region, both of which keep the exact placement they'd have
///     with this field unset.
/// </param>
/// <param name="NormalizeTrailingWhitespace">
///     When true, <see cref="BenchmarkResult.Passed" /> trims trailing whitespace from both the
///     captured output and <see cref="ExpectedOutputPath" />'s contents before comparing, instead
///     of requiring a byte-exact match. Off by default, so an existing byte-exact reference keeps
///     its current, stricter behaviour. Real reference-output files (the SPEC-style convention)
///     almost always end in a trailing newline regardless of whether the guest's last write did —
///     set this per-benchmark to stop that alone from failing the comparison.
/// </param>
public sealed record BenchmarkConfig(
    string Name,
    string ElfPath,
    IReadOnlyList<string>? Args = null,
    string? StdinPath = null,
    string? ExpectedOutputPath = null,
    int? MemorySizeBytes = null,
    int? MmapArenaBytes = null,
    bool NormalizeTrailingWhitespace = false
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
    string? ExpectedOutput,
    bool NormalizeTrailingWhitespace = false
) {
    /// <summary>True when an <see cref="ExpectedOutput" /> was supplied, so <see cref="Passed" /> means something.</summary>
    public bool Checked => ExpectedOutput is not null;

    /// <summary>
    ///     True when there was no reference output to check, or the captured output matches it —
    ///     byte-for-byte by default (see <see cref="Experiment.RunBenchmark" /> for how both sides
    ///     are read), or with trailing whitespace trimmed from both sides first when
    ///     <see cref="NormalizeTrailingWhitespace" /> is set (see
    ///     <see cref="BenchmarkConfig.NormalizeTrailingWhitespace" />). Check <see cref="Checked" />
    ///     to tell "nothing to verify" apart from "verified".
    /// </summary>
    public bool Passed => ExpectedOutput is null || (NormalizeTrailingWhitespace
        ? Output.TrimEnd() == ExpectedOutput.TrimEnd()
        : Output == ExpectedOutput);
}
