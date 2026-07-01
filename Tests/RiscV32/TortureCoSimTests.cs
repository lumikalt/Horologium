using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.CoSim;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Runs the pre-generated RV32 torture tests under Spike lock-step co-simulation.
/// <para>
/// Torture tests are random RV32IMAF instruction sequences produced by
/// <c>TestBinaries/gen_torture.py</c> (seed 42, 20 tests × 300 instructions).
/// The compiled ELFs live under <c>TestBinaries/torture/</c> and are committed
/// alongside their <c>.S</c> sources; rebuild with <c>make torture</c> in that
/// directory.
/// </para>
/// <para>
/// Spike is the correctness oracle — the test compares every committed
/// instruction (PC, encoding, register write) against Spike in lock step.
/// A <see cref="CoSimDivergenceException"/> is thrown at the first divergence.
/// </para>
/// <para>
/// These tests require <c>spike</c> and <c>dtc</c> on PATH (both provided by
/// the Nix dev-shell). Without them the tests skip gracefully; set
/// <c>HOROLOGIUM_REQUIRE_COSIM=1</c> to turn a missing toolchain into a hard
/// failure for CI.
/// </para>
/// </summary>
public class TortureCoSimTests {
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_COSIM";

    private static readonly string TortureDir =
        Path.Combine(AppContext.BaseDirectory, "torture");

    // ── Test data ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> TortureElfs() =>
        Directory.EnumerateFiles(TortureCoSimTests.TortureDir, "torture_*.elf")
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("torture", Path.GetFileName(p)), });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RequireSpikeOrSkip() {
        if (SpikeCoSimReference.IsAvailable()) return;

        bool required = Environment.GetEnvironmentVariable(TortureCoSimTests.RequireEnvVar) is "1" or "true";
        if (required)
            throw new InvalidOperationException(
                $"{TortureCoSimTests.RequireEnvVar} is set but the Spike co-sim toolchain " +
                "(spike + dtc) was not found. Run inside the Nix dev-shell or unset the variable."
            );

        Skip.If(
            true, "spike/dtc not available — skipping torture co-sim. Set HOROLOGIUM_REQUIRE_COSIM=1 to require it."
        );
    }

    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static void RunCoSim(
        string elfName,
        Func<Rv32Mechanism, FlatMemory, ulong, ICommitObserver, object> factory
    ) {
        RequireSpikeOrSkip();

        string elfPath = ElfPath(elfName);
        var workload   = new Rv32ElfWorkload(elfPath);
        var mem        = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(
            elfPath,
            workload.BaseAddress,
            workload.MemorySize,
            isa: "rv32imafc"
        );

        switch (factory(new Rv32Mechanism(), mem, workload.EntryPoint, cosim)) {
            case SingleCycleTrain t: t.Run(); break;
            case FiveStageTrain   t: t.Run(); break;
            case OooeTrain        t: t.Run(); break;
            default: throw new InvalidOperationException("unknown train");
        }
    }

    private static SingleCycleTrain SingleCycle(Rv32Mechanism m, FlatMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static FiveStageTrain FiveStage(Rv32Mechanism m, FlatMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static OooeTrain Oooe(Rv32Mechanism m, FlatMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableTheory]
    [MemberData(nameof(TortureElfs))]
    public void SingleCycle_TortureElf_MatchesSpike(string elf) => RunCoSim(elf, SingleCycle);

    [SkippableTheory]
    [MemberData(nameof(TortureElfs))]
    public void FiveStage_TortureElf_MatchesSpike(string elf) => RunCoSim(elf, FiveStage);

    [SkippableTheory]
    [MemberData(nameof(TortureElfs))]
    public void OoOE_TortureElf_MatchesSpike(string elf) => RunCoSim(elf, Oooe);
}