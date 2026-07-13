using System.Collections.Concurrent;
using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using Xunit.Abstractions;

namespace Tests.RiscV32.Analysis;

/// <summary>
///     Runs the riscv-tests benchmark suite under all three pipeline configurations.
///     <para>
///         Benchmarks use HTIF exit: tohost_exit(code) writes (code&lt;&lt;1)|1 to the
///         'tohost' symbol and then spins forever (infinite self-loop).  The simulator's
///         existing halt detection catches the self-loop; the test then reads the
///         32-bit low word of 'tohost' from memory.
///     </para>
///     <para>
///         Exit code 0  → tohost low word == 1  → PASS
///         Exit code N≠0 → tohost low word == (N&lt;&lt;1)|1 → FAIL
///         tohost == 0   → simulation timed out before halting
///     </para>
///     <para>
///         Benchmarks also print performance counters via HTIF printstr (one char per
///         HTIF write to tohost), polling fromhost (tohost+8) for acknowledgement.
///         HtifMemory provides the minimal auto-ACK, so printstr returns instead of
///         spinning forever, allowing the benchmark to reach tohost_exit normally.
///     </para>
///     <para>
///         xUnit serializes every test case within a class onto one thread (test
///         collections, not test methods, are the parallelism unit), so a plain
///         <c>[Theory]</c> per ELF would run all binaries one after another. Each
///         benchmark run builds its own memory image and train with no shared state,
///         so <see cref="RunAllBenchmarks" /> fans them out itself via <c>Parallel.ForEach</c>
///         instead of relying on xUnit's collection-level parallelism.
///     </para>
/// </summary>
public class BenchmarkTests(ITestOutputHelper output) {
    private const int MemoryBytes = 4 * 1024 * 1024; // 4 MB: code + data + 128 KB stack

    private static readonly string BenchmarksDir =
        Path.Combine(AppContext.BaseDirectory, "benchmarks");

    // ── Test data ─────────────────────────────────────────────────────────────

    private static IEnumerable<string> AllBenchmarkNames() =>
        Directory
           .EnumerateFiles(BenchmarkTests.BenchmarksDir, "*.elf")
           .OrderBy(p => p)
           .Select(p => Path.GetFileNameWithoutExtension(p));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ElfPath(string name) =>
        Path.Combine(BenchmarkTests.BenchmarksDir, name + ".elf");

    private static (FlatMemory Mem, IMemory HtifMem, ulong EntryPoint, ulong TohostAddr) Load(string name) {
        var wl = new Rv32ElfWorkload(ElfPath(name), BenchmarkTests.MemoryBytes);
        var mem = new FlatMemory(BenchmarkTests.MemoryBytes, wl.BaseAddress);
        wl.Load(mem);
        ulong tohost = wl.FindSymbol("tohost");
        return (mem, new HtifMemory(mem, tohost), wl.EntryPoint, tohost);
    }

    private static ulong ReadTohostLow(FlatMemory mem, ulong addr) =>
        mem.Read(addr, 4);

    private static void AssertPass(ulong tohostLow, string name) {
        switch (tohostLow) {
            case 1:  return; // exit(0) → PASS
            case 0:  throw new Exception($"{name}: simulation hit maxTicks before halting");
            default: throw new Exception($"{name}: FAIL (exit code {tohostLow >> 1}, tohost=0x{tohostLow:X})");
        }
    }

    /// <summary>
    ///     Runs <paramref name="runOne" /> for every benchmark ELF in parallel and
    ///     aggregates failures into a single assertion.
    /// </summary>
    private static void RunAllBenchmarks(Action<string> runOne) {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(
            AllBenchmarkNames(), name => {
                try { runOne(name); }
                catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
            }
        );
        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(f => f)));
    }

    // ── SingleCycleTrain ──────────────────────────────────────────────────────

    [Fact]
    public void SingleCycle_Passes() =>
        RunAllBenchmarks(name => {
                (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
                var train = new SingleCycleTrain(new Rv32Mechanism(), htifMem, entry);
                train.Run(10_000_000);
                AssertPass(ReadTohostLow(mem, tohost), name);
            }
        );

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Fact]
    public void FiveStage_Passes() =>
        RunAllBenchmarks(name => {
                (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
                var train = new FiveStageTrain(new Rv32Mechanism(), htifMem, entry);
                train.Run(20_000_000);
                AssertPass(ReadTohostLow(mem, tohost), name);
            }
        );

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Fact]
    public void OoOE_Passes() =>
        RunAllBenchmarks(name => {
                (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
                var train = new OooeTrain(new Rv32Mechanism(), htifMem, entry);
                train.Run(20_000_000);
                AssertPass(ReadTohostLow(mem, tohost), name);
            }
        );

    // ── Performance summary ───────────────────────────────────────────────────
    //
    // Reports IPC across the benchmark suite on the OoOE pipeline.
    // Not an assertion test — always passes if every benchmark halts.
    // Run with: dotnet test --filter "Name=BenchmarkPerfSummary"

    [Fact]
    public void BenchmarkPerfSummary() {
        var rows = new ConcurrentBag<(string Name, long Cycles, long Retired, double Ipc)>();

        Parallel.ForEach(
            AllBenchmarkNames(), name => {
                (FlatMemory _, IMemory htifMem, ulong entry, ulong _) = Load(name);
                var train = new OooeTrain(new Rv32Mechanism(), htifMem, entry);
                RevolutionResult result = train.Run(20_000_000);

                DialBoardSnapshot? snap = result.Find("ooo.pipeline");
                long cycles = snap?.Counters.GetValueOrDefault("cycles") ?? 0;
                long retired = snap?.Counters.GetValueOrDefault("retired") ?? 0;
                double ipc = cycles > 0 ? retired / (double)cycles : 0.0;
                rows.Add((name, cycles, retired, ipc));
            }
        );

        output.WriteLine("| Benchmark | Cycles | Retired | IPC |");
        output.WriteLine("|-----------|-------:|--------:|----:|");
        foreach ((string n, long c, long r, double ipc) in rows.OrderBy(x => x.Name))
            output.WriteLine($"| {n} | {c} | {r} | {ipc:F3} |");

        Assert.NotEmpty(rows);
    }
}