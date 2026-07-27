#region

using Mechanism;
using Pipeline;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

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

    private static string TlsProbeElf => Path.Combine(AppContext.BaseDirectory, "tls_probe.elf");

    [Fact]
    public void HelloMusl_RealPrintfAndArgv_ProducesExpectedOutput() {
        var workload = new Rv64ElfWorkload(Hello64MuslElf, 8 * 1024 * 1024);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["hello64_musl.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
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

    /// <summary>
    ///     A real <c>__thread</c> variable, not just a hand-assembled probe: musl's own
    ///     <c>_start</c>/<c>__init_tls</c> walks the program header table (found via the AT_PHDR/
    ///     AT_PHENT/AT_PHNUM auxv entries) to locate PT_TLS and set the thread pointer (<c>tp</c>,
    ///     x4) with a plain register move — no syscall.
    ///     <see cref="TlsProbe_WithPlaceholderZeroPhdrAuxv_CrashesInsteadOfWorking" />
    ///     is the discriminating control: the same binary, with only the auxv's AT_PHDR/
    ///     AT_PHENT/AT_PHNUM zeroed out instead of real, faults instead of running to completion —
    ///     proving this fixture actually exercises the fix, not just something that happens to work
    ///     either way.
    /// </summary>
    [Fact]
    public void TlsProbe_RealThreadLocalVariable_InitializesCorrectly() {
        var workload = new Rv64ElfWorkload(TlsProbeElf, 8 * 1024 * 1024);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["tls_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        train.Run(2_000_000);

        Assert.True(train.IsIdle);
        // tls_var is initialized to 42 in the TLS init image; tp and &tls_var coinciding is this
        // binary's actual (single-__thread-variable) layout, not an assumption this test bakes in.
        var output = sw.ToString();
        Assert.Contains("tls_var=42", output);
        Assert.DoesNotContain("tls_var=0 ", output);
    }

    /// <summary>Regression control for <see cref="TlsProbe_RealThreadLocalVariable_InitializesCorrectly" />.</summary>
    [Fact]
    public void TlsProbe_WithPlaceholderZeroPhdrAuxv_CrashesInsteadOfWorking() {
        var workload = new Rv64ElfWorkload(TlsProbeElf, 8 * 1024 * 1024);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["tls_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(0, 0, 0, workload.EntryPoint)
        );

        var handler = new LinuxSyscallEmulator(workload.InitialBreak, TextWriter.Null, 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        Assert.Throws<AccessViolationException>(() => train.Run(2_000_000));
    }
}