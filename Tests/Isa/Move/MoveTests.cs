using Move;
using Pipeline;
using RiscV32.Memory;

namespace Tests.Isa.Move;

/// <summary>
///     TTA/MOVE unit tests. All instructions are 32-bit: [31:24]=dst [23:16]=src [15:0]=imm.
///     Use Run(n) for exactly n ticks. The halt instruction (dst=0xFF) terminates early.
///     Code placed at byte 0; data placed at DataBase (byte 0x200) to avoid overlap.
/// </summary>
public class MoveTests {
    private const int DataBase = 0x200;

    // Destination port constants
    private const byte R0 = 0x00, R1 = 0x01, R2 = 0x02;
    private const byte R5 = 0x05;
    private const byte AluOp = 0x10;
    private const byte AluIn1 = 0x11;
    private const byte AluIn2 = 0x12;
    private const byte MemLoad = 0x20;
    private const byte MemAddr = 0x21;
    private const byte MemStore = 0x22;
    private const byte BrCond = 0x30;
    private const byte BrTarget = 0x31;
    private const byte Halt = 0xFF;

    // Source port constants
    private const byte SrcAluout = 0x10;
    private const byte SrcMemout = 0x20;
    private const byte SrcImm = 0xFE;
    private const byte SrcPc = 0xFF;

    // ALU operation codes
    private const ushort AluSub = 0x01;
    private const ushort AluAnd = 0x02;
    private const ushort AluOr = 0x03;
    private const ushort AluXor = 0x04;
    private const ushort AluNot = 0x05;
    private const ushort AluShl = 0x06;
    private const ushort AluShr = 0x07;
    private const ushort AluSra = 0x08;
    private const ushort AluEq = 0x09;
    private const ushort AluLt = 0x0A;
    private const ushort AluUlt = 0x0B;
    private const ushort AluNeg = 0x0C;
    private const ushort AluInc = 0x0D;
    private const ushort AluDec = 0x0E;
    private const ushort AluCopy = 0x0F;

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

    // ── Immediate to register ─────────────────────────────────────────────────

    [Fact]
    public void ImmToReg_SetsRegister() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R0, MoveTests.SrcImm, 42));
        t.Run(1);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void ImmToReg_AllRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        for (var i = 0; i < 8; i++) I(m, i * 4, Mov((byte)i, MoveTests.SrcImm, (ushort)(10 + i)));
        t.Run(8);
        for (var i = 0; i < 8; i++) Assert.Equal(10 + i, s.R[i]);
    }

    // ── Register-to-register via ALU COPY ────────────────────────────────────

    [Fact]
    public void AluCopy_RegisterToRegister() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // alu.op = COPY, r1 = 99, r1 → alu.in2 (trigger), alu.out → r2
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluCopy));
        I(m, 4, Mov(MoveTests.R1, MoveTests.SrcImm, 99));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.R1));
        I(m, 12, Mov(MoveTests.R2, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(99, s.R[2]);
    }

    // ── ALU arithmetic ───────────────────────────────────────────────────────

    [Fact]
    public void AluAdd_TwoRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SrcImm, 30));
        I(m, 8, Mov(MoveTests.R1, MoveTests.SrcImm, 12));
        I(m, 12, Mov(MoveTests.AluIn1, MoveTests.R0));
        I(m, 16, Mov(MoveTests.AluIn2, MoveTests.R1));
        I(m, 20, Mov(MoveTests.R2, MoveTests.SrcAluout));
        t.Run(6);
        Assert.Equal(42, s.R[2]);
    }

    [Fact]
    public void AluSub_TwoRegisters() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluSub));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SrcImm, 50));
        I(m, 8, Mov(MoveTests.R1, MoveTests.SrcImm, 8));
        I(m, 12, Mov(MoveTests.AluIn1, MoveTests.R0));
        I(m, 16, Mov(MoveTests.AluIn2, MoveTests.R1));
        I(m, 20, Mov(MoveTests.R2, MoveTests.SrcAluout));
        t.Run(6);
        Assert.Equal(42, s.R[2]);
    }

    [Fact]
    public void AluAndTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluAnd));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0xFF0F));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 0x0FF0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0x0F00, s.R[0]);
    }

    [Fact]
    public void AluOrTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluOr));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0xF000));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 0x000F));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0xF00F, s.R[0]);
    }

    [Fact]
    public void AluXorTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluXor));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0xFF00));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 0xF0F0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0x0FF0, s.R[0]);
    }

    [Fact]
    public void AluNotTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluNot));
        I(m, 4, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 0xABCD));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(3);
        Assert.Equal(0x5432, s.R[0]); // ~0xABCD masked to 16 bits
    }

    [Fact]
    public void AluShlTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluShl));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 1));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 3));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(8, s.R[0]);
    }

    [Fact]
    public void AluShr_Logical() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluShr));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0x8000));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0x4000, s.R[0]); // logical: sign bit not propagated
    }

    [Fact]
    public void AluSra_Arithmetic() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluSra));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0x8000));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0xC000, s.R[0]); // arithmetic: sign bit propagated
    }

    [Fact]
    public void AluEq_Equal() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluEq));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 7));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 7));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(1, s.R[0]);
    }

    [Fact]
    public void AluEq_NotEqual() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluEq));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 7));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 8));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(0, s.R[0]);
    }

    [Fact]
    public void AluLt_SignedLess() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluLt));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 0xFFFF)); // -1 signed
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 1));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(1, s.R[0]); // -1 < 1
    }

    [Fact]
    public void AluUlt_UnsignedLess() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluUlt));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 1));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 0xFFFF)); // 65535 unsigned
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(4);
        Assert.Equal(1, s.R[0]); // 1 < 65535
    }

    [Fact]
    public void AluNegTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluNeg));
        I(m, 4, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 5));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(3);
        Assert.Equal(0xFFFB, s.R[0]); // -5 in ushort
    }

    [Fact]
    public void AluIncTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluInc));
        I(m, 4, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 41));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(3);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void AluDecTest() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluDec));
        I(m, 4, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 43));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(3);
        Assert.Equal(42, s.R[0]);
    }

    [Fact]
    public void AluOp_Persistent_AcrossInstructions() {
        // Set alu.op once, use ALU twice with different inputs — op must not reset.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 10));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 5));  // trigger: alu.out = 15
        I(m, 12, Mov(MoveTests.AluIn1, MoveTests.SrcAluout)); // in1 = 15 (no new alu.op)
        I(m, 16, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 3)); // trigger: alu.out = 15 + 3 = 18
        I(m, 20, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(6);
        Assert.Equal(18, s.R[0]);
    }

    // ── Memory load ───────────────────────────────────────────────────────────

    [Fact]
    public void MemLoad_ImmediateAddress() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 0xBEEF);
        I(m, 0, Mov(MoveTests.MemLoad, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.R0, MoveTests.SrcMemout));
        t.Run(2);
        Assert.Equal(0xBEEF, s.R[0]);
    }

    [Fact]
    public void MemLoad_RegisterAddress() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase + 4, 0x1234);
        I(m, 0, Mov(MoveTests.R1, MoveTests.SrcImm, MoveTests.DataBase + 4));
        I(m, 4, Mov(MoveTests.MemLoad, MoveTests.R1));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcMemout));
        t.Run(3);
        Assert.Equal(0x1234, s.R[0]);
    }

    [Fact]
    public void MemOut_Persistent_AfterLoad() {
        // mem.out stays latched; can be read again on a later instruction.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 99);
        I(m, 0, Mov(MoveTests.MemLoad, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluCopy)); // intervening instruction
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcMemout));                    // still valid
        t.Run(3);
        Assert.Equal(99, s.R[0]);
    }

    // ── Memory store ─────────────────────────────────────────────────────────

    [Fact]
    public void MemStore_ImmediateData() {
        (SingleCycleTrain t, MoveArchState _, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.MemAddr, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.MemStore, MoveTests.SrcImm, 0xCAFE));
        t.Run(2);
        Assert.Equal(0xCAFE, ReadD(m, MoveTests.DataBase));
    }

    [Fact]
    public void MemStore_RegisterData() {
        (SingleCycleTrain t, MoveArchState _, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R5, MoveTests.SrcImm, 0x5555));
        I(m, 4, Mov(MoveTests.MemAddr, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 8, Mov(MoveTests.MemStore, MoveTests.R5));
        t.Run(3);
        Assert.Equal(0x5555, ReadD(m, MoveTests.DataBase));
    }

    [Fact]
    public void MemStoreAndLoad_RoundTrip() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.MemAddr, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 4, Mov(MoveTests.MemStore, MoveTests.SrcImm, 0xABCD));
        I(m, 8, Mov(MoveTests.MemLoad, MoveTests.SrcImm, MoveTests.DataBase));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcMemout));
        t.Run(4);
        Assert.Equal(0xABCD, s.R[0]);
    }

    // ── Branch ───────────────────────────────────────────────────────────────

    [Fact]
    public void Branch_Taken_NonzeroCondition() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // br.cond = 1, br.target = 12, then set r0 = 99 at PC=12
        I(m, 0, Mov(MoveTests.BrCond, MoveTests.SrcImm, 1));
        I(m, 4, Mov(MoveTests.BrTarget, MoveTests.SrcImm, 12)); // taken: PC → 12
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcImm));           // skipped
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcImm, 99));      // executed
        t.Run(3);                                               // 2 (cond+target) + 1 (r0=99)
        Assert.Equal(99, s.R[0]);
    }

    [Fact]
    public void Branch_NotTaken_ZeroCondition() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.BrCond, MoveTests.SrcImm));
        I(m, 4, Mov(MoveTests.BrTarget, MoveTests.SrcImm, 12)); // not taken
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcImm, 77));       // executed (falls through)
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcImm, 99));      // also executed (next tick)
        t.Run(3);                                               // 2 (cond+target) + 1 (r0=77)
        Assert.Equal(77, s.R[0]);
    }

    [Fact]
    public void BrCond_Persistent_CanReuse() {
        // Set br.cond once; reuse it for a second branch.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.BrCond, MoveTests.SrcImm, 1)); // set cond = 1
        I(m, 4, Mov(MoveTests.R0, MoveTests.SrcImm, 5));
        I(m, 8, Mov(MoveTests.BrTarget, MoveTests.SrcImm, 16));  // taken
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcImm));           // skipped
        I(m, 16, Mov(MoveTests.BrTarget, MoveTests.SrcImm, 24)); // taken again (cond still 1)
        I(m, 20, Mov(MoveTests.R0, MoveTests.SrcImm));           // skipped
        I(m, 24, Mov(MoveTests.R1, MoveTests.SrcImm, 99));       // reached
        t.Run(5);                                                // cond, r0, br1, br2, r1
        Assert.Equal(5, s.R[0]);
        Assert.Equal(99, s.R[1]);
    }

    // ── Halt ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Halt_StopsExecution() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.R0, MoveTests.SrcImm, 42));
        I(m, 4, Mov(MoveTests.Halt, MoveTests.SrcImm));
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcImm, 99)); // must not execute
        t.Run(1000);                                      // halts at tick 2
        Assert.Equal(42, s.R[0]);
    }

    // ── PC source ────────────────────────────────────────────────────────────

    [Fact]
    public void SrcPc_ReadsCurrentPc() {
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        // Instruction at PC=8 reads PC; should get 8 as an ushort.
        I(m, 0, Mov(MoveTests.R0, MoveTests.SrcImm)); // placeholder
        I(m, 4, Mov(MoveTests.R0, MoveTests.SrcImm)); // placeholder
        I(m, 8, Mov(MoveTests.R0, MoveTests.SrcPc));  // r0 = PC = 8
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
        I(m, 0, Mov(MoveTests.R0, MoveTests.SrcImm, 5));
        I(m, 4, Mov(MoveTests.AluOp, MoveTests.SrcImm, MoveTests.AluDec));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.R0));
        I(m, 12, Mov(MoveTests.R0, MoveTests.SrcAluout));
        I(m, 16, Mov(MoveTests.BrCond, MoveTests.R0));
        I(m, 20, Mov(MoveTests.BrTarget, MoveTests.SrcImm, 8));
        I(m, 24, Mov(MoveTests.Halt, MoveTests.SrcImm));
        t.Run(1000);
        Assert.Equal(0, s.R[0]);
    }

    // ── AluIn2 uses transported value, not a latch ────────────────────────────

    [Fact]
    public void AluIn2_UsesTransportedValue() {
        // Trigger ALU twice in succession; second trigger sees the correct new in2.
        (SingleCycleTrain t, MoveArchState s, FlatMemory m) = Make();
        I(m, 0, Mov(MoveTests.AluOp, MoveTests.SrcImm));
        I(m, 4, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 10));
        I(m, 8, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 5)); // alu.out = 15
        I(m, 12, Mov(MoveTests.AluIn1, MoveTests.SrcImm, 20));
        I(m, 16, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 2)); // alu.out = 22
        I(m, 20, Mov(MoveTests.R0, MoveTests.SrcAluout));
        t.Run(6);
        Assert.Equal(22, s.R[0]);
    }

    // ── Load then compute ────────────────────────────────────────────────────

    [Fact]
    public void LoadThenCompute() {
        // Load a value from memory, add an immediate, store the result.
        (SingleCycleTrain t, MoveArchState _, FlatMemory m) = Make();
        D(m, MoveTests.DataBase, 100);
        I(m, 0, Mov(MoveTests.MemLoad, MoveTests.SrcImm, MoveTests.DataBase)); // mem.out = 100
        I(m, 4, Mov(MoveTests.AluOp, MoveTests.SrcImm));
        I(m, 8, Mov(MoveTests.AluIn1, MoveTests.SrcMemout));   // in1 = 100
        I(m, 12, Mov(MoveTests.AluIn2, MoveTests.SrcImm, 42)); // trigger: alu.out = 142
        I(m, 16, Mov(MoveTests.MemAddr, MoveTests.SrcImm, MoveTests.DataBase + 2));
        I(m, 20, Mov(MoveTests.MemStore, MoveTests.SrcAluout)); // mem[base+2] = 142
        t.Run(6);
        Assert.Equal(142, ReadD(m, MoveTests.DataBase + 2));
    }
}