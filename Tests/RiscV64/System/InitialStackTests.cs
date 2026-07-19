using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

namespace Tests.RiscV64.System;

/// <summary>
///     End-to-end proof that <see cref="InitialStackBuilder" />'s SP lands exactly where a real
///     <c>_start</c> would expect it — a hand-assembled probe (<c>abi_probe64.s</c>, built via the
///     bare-metal <c>riscv64-none-elf-gcc</c> toolchain) reads <c>argc</c>/<c>argv[0]</c> off the
///     stack using its own, independently hand-written offset arithmetic, so this test can't pass
///     just because the probe and the builder share the same (potentially wrong) assumption — see
///     <see cref="Tests.Mechanism.InitialStackBuilderTests" /> for the layout-correctness authority
///     this test complements rather than replaces.
/// </summary>
public class InitialStackTests {
    private static string AbiProbe64Elf => Path.Combine(AppContext.BaseDirectory, "abi_probe64.elf");

    [Fact]
    public void BuildInitialStack_SpLandsWhereProbeExpectsIt_OutputMatchesArgv0() {
        var workload = new Rv64ElfWorkload(AbiProbe64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, wordSize: 8, argv: ["a.out", "hello"], envp: [], auxv: []
        );

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp); // x2 = sp

        train.Run();
        Assert.Equal("a.out", sw.ToString()); // argv[0]
    }
}
