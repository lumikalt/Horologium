using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using Xunit.Abstractions;

namespace Tests.RiscV32;

/// <summary>
/// Runs the riscv-tests benchmark suite under all three pipeline configurations.
/// <para>
/// Benchmarks use HTIF exit: tohost_exit(code) writes (code&lt;&lt;1)|1 to the
/// 'tohost' symbol and then spins forever (infinite self-loop).  The simulator's
/// existing halt detection catches the self-loop; the test then reads the
/// 32-bit low word of 'tohost' from memory.
/// </para>
/// <para>
/// Exit code 0  → tohost low word == 1  → PASS
/// Exit code N≠0 → tohost low word == (N&lt;&lt;1)|1 → FAIL
/// tohost == 0   → simulation timed out before halting
/// </para>
/// <para>
/// Benchmarks also print performance counters via HTIF printstr (one char per
/// HTIF write to tohost), polling fromhost (tohost+8) for acknowledgement.
/// HtifMemory provides the minimal auto-ACK, so printstr returns instead of
/// spinning forever, allowing the benchmark to reach tohost_exit normally.
/// </para>
/// </summary>
public class BenchmarkTests(ITestOutputHelper output) {
    private static readonly string BenchmarksDir =
        Path.Combine(AppContext.BaseDirectory, "benchmarks");

    private const int MemoryBytes = 4 * 1024 * 1024; // 4 MB: code + data + 128 KB stack

    // ── Test data ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllBenchmarks() =>
        Directory
           .EnumerateFiles(BenchmarkTests.BenchmarksDir, "*.elf")
           .OrderBy(p => p)
           .Select(p => new object[] { Path.GetFileNameWithoutExtension(p), });

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

    // ── SingleCycleTrain ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllBenchmarks))]
    public void SingleCycle_Passes(string name) {
        (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
        var train = new SingleCycleTrain(new Rv32Mechanism(), htifMem, entry);
        train.Run(10_000_000);
        AssertPass(ReadTohostLow(mem, tohost), name);
    }

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllBenchmarks))]
    public void FiveStage_Passes(string name) {
        (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
        var train = new FiveStageTrain(new Rv32Mechanism(), htifMem, entry);
        train.Run(20_000_000);
        AssertPass(ReadTohostLow(mem, tohost), name);
    }

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllBenchmarks))]
    public void OoOE_Passes(string name) {
        (FlatMemory mem, IMemory htifMem, ulong entry, ulong tohost) = Load(name);
        var train = new OooeTrain(new Rv32Mechanism(), htifMem, entry);
        train.Run(20_000_000);
        AssertPass(ReadTohostLow(mem, tohost), name);
    }

    // ── Performance summary ───────────────────────────────────────────────────
    //
    // Reports IPC across the benchmark suite on the OoOE pipeline.
    // Not an assertion test — always passes if every benchmark halts.
    // Run with: dotnet test --filter "Name=BenchmarkPerfSummary"

    [Fact]
    public void BenchmarkPerfSummary() {
        var rows = new List<(string Name, long Cycles, long Retired, double Ipc)>();

        foreach (object[] row in AllBenchmarks()) {
            var name = (string)row[0];
            (FlatMemory _, IMemory htifMem, ulong entry, ulong _) = Load(name);
            var train = new OooeTrain(new Rv32Mechanism(), htifMem, entry);
            RevolutionResult result = train.Run(20_000_000);

            DialBoardSnapshot? snap = result.Find("ooo.pipeline");
            long cycles = snap?.Counters.GetValueOrDefault("cycles") ?? 0;
            long retired = snap?.Counters.GetValueOrDefault("retired") ?? 0;
            double ipc = cycles > 0 ? retired / (double)cycles : 0.0;
            rows.Add((name, cycles, retired, ipc));
        }

        output.WriteLine("| Benchmark | Cycles | Retired | IPC |");
        output.WriteLine("|-----------|-------:|--------:|----:|");
        foreach ((string n, long c, long r, double ipc) in rows.OrderBy(x => x.Name))
            output.WriteLine($"| {n} | {c} | {r} | {ipc:F3} |");

        Assert.NotEmpty(rows);
    }
}