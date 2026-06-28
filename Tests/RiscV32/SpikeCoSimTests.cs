using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.CoSim;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Lock-step co-simulation tests against Spike.
/// These tests require Spike and dtc to be available. In the Nix dev-shell
/// both are provided; outside the shell run <c>direnv reload</c> first.
/// To skip them in CI without Spike: <c>--filter "FullyQualifiedName!~SpikeCoSim"</c>.
///
/// Each test runs one of the three trains against an ELF with Spike attached
/// as a live <see cref="ICommitObserver"/>; a <see cref="CoSimDivergenceException"/>
/// is thrown at the first commit that disagrees with Spike. <c>test.elf</c> is
/// the simple RV32I golden path; <c>rich.elf</c> (RV32IM) adds multiply/divide,
/// an insertion sort, and heavy data-dependent branching to exercise the
/// multi-cycle functional units, store-to-load forwarding, and flush paths.
/// </summary>
public class SpikeCoSimTests {
    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    // Runs the ELF through the train produced by trainFactory with Spike attached.
    // Spike runs as a live child process; dispose kills it when the run ends.
    private static void RunCoSim(
        string elfName,
        Func<IMechanism, IMemory, ulong, ICommitObserver, object> trainFactory
    ) {
        string elfPath = ElfPath(elfName);
        var workload = new Rv32ElfWorkload(elfPath);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(elfPath, workload.BaseAddress, workload.MemorySize);

        // CoSimDivergenceException is thrown on the first mismatch during Run().
        switch (trainFactory(new Rv32Mechanism(), mem, workload.EntryPoint, cosim)) {
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

    [Fact]
    public void SingleCycle_TestElf_MatchesSpike() => RunCoSim("test.elf", SingleCycle);

    [Fact]
    public void FiveStage_TestElf_MatchesSpike() => RunCoSim("test.elf", FiveStage);

    [Fact]
    public void Oooe_TestElf_MatchesSpike() => RunCoSim("test.elf", Oooe);

    // ── rich.elf: RV32IM mul/div + sort + heavy branching ───────────────────────

    [Fact]
    public void SingleCycle_RichElf_MatchesSpike() => RunCoSim("rich.elf", SingleCycle);

    [Fact]
    public void FiveStage_RichElf_MatchesSpike() => RunCoSim("rich.elf", FiveStage);

    [Fact]
    public void Oooe_RichElf_MatchesSpike() => RunCoSim("rich.elf", Oooe);
}
