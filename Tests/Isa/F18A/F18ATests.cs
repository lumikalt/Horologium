#region

using F18A;
using F18A.Decode;
using F18A.MultiCore;
using Pipeline;
using RiscV32.Memory;

#endregion

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.Isa.F18A;

/// <summary>
///     F18A unit tests. Memory is word-addressed (4 bytes per word).
///     Encoding note: F18A slots are 5+5+5+3 bits. The 3-bit slot-3 has NO nop opcode —
///     F18AOp.Nop (26) would truncate to 2 = Jump(0) in that slot, corrupting slot-2 too.
///     Tests avoid slot-3 by using the Op1/FP helpers that put Jump(next) in slot-1,
///     so control flow exits before slot-2 or slot-3 are reached.
/// </summary>
public class F18ATests {
    private static (SingleCycleTrain Train, F18AArchState State, FlatMemory Mem) Make(int sizeBytes = 4096) {
        var mem = new FlatMemory(sizeBytes);
        var train = new SingleCycleTrain(new F18AMechanism(), mem);
        return (train, (F18AArchState)train.ArchState, mem);
    }

    // ── Encoding helpers ─────────────────────────────────────────────────────

    // Store an 18-bit word at word address wAddr (byte = wAddr*4).
    private static void W(FlatMemory m, int wAddr, uint word) =>
        m.Write((ulong)(wAddr * 4), word & 0x3FFFFu, 4);

    // Single non-branch, non-@p+ op at wAddr.
    // Produces: slot0=op, slot1=Jump(wAddr+1) so execution continues sequentially.
    // After execution, PC = (wAddr+1)*4.
    private static void Op1(FlatMemory m, int wAddr, byte op) {
        uint next = (uint)(wAddr + 1) & 0x1FFu;
        W(m, wAddr, ((uint)op << 13) | ((uint)F18AOp.Jump << 8) | next);
    }

    // @p+ with literal at wAddr+1. Literal is consumed and T is pushed.
    // slot0=@p+, slot1=Jump(wAddr+2) so the literal word is skipped by the branch.
    // After execution, PC = (wAddr+2)*4.
    private static void Fp(FlatMemory m, int wAddr, uint literal) {
        uint next = (uint)(wAddr + 2) & 0x1FFu;
        W(m, wAddr, ((uint)F18AOp.FetchP << 13) | ((uint)F18AOp.Jump << 8) | next);
        W(m, wAddr + 1, literal);
    }

    // Jump (slot-0), remaining 13 bits = word address
    private static uint MkJump(uint wordAddr) =>
        ((uint)F18AOp.Jump << 13) | (wordAddr & 0x1FFFu);

    // Call (slot-0), remaining 13 bits = word address
    private static uint MkCall(uint wordAddr) =>
        ((uint)F18AOp.Call << 13) | (wordAddr & 0x1FFFu);

    // If (slot-0), remaining 13 bits = word address
    private static uint MkIf(uint wordAddr) =>
        ((uint)F18AOp.If << 13) | (wordAddr & 0x1FFFu);

    // -If (slot-0), remaining 13 bits = word address
    private static uint MkMinusIf(uint wordAddr) =>
        ((uint)F18AOp.MinusIf << 13) | (wordAddr & 0x1FFFu);

    // Next (slot-0), remaining 13 bits = word address
    private static uint MkNext(uint wordAddr) =>
        ((uint)F18AOp.Next << 13) | (wordAddr & 0x1FFFu);

    // Return (slot-0), other slots = nop (5-bit nop won't bleed into slot-3 for Return)
    private static uint MkReturn() =>
        ((uint)F18AOp.Return << 13) | ((uint)F18AOp.Nop << 8) | ((uint)F18AOp.Nop << 3) | (F18AOp.Nop & 0x07u);

    // ── Literal load (@p+) ───────────────────────────────────────────────────

    [Fact]
    public void FetchP_PushesLiteralAndSkipsIt() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x12345);
        t.Run(2);
        Assert.Equal(0x12345u, s.T);
    }

    [Fact]
    public void FetchP_AdvancesPastLiteral() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0xABCD); // words 0,1 — pushes 0xABCD
        Fp(m, 2, 0x1111); // words 2,3 — pushes 0x1111 (on top)
        t.Run(3);
        Assert.Equal(0x1111u, s.T);
        Assert.Equal(0xABCDu, s.S);
    }

    // ── ALU operations ───────────────────────────────────────────────────────

    [Fact]
    public void Dup_DuplicatesT() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 42);
        Op1(m, 2, F18AOp.Dup);
        t.Run(3);
        Assert.Equal(42u, s.T);
        Assert.Equal(42u, s.S);
    }

    [Fact]
    public void Drop_DiscardsT() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 99);
        Fp(m, 2, 77);
        Op1(m, 4, F18AOp.Drop);
        t.Run(4);
        Assert.Equal(99u, s.T);
    }

    [Fact]
    public void Add_SumsTwoValues() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 10);
        Fp(m, 2, 20);
        Op1(m, 4, F18AOp.Add);
        t.Run(4);
        Assert.Equal(30u, s.T);
    }

    [Fact]
    public void And_MasksBits() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x3C3C3u);
        Fp(m, 2, 0x0F0F0u);
        Op1(m, 4, F18AOp.And);
        t.Run(4);
        Assert.Equal(0x0C0C0u, s.T);
    }

    [Fact]
    public void Xor_TogglesExpectedBits() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x3FFFFu);
        Fp(m, 2, 0x0F0F0u);
        Op1(m, 4, F18AOp.Xor);
        t.Run(4);
        Assert.Equal(0x30F0Fu, s.T);
    }

    [Fact]
    public void Not_InvertsAllBits() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0u);
        Op1(m, 2, F18AOp.Not);
        t.Run(3);
        Assert.Equal(0x3FFFFu, s.T);
    }

    [Fact]
    public void Shift2L_ShiftsLeft() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0b101u);
        Op1(m, 2, F18AOp.Shift2L);
        t.Run(3);
        Assert.Equal(0b1010u, s.T);
    }

    [Fact]
    public void Shift2R_SignExtendsNegative() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x20000u); // bit 17 set = negative in 18-bit
        Op1(m, 2, F18AOp.Shift2R);
        t.Run(3);
        // arithmetic right shift: sign bit propagates → 0x30000
        Assert.Equal(0x30000u, s.T);
    }

    [Fact]
    public void Over_CopiesS() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 10);
        Fp(m, 2, 20);
        Op1(m, 4, F18AOp.Over);
        t.Run(4);
        Assert.Equal(10u, s.T); // S was 10, now pushed to top
        Assert.Equal(20u, s.S);
    }

    [Fact]
    public void Add_Wraps18Bits() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x3FFFFu);
        Fp(m, 2, 1u);
        Op1(m, 4, F18AOp.Add);
        t.Run(4);
        Assert.Equal(0u, s.T); // wraps to 0
    }

    // ── A / B register operations ────────────────────────────────────────────

    [Fact]
    public void AStore_SetsA() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x55u);
        Op1(m, 2, F18AOp.AStore);
        t.Run(3);
        Assert.Equal(0x55u, s.A);
    }

    [Fact]
    public void APush_PushesAOntoStack() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 77u);
        Op1(m, 2, F18AOp.AStore); // A = 77
        Op1(m, 3, F18AOp.APush);  // push A
        t.Run(4);
        Assert.Equal(77u, s.T);
    }

    [Fact]
    public void BStore_SetsB() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0xABu);
        Op1(m, 2, F18AOp.BStore);
        t.Run(3);
        Assert.Equal(0xABu, s.B);
    }

    // ── Memory load/store ────────────────────────────────────────────────────

    [Fact]
    public void FetchA_LoadsWordAtA() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        // Set A = 50 (byte addr 200), store value there
        m.Write(200, 0xBEEFu, 4);
        Fp(m, 0, 50u);            // push word-addr 50
        Op1(m, 2, F18AOp.AStore); // A = 50
        Op1(m, 3, F18AOp.FetchA); // @ → push word at A=50
        t.Run(4);
        Assert.Equal(0xBEEFu & 0x3FFFFu, s.T);
    }

    [Fact]
    public void FetchAp_LoadsAndIncrements() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        m.Write(40, 0xAABBu, 4);   // word 10 (byte 40)
        m.Write(44, 0x1234u, 4);   // word 11 (byte 44)
        Fp(m, 0, 10u);             // push 10
        Op1(m, 2, F18AOp.AStore);  // A = 10
        Op1(m, 3, F18AOp.FetchAp); // @+ → push word 10, A=11
        Op1(m, 4, F18AOp.FetchAp); // @+ → push word 11, A=12
        t.Run(5);
        Assert.Equal(0x1234u, s.T);
        Assert.Equal(0xAABBu & 0x3FFFFu, s.S);
        Assert.Equal(12u, s.A);
    }

    [Fact]
    public void StoreA_WritesWordAtA() {
        (SingleCycleTrain t, F18AArchState _, FlatMemory m) = Make();
        Fp(m, 0, 15u);            // push 15 (word-addr)
        Op1(m, 2, F18AOp.AStore); // A = 15
        Fp(m, 3, 0xDEADu);        // push value to store
        Op1(m, 5, F18AOp.StoreA); // ! → store T to A
        t.Run(5);
        Assert.Equal(0xDEADu & 0x3FFFFu, (uint)m.Read(60, 4) & 0x3FFFFu);
    }

    // ── Return stack ─────────────────────────────────────────────────────────

    [Fact]
    public void PushPop_RoundTrips() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0xCAFEu);
        Op1(m, 2, F18AOp.Push); // T → R
        Fp(m, 3, 0u);           // push 0 (dummy on data stack)
        Op1(m, 5, F18AOp.Pop);  // R → T
        t.Run(5);
        Assert.Equal(0xCAFEu, s.T);
    }

    [Fact]
    public void Ex_ExchangesTandR() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 111u);
        Op1(m, 2, F18AOp.Push); // 111 → R
        Fp(m, 3, 222u);         // T = 222
        Op1(m, 5, F18AOp.Ex);   // exchange T ↔ R
        t.Run(4);
        Assert.Equal(111u, s.T); // former R is now T
        Assert.Equal(222u, s.R); // former T is now R
    }

    // ── Control flow ─────────────────────────────────────────────────────────

    [Fact]
    public void Jump_RedirectsPC() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        W(m, 0, MkJump(5)); // jump to word 5
        // words 1,2 should be skipped
        Fp(m, 5, 0xBEEFu); // words 5,6: push 0xBEEF
        t.Run(3);
        Assert.Equal(0xBEEFu, s.T);
    }

    [Fact]
    public void Call_PushesReturnAddr() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        W(m, 0, MkCall(5));    // call word 5; return addr = 1
        Op1(m, 5, F18AOp.Pop); // pop R to T (R holds return addr)
        t.Run(3);
        Assert.Equal(1u, s.T); // return word-address = 1
    }

    [Fact]
    public void Return_JumpsToR() {
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 7u);           // push word-addr 7
        Op1(m, 2, F18AOp.Push); // 7 → R
        W(m, 3, MkReturn());    // ; → PC = R*4 = 28 = word 7
        Fp(m, 7, 0xFACEu);      // words 7,8: push 0xFACE
        t.Run(4);
        Assert.Equal(0xFACEu, s.T);
    }

    [Fact]
    public void If_TakenWhenTZero() {
        // Canonical F18A: 'if' branches to addr when T == 0 (false condition).
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0u);     // words 0,1: T = 0
        W(m, 2, MkIf(6)); // word 2: if T==0 jump to word 6
        Fp(m, 6, 42u);    // words 6,7: push 42
        // 3 ticks: FP(0)→word2, If(6)→word6, FP(6)→T=42
        t.Run(3);
        Assert.Equal(42u, s.T);
    }

    [Fact]
    public void If_FallsThroughWhenTNonZero() {
        // Canonical F18A: 'if' falls through (sequential) when T != 0 (true condition).
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 1u);      // words 0,1: T = 1 (non-zero)
        W(m, 2, MkIf(10)); // word 2: if T==0 jump to 10; T=1 → fall through to word 3
        Fp(m, 3, 55u);     // words 3,4: push 55
        // 3 ticks: FP(0)→word2, If fall-through→word3, FP(3)→T=55
        t.Run(3);
        Assert.Equal(55u, s.T);
    }

    [Fact]
    public void Next_LoopsUntilRZero() {
        // Push count 2 to R. Loop body: push 1, add to T. next decrements R.
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0u);           // words 0,1: T = 0 (accumulator)
        Fp(m, 2, 2u);           // words 2,3: push 2 (loop count)
        Op1(m, 4, F18AOp.Push); // word 4: 2 → R
        Fp(m, 5, 1u);           // words 5,6: push 1 (loop body)
        Op1(m, 7, F18AOp.Add);  // word 7: T = T + 1
        W(m, 8, MkNext(5));     // word 8: next → terminates word; R>0→jump5, R=0→word9
        // Loop runs 3 times (R=2→1→0→exit): T = 0+1+1+1 = 3
        // Tick count: 3 setup + 3 per iteration * 3 = 12
        t.Run(12);
        Assert.Equal(3u, s.T);
    }

    [Fact]
    public void Unext_LoopsCurrentWord() {
        // Push 5 to R; word containing nop nop nop unext loops 5+1 times.
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 5u);           // T = 5 (loop count)
        Op1(m, 2, F18AOp.Push); // 5 → R
        // Word 3: nop nop nop unext (all 3-bit ops: Nop&7=2=Jump? No — use Nop for slots 0-2 which are 5-bit, and Unext=4 for slot-3 which is 3-bit)
        // Slot 0 = Nop(26), Slot 1 = Nop(26), Slot 2 = Nop(26), Slot 3 = Unext(4)
        // No slot-3 bleed because Unext=4 < 8
        W(m, 3, ((uint)F18AOp.Nop << 13) | ((uint)F18AOp.Nop << 8) | ((uint)F18AOp.Nop << 3) | F18AOp.Unext);
        Fp(m, 4, 0xABCu); // words 4,5 — reached after unext exits
        t.Run(20);
        Assert.Equal(0xABCu, s.T);
    }

    // ── Multi-slot word ──────────────────────────────────────────────────────

    [Fact]
    public void MultiSlot_TwoOpsInOneWord() {
        // slot0=FetchP(@p+), slot1=Dup, slot2=Jump(3), slot3-ignored(addr field)
        // But: FetchP in slot0 reads from word pc+1 (literal), then slot1=Dup duplicates it.
        // Then slot2=Jump(3) jumps to word 3.
        // Literal is at word 1. Word 2 = part of address. Word 3 = @p+ of 0x5678.
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        // Word 0: @p+(slot0), Dup(slot1), Jump(slot2) with addr=3 from bits[2:0]
        // slot2=Jump means remaining bits = addr. Bits [2:0] = 3.
        // Encoding: (FetchP<<13)|(Dup<<8)|(Jump<<3)|3
        const uint word0 = ((uint)F18AOp.FetchP << 13) | ((uint)F18AOp.Dup << 8) | ((uint)F18AOp.Jump << 3) | 3u;
        W(m, 0, word0);
        W(m, 1, 0x1234u); // literal for @p+
        // word 2 is the remainder of jump address (bits not reached)
        Fp(m, 3, 0x5678u); // push another value
        t.Run(2);
        Assert.Equal(0x5678u, s.T); // top of stack from second @p+
        Assert.Equal(0x1234u, s.S); // dup'd value from first @p+
    }

    [Fact]
    public void MultiSlot_BranchInSlot0_SkipsRemainingSlots() {
        // Jump in slot-0: remaining 13 bits are address, NOT more opcodes.
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0u);       // T = 0
        W(m, 2, MkJump(6)); // jump to word 6; slots 1-3 are addr field, not executed
        Fp(m, 6, 0xCAFEu);
        t.Run(4);
        Assert.Equal(0xCAFEu, s.T);
    }

    // ── -if (MinusIf) ────────────────────────────────────────────────────────

    [Fact]
    public void MinusIf_TakenWhenTNonnegative() {
        // -if branches to addr when T ≥ 0 (sign bit 17 is clear).
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 5u);          // T = 5 (positive)
        W(m, 2, MkMinusIf(6)); // -if: T≥0 → jump to 6
        Fp(m, 6, 42u);
        t.Run(3);
        Assert.Equal(42u, s.T);
    }

    [Fact]
    public void MinusIf_FallsThroughWhenTNegative() {
        // -if falls through when T < 0 (sign bit 17 is set).
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 0x20000u); // T = 0x20000 (bit 17 set = negative in 18-bit)
        W(m, 2, MkMinusIf(10));
        Fp(m, 3, 55u);
        t.Run(3);
        Assert.Equal(55u, s.T);
    }

    // ── +* (MulStep) ─────────────────────────────────────────────────────────

    [Fact]
    public void MulStep_18Steps_ProducesProduct() {
        // 18 iterations of +* implement an 18×18 multiply: S=2, T=0, A=3 → T=0, A=6.
        // Setup (ticks): FP×5=10, AStore=1, Push=1, loop×18=18 — wait, FP uses 1 tick each.
        // Tick count: FP(0)=1, FP(2)=1, FP(4)=1, AStore(6)=1, FP(7)=1, Push(9)=1, loop×18=18 → 24 ticks.
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        Fp(m, 0, 2u);             // push 2 → T=2
        Fp(m, 2, 0u);             // push 0 → T=0, S=2
        Fp(m, 4, 3u);             // push 3 → T=3, S=0, depth=[2]
        Op1(m, 6, F18AOp.AStore); // A=3; pop → T=0, S=2
        Fp(m, 7, 17u);            // push 17 → T=17, S=0, depth=[2]
        Op1(m, 9, F18AOp.Push);   // 17→R; T=0, S=2
        // Word 10: +* Nop Nop Unext — Nop's low 3 bits = 4 = Unext, loops 18 times (R=17→0)
        W(
            m, 10, ((uint)F18AOp.MulStep << 13) | ((uint)F18AOp.Nop << 8) |
                   ((uint)F18AOp.Nop << 3) | F18AOp.Unext
        );
        t.Run(24);
        Assert.Equal(0u, s.T);
        Assert.Equal(6u, s.A);
    }

    // ── tonyrog add.f18: @p @p in the same instruction word ─────────────────

    [Fact]
    public void TonyrogAdd_TwoFetchPInOneWord() {
        // Replicates add.f18: two @p in slots 0 and 1 of the same word read sequential
        // literals (100 and 200), then + sums them. Jump in slot 2 skips the literal words.
        //
        // Word 0: FetchP(s0) FetchP(s1) Jump(s2→3) — bits[2:0]=3 are the 3-bit slot2 address
        // Word 1: literal 100 (consumed by slot0 @p, pOffset=1)
        // Word 2: literal 200 (consumed by slot1 @p, pOffset=2)
        // Word 3: Add; T = 200+100 = 300
        (SingleCycleTrain t, F18AArchState s, FlatMemory m) = Make();
        m.Write(
            0, ((uint)F18AOp.FetchP << 13) | ((uint)F18AOp.FetchP << 8) |
               ((uint)F18AOp.Jump << 3) | 3u, 4
        );
        m.Write(4, 100u, 4);
        m.Write(8, 200u, 4);
        Op1(m, 3, F18AOp.Add);
        t.Run(2);
        Assert.Equal(300u, s.T);
    }

    // ── Grid / multi-core ────────────────────────────────────────────────────

    [Fact]
    public void Grid_CanBeConstructed() {
        var grid = new F18AGrid(2, 2);
        Assert.Equal(2, grid.Rows);
        Assert.Equal(2, grid.Cols);
        Assert.NotNull(grid.NodeAt(0, 0));
        Assert.NotNull(grid.NodeAt(1, 1));
    }

    [Fact]
    public void Grid_EastWestArborsWired() {
        var grid = new F18AGrid(1, 2);
        F18ANode left = grid.NodeAt(0, 0);
        F18ANode right = grid.NodeAt(0, 1);
        Assert.NotNull(left.EastArbor);
        Assert.Same(left.EastArbor, right.WestArbor);
    }

    [Fact]
    public void Grid_NorthSouthArborsWired() {
        var grid = new F18AGrid(2, 1);
        F18ANode top = grid.NodeAt(0, 0);
        F18ANode bot = grid.NodeAt(1, 0);
        Assert.NotNull(top.SouthArbor);
        Assert.Same(top.SouthArbor, bot.NorthArbor);
    }

    [Fact]
    public void Grid_StepDoesNotCrashEmptyNodes() {
        var grid = new F18AGrid(2, 2);
        // All nodes start at PC=0; zero memory = Return opcode = self-loop which WillBlock returns false for
        Assert.True(grid.Step() >= 0);
    }
}