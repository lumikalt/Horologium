using Mechanism;
using Orrery.Devices;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.System;

/// <summary>
///     Integration tests for CLINT + RvTrapController timer interrupt delivery.
///     A hand-assembled RV32 program:
///     1. Configures the trap vector and enables MIE/MTIE.
///     2. Sets mtimecmp to a small future value via MMIO writes to the CLINT.
///     3. Spins in a tight loop (reading mtime) until the timer fires.
///     4. The M-mode trap handler sets a flag and returns.
///     5. After the first MRET the main loop sees the flag and ebreaks.
/// </summary>
public class ClintInterruptTests {
    // Memory layout
    private const uint Base = 0x80000000u;
    private const int MemSize = 256 * 1024; // 256 KiB

    // CLINT register addresses
    private const ulong ClintBase = ClintDevice.DefaultBase;
    private const ulong Mtimecmp0Lo = ClintInterruptTests.ClintBase + 0x4000;

    // RAM addresses (byte offsets from Base)
    private const uint TrapVecOff = 0x00; // 4 instructions for trap handler
    private const uint MainOff = 0x20;    // main program starts here
    private const uint FlagOff = 0xF0;    // flag word written by trap handler

    private static byte[] BuildProgram() {
        // Assemble into a uint[] at word index = byteOffset / 4
        var words = new uint[ClintInterruptTests.MemSize / 4];

        // ── Trap handler at Base+TrapVecOff ──────────────────────────────────
        // The handler:
        //   la   t0, flag
        //   li   t1, 1
        //   sw   t1, 0(t0)   ← set flag
        //   mret

        const uint flagAddr = ClintInterruptTests.Base + ClintInterruptTests.FlagOff;
        const uint flagAddrHi = flagAddr >> 12;
        const int flagAddrLo = (int)(flagAddr & 0xFFF);

        // lui t0, flagAddr[31:12]
        W(ClintInterruptTests.TrapVecOff + 0, (flagAddrHi << 12) | (5u << 7) | 0x37u);
        // addi t0, t0, flagAddr[11:0]  (sign-extended)
        W(ClintInterruptTests.TrapVecOff + 4, (flagAddrLo << 20) | (5u << 15) | (0u << 12) | (5u << 7) | 0x13u);
        // li   t1, 1   →   addi t1, x0, 1
        W(ClintInterruptTests.TrapVecOff + 8, (1u << 20) | (0u << 15) | (0u << 12) | (6u << 7) | 0x13u);
        // sw   t1, 0(t0)
        W(ClintInterruptTests.TrapVecOff + 12, (0u << 25) | (6u << 20) | (5u << 15) | (2u << 12) | (0u << 7) | 0x23u);
        // mret
        W(ClintInterruptTests.TrapVecOff + 16, 0x30200073u);

        // ── Main program at Base+MainOff ──────────────────────────────────────
        // Set mtvec = Base + TrapVecOff
        const uint tvecVal = ClintInterruptTests.Base + ClintInterruptTests.TrapVecOff;
        const uint tvecHi = tvecVal >> 12;
        const int tvecLo = (int)(tvecVal & 0xFFF);

        // lui  t0, tvecVal[31:12]
        W(ClintInterruptTests.MainOff + 0, (tvecHi << 12) | (5u << 7) | 0x37u);
        // addi t0, t0, tvecVal[11:0]
        W(ClintInterruptTests.MainOff + 4, (tvecLo << 20) | (5u << 15) | (0u << 12) | (5u << 7) | 0x13u);
        // csrw mtvec, t0   →   csrrw x0, 0x305, t0
        W(ClintInterruptTests.MainOff + 8, (0x305u << 20) | (5u << 15) | (1u << 12) | (0u << 7) | 0x73u);

        // Set mtimecmp[0] = mtime + 20  (20 ticks ahead)
        // First read mtime_lo:  lui t0, clintBase[31:12];  lw t1, clintBase[11:0]+0xBFF8(t0)
        // Then compute mtimecmp_lo = t1 + 20
        // Write it:  sw t2, mtimecmpLo(t0)  and  sw x0, mtimecmpHi(t0)
        //
        // Rather than a complicated sequence, write a fixed mtimecmp_lo = 1000
        // (mtime starts at 0, TicksPerInstruction = 1, so it fires after 1000 ticks)
        const ulong mcmpBase = ClintInterruptTests.Mtimecmp0Lo;
        const uint mcmpHi = (uint)(mcmpBase >> 12);
        const int mcmpLoOff = (int)(mcmpBase & 0xFFF);

        // lui  t0, clintMtimecmpBase
        W(ClintInterruptTests.MainOff + 12, (mcmpHi << 12) | (5u << 7) | 0x37u);
        // addi t0, t0, offset_lo  (address of mtimecmp[0]_lo)
        W(ClintInterruptTests.MainOff + 16, (mcmpLoOff << 20) | (5u << 15) | (0u << 12) | (5u << 7) | 0x13u);
        // li   t1, 500  →  addi t1, x0, 500
        W(ClintInterruptTests.MainOff + 20, (500u << 20) | (0u << 15) | (0u << 12) | (6u << 7) | 0x13u);
        // sw   t1, 0(t0)   ← write mtimecmp[0]_lo = 500
        W(ClintInterruptTests.MainOff + 24, (0u << 25) | (6u << 20) | (5u << 15) | (2u << 12) | (0u << 7) | 0x23u);
        // sw   x0, 4(t0)   ← write mtimecmp[0]_hi = 0
        W(ClintInterruptTests.MainOff + 28, (0u << 25) | (0u << 20) | (5u << 15) | (2u << 12) | (4u << 7) | 0x23u);

        // Enable MIE and MTIE:
        // li   t1, 0x8    (MIE bit in mstatus)
        W(ClintInterruptTests.MainOff + 32, (8u << 20) | (0u << 15) | (0u << 12) | (6u << 7) | 0x13u);
        // csrs mstatus, t1  →  csrrs x0, 0x300, t1
        W(ClintInterruptTests.MainOff + 36, (0x300u << 20) | (6u << 15) | (2u << 12) | (0u << 7) | 0x73u);
        // li   t1, 0x80   (MTIE bit in mie)
        W(ClintInterruptTests.MainOff + 40, (0x80u << 20) | (0u << 15) | (0u << 12) | (6u << 7) | 0x13u);
        // csrs mie, t1    →  csrrs x0, 0x304, t1
        W(ClintInterruptTests.MainOff + 44, (0x304u << 20) | (6u << 15) | (2u << 12) | (0u << 7) | 0x73u);

        // Spin-wait loop: read flag at flagAddr; branch-back if zero; break when nonzero
        // la   t0, flagAddr
        const uint faHi = flagAddr >> 12;
        const int faLo = (int)(flagAddr & 0xFFF);

        W(ClintInterruptTests.MainOff + 48, (faHi << 12) | (5u << 7) | 0x37u); // lui  t0, flagAddr_hi
        W(
            ClintInterruptTests.MainOff + 52, (faLo << 20) | (5u << 15) | (0u << 12) | (5u << 7) | 0x13u
        ); // addi t0, t0, flagAddr_lo
        // Loop: lw t1, 0(t0)
        W(ClintInterruptTests.MainOff + 56, (0u << 20) | (5u << 15) | (2u << 12) | (6u << 7) | 0x03u); // lw   t1, 0(t0)
        // beqz t1, -4  →  beq t1, x0, -4  (offset -4 bytes = branch back 1 instruction)
        // B-type: imm[12]=1, imm[10:5]=111111, rs2=x0, rs1=x6, funct3=000, imm[4:1]=1110, imm[11]=1
        // = 0xFE030EE3
        W(ClintInterruptTests.MainOff + 60, 0xFE030EE3u); // beq  t1, x0, -4
        // ebreak
        W(ClintInterruptTests.MainOff + 64, 0x00100073u);

        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;

        // Helper: write an instruction at a byte offset from Base
        void W(uint offset, uint instr) => words[offset / 4] = instr;
    }

    [Fact]
    public void SingleCycle_TimerInterrupt_FiringDelivered() {
        byte[] program = BuildProgram();

        var clint = new ClintDevice();
        var mechanism = new Rv32Mechanism(clint: clint);

        var mem = new FlatMemory(ClintInterruptTests.MemSize, ClintInterruptTests.Base);
        mem.Load(ClintInterruptTests.Base, program);
        IMemory bus = new PeripheralBus(mem, [(clint, ClintDevice.DefaultBase, ClintDevice.RegionSize),]);

        var train = new SingleCycleTrain(mechanism, bus, ClintInterruptTests.Base + ClintInterruptTests.MainOff);
        train.Run(20_000);

        // The flag word should be 1 if the interrupt fired and the handler ran.
        ulong flag = mem.Read(ClintInterruptTests.Base + ClintInterruptTests.FlagOff, 4);
        Assert.Equal(1UL, flag);
    }
}