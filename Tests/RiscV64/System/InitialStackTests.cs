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
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, wordSize: 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp); // x2 = sp

        train.Run();
        Assert.Equal("a.out", sw.ToString()); // argv[0]
    }

    [Fact]
    public void SyscallMemoryAccess_UnderOooeTrain_DoesNotCorruptLoadStoreQueues() {
        // Regression: LinuxSyscallEmulator's SYS_write reads the guest buffer through the same
        // IMemory ExecuteOne uses for real loads/stores. Before the fix, ExecResult.HasLoadAccess
        // was set straight from _capMem.HasRead regardless of instruction class, so an ECALL
        // (System class, no LQ entry allocated at Dispatch) tripped `_lq.At(issuedRob.LqIdx)` with
        // LqIdx == -1 → IndexOutOfRangeException. Found running a real musl binary's startup
        // ECALLs through OoOe for the first time (see project memory) — this probe reproduces it
        // in a couple thousand instructions instead of a multi-million-instruction real binary.
        var workload = new Rv64ElfWorkload(AbiProbe64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, wordSize: 8, argv: ["a.out", "hello"], envp: [], auxv: []
        );

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, wordSize: 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new OooeTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        train.Run(100_000);
        Assert.True(train.IsIdle);
        Assert.Equal("a.out", sw.ToString());
    }

    private static string StdinEcho64Elf => Path.Combine(AppContext.BaseDirectory, "stdin_echo64.elf");

    [Fact]
    public void LinuxSyscallEmulator_InjectedStdin_EchoedBackThroughSysReadSysWrite() {
        // End-to-end proof that a stream injected as LinuxSyscallEmulator's stdin reaches a real
        // guest's SYS_read and comes back out through SYS_write — the ELF-driven complement to the
        // direct Handle()-level unit tests in SyscallRealismTests.
        var workload = new Rv64ElfWorkload(StdinEcho64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(mem, stackTop, wordSize: 8, argv: ["a.out"], envp: [], auxv: []);

        var sw = new StringWriter();
        using var stdin = new MemoryStream("ping"u8.ToArray());
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, wordSize: 8, input: stdin);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        train.Run();
        Assert.Equal("ping", sw.ToString());
    }

    [Fact]
    public void SyscallMemoryWrite_UnderOooeTrain_DoesNotCorruptStoreQueue() {
        // Store-side sibling of SyscallMemoryAccess_UnderOooeTrain_DoesNotCorruptLoadStoreQueues:
        // SYS_clock_gettime WRITES a struct timespec into guest memory via IMemory.Write (the same
        // path ExecuteOne uses for real stores — unlike SYS_read, which fills its buffer through
        // IMemory.Load, a bulk copy CapturingMemory doesn't track as a "store" at all). The
        // load-side and store-side gates (ExecResult.HasLoadAccess/HasStoreCapture) are separate
        // fields set from separate CapturingMemory flags — a fix proven correct for HasLoadAccess
        // (via the sibling test) says nothing about HasStoreCapture, which needed its own
        // discriminating regression: unguarded, this crashes at `_sq.At(rob.SqIdx)` with
        // SqIdx == -1, the same shape of bug as the LQ crash but on the write side.
        //
        // Hand-encoded rather than a real ELF: no fixture here uses a memory-writing syscall in
        // isolation, and clock_gettime's result is deterministic (LinuxSyscallEmulator's synthetic
        // clock), letting this assert the exact written value, not just "didn't crash".
        const ulong tsPtr = 0x1000;
        byte[] program = InitialStackTests.Encode(
            0x000015B7u, // lui  a1, 1         — a1 = 0x1000 (struct timespec*)
            0x07100893u, // addi a7, x0, 113   — SYS_clock_gettime
            0x00000073u, // ecall
            0x00000513u, // addi a0, x0, 0     — exit code
            0x05D00893u, // addi a7, x0, 93    — SYS_exit
            0x00000073u // ecall
        );

        var workload = new ByteArrayWorkload(program, memorySizeBytes: 0x2000);
        var mem = new FlatMemory(workload.MemorySize, 0);
        workload.Load(mem);

        var handler = new LinuxSyscallEmulator(initialBreak: 0x2000, wordSize: 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new OooeTrain(mech, mem, workload.EntryPoint);

        train.Run(100_000);
        Assert.True(train.IsIdle);

        // First clock_gettime call: _fakeNanos advances by 1ms before writing (see
        // LinuxSyscallEmulator.ClockGettime) — tv_sec=0, tv_nsec=1_000_000.
        Assert.Equal(0UL, mem.Read(tsPtr, 8));
        Assert.Equal(1_000_000UL, mem.Read(tsPtr + 8, 8));
    }

    [Fact]
    public void LinuxSyscallEmulator_InjectedStdin_EchoedBackThroughSysReadSysWrite_UnderOooeTrain() {
        // Regression for the TODO.md-documented register-delivery gap: ECALL's return value
        // was delivered only via ExecuteResult.SideEffect applied at Commit, never through the
        // PRF/rename, so `mv a2, a0` (SYS_write's count, depending on SYS_read's return value)
        // could rename to a0's pre-ECALL physical register and read a stale/never-written slot
        // instead of the syscall's result — producing empty output under OooeTrain even though
        // the identical program worked under SingleCycleTrain (see the sibling test above).
        // Fixed by giving RvEcall a SecondaryDestinationRegister of x10 (a0): the existing
        // generic HasPendingSecondaryDest/CommitRegisters machinery (previously used only for
        // amocas.d's register-pair high half) now stalls renaming of any younger consumer of a0
        // until the ECALL retires, and syncs the PRF slot RAT still maps for a0 at that point.
        var workload = new Rv64ElfWorkload(StdinEcho64Elf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(mem, stackTop, wordSize: 8, argv: ["a.out"], envp: [], auxv: []);

        var sw = new StringWriter();
        using var stdin = new MemoryStream("ping"u8.ToArray());
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, wordSize: 8, input: stdin);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new OooeTrain(mech, mem, workload.EntryPoint);
        train.ArchState.IntegerRegisters.Write(2, sp);

        train.Run(100_000);
        Assert.True(train.IsIdle);
        Assert.Equal("ping", sw.ToString());
    }

    [Fact]
    public void SyscallMemoryWrite_OrdersBeforeYoungerLoad_UnderOooeTrain() {
        // Regression for the TODO.md-documented memory-ordering hazard: a younger load could
        // issue and execute before a head-serialized ECALL that writes overlapping memory,
        // reading stale data. HasPrecedingVectorStore (OooeTrain.cs) previously only blocked
        // loads behind vector stores; ECALL was never added to that blocking set, even though
        // it is equally non-speculative and equally bypasses the normal store-forwarding
        // machinery (its writes go straight through DLayers.Accessor, not the Store Queue).
        //
        // To make the race deterministic (rather than relying on scheduling luck), a long
        // dependent add-chain precedes the ECALL — the chain must fully retire before ECALL
        // can reach the ROB head and issue (System-class instructions only issue at the ROB
        // head), while the younger load's address (a1, set once at the very start) is ready
        // from cycle zero. Without the fix, the load races ahead and reads a poison value
        // stored at the same address before the chain even begins; with the fix, it is
        // blocked until the ECALL retires and observes the post-syscall value instead.
        const ulong tsPtr = 0x1000;
        const ulong capturePtr = 0x1020;

        var words = new List<uint> {
            0x000015B7u, // lui  a1, 1          — a1 = 0x1000 (struct timespec* / poison target)
            0x12300293u, // addi t0, x0, 0x123  — poison marker
            0x0055A023u, // sw   t0, 0(a1)      — poison mem[0x1000..0x1004)
            0x00100313u, // addi t1, x0, 1      — start of dependent chain
        };
        for (var i = 0; i < 64; i++) words.Add(0x00130313u); // addi t1, t1, 1 (chain delays ECALL)
        words.AddRange([
            0x07100893u, // addi a7, x0, 113 — SYS_clock_gettime
            0x00000073u, // ecall            — overwrites mem[0x1000..0x1010) with tv_sec/tv_nsec
            0x0005A383u, // lw   t2, 0(a1)   — younger load: races against the ECALL's write
            0x02058613u, // addi a2, a1, 32  — a2 = 0x1020 (capture slot)
            0x00762023u, // sw   t2, 0(a2)   — capture what the load actually saw
            0x00000513u, // addi a0, x0, 0   — exit code
            0x05D00893u, // addi a7, x0, 93  — SYS_exit
            0x00000073u, // ecall
        ]);

        byte[] program = InitialStackTests.Encode(words.ToArray());

        var workload = new ByteArrayWorkload(program, memorySizeBytes: 0x2000);
        var mem = new FlatMemory(workload.MemorySize, 0);
        workload.Load(mem);

        var handler = new LinuxSyscallEmulator(initialBreak: 0x2000, wordSize: 8);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new OooeTrain(mech, mem, workload.EntryPoint);

        train.Run(100_000);
        Assert.True(train.IsIdle);

        Assert.Equal(0UL, mem.Read(tsPtr, 8)); // ECALL's write landed (tv_sec = 0 on first call)
        Assert.Equal(0UL, mem.Read(capturePtr, 4)); // load saw the post-ECALL value, not the 0x123 poison
    }

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }
}
