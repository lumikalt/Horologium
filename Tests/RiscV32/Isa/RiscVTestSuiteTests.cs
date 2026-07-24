#region

using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using Xunit.Abstractions;

#endregion

namespace Tests.RiscV32.Isa;

/// <summary>
///     Runs the official RISC-V ISA test suite (rv32ui-p-* and rv32um-p-*)
///     under all three pipeline configurations.
///     <para>
///         Each ELF uses a custom test environment (TestBinaries/env/riscv_test.h)
///         that halts with EBREAK and leaves the result in gp (x3):
///         gp == 1            → PASS
///         gp == (N&lt;&lt;1) | 1  → FAIL at subtest N
///     </para>
/// </summary>
public class RiscVTestSuiteTests(ITestOutputHelper testOutputHelper) {
    private static readonly string IsaDir =
        Path.Combine(AppContext.BaseDirectory, "isa");

    // ── Test data ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllTests() =>
        Directory
           .EnumerateFiles(RiscVTestSuiteTests.IsaDir, "*.elf")
           .Where(p => {
                    string name = Path.GetFileName(p);
                    return name.StartsWith("rv32ui-") || name.StartsWith("rv32um-");
                }
            )
           .OrderBy(p => p)
           .Select(p => new object[] { Path.GetFileNameWithoutExtension(p), });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ElfPath(string name) => Path.Combine(RiscVTestSuiteTests.IsaDir, name + ".elf");

    private static uint Gp(IArchState state) =>
        (uint)state.IntegerRegisters.Read(3); // x3 = gp

    private static void AssertPass(uint gp, string name) {
        switch (gp) {
            case 1: return;
            case 0: throw new Exception($"{name}: simulation hit maxTicks before halting (gp still 0)");
            default: {
                var failedTest = (int)(gp >> 1);
                throw new Exception($"{name}: FAIL at sub-test {failedTest} (gp=0x{gp:X})");
            }
        }
    }

    // ── SingleCycleTrain ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTests))]
    public void SingleCycle_Passes(string name) {
        var wl = new Rv32ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, wl.EntryPoint);
        train.Run(200_000);

        AssertPass(Gp(train.ArchState), name);
    }

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTests))]
    public void FiveStage_Passes(string name) {
        var wl = new Rv32ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new FiveStageTrain(new Rv32Mechanism(), mem, wl.EntryPoint);
        train.Run(400_000);

        AssertPass(Gp(train.ArchState), name);
    }

    // ── OooTrain ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTests))]
    public void OoOE_Passes(string name) {
        var wl = new Rv32ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new OooTrain(new Rv32Mechanism(), mem, wl.EntryPoint);
        train.Run(400_000);

        AssertPass(Gp(train.ArchState), name);
    }

    // ── Performance summary (single-cycle baseline) ───────────────────────────
    //
    // Not an assertion test — prints an IPC table across the suite.
    // Run with: dotnet test --filter "Name=PerfSummary_AllTests"

    [Fact]
    public void PerfSummary_AllTests() {
        var rows = new List<(string Name, long Cycles, long Retired, double Ipc)>();

        foreach (object[] row in AllTests()) {
            var name = (string)row[0];
            var wl = new Rv32ElfWorkload(ElfPath(name));
            var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
            wl.Load(mem);

            var train = new OooTrain(new Rv32Mechanism(), mem, wl.EntryPoint);
            RevolutionResult result = train.Run(200_000);

            DialBoardSnapshot? snap = result.Find("ooo.pipeline");
            long cycles = snap?.Counters.GetValueOrDefault("cycles") ?? 0;
            long retired = snap?.Counters.GetValueOrDefault("retired") ?? 0;
            double ipc = cycles > 0 ? retired / (double)cycles : 0.0;
            rows.Add((name, cycles, retired, ipc));
        }

        // Emit a simple Markdown table to test output.
        testOutputHelper.WriteLine("| Test | Cycles | Retired | IPC |");
        testOutputHelper.WriteLine("|------|-------:|--------:|----:|");
        foreach ((string n, long c, long r, double ipc) in rows.OrderBy(x => x.Name))
            testOutputHelper.WriteLine($"| {n} | {c} | {r} | {ipc:F3} |");

        // The test itself always passes — it's a reporting fixture.
        Assert.NotEmpty(rows);
    }
}