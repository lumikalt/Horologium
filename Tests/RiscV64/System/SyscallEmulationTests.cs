using Orrery.Train;
using Pipeline;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

namespace Tests.RiscV64.System;

/// <summary>
///     RV64 counterpart to <see cref="Tests.RiscV32.System.SyscallEmulationTests" />'s SE-mode
///     section — <see cref="LinuxSyscallEmulator" /> intercepting ECALL on a 64-bit hart. No HTIF
///     section here: <see cref="RiscV32.Memory.HtifMemory" /> coverage is RV32-specific and
///     orthogonal to this test file's purpose (proving the ECALL-intercept path, added to
///     <see cref="Rv64Mechanism" /> this session, works end-to-end on RV64).
/// </summary>
public class SyscallEmulationTests {
    private static string SeHello64Elf => Path.Combine(AppContext.BaseDirectory, "se_hello64.elf");

    [Fact]
    public void SyscallEmulation_HelloWorld_OutputCaptured() {
        var workload = new Rv64ElfWorkload(SeHello64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);

        train.Run();
        Assert.Equal("Hello, SE mode!\n", sw.ToString());
    }

    [Fact]
    public void SyscallEmulation_ExitHalts() {
        var workload = new Rv64ElfWorkload(SeHello64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        var handler = new LinuxSyscallEmulator(workload.InitialBreak);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        RevolutionResult result = new SingleCycleTrain(mech, mem, workload.EntryPoint).Run();

        Assert.True(result.TotalTicks < 100_000, "SYS_exit should have halted before budget");
    }

    /// <summary>
    ///     Authoritative test for <see cref="Rv64ElfWorkload.InitialBreak" /> — <c>se_hello</c>
    ///     never calls <c>brk</c>, so the end-to-end tests above can't confirm this property was
    ///     computed correctly on its own.
    /// </summary>
    [Fact]
    public void InitialBreak_ComputedFromPtLoadSegments() {
        var workload = new Rv64ElfWorkload(SeHello64Elf);
        Assert.Equal(0UL, workload.InitialBreak % 4096);
        Assert.True(workload.InitialBreak >= workload.BaseAddress);
    }
}
