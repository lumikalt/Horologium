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
/// </summary>
public class SpikeCoSimTests {
    private static string TestElfPath =>
        Path.Combine(AppContext.BaseDirectory, "test.elf");

    /// <summary>
    /// The simplest golden-path check: SingleCycleTrain running test.elf must match
    /// Spike commit-for-commit in PC order and integer register writes.
    /// </summary>
    [Fact]
    public void SingleCycle_TestElf_MatchesSpike() {
        var workload = new Rv32ElfWorkload(TestElfPath);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        // Spike runs as a live child process; dispose kills it when the simulation ends.
        using var cosim = new SpikeCoSimReference(TestElfPath, workload.BaseAddress, workload.MemorySize);

        var train = new SingleCycleTrain(
            new Rv32Mechanism(), mem, workload.EntryPoint,
            commitObserver: cosim
        );

        // CoSimDivergenceException is thrown on the first mismatch.
        train.Run();
    }
}
