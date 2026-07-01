using J1;
using Pipeline;
using RiscV32.Memory;

namespace Tests.J1;

/// <summary>
/// J1 Forth CPU unit tests.
/// PC is a byte address; J1 word n lives at byte 2n. Code starts at word 0.
/// Data placed at DataWord (word 64, byte 128) to avoid overlap with code.
/// Use Run(n) for exactly n instruction ticks; no HLT exists in the J1 ISA.
/// </summary>
public class J1Tests {
    private const int DataWord = 64;

    private static (SingleCycleTrain Train, J1ArchState State, FlatMemory Mem) Make(int words = 256) {
        var mem = new FlatMemory(words * 2);
        var train = new SingleCycleTrain(new J1Mechanism(), mem);
        return (train, (J1ArchState)train.ArchState, mem);
    }

    private static void W(FlatMemory m, int wordAddr, int value) =>
        m.Write((ulong)(wordAddr * 2), (ushort)value, 2);

    private static int ReadW(FlatMemory m, int wordAddr) =>
        (int)(m.Read((ulong)(wordAddr * 2), 2) & 0xFFFF);

    // ── Instruction encoders ─────────────────────────────────────────────────

    private static int Lit(int v) => 0x8000 | (v & 0x7FFF);
    private static int Jump(int wt) => wt & 0x1FFF;
    private static int CondJump(int wt) => 0x2000 | (wt & 0x1FFF);
    private static int Call(int wt) => 0x4000 | (wt & 0x1FFF);

    // ALU op: DDelta/RDelta use 2-bit two's complement (0→0, +1→1, -2→2, -1→3).
    private static int AluOp(
        int tOut = 0,
        bool returnFromR = false,
        bool tToN = false,
        bool tToR = false,
        bool nToMem = false,
        int dDelta = 0,
        int rDelta = 0
    ) {
        int dd = dDelta switch { 0 => 0, 1 => 1, -2 => 2, _ => 3, };
        int rd = rDelta switch { 0 => 0, 1 => 1, -2 => 2, _ => 3, };
        return 0x6000
             | (returnFromR ? 0x1000 : 0)
             | ((tOut & 0xF) << 8)
             | (tToN ? 0x80 : 0)
             | (tToR ? 0x40 : 0)
             | (nToMem ? 0x20 : 0)
             | (dd << 2)
             | rd;
    }

    // ── Common Forth words ────────────────────────────────────────────────────

    private static int Nop() => AluOp();
    private static int Dup() => AluOp(tToN: true, dDelta: +1);
    private static int Drop() => AluOp(0x1, dDelta: -1);
    private static int Swap() => AluOp(0x1, tToN: true);
    private static int Over() => AluOp(0x1, tToN: true, dDelta: +1);
    private static int Add() => AluOp(0x2, dDelta: -1);
    private static int And() => AluOp(0x3, dDelta: -1);
    private static int Or() => AluOp(0x4, dDelta: -1);
    private static int Xor() => AluOp(0x5, dDelta: -1);
    private static int Invert() => AluOp(0x6);
    private static int Eq() => AluOp(0x7, dDelta: -1);
    private static int Lt() => AluOp(0x8, dDelta: -1);
    private static int Ult() => AluOp(0xF, dDelta: -1);
    private static int Dec() => AluOp(0xA);
    private static int Fetch() => AluOp(0xC);
    private static int Store() => AluOp(nToMem: true, dDelta: -1);
    private static int Tor() => AluOp(tToR: true, dDelta: -1, rDelta: +1);
    private static int Rfrom() => AluOp(0xB, dDelta: +1, rDelta: -1);
    private static int Rget() => AluOp(0xB, tToN: true, dDelta: +1);
    private static int Exit() => AluOp(returnFromR: true, rDelta: -1);

    // ── Literal ───────────────────────────────────────────────────────────────

    [Fact]
    public void Literal_PushesValue() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(42));
        t.Run(1);
        Assert.Equal(42, s.T);
    }

    [Fact]
    public void Literal_ZeroExtends() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x7FFF));
        t.Run(1);
        Assert.Equal(0x7FFF, s.T);
    }

    [Fact]
    public void Literal_TwoPushes_StacksCorrectly() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(10));
        W(m, 1, Lit(20));
        t.Run(2);
        Assert.Equal(20, s.T);
        Assert.Equal(10, s.N);
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Jump_ChangesPC() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Jump(5));
        W(m, 5, Lit(99));
        t.Run(2); // tick1=Jump, tick2=Lit at word5
        Assert.Equal(99, s.T);
    }

    // ── CondJump ──────────────────────────────────────────────────────────────

    [Fact]
    public void CondJump_TakenWhenTIsZero() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0));
        W(m, 1, CondJump(5));
        W(m, 5, Lit(77));
        t.Run(3);
        Assert.Equal(77, s.T);
    }

    [Fact]
    public void CondJump_NotTakenWhenTIsNonZero() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(3));
        W(m, 1, CondJump(10));
        W(m, 2, Lit(55));
        t.Run(3);
        Assert.Equal(55, s.T);
    }

    [Fact]
    public void CondJump_AlwaysPopsT() {
        // T=3 is non-zero → not taken. CondJump pops T=3 → T returns to N=7.
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(7));
        W(m, 1, Lit(3));
        W(m, 2, CondJump(10));
        W(m, 3, Nop());
        t.Run(4);
        Assert.Equal(7, s.T);
    }

    // ── Call / EXIT ───────────────────────────────────────────────────────────

    [Fact]
    public void Call_PushesReturnWordAddressToR() {
        // Call at word 0, return word addr = (0/2)+1 = 1.
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Call(3));
        W(m, 3, Nop());
        t.Run(2);
        Assert.Equal(1, s.R);
    }

    [Fact]
    public void Exit_ReturnsToReturnAddress() {
        // word 0: lit 99; word 1: call(4); word 2: lit 42; word 4: EXIT
        // EXIT: PC = R*2 = 2*2 = 4 → word 2; lit 42 pushes onto stack
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(99));
        W(m, 1, Call(4));
        W(m, 2, Lit(42));
        W(m, 4, Exit());
        t.Run(4); // lit99, call4, EXIT, lit42
        Assert.Equal(42, s.T);
    }

    // ── ALU: stack ops ────────────────────────────────────────────────────────

    [Fact]
    public void Dup_CopiesTtoN() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(5));
        W(m, 1, Dup());
        t.Run(2);
        Assert.Equal(5, s.T);
        Assert.Equal(5, s.N);
    }

    [Fact]
    public void Drop_RemovesT() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(1));
        W(m, 1, Lit(2));
        W(m, 2, Drop());
        t.Run(3);
        Assert.Equal(1, s.T);
    }

    [Fact]
    public void Swap_ExchangesTandN() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(10));
        W(m, 1, Lit(20));
        W(m, 2, Swap());
        t.Run(3);
        Assert.Equal(10, s.T);
        Assert.Equal(20, s.N);
    }

    [Fact]
    public void Over_CopiesNtoTop() {
        // Stack: bottom=3, top=7. OVER → stack: 3,7,3 (T=3, N=7).
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(3));
        W(m, 1, Lit(7));
        W(m, 2, Over());
        t.Run(3);
        Assert.Equal(3, s.T);
        Assert.Equal(7, s.N);
    }

    // ── ALU: arithmetic ───────────────────────────────────────────────────────

    [Fact]
    public void Add_SumsTopTwo() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(10));
        W(m, 1, Lit(32));
        W(m, 2, Add());
        t.Run(3);
        Assert.Equal(42, s.T);
    }

    [Fact]
    public void Add_Wraps16Bit() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x7FFF));
        W(m, 1, Lit(1));
        W(m, 2, Add());
        t.Run(3);
        Assert.Equal(0x8000, s.T);
    }

    [Fact]
    public void Dec_SubtractsOne() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(5));
        W(m, 1, Dec());
        t.Run(2);
        Assert.Equal(4, s.T);
    }

    // ── ALU: bitwise ─────────────────────────────────────────────────────────

    [Fact]
    public void And_MasksTopTwo() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x0F0F));
        W(m, 1, Lit(0x00FF));
        W(m, 2, And());
        t.Run(3);
        Assert.Equal(0x000F, s.T);
    }

    [Fact]
    public void Or_CombinesTopTwo() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x0F00));
        W(m, 1, Lit(0x000F));
        W(m, 2, Or());
        t.Run(3);
        Assert.Equal(0x0F0F, s.T);
    }

    [Fact]
    public void Xor_XorsTopTwo() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x00FF));
        W(m, 1, Lit(0x0F0F));
        W(m, 2, Xor());
        t.Run(3);
        Assert.Equal(0x0FF0, s.T);
    }

    [Fact]
    public void Invert_FlipsAllBits() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0));
        W(m, 1, Invert());
        t.Run(2);
        Assert.Equal(0xFFFF, s.T);
    }

    // ── ALU: comparisons ─────────────────────────────────────────────────────

    [Fact]
    public void Eq_ReturnsTrueWhenEqual() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(7));
        W(m, 1, Lit(7));
        W(m, 2, Eq());
        t.Run(3);
        Assert.Equal(0xFFFF, s.T);
    }

    [Fact]
    public void Eq_ReturnsFalseWhenNotEqual() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(7));
        W(m, 1, Lit(8));
        W(m, 2, Eq());
        t.Run(3);
        Assert.Equal(0, s.T);
    }

    [Fact]
    public void Lt_TrueWhenNLessThanTSigned() {
        // Push 2 (N), then 5 (T). N < T signed = true.
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(2));
        W(m, 1, Lit(5));
        W(m, 2, Lt());
        t.Run(3);
        Assert.Equal(0xFFFF, s.T);
    }

    [Fact]
    public void Lt_FalseWhenNGreaterThanTSigned() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(5));
        W(m, 1, Lit(2));
        W(m, 2, Lt());
        t.Run(3);
        Assert.Equal(0, s.T);
    }

    [Fact]
    public void Ult_TrueWhenNLessThanTUnsigned() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(0x0001));
        W(m, 1, Lit(0x7FFF));
        W(m, 2, Ult());
        t.Run(3);
        Assert.Equal(0xFFFF, s.T);
    }

    // ── ALU: memory ──────────────────────────────────────────────────────────

    [Fact]
    public void Fetch_ReadsFromMemory() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, J1Tests.DataWord, 0xABCD);
        W(m, 0, Lit(J1Tests.DataWord));
        W(m, 1, Fetch());
        t.Run(2);
        Assert.Equal(0xABCD, s.T);
    }

    [Fact]
    public void Store_WritesToMemory() {
        (SingleCycleTrain t, J1ArchState _, FlatMemory m) = Make();
        W(m, 0, Lit(0x1234));
        W(m, 1, Lit(J1Tests.DataWord));
        W(m, 2, Store());
        t.Run(3);
        Assert.Equal(0x1234, ReadW(m, J1Tests.DataWord));
    }

    // ── ALU: return-stack ops ────────────────────────────────────────────────

    [Fact]
    public void TOR_MovesValueToReturnStack() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(42));
        W(m, 1, Tor());
        t.Run(2);
        Assert.Equal(42, s.R);
    }

    [Fact]
    public void RFROM_MovesFromReturnStackToT() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(99));
        W(m, 1, Tor());
        W(m, 2, Rfrom());
        t.Run(3);
        Assert.Equal(99, s.T);
    }

    [Fact]
    public void RGET_CopiesReturnStackToT() {
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(55));
        W(m, 1, Tor());
        W(m, 2, Rget());
        t.Run(3);
        Assert.Equal(55, s.T);
        Assert.Equal(55, s.R); // R still holds value
    }

    // ── Integration: countdown loop ───────────────────────────────────────────

    [Fact]
    public void CountdownLoop_TerminatesWithZeroOnStack() {
        // word 0: lit 5
        // word 1: DUP         ← loop top
        // word 2: CondJump(5) ← exit to word 5 when T==0 (pops the DUP'd copy)
        // word 3: DEC         ← only reached when T≠0
        // word 4: Jump(1)     ← back to loop top
        // word 5: NOP         ← T should be 0 here
        //
        // Tick count: 1 (lit) + 5*(dup+condjump+dec+jump) + (dup+condjump) + 1(nop) = 24
        (SingleCycleTrain t, J1ArchState s, FlatMemory m) = Make();
        W(m, 0, Lit(5));
        W(m, 1, Dup());
        W(m, 2, CondJump(5));
        W(m, 3, Dec());
        W(m, 4, Jump(1));
        W(m, 5, Nop());
        t.Run(24);
        Assert.Equal(0, s.T);
    }
}