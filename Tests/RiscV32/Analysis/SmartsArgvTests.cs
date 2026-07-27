#region

using Mechanism;
using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     Validates the two pieces <c>Experiment.RunSmarts</c>'s argv/Linux-ABI support builds on:
///     <see cref="SmartsDriver.Run" />'s <c>seedInitialState</c> hook (the one-time seam that lets
///     a psABI stack pointer reach whichever train is constructed first, since
///     <see cref="ArchStateTransfer.CopyInto" /> only ever fires once something has already been
///     <c>carried</c>), and <c>RunSmarts</c>'s own guard against a non-<see cref="IElfWorkload" />
///     workload. <see cref="Tests.RiscV64.System.RealLinkedSmartsTests" /> covers the argv path
///     end-to-end against a real compiled binary; these are the cheap, deterministic unit-level
///     checks of the mechanism itself.
/// </summary>
public class SmartsArgvTests {
    // x2 (sp) is never read or written by SmartsTestWorkload's program (it only touches
    // x1/x3/x4/x5/x6) — so a value seeded into it must survive completely untouched across the
    // functional fast-forward and into whatever the first detailed unit is handed.
    private const ulong SeededSp = 0x0000_0000_0000_1FF0UL;

    [Fact]
    public void SeedInitialState_FiresExactlyOnce_AndSurvivesIntoTheFirstUnit() {
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        var iLayers = MemoryLayers.Build(mem, MemoryConfig.None);
        var dLayers = MemoryLayers.Build(mem, SmartsTestWorkload.DCache());
        var parameters = new SmartsParameters(60, 12, 600, 60, 3);

        var seedCalls = 0;
        ulong? observedAtFirstUnit = null;

        SmartsDriver.Run(
            mechanism, 0, iLayers, dLayers, null, parameters, SmartsDriver.FiveStage(),
            (_, _, state) => observedAtFirstUnit ??= state.IntegerRegisters.Read(2),
            state => {
                seedCalls++;
                state.IntegerRegisters.Write(2, SmartsArgvTests.SeededSp);
            }
        );

        Assert.Equal(1, seedCalls);
        Assert.Equal(SmartsArgvTests.SeededSp, observedAtFirstUnit);
    }

    [Fact]
    public void RunSmarts_WithArgvOnNonElfWorkload_Throws() {
        var workload = new RawBinaryWorkload(new byte[64], memorySizeBytes: 4096);
        var mechanism = new Rv32Mechanism();
        var parameters = new SmartsParameters(10, 5, 20, 0, 1);
        var config = new TrainConfig();

        Assert.Throws<NotSupportedException>(() => Experiment.RunSmarts(
                                                 workload, mechanism, config, parameters, ["prog",]
                                             )
        );
    }
}