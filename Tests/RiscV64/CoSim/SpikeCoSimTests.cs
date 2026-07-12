using Mechanism;
using Pipeline;
using RiscV32.CoSim;
using RiscV32.Memory;
using RiscV64;
using RiscV64.Memory;

namespace Tests.RiscV64.CoSim;

/// <summary>
/// RV64 counterpart of <see cref="Tests.RiscV32.CoSim.SpikeCoSimTests"/> — same
/// lock-step co-simulation contract against Spike, retargeted to
/// <see cref="Rv64Mechanism"/>/<see cref="Rv64ElfWorkload"/> and the
/// <c>rv64imafdc</c> ISA string (IMAFDC — no V; RV64+V co-sim parity is out of
/// scope here, see TODO.md).
/// <para>
/// The cheap-tier fixtures are ported: test64.elf/rich64.elf/htif64.elf, the
/// official riscv-tests rv64u* conformance ELFs (already built by
/// <c>TestBinaries/Makefile</c>), and RV64 torture co-simulation (see
/// <see cref="TortureCoSimTests"/>). The zcmop/zimop/zabha/zawrs/cbo/vector
/// custom-workload theories are RV32-only — no RV64 C sources exist for them.
/// </para>
/// </summary>
public class SpikeCoSimTests {
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_COSIM";

    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static void RequireSpikeOrSkip() {
        if (SpikeCoSimReference.IsAvailable()) return;

        bool required = Environment.GetEnvironmentVariable(SpikeCoSimTests.RequireEnvVar) is "1" or "true";
        if (required)
            throw new InvalidOperationException(
                $"{SpikeCoSimTests.RequireEnvVar} is set but the Spike co-sim toolchain (spike + dtc) was not found. " +
                "Run inside the Nix dev-shell, which provides both, or unset the variable to allow skipping."
            );

        Skip.If(true, "spike/dtc not available — skipping co-sim. Set HOROLOGIUM_REQUIRE_COSIM=1 to require it.");
    }

    private static void RunCoSim(
        string elfName,
        Func<IMechanism, IMemory, ulong, ICommitObserver, object> trainFactory,
        int? memorySize = null,
        string isa = "rv64imafdc"
    ) {
        RequireSpikeOrSkip();

        string elfPath = ElfPath(elfName);
        var workload = new Rv64ElfWorkload(elfPath, memorySize);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(elfPath, workload.BaseAddress, workload.MemorySize, isa);

        ulong? tohost = workload.TryFindSymbol("tohost", out ulong tohostAddr) ? tohostAddr : null;

        switch (trainFactory(new Rv64Mechanism(tohost), mem, workload.EntryPoint, cosim)) {
            case SingleCycleTrain t: t.Run(); break;
            case FiveStageTrain t:   t.Run(); break;
            case OooeTrain t:        t.Run(); break;
            default:                 throw new InvalidOperationException("unknown train");
        }
    }

    private static SingleCycleTrain SingleCycle(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static FiveStageTrain FiveStage(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static OooeTrain Oooe(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    // ── test64.elf: simple RV64I golden path ────────────────────────────────────

    [SkippableFact]
    public void SingleCycle_TestElf_MatchesSpike() => RunCoSim("test64.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_TestElf_MatchesSpike() => RunCoSim("test64.elf", FiveStage);

    [SkippableFact]
    public void Oooe_TestElf_MatchesSpike() => RunCoSim("test64.elf", Oooe);

    // ── rich64.elf: RV64IM mul/div + sort + heavy branching ─────────────────────

    [SkippableFact]
    public void SingleCycle_RichElf_MatchesSpike() => RunCoSim("rich64.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_RichElf_MatchesSpike() => RunCoSim("rich64.elf", FiveStage);

    [SkippableFact]
    public void Oooe_RichElf_MatchesSpike() => RunCoSim("rich64.elf", Oooe);

    // ── htif64.elf: RV64IM workload that exits via the HTIF tohost register ──────

    private const int HtifMemoryBytes = 0x100000;

    [SkippableFact]
    public void SingleCycle_HtifElf_MatchesSpike() =>
        RunCoSim("htif64.elf", SingleCycle, SpikeCoSimTests.HtifMemoryBytes);

    [SkippableFact]
    public void FiveStage_HtifElf_MatchesSpike() =>
        RunCoSim("htif64.elf", FiveStage, SpikeCoSimTests.HtifMemoryBytes);

    [SkippableFact]
    public void Oooe_HtifElf_MatchesSpike() => RunCoSim("htif64.elf", Oooe, SpikeCoSimTests.HtifMemoryBytes);

    // ── Official riscv-tests conformance suite ────────────────────────────────
    //
    // rv64ui / rv64um / rv64ua / rv64uc / rv64uf / rv64ud — all covered by
    // rv64imafdc. rv64si (supervisor) and ma_data are excluded for the same
    // reasons as the RV32 suite. rv64uzb*/rv64uzfh need extended ISA strings —
    // handled by separate theories below. There is no rv64uzicond ELF built.
    //
    // Two further exclusions, neither an RV32 issue:
    //
    //   - rv64ud-p-ldst / rv64ui-p-st_ld: not a Horologium bug — confirmed by running
    //     Horologium standalone (no Spike) against both, which halts cleanly with
    //     gp=1 (RVTEST_PASS) and no CSR trap (mcause/mepc/mtval all zero) in under
    //     700 retired instructions. The pinned `spike-1.1.0-unstable-2024-09-21`
    //     binary itself hangs (confirmed with a bare `spike --isa=rv64imafdc ...`
    //     invocation, no Horologium or --log-commits involved) partway through both
    //     — rv64ud-p-ldst locks up immediately after its first `fld` (RV64 D-extension
    //     load), rv64ui-p-st_ld partway through its sd/ld stress cases. The sibling
    //     rv64uf-p-ldst (single-precision) and rv64uzfh-p-ldst (half-precision) ELFs
    //     do not hit this and pass normally, so it looks narrow to 64-bit float
    //     loads/some sd/ld sequences in this Spike build, not a general RV64 issue.

    private static readonly string IsaDir = Path.Combine(AppContext.BaseDirectory, "isa");

    public static IEnumerable<object[]> ConformanceElfs() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv64u*.elf")
                 .Where(p => !p.Contains("ma_data")
                          && !Path.GetFileName(p).StartsWith("rv64uzb")
                          && !Path.GetFileName(p).StartsWith("rv64uzfh")
                          && !Path.GetFileName(p).Equals("rv64ud-p-ldst.elf")
                          && !Path.GetFileName(p).Equals("rv64ui-p-st_ld.elf")
                  )
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfs))]
    public void SingleCycle_Conformance_MatchesSpike(string elf) => RunCoSim(elf, SingleCycle);

    // rv64uzba / rv64uzbb / rv64uzbc / rv64uzbs — need Zba/Zbb/Zbc/Zbs in ISA string.
    public static IEnumerable<object[]> ConformanceElfsZb() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv64uzb*.elf")
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfsZb))]
    public void SingleCycle_ConformanceZb_MatchesSpike(string elf) =>
        RunCoSim(elf, SingleCycle, isa: "rv64imafdc_zba_zbb_zbc_zbs");

    // rv64uzfh — needs Zfh in ISA string.
    public static IEnumerable<object[]> ConformanceElfsZfh() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv64uzfh-p-*.elf")
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfsZfh))]
    public void SingleCycle_ConformanceZfh_MatchesSpike(string elf) =>
        RunCoSim(elf, SingleCycle, isa: "rv64imafdc_zfh");
}