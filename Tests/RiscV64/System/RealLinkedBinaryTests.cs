using Mechanism;
using Pipeline;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

namespace Tests.RiscV64.System;

/// <summary>
///     End-to-end proof against a genuinely compiled, statically-linked binary (musl libc, real
///     <c>_start</c>, real <c>printf</c>/stdio) — not a hand-assembled probe that already assumes
///     the layout it's checking, like <see cref="InitialStackTests" />'s fixtures. This is the test
///     the whole SPEC-harness/batch-mode effort has been building toward: a real linked binary's
///     argv and stdout coming through <see cref="InitialStackBuilder" /> and
///     <see cref="LinuxSyscallEmulator" /> correctly.
///     <para>
///         Getting this far surfaced a real bug: musl's buffered stdio writes via
///         <c>SYS_writev</c>, not plain <c>SYS_write</c> — unimplemented, it silently produced no
///         output at all (the <c>ENOSYS</c> return is swallowed by musl's stdio error path rather
///         than surfaced), discovered only once a real linked binary could finally be built and
///         run (see <c>flake.nix</c>'s <c>riscv64-unknown-linux-musl-gcc</c>).
///     </para>
/// </summary>
public class RealLinkedBinaryTests {
    private static string Hello64MuslElf => Path.Combine(AppContext.BaseDirectory, "hello64_musl.elf");

    [Fact]
    public void HelloMusl_RealPrintfAndArgv_ProducesExpectedOutput() {
        var workload = new Rv64ElfWorkload(Hello64MuslElf, 8 * 1024 * 1024);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["hello64_musl.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(0, 0, 0, workload.EntryPoint)
        );

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        train.Run(2_000_000);

        Assert.True(train.IsIdle); // reached SYS_exit_group cleanly, didn't hit the tick budget
        Assert.Equal("hello from a real linked binary, argv[0]=hello64_musl.elf\n", sw.ToString());
    }
}