#region

using Mechanism;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     Per-hart <c>gettid</c> and <c>SYS_exit</c>-vs-<c>SYS_exit_group</c> semantics, threaded through
///     <see cref="ISyscallHandler.Handle" />'s <c>hartId</c> parameter. Hand-assembled RV32I programs,
///     mirroring <see cref="CloneTests" />' direct-syscall-invocation style.
/// </summary>
public class HartIdentityTests {
    private const ulong Hart0TidAddr = 0x300;
    private const ulong Hart1TidAddr = 0x304;
    private const ulong Hart1MarkerAddr = 0x308;
    private const uint Ecall = 0x0000_0073;
    private const uint Ebreak = 0x0010_0073;

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Sw(int rs2, int rs1, int imm) {
        uint immU = (uint)imm & 0xFFF;
        uint imm11To5 = (immU >> 5) & 0x7F;
        uint imm4To0 = immU & 0x1F;
        return (imm11To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4To0 << 7) | 0b0100011u;
    }

    // beq rs1, rs2, immOffset — used (not jal) for hart 0's infinite loop below, since a jal that
    // branches to its own address is already caught by MultiHartKernel's self-loop-halt idiom
    // (state.Pc == pc && Class == Branch); a two-instruction back-branch loop isn't.
    private static uint Beq(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b000u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
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

    // gettid() (a7=178) with no args, storing a0's return value.
    private static uint[] GettidAndStore(ulong storeAddr) => [
        Addi(17, 0, 178),
        HartIdentityTests.Ecall,
        Sw(10, 0, (int)storeAddr),
    ];

    [Fact]
    public void Gettid_ReturnsDistinctPerHartValue_NeverZero() {
        var mem = new FlatMemory(0x400);
        uint[] hart0 = [.. GettidAndStore(HartIdentityTests.Hart0TidAddr), HartIdentityTests.Ebreak,];
        uint[] hart1 = [.. GettidAndStore(HartIdentityTests.Hart1TidAddr), HartIdentityTests.Ebreak,];
        Load(mem, 0x00, hart0);
        Load(mem, 0x40, hart1);

        var handler = new LinuxSyscallEmulator(0x400);
        var kernel = new MultiHartKernel(
            mem, 2,
            new Rv32Mechanism(syscallHandler: handler, hartId: 0),
            new Rv32Mechanism(syscallHandler: handler, hartId: 1)
        );
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.Run(20);

        Assert.Equal(1UL, mem.Read(HartIdentityTests.Hart0TidAddr, 4));
        Assert.Equal(2UL, mem.Read(HartIdentityTests.Hart1TidAddr, 4));
    }

    [Fact]
    public void ExitGroup_HaltsEveryHart_NotJustTheCaller() {
        var mem = new FlatMemory(0x400);
        // Hart 0: a genuine, unbounded two-instruction back-branch loop (not a jal-to-self, which
        // MultiHartKernel already halts on its own via the self-loop idiom) — only exit_group
        // reaching across from hart 1 can stop it before maxTicks.
        Load(
            mem,
            0x00,
            Addi(5, 5, 1), // 0x00: t0 += 1
            Beq(0, 0, -4)  // 0x04: unconditional branch back to 0x00
        );

        // Hart 1: SYS_exit_group (94) immediately.
        Load(
            mem,
            0x40,
            Addi(10, 0, 0),  // a0 = exit status
            Addi(17, 0, 94), // a7 = SYS_exit_group
            HartIdentityTests.Ecall,
            Addi(6, 0, 555),
            Sw(6, 0, (int)HartIdentityTests.Hart1MarkerAddr)
        );

        var handler = new LinuxSyscallEmulator(0x400);
        var kernel = new MultiHartKernel(
            mem, 2, new Rv32Mechanism(syscallHandler: handler), new Rv32Mechanism(syscallHandler: handler)
        );
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.Run();

        // If exit_group only halted the calling hart (the bug this test guards against), hart 0's
        // back-branch loop would run for the full 1,000,000 ticks; RequestHaltAll must cut it short.
        Assert.True(kernel.Ticks < 1000, $"expected early termination, got {kernel.Ticks} ticks");
        // exit_group halts before its own next instruction commits (same as SYS_exit), so hart 1's
        // marker store never executes.
        Assert.Equal(0UL, mem.Read(HartIdentityTests.Hart1MarkerAddr, 4));
    }
}