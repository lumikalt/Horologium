#region

using Mechanism;
using Pipeline;
using RiscV32.Memory;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.Isa;

/// <summary>
///     Runs the official RISC-V ISA test suite (rv64ui-p-*, rv64um-p-*, rv64ua-p-*,
///     rv64uc-p-*, rv64uf-p-*, rv64ud-p-*, rv64uzfh-p-*, rv64uzb*-p-*, rv64si-p-*)
///     under all three pipeline configurations.
///     <para>
///         Each ELF uses the same custom test environment as the RV32 suite
///         (TestBinaries/env/riscv_test.h) that halts with EBREAK and leaves the
///         result in gp (x3):
///         gp == 1            → PASS
///         gp == (N&lt;&lt;1) | 1  → FAIL at subtest N
///     </para>
/// </summary>
public class RiscVTestSuiteTests {
    private static readonly string IsaDir =
        Path.Combine(AppContext.BaseDirectory, "isa");

    private static readonly string[] Prefixes = [
        "rv64ui-", "rv64um-", "rv64ua-", "rv64uc-",
        "rv64uf-", "rv64ud-", "rv64uzfh-",
        "rv64uzba-", "rv64uzbb-", "rv64uzbc-", "rv64uzbs-",
        "rv64si-",
    ];

    // ── Test data ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllTests() =>
        Directory
           .EnumerateFiles(RiscVTestSuiteTests.IsaDir, "*.elf")
           .Where(p => {
                    string name = Path.GetFileName(p);
                    return RiscVTestSuiteTests.Prefixes.Any(name.StartsWith);
                }
            )
           .OrderBy(p => p)
           .Select(p => new object[] { Path.GetFileNameWithoutExtension(p), });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ElfPath(string name) => Path.Combine(RiscVTestSuiteTests.IsaDir, name + ".elf");

    private static ulong Gp(IArchState state) =>
        state.IntegerRegisters.Read(3); // x3 = gp

    private static void AssertPass(ulong gp, string name) {
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
        var wl = new Rv64ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new SingleCycleTrain(
            new Rv64Mechanism(ebreakAlwaysHalts: true, wfiNeverHalts: true), mem, wl.EntryPoint
        );
        train.Run(200_000);

        AssertPass(Gp(train.ArchState), name);
    }

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTests))]
    public void FiveStage_Passes(string name) {
        var wl = new Rv64ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new FiveStageTrain(
            new Rv64Mechanism(ebreakAlwaysHalts: true, wfiNeverHalts: true), mem, wl.EntryPoint
        );
        train.Run(400_000);

        AssertPass(Gp(train.ArchState), name);
    }

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTests))]
    public void OoOE_Passes(string name) {
        var wl = new Rv64ElfWorkload(ElfPath(name));
        var mem = new FlatMemory(wl.MemorySize, wl.BaseAddress);
        wl.Load(mem);

        var train = new OooeTrain(
            new Rv64Mechanism(ebreakAlwaysHalts: true, wfiNeverHalts: true), mem, wl.EntryPoint
        );
        train.Run(400_000);

        AssertPass(Gp(train.ArchState), name);
    }
}