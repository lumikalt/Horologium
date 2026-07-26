#region

using Mechanism;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.System;

/// <summary>
///     End-to-end proof that a genuinely compiled, statically-linked <c>pthread_create</c>/
///     <c>pthread_join</c> binary (musl libc, real <c>_start</c>) runs to completion on
///     <see cref="MultiHartKernel" /> — the decisive integration test for the LoopPoint
///     prerequisite chain (thread pointer, <c>clone()</c>, <c>futex()</c>, per-hart <c>gettid</c>).
///     <para>
///         Getting this far surfaced a real bug, found by disassembling this exact binary after it
///         hung: real musl passes <c>&amp;__thread_list_lock</c> (a different global lock, not
///         <c>&amp;self-&gt;tid</c>) as <c>clone()</c>'s <c>ctid</c>, relying on the kernel's
///         <c>CLONE_CHILD_CLEARTID</c> as a backstop to release that lock on a thread's exit —
///         <c>__pthread_exit</c> does not reliably call <c>__tl_unlock</c> along every exit path, so
///         without <c>CLONE_CHILD_CLEARTID</c> a second thread stays blocked on that lock forever
///         once a prior one exits (both hart0 and the surviving created hart were observed polling
///         it in a real run). An initial hypothesis that the small <c>hartId + 1</c> tid values were
///         also load-bearing (colliding with a sentinel range in musl's join loop) did not survive
///         an isolating re-test — reverting just the tid change while keeping
///         <c>CLONE_CHILD_CLEARTID</c> still passed, so tids stayed at <c>hartId + 1</c>.
///     </para>
/// </summary>
public class PthreadProbeTests {
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");

    [Fact]
    public void PthreadCreateAndJoin_RealCompiledBinary_RunsToCompletion() {
        const int memorySizeBytes = 16 * 1024 * 1024;
        var workload = new Rv64ElfWorkload(PthreadProbeTests.PthreadProbeElf, memorySizeBytes);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["pthread_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        // mmap arena for the two threads' stacks (pthread_create's own mmap call), placed well
        // between the ELF/heap region (near the base) and the main thread's own stack (at the top).
        ulong mmapBase = workload.BaseAddress + 8UL * 1024 * 1024;
        ulong mmapLimit = workload.BaseAddress + 14UL * 1024 * 1024;

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, 8, mmapBase, mmapLimit);

        // One active hart (the main thread) plus two dormant slots — this binary calls
        // pthread_create exactly twice. Every mechanism's hartId matches its eventual slot index,
        // the invariant clone()'s returned tid / gettid() consistency depends on (see
        // LinuxSyscallEmulator's class doc comment and HartIdentityTests).
        var kernel = new MultiHartKernel(
            mem, 1,
            new Rv64Mechanism(syscallHandler: handler, hartId: 0),
            new Rv64Mechanism(syscallHandler: handler, hartId: 1),
            new Rv64Mechanism(syscallHandler: handler, hartId: 2)
        );
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        kernel.Run(2_000_000);

        Assert.False(kernel.IsDormant(1)); // both pthread_create calls actually spawned
        Assert.False(kernel.IsDormant(2));
        string output = sw.ToString();
        Assert.Contains("thread 1 running\n", output);
        Assert.Contains("thread 2 running\n", output);
        // "done\n" printed only after both pthread_join calls return, so its presence at all (not
        // just its position) proves the join round-trip actually unblocked rather than the run just
        // hitting the tick budget with both threads' prints already flushed.
        Assert.EndsWith("done\n", output);
    }
}
