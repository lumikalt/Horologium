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
    private static string MmapProbe64Elf => Path.Combine(AppContext.BaseDirectory, "mmap_probe64.elf");

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
    public void RunBenchmark_TrailingNewlineMismatch_FailsByDefault() {
        // Byte-exact is still the default: a reference file with the trailing newline convention
        // most such files use (but that the guest's own output doesn't emit) must fail unless
        // NormalizeTrailingWhitespace opts into trimming both sides first (see the sibling test).
        string expectedPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(expectedPath, "abi_probe64.elf\n");
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
    public void RunBenchmark_NormalizeTrailingWhitespace_IgnoresTrailingNewlineMismatch() {
        string expectedPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(expectedPath, "abi_probe64.elf\n");
        try {
            var bench = new BenchmarkConfig(
                "probe", AbiProbe64Elf, ExpectedOutputPath: expectedPath, NormalizeTrailingWhitespace: true
            );
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
    public void RunBenchmark_NormalizeTrailingWhitespace_StillFailsOnRealContentMismatch() {
        // The flag only trims trailing whitespace runs, not internal content — must not mask an
        // actual output difference just because it also happens to add a trailing newline.
        string expectedPath = Path.Combine(Path.GetTempPath(), $"horologium_bench_{Guid.NewGuid():N}.txt");
        File.WriteAllText(expectedPath, "not the right output\n");
        try {
            var bench = new BenchmarkConfig(
                "probe", AbiProbe64Elf, ExpectedOutputPath: expectedPath, NormalizeTrailingWhitespace: true
            );
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

    [Fact]
    public void RunBenchmark_MmapArenaEnabled_MmapSucceedsAndMappedMemoryIsReadWrite() {
        var bench = new BenchmarkConfig("mmap", MmapProbe64Elf, MmapArenaBytes: 3 * 4096);
        var workload = new Rv64ElfWorkload(bench.ElfPath);

        BenchmarkResult result = Experiment.RunBenchmark(
            bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
        );

        Assert.True(result.Halted);
        Assert.Equal("*", result.Output); // the byte the probe wrote into (then read back from) the mapped page
    }

    [Fact]
    public void RunBenchmark_MmapArenaEnabled_DoesNotDisturbArgvOrStackPlacement() {
        // The arena is appended past the workload's own memory (see RunBenchmark's doc comment) —
        // enabling it must not shift where the stack/argv end up. Reuses the argv probe with
        // MmapArenaBytes set, rather than a fresh assertion, so a regression that moved the stack
        // into (or the arena underneath) the existing layout shows up as a wrong argv[0] readback.
        var bench = new BenchmarkConfig("probe", AbiProbe64Elf, ["hello"], MmapArenaBytes: 3 * 4096);
        var workload = new Rv64ElfWorkload(bench.ElfPath);

        BenchmarkResult result = Experiment.RunBenchmark(
            bench, workload, wordSize: 8, handler => new Rv64Mechanism(syscallHandler: handler)
        );

        Assert.True(result.Halted);
        Assert.Equal("abi_probe64.elf", result.Output);
    }
}
