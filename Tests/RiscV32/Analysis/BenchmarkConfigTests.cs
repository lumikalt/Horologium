using Mechanism;
using RiscV32.Analysis;
using RiscV64;
using RiscV64.Memory;

namespace Tests.RiscV32.Analysis;

/// <summary>
///     <see cref="BenchmarkConfig" /> JSON round-trip and <see cref="Experiment.RunBenchmark" />
///     end to end: argv reaching a real guest through the psABI initial stack, stdin redirection,
///     and reference-output comparison. Reuses the existing RV64 SE-mode fixtures
///     (<c>abi_probe64.elf</c>, <c>stdin_echo64.elf</c>) — there is no RV32 equivalent, but
///     <see cref="Experiment.RunBenchmark" /> is ISA-agnostic (takes an already-built
///     <see cref="IElfWorkload" /> and a caller-supplied mechanism factory), so exercising it once
///     per ISA isn't required for correctness coverage.
/// </summary>
public class BenchmarkConfigTests {
    private static string AbiProbe64Elf => Path.Combine(AppContext.BaseDirectory, "abi_probe64.elf");
    private static string StdinEcho64Elf => Path.Combine(AppContext.BaseDirectory, "stdin_echo64.elf");

    [Fact]
    public void FromJson_ToJson_RoundTrips() {
        // Asserted field-by-field rather than via record equality on the whole list: Args is an
        // IReadOnlyList<string>, and record-generated Equals compares reference-typed properties
        // via EqualityComparer<T>.Default (reference equality for List<T>/arrays, not content) —
        // it would spuriously fail even on a correct round-trip. Direct Assert.Equal on two
        // enumerables (as done for Args below) does do structural sequence comparison; that's a
        // different, safe code path.
        IReadOnlyList<BenchmarkConfig> benchmarks = [
            new("probe", "abi_probe64.elf", ["hello"], ExpectedOutputPath: "expected.txt"),
            new("echo", "stdin_echo64.elf", StdinPath: "in.txt"),
        ];

        string json = BenchmarkConfig.ToJson(benchmarks);
        IReadOnlyList<BenchmarkConfig> roundTripped = BenchmarkConfig.FromJson(json);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal("probe", roundTripped[0].Name);
        Assert.Equal("abi_probe64.elf", roundTripped[0].ElfPath);
        Assert.Equal(["hello",], roundTripped[0].Args);
        Assert.Equal("expected.txt", roundTripped[0].ExpectedOutputPath);
        Assert.Null(roundTripped[0].StdinPath);

        Assert.Equal("echo", roundTripped[1].Name);
        Assert.Equal("in.txt", roundTripped[1].StdinPath);
        Assert.Null(roundTripped[1].Args);
        Assert.Null(roundTripped[1].ExpectedOutputPath);
    }

    [Fact]
    public void RunBenchmark_ArgvReachesGuest_OutputMatchesArgv0() {
        var bench = new BenchmarkConfig("probe", AbiProbe64Elf, ["hello"]);
        var workload = new Rv64ElfWorkload(bench.ElfPath);

        BenchmarkResult result = Experiment.RunBenchmark(
            bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
        );

        Assert.True(result.Halted);
        Assert.Equal("abi_probe64.elf", result.Output); // argv[0], derived from the ELF's file name
        Assert.False(result.Checked); // no ExpectedOutputPath supplied
        Assert.True(result.Passed);   // nothing to check → considered passed
    }

    [Fact]
    public void RunBenchmark_ExpectedOutputMatches_Passes() {
        string expectedPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(expectedPath, "abi_probe64.elf");
        try {
            var bench = new BenchmarkConfig("probe", AbiProbe64Elf, ExpectedOutputPath: expectedPath);
            var workload = new Rv64ElfWorkload(bench.ElfPath);

            BenchmarkResult result = Experiment.RunBenchmark(
                bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
            );

            Assert.True(result.Checked);
            Assert.True(result.Passed);
        }
        finally { File.Delete(expectedPath); }
    }

    [Fact]
    public void RunBenchmark_ExpectedOutputMismatches_Fails() {
        string expectedPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(expectedPath, "not the right output");
        try {
            var bench = new BenchmarkConfig("probe", AbiProbe64Elf, ExpectedOutputPath: expectedPath);
            var workload = new Rv64ElfWorkload(bench.ElfPath);

            BenchmarkResult result = Experiment.RunBenchmark(
                bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
            );

            Assert.True(result.Checked);
            Assert.False(result.Passed);
        }
        finally { File.Delete(expectedPath); }
    }

    [Fact]
    public void RunBenchmark_StdinPath_IsRedirectedToGuest() {
        string stdinPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(stdinPath, "ping");
        try {
            var bench = new BenchmarkConfig("echo", StdinEcho64Elf, StdinPath: stdinPath);
            var workload = new Rv64ElfWorkload(bench.ElfPath);

            BenchmarkResult result = Experiment.RunBenchmark(
                bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
            );

            Assert.Equal("ping", result.Output);
        }
        finally { File.Delete(stdinPath); }
    }
}
