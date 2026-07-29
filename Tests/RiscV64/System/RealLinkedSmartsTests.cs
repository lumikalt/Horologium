#region

using Pipeline;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.System;

/// <summary>
///     Validates <see cref="Experiment.RunSmarts" />'s argv/Linux-ABI support (the machinery
///     behind <c>--smarts-argv</c>) against the same genuinely compiled, statically-linked
///     binary <see cref="RealLinkedSimPointTests" /> uses (<c>simpoint_kernel.elf</c>) — the
///     first time SMARTS's live train-switching handoff sees a real psABI stack and a
///     <see cref="LinuxSyscallEmulator" /> instead of a bare-metal HTIF probe. Unlike SimPoint's
///     checkpoint-and-measure design (whose points can be placed to dodge this binary's startup
///     ECALLs, per <see cref="RealLinkedSimPointTests" />'s doc comment), SMARTS's sampling units
///     are scattered starting from instruction 0, so this deliberately runs straight through
///     musl's startup syscalls inside a detailed <c>OooTrain</c> window — exactly the OoO+ECALL
///     interaction that surfaced real bugs when this codebase first ran <c>LinuxSyscallEmulator</c>
///     through <c>OooTrain</c> (see docs/scripting-and-checkpointing.md).
/// </summary>
public class RealLinkedSmartsTests {
    private const int WordSize = 8;
    private static readonly string[] Argv = ["simpoint_kernel.elf",];
    private static string ElfPath => Path.Combine(AppContext.BaseDirectory, "simpoint_kernel.elf");

    private static Rv64ElfWorkload MakeWorkload() => new(ElfPath, 8 * 1024 * 1024);

    [Fact]
    public void SmartsArgv_OnRealBinary_MeasuresPlausibleCpiThroughStartupAndCompute() {
        Rv64ElfWorkload workload = MakeWorkload();
        var mechanism = new Rv64Mechanism(
            syscallHandler: new LinuxSyscallEmulator(
                workload.InitialBreak, TextWriter.Null, RealLinkedSmartsTests.WordSize
            )
        );
        var parameters = new SmartsParameters(3_000, 0, 5_000, 0, 5);
        var config = new TrainConfig("ooo", RobCapacity: 32);

        SmartsResult result = Experiment.RunSmarts(
            workload, mechanism, config, parameters, RealLinkedSmartsTests.Argv,
            RealLinkedSmartsTests.WordSize
        );

        Assert.NotEmpty(result.Units);
        Assert.True(result.MeanCpi > 0 && double.IsFinite(result.MeanCpi), $"implausible mean CPI: {result.MeanCpi}");
    }

    [Fact]
    public void SmartsArgv_OnNonElfWorkload_Throws() {
        Rv64ElfWorkload elfWorkload = MakeWorkload();

        // A bare-metal-shaped IWorkload (no IElfWorkload) — should be rejected up front rather
        // than silently running with an un-injected stack pointer.
        var bareWorkload = new RawBinaryWorkload(new byte[64], elfWorkload.BaseAddress);
        var mechanism = new Rv64Mechanism(
            syscallHandler: new LinuxSyscallEmulator(0, TextWriter.Null, RealLinkedSmartsTests.WordSize)
        );
        var parameters = new SmartsParameters(10, 5, 50, 0, 1);
        var config = new TrainConfig("ooo");

        Assert.Throws<NotSupportedException>(() => Experiment.RunSmarts(
                                                 bareWorkload, mechanism, config, parameters, RealLinkedSmartsTests.Argv
                                             )
        );
    }
}