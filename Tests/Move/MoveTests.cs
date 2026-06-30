using Move;
using Pipeline;
using RiscV32.Memory;

namespace Tests.Move;

/// <summary>
/// TTA/MOVE unit tests. All instructions are 32-bit: [31:24]=dst [23:16]=src [15:0]=imm.
/// Use Run(n) for exactly n ticks. The halt instruction (dst=0xFF) terminates early.
/// Code placed at byte 0; data placed at DataBase (byte 0x200) to avoid overlap.
/// </summary>
public class MoveTests {
    private const int DataBase = 0x200;

    private static (SingleCycleTrain Train, MoveArchState State, FlatMemory Mem) Make(int sizeBytes = 4096) {
        var mem = new FlatMemory(sizeBytes);
        var train = new SingleCycleTrain(new MoveMechanism(), mem);
        return (train, (MoveArchState)train.ArchState, mem);
    }

    // Write a 4-byte instruction at the given byte address.
    private static void I(FlatMemory m, int byteAddr, uint instr) =>
        m.Write((ulong)byteAddr, instr, 4);

    // Write a 16-bit word to data memory.
    private static void D(FlatMemory m, int byteAddr, ushort value) =>
        m.Write((ulong)byteAddr, value, 2);

    // Read a 16-bit word from data memory.
    private static ushort ReadD(FlatMemory m, int byteAddr) =>
        (ushort)m.Read((ulong)byteAddr, 2);

    // ── Instruction encoder ───────────────────────────────────────────────────

    private static uint Mov(byte dst, byte src, ushort imm = 0) =>
        (uint)((dst << 24) | (src << 16) | imm);

    // Destination port constants
    private const byte R0 = 0x00, R1 = 0x01, R2 = 0x02, R3 = 0x03;
    private const byte R4 = 0x04, R5 = 0x05, R6 = 0x06, R7 = 0x07;
    private const byte ALU_OP = 0x10;
    private const byte ALU_IN1 = 0x11;
    private const byte ALU_IN2 = 0x12;
    private const byte MEM_LOAD = 0x20;
    private const byte MEM_ADDR = 0x21;
    private const byte MEM_STORE = 0x22;
    private const byte BR_COND = 0x30;
    private const byte BR_TARGET = 0x31;
    private const byte HALT = 0xFF;

    // Source port constants
    private const byte SRC_ALUOUT = 0x10;
    private const byte SRC_MEMOUT = 0x20;
    private const byte SRC_IMM = 0xFE;
    private const byte SRC_PC = 0xFF;

    // ALU operation codes
    private const ushort ALU_ADD = 0x00;
    private const ushort ALU_SUB = 0x01;
    private const ushort ALU_AND = 0x02;
    private const ushort ALU_OR = 0x03;
    private const ushort ALU_XOR = 0x04;
    private const ushort ALU_NOT = 0x05;
    private const ushort ALU_SHL = 0x06;
    private const ushort ALU_SHR = 0x07;
    private const ushort ALU_SRA = 0x08;
    private const ushort ALU_EQ = 0x09;
    private const ushort ALU_LT = 0x0A;
    private const ushort ALU_ULT = 0x0B;
    private const ushort ALU_NEG = 0x0C;
    private const ushort ALU_INC = 0x0D;
    private const ushort ALU_DEC = 0x0E;
    private const ushort ALU_COPY = 0x0F;

    // ── Immediate to register ─────────────────────────────────────────────────

    [Fact]
    public void ImmToReg_SetsRegister() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R0, MoveTests.SRC_IMM, 42));
        t.Run(1);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void ImmToReg_AllRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        for (var i = 0; i < 8; i++) I(m, i * 4, Mov((byte)i, MoveTests.SRC_IMM, (ushort)(10 + i)));
        t.Run(8);
        for (var i = 0; i < 8; i++) Assert.Equal(10 + i, s.R[i]);
    }

    // ── Register-to-register via ALU COPY ────────────────────────────────────

    [Fact]
    public void AluCopy_RegisterToRegister() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // alu.op = COPY, r1 = 99, r1 → alu.in2 (trigger), alu.out → r2
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_COPY));
        I(m, 4, Mov(MoveTests.R1, MoveTests.SRC_IMM, 99));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.R1));
        I(m, 12, Mov(MoveTests.R2, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(99, s.R[2]);
    }

    // ── ALU arithmetic ───────────────────────────────────────────────────────

    [Fact]
    public void AluAdd_TwoRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_ADD));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SRC_IMM, 30));
        I(m, 8, Mov(MoveTests.R1, MoveTests.SRC_IMM, 12));
        I(m, 12, Mov(MoveTests.ALU_IN1, MoveTests.R0));
        I(m, 16, Mov(MoveTests.ALU_IN2, MoveTests.R1));
        I(m, 20, Mov(MoveTests.R2, MoveTests.SRC_ALUOUT));
        t.Run(6);
        Assert.Equal(42, s.R[2]);
    }

    [Fact]
    public void AluSub_TwoRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_SUB));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SRC_IMM, 50));
        I(m, 8, Mov(MoveTests.R1, MoveTests.SRC_IMM, 8));
        I(m, 12, Mov(MoveTests.ALU_IN1, MoveTests.R0));
        I(m, 16, Mov(MoveTests.ALU_IN2, MoveTests.R1));
        I(m, 20, Mov(MoveTests.R2, MoveTests.SRC_ALUOUT));
        t.Run(6);
        Assert.Equal(42, s.R[2]);
    }

    [Fact]
    public void AluAnd() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_AND));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0xFF0F));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 0x0FF0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0x0F00, s.R[0]);
    }

    [Fact]
    public void AluOr() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_OR));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0xF000));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 0x000F));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0xF00F, s.R[0]);
    }

    [Fact]
    public void AluXor() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_XOR));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0xFF00));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 0xF0F0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0x0FF0, s.R[0]);
    }

    [Fact]
    public void AluNot() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_NOT));
        I(m, 4, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 0xABCD));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(3);
        Assert.Equal(0x5432, s.R[0]); // ~0xABCD masked to 16 bits
    }

    [Fact]
    public void AluShl() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_SHL));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 1));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 3));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(8, s.R[0]);
    }

    [Fact]
    public void AluShr_Logical() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_SHR));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0x8000));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0x4000, s.R[0]); // logical: sign bit not propagated
    }

    [Fact]
    public void AluSra_Arithmetic() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_SRA));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0x8000));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0xC000, s.R[0]); // arithmetic: sign bit propagated
    }

    [Fact]
    public void AluEq_Equal() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_EQ));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 7));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 7));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(1, s.R[0]);
    }

    [Fact]
    public void AluEq_NotEqual() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_EQ));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 7));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 8));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(0, s.R[0]);
    }

    [Fact]
    public void AluLt_SignedLess() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_LT));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 0xFFFF)); // -1 signed
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(1, s.R[0]); // -1 < 1
    }

    [Fact]
    public void AluUlt_UnsignedLess() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_ULT));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 1));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 0xFFFF)); // 65535 unsigned
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(4);
        Assert.Equal(1, s.R[0]); // 1 < 65535
    }

    [Fact]
    public void AluNeg() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_NEG));
        I(m, 4, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 5));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(3);
        Assert.Equal(0xFFFB, s.R[0]); // -5 in ushort
    }

    [Fact]
    public void AluInc() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_INC));
        I(m, 4, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 41));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(3);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void AluDec() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_DEC));
        I(m, 4, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 43));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(3);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void AluOp_Persistent_AcrossInstructions() {
        // Set alu.op once, use ALU twice with different inputs — op must not reset.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_ADD));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 10));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 5));  // trigger: alu.out = 15
        I(m, 12, Mov(MoveTests.ALU_IN1, MoveTests.SRC_ALUOUT)); // in1 = 15 (no new alu.op)
        I(m, 16, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 3)); // trigger: alu.out = 15 + 3 = 18
        I(m, 20, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(6);
        Assert.Equal(18, s.R[0]);
    }

    // ── Memory load ───────────────────────────────────────────────────────────

    [Fact]
    public void MemLoad_ImmediateAddress() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 0xBEEF);
        I(m, 0, Mov(MoveTests.MEM_LOAD, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SRC_MEMOUT));
        t.Run(2);
        Assert.Equal(0xBEEF, s.R[0]);
    }

    [Fact]
    public void MemLoad_RegisterAddress() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase + 4, 0x1234);
        I(m, 0, Mov(MoveTests.R1, MoveTests.SRC_IMM, (ushort)(MoveTests.DataBase + 4)));
        I(m, 4, Mov(MoveTests.MEM_LOAD, MoveTests.R1));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_MEMOUT));
        t.Run(3);
        Assert.Equal(0x1234, s.R[0]);
    }

    [Fact]
    public void MemOut_Persistent_AfterLoad() {
        // mem.out stays latched; can be read again on a later instruction.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 99);
        I(m, 0, Mov(MoveTests.MEM_LOAD, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_COPY)); // intervening instruction
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_MEMOUT));                      // still valid
        t.Run(3);
        Assert.Equal(99, s.R[0]);
    }

    // ── Memory store ─────────────────────────────────────────────────────────

    [Fact]
    public void MemStore_ImmediateData() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.MEM_ADDR, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.MEM_STORE, MoveTests.SRC_IMM, 0xCAFE));
        t.Run(2);
        Assert.Equal(0xCAFE, ReadD(m, MoveTests.DataBase));
    }

    [Fact]
    public void MemStore_RegisterData() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R5, MoveTests.SRC_IMM, 0x5555));
        I(m, 4, Mov(MoveTests.MEM_ADDR, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 8, Mov(MoveTests.MEM_STORE, MoveTests.R5));
        t.Run(3);
        Assert.Equal(0x5555, ReadD(m, MoveTests.DataBase));
    }

    [Fact]
    public void MemStoreAndLoad_RoundTrip() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.MEM_ADDR, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.MEM_STORE, MoveTests.SRC_IMM, 0xABCD));
        I(m, 8, Mov(MoveTests.MEM_LOAD, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_MEMOUT));
        t.Run(4);
        Assert.Equal(0xABCD, s.R[0]);
    }

    // ── Branch ───────────────────────────────────────────────────────────────

    [Fact]
    public void Branch_Taken_NonzeroCondition() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // br.cond = 1, br.target = 12, then set r0 = 99 at PC=12
        I(m, 0, Mov(MoveTests.BR_COND, MoveTests.SRC_IMM, 1));
        I(m, 4, Mov(MoveTests.BR_TARGET, MoveTests.SRC_IMM, 12)); // taken: PC → 12
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_IMM, 0));         // skipped
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_IMM, 99));       // executed
        t.Run(3);                                                 // 2 (cond+target) + 1 (r0=99)
        Assert.Equal(99, s.R[0]);
    }

    [Fact]
    public void Branch_NotTaken_ZeroCondition() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.BR_COND, MoveTests.SRC_IMM, 0));
        I(m, 4, Mov(MoveTests.BR_TARGET, MoveTests.SRC_IMM, 12)); // not taken
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_IMM, 77));        // executed (falls through)
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_IMM, 99));       // also executed (next tick)
        t.Run(3);                                                 // 2 (cond+target) + 1 (r0=77)
        Assert.Equal(77, s.R[0]);
    }

    [Fact]
    public void BrCond_Persistent_CanReuse() {
        // Set br.cond once; reuse it for a second branch.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.BR_COND, MoveTests.SRC_IMM, 1)); // set cond = 1
        I(m, 4, Mov(MoveTests.R0, MoveTests.SRC_IMM, 5));
        I(m, 8, Mov(MoveTests.BR_TARGET, MoveTests.SRC_IMM, 16));  // taken
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_IMM, 0));         // skipped
        I(m, 16, Mov(MoveTests.BR_TARGET, MoveTests.SRC_IMM, 24)); // taken again (cond still 1)
        I(m, 20, Mov(MoveTests.R0, MoveTests.SRC_IMM, 0));         // skipped
        I(m, 24, Mov(MoveTests.R1, MoveTests.SRC_IMM, 99));        // reached
        t.Run(5);                                                  // cond, r0, br1, br2, r1
        Assert.Equal(5, s.R[0]);
        Assert.Equal(99, s.R[1]);
    }

    // ── Halt ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Halt_StopsExecution() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R0, MoveTests.SRC_IMM, 42));
        I(m, 4, Mov(MoveTests.HALT, MoveTests.SRC_IMM, 0));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_IMM, 99)); // must not execute
        t.Run(1000);                                       // halts at tick 2
        Assert.Equal(42, s.R[0]);
    }

    // ── PC source ────────────────────────────────────────────────────────────

    [Fact]
    public void SrcPc_ReadsCurrentPc() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // Instruction at PC=8 reads PC; should get 8 as a ushort.
        I(m, 0, Mov(MoveTests.R0, MoveTests.SRC_IMM, 0)); // placeholder
        I(m, 4, Mov(MoveTests.R0, MoveTests.SRC_IMM, 0)); // placeholder
        I(m, 8, Mov(MoveTests.R0, MoveTests.SRC_PC));     // r0 = PC = 8
        t.Run(3);
        Assert.Equal(8, s.R[0]);
    }

    // ── Countdown loop ───────────────────────────────────────────────────────

    [Fact]
    public void CountdownLoop_FiveIterations() {
        // r0 = 5, loop: r0 = r0 - 1, until r0 = 0, then halt.
        // PC=0: imm 5 → r0
        // PC=4: imm DEC → alu.op
        // PC=8: r0 → alu.in2 (trigger: alu.out = r0 - 1)      ← loop
        // PC=12: alu.out → r0
        // PC=16: r0 → br.cond
        // PC=20: imm 8 → br.target (if r0 ≠ 0, goto 8)
        // PC=24: halt
        // Ticks: 2 (init) + 5 × 4 (loop body) + 1 (halt) = 23
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R0, MoveTests.SRC_IMM, 5));
        I(m, 4, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_DEC));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.R0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        I(m, 16, Mov(MoveTests.BR_COND, MoveTests.R0));
        I(m, 20, Mov(MoveTests.BR_TARGET, MoveTests.SRC_IMM, 8));
        I(m, 24, Mov(MoveTests.HALT, MoveTests.SRC_IMM, 0));
        t.Run(1000);
        Assert.Equal(0, s.R[0]);
    }

    // ── AluIn2 uses transported value, not a latch ────────────────────────────

    [Fact]
    public void AluIn2_UsesTransportedValue() {
        // Trigger ALU twice in succession; second trigger sees the correct new in2.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_ADD));
        I(m, 4, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 10));
        I(m, 8, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 5)); // alu.out = 15
        I(m, 12, Mov(MoveTests.ALU_IN1, MoveTests.SRC_IMM, 20));
        I(m, 16, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 2)); // alu.out = 22
        I(m, 20, Mov(MoveTests.R0, MoveTests.SRC_ALUOUT));
        t.Run(6);
        Assert.Equal(22, s.R[0]);
    }

    // ── Load then compute ────────────────────────────────────────────────────

    [Fact]
    public void LoadThenCompute() {
        // Load a value from memory, add an immediate, store the result.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 100);
        I(m, 0, Mov(MoveTests.MEM_LOAD, MoveTests.SRC_IMM, (ushort)MoveTests.DataBase)); // mem.out = 100
        I(m, 4, Mov(MoveTests.ALU_OP, MoveTests.SRC_IMM, MoveTests.ALU_ADD));
        I(m, 8, Mov(MoveTests.ALU_IN1, MoveTests.SRC_MEMOUT));   // in1 = 100
        I(m, 12, Mov(MoveTests.ALU_IN2, MoveTests.SRC_IMM, 42)); // trigger: alu.out = 142
        I(m, 16, Mov(MoveTests.MEM_ADDR, MoveTests.SRC_IMM, (ushort)(MoveTests.DataBase + 2)));
        I(m, 20, Mov(MoveTests.MEM_STORE, MoveTests.SRC_ALUOUT)); // mem[base+2] = 142
        t.Run(6);
        Assert.Equal(142, ReadD(m, MoveTests.DataBase + 2));
    }
}