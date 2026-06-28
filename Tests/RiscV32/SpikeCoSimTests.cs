using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.CoSim;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Lock-step co-simulation tests against Spike — the project's correctness
/// contract for the ISA datapath (see the "Co-simulation contract" section of
/// the README).
///
/// These tests require the <c>spike</c> simulator and the <c>dtc</c> device-tree
/// compiler. The Nix dev-shell provides both; outside the shell run
/// <c>direnv reload</c> first. When the toolchain is absent the tests
/// <em>skip</em> (via <see cref="SkippableFactAttribute"/>) rather than fail —
/// unless <c>HOROLOGIUM_REQUIRE_COSIM</c> is set, in which case a missing
/// toolchain is a hard failure so a CI job that enforces the contract cannot
/// silently pass with the checks skipped.
///
/// Each test runs one of the three trains against an ELF with Spike attached
/// as a live <see cref="ICommitObserver"/>; a <see cref="CoSimDivergenceException"/>
/// is thrown at the first commit that disagrees with Spike. <c>test.elf</c> is
/// the simple RV32I golden path; <c>rich.elf</c> (RV32IM) adds multiply/divide,
/// an insertion sort, and heavy data-dependent branching to exercise the
/// multi-cycle functional units, store-to-load forwarding, and flush paths.
/// </summary>
public class SpikeCoSimTests {
    /// <summary>Set to 1/true to turn a missing Spike toolchain into a hard failure.</summary>
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_COSIM";

    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    // Skips the test when the Spike toolchain is unavailable, unless the run is
    // configured to require co-sim (HOROLOGIUM_REQUIRE_COSIM) — then it fails
    // loudly, so an enforcing CI cannot pass with the contract silently skipped.
    private static void RequireSpikeOrSkip() {
        if (SpikeCoSimReference.IsAvailable()) return;

        bool required = Environment.GetEnvironmentVariable(RequireEnvVar) is "1" or "true";
        if (required)
            throw new InvalidOperationException(
                $"{RequireEnvVar} is set but the Spike co-sim toolchain (spike + dtc) was not found. " +
                "Run inside the Nix dev-shell, which provides both, or unset the variable to allow skipping."
            );

        Skip.If(true, "spike/dtc not available — skipping co-sim. Set HOROLOGIUM_REQUIRE_COSIM=1 to require it.");
    }

    // Runs the ELF through the train produced by trainFactory with Spike attached.
    // Spike runs as a live child process; dispose kills it when the run ends.
    // memorySize overrides the ELF-derived sizing (the HTIF fixture reserves a
    // stack beyond its tiny load extent, so it needs an explicit region size).
    private static void RunCoSim(
        string elfName,
        Func<IMechanism, IMemory, ulong, ICommitObserver, object> trainFactory,
        int? memorySize = null
    ) {
        RequireSpikeOrSkip();

        string elfPath = ElfPath(elfName);
        var workload = new Rv32ElfWorkload(elfPath, memorySize);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(elfPath, workload.BaseAddress, workload.MemorySize);

        // When the ELF exits via HTIF, give the mechanism the tohost address so
        // the exit store terminates the run at the write itself (first-class
        // RequestHalt). ELFs without tohost (EBREAK-terminated) get null.
        ulong? tohost = workload.TryFindSymbol("tohost", out ulong tohostAddr) ? tohostAddr : null;

        // CoSimDivergenceException is thrown on the first mismatch during Run().
        switch (trainFactory(new Rv32Mechanism(tohost), mem, workload.EntryPoint, cosim)) {
            case SingleCycleTrain t: t.Run(); break;
            case FiveStageTrain t: t.Run(); break;
            case OooeTrain t: t.Run(); break;
            default: throw new InvalidOperationException("unknown train");
        }
    }

    private static SingleCycleTrain SingleCycle(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static FiveStageTrain FiveStage(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static OooeTrain Oooe(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    // ── test.elf: simple RV32I golden path ──────────────────────────────────────

    [SkippableFact]
    public void SingleCycle_TestElf_MatchesSpike() => RunCoSim("test.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_TestElf_MatchesSpike() => RunCoSim("test.elf", FiveStage);

    [SkippableFact]
    public void Oooe_TestElf_MatchesSpike() => RunCoSim("test.elf", Oooe);

    // ── rich.elf: RV32IM mul/div + sort + heavy branching ───────────────────────

    [SkippableFact]
    public void SingleCycle_RichElf_MatchesSpike() => RunCoSim("rich.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_RichElf_MatchesSpike() => RunCoSim("rich.elf", FiveStage);

    [SkippableFact]
    public void Oooe_RichElf_MatchesSpike() => RunCoSim("rich.elf", Oooe);

    // ── htif.elf: RV32IM workload that exits via the HTIF tohost register ────────
    //
    // Spike exits cleanly (no EBREAK debug-stub hang) and logs the post-exit
    // self-loop an indeterminate number of times; each train commits the exit
    // store and a single self-loop jump, then halts, so its stream is a clean
    // prefix of Spike's. A 1 MB region covers the fixture's reserved stack.

    private const int HtifMemoryBytes = 0x100000;

    [SkippableFact]
    public void SingleCycle_HtifElf_MatchesSpike() => RunCoSim("htif.elf", SingleCycle, HtifMemoryBytes);

    [SkippableFact]
    public void FiveStage_HtifElf_MatchesSpike() => RunCoSim("htif.elf", FiveStage, HtifMemoryBytes);

    [SkippableFact]
    public void Oooe_HtifElf_MatchesSpike() => RunCoSim("htif.elf", Oooe, HtifMemoryBytes);
}
