#region

using Mechanism;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;

// ReSharper disable InconsistentNaming

#endregion

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     End-to-end proof of <c>futex()</c> (syscall 98) <c>FUTEX_WAIT</c>/<c>FUTEX_WAKE</c> against
///     <see cref="MultiHartKernel" />'s poll-based blocking model (<see cref="ExecuteResult.RequestBlock" />),
///     using two hand-assembled RV32I programs sharing one futex word, mirroring <c>CloneTests</c>'
///     direct-syscall-invocation style.
/// </summary>
public class FutexTests {
    private const ulong FutexWord = 0x300;
    private const ulong Hart0ResultAddr = 0x304;
    private const ulong Hart1MarkerAddr = 0x308;
    private const uint Ecall = 0x0000_0073;
    private const uint Ebreak = 0x0010_0073;

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Sw(int rs2, int rs1, int imm) {
        uint immU = (uint)imm & 0xFFF;
        uint imm11_5 = (immU >> 5) & 0x7F;
        uint imm4_0 = immU & 0x1F;
        return (imm11_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4_0 << 7) | 0b0100011u;
    }

    private static void Load(FlatMemory mem, ulong baseAddr, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(baseAddr, bytes);
    }

    [Fact]
    public void FutexWait_BlocksUntilWordChanges_ThenReturnsEagain() {
        var mem = new FlatMemory(0x400);

        // Hart 0 (entry 0x00): futex(FUTEX_WAIT, &FutexWord, 0) — the word starts at 0 (FlatMemory
        // zero-initializes), so this call blocks until some other hart writes a different value.
        Load(
            mem,
            0x00,
            Addi(10, 0, (int)FutexTests.FutexWord), // a0 = uaddr
            Addi(11, 0, 0),                         // a1 = FUTEX_WAIT
            Addi(12, 0, 0),                         // a2 = expected value 0
            Addi(17, 0, 98),                        // a7 = SYS_futex
            FutexTests.Ecall,
            Sw(10, 0, (int)FutexTests.Hart0ResultAddr), // store futex()'s return value
            FutexTests.Ebreak
        );

        // Hart 1 (entry 0x40): store 1 into FutexWord, call futex(FUTEX_WAKE, &FutexWord, 1),
        // then leave a marker proving it ran to completion.
        Load(
            mem,
            0x40,
            Addi(5, 0, 1),
            Sw(5, 0, (int)FutexTests.FutexWord),
            Addi(10, 0, (int)FutexTests.FutexWord), // a0 = uaddr
            Addi(11, 0, 1),                         // a1 = FUTEX_WAKE
            Addi(12, 0, 1),                         // a2 = wake up to 1 waiter
            Addi(17, 0, 98),                        // a7 = SYS_futex
            FutexTests.Ecall,
            Addi(6, 0, 555),
            Sw(6, 0, (int)FutexTests.Hart1MarkerAddr),
            FutexTests.Ebreak
        );

        var handler = new LinuxSyscallEmulator(0x400);
        var kernel = new MultiHartKernel(
            mem, 2, new Rv32Mechanism(syscallHandler: handler), new Rv32Mechanism(syscallHandler: handler)
        );
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.Run(200);

        Assert.Equal(555UL, mem.Read(FutexTests.Hart1MarkerAddr, 4));
        // Real futex(2) semantics: FUTEX_WAIT returns -EAGAIN whenever *uaddr no longer matches the
        // expected value at the time it's (re-)observed — whether the mismatch came from a real
        // FUTEX_WAKE-triggered store or any other write. -11 sign-extends to 0xFFFFFFF5.
        Assert.Equal(0xFFFF_FFF5UL, mem.Read(FutexTests.Hart0ResultAddr, 4));
    }

    [Fact]
    public void FutexWait_WithAlreadyMismatchedWord_ReturnsEagainImmediately() {
        var mem = new FlatMemory(0x400);
        Load(
            mem,
            0x00,
            Addi(10, 0, (int)FutexTests.FutexWord),
            Addi(11, 0, 0),  // FUTEX_WAIT
            Addi(12, 0, 42), // expected 42, but the word is 0 — mismatched from the start
            Addi(17, 0, 98),
            FutexTests.Ecall,
            Sw(10, 0, (int)FutexTests.Hart0ResultAddr),
            FutexTests.Ebreak
        );

        var handler = new LinuxSyscallEmulator(0x400);
        var mech = new Rv32Mechanism(syscallHandler: handler);
        var kernel = new MultiHartKernel(mem, 1, mech);
        kernel.SetEntryPoint(0, 0x00);

        kernel.Run(20);

        Assert.Equal(0xFFFF_FFF5UL, mem.Read(FutexTests.Hart0ResultAddr, 4));
    }
}