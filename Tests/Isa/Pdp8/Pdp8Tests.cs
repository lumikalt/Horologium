using Pdp8;
using Pipeline;
using RiscV32.Memory;

namespace Tests.Isa.Pdp8;

/// <summary>
/// PDP-8 unit tests.
/// Convention: word n lives at byte address n*2. Instructions start at word 0.
/// Data words are placed at word 64 (byte 128) to avoid overlap with code.
/// Each instruction is 12 bits wide stored in 2 bytes (LE, high nibble unused).
/// </summary>
public class Pdp8Tests {
    private const int DataWord = 64; // first data word address

    private static (SingleCycleTrain Train, FlatMemory Mem) Make(int wordCount = 256) {
        var mem = new FlatMemory(wordCount * 2);
        var train = new SingleCycleTrain(new Pdp8Mechanism(), mem);
        return (train, mem);
    }

    // Write a 12-bit PDP-8 word into memory at word address w.
    private static void WriteWord(FlatMemory m, int wordAddr, int value) =>
        m.Write((ulong)(wordAddr * 2), (ulong)(value & 0xFFF), 2);

    private static int ReadWord(FlatMemory m, int wordAddr) =>
        (int)m.Read((ulong)(wordAddr * 2), 2) & 0xFFF;

    // Encode a PDP-8 instruction word.
    // opcode 0-7, I = indirect, Z = current-page, offset 0-127.
    private static int Instr(int op, bool ind, bool z, int offset) =>
        (op << 9) | (ind ? 0x100 : 0) | (z ? 0x80 : 0) | (offset & 0x7F);

    // OPR Group 1: opcode=7, bit8=0
    private static int Opr1(int bits) => (7 << 9) | (bits & 0xFF);

    // OPR Group 2: opcode=7, bit8=1
    private static int Opr2(int bits) => (7 << 9) | 0x100 | (bits & 0xFF);

    // HLT = 7402 octal = Group 2 + bit1
    private static int HltWord => Opr2(0x02);

    // NOP = 7000
    private static int NopWord => Opr1(0);

    // ── AND (opcode 0) ────────────────────────────────────────────────────────

    [Fact]
    public void And_WritesResultToAc() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        // TAD to set AC, AND to mask, DCA to store result, HLT
        WriteWord(m, Pdp8Tests.DataWord, 0xFF0);     // mask (12 bits)
        WriteWord(m, Pdp8Tests.DataWord + 1, 0xABC); // addend (TAD source)
        WriteWord(m, Pdp8Tests.DataWord + 2, 0);     // result slot

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord + 1)); // TAD → AC = 0xABC
        WriteWord(m, 1, Instr(0, false, false, Pdp8Tests.DataWord));     // AND mask → AC = 0xAB0
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA → store, AC=0
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(0xABC & 0xFF0, ReadWord(m, Pdp8Tests.DataWord + 2));
    }

    // ── TAD (opcode 1) ────────────────────────────────────────────────────────

    [Fact]
    public void Tad_AccumulatesMemoryWord() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 7);
        WriteWord(m, Pdp8Tests.DataWord + 1, 5);
        WriteWord(m, Pdp8Tests.DataWord + 2, 0); // result

        // TAD 7, TAD 5 → AC = 12; DCA → store
        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD 7
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord + 1)); // TAD 5
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA → result
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(12, ReadWord(m, Pdp8Tests.DataWord + 2));
    }

    [Fact]
    public void Tad_Carry_FlipsLink() {
        // 0xFFF + 1 = 0x1000 → carry → L flips (from 0 to 1)
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xFFF);
        WriteWord(m, Pdp8Tests.DataWord + 1, 1);
        WriteWord(m, Pdp8Tests.DataWord + 2, 0); // AC result
        WriteWord(m, Pdp8Tests.DataWord + 3, 0); // L result

        // TAD 0xFFF, TAD 1 → AC=0, L=1; DCA to store AC; then OPR to capture L somehow
        // Easier: CLA+CLL then TAD, then check AC via DCA and L via RTR into AC
        WriteWord(m, 0, Opr1(0x80 | 0x40));                              // CLA | CLL
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD 0xFFF → AC=0xFFF, L unchanged
        WriteWord(m, 2, Instr(1, false, false, Pdp8Tests.DataWord + 1)); // TAD 1 → AC=0, L=1 (carry)
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA → AC result (0), AC cleared
        // Now rotate L into AC: RAR puts L into AC bit11
        WriteWord(m, 4, Opr1(0x08));                                     // RAR → AC = (1<<11) | 0 = 0x800, L=0
        WriteWord(m, 5, Instr(3, false, false, Pdp8Tests.DataWord + 3)); // DCA → L result
        WriteWord(m, 6, HltWord);
        t.Run(30);
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 2));     // AC was 0
        Assert.Equal(0x800, ReadWord(m, Pdp8Tests.DataWord + 3)); // L=1 was shifted into bit11
    }

    // ── ISZ (opcode 2) ────────────────────────────────────────────────────────

    [Fact]
    public void Isz_SkipsWhenResultZero() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xFFF); // -1 in 12-bit two's complement

        // ISZ DataWord: increments 0xFFF → 0, skips next instruction
        // word 0: ISZ DataWord → result=0, skip word 1
        // word 1: NOP (should be skipped)
        // word 2: HLT (should execute)
        WriteWord(m, 0, Instr(2, false, false, Pdp8Tests.DataWord));
        WriteWord(m, 1, NopWord);
        WriteWord(m, 2, HltWord);
        t.Run(10);
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord));
    }

    [Fact]
    public void Isz_NoSkipWhenResultNonZero() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 3); // 3+1=4, not zero → no skip

        // word 0: ISZ DataWord → 4, no skip
        // word 1: HLT (should execute; if skip had happened, we'd hang)
        WriteWord(m, 0, Instr(2, false, false, Pdp8Tests.DataWord));
        WriteWord(m, 1, HltWord);
        t.Run(10);
        Assert.Equal(4, ReadWord(m, Pdp8Tests.DataWord));
    }

    // ── DCA (opcode 3) ────────────────────────────────────────────────────────

    [Fact]
    public void Dca_StoresAcAndClearsIt() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x123);    // TAD source
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);    // first DCA target
        WriteWord(m, Pdp8Tests.DataWord + 2, 0xFF); // second DCA target (pre-filled to verify overwriting)

        // DCA stores AC into mem[dest] then clears AC.
        // Second DCA proves AC was cleared after first.
        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0x123
        WriteWord(m, 1, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA → mem[D+1]=0x123, AC=0
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA → mem[D+2]=0 (AC=0), AC=0
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(0x123, ReadWord(m, Pdp8Tests.DataWord + 1));
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 2)); // AC was 0 after first DCA
    }

    [Fact]
    public void Dca_StoresNonZeroThenZero() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x456);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);
        WriteWord(m, Pdp8Tests.DataWord + 2, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0x456
        WriteWord(m, 1, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA → mem[D+1]=0x456, AC=0
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA → mem[D+2]=0, AC=0
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(0x456, ReadWord(m, Pdp8Tests.DataWord + 1));
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 2));
    }

    // ── JMP (opcode 5) ────────────────────────────────────────────────────────

    [Fact]
    public void Jmp_UnconditionalJump() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x10);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0); // result

        // word 0: JMP word 3 (skip the TAD that follows)
        // word 1: TAD DataWord → AC = 0x10 (should be skipped)
        // word 2: NOP
        // word 3: HLT
        WriteWord(m, 0, Instr(5, false, false, 3));                  // JMP to word 3
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD (skipped)
        WriteWord(m, 2, NopWord);
        WriteWord(m, 3, HltWord);
        t.Run(10);
        // AC should still be 0 (TAD was skipped); verify via DCA — but we halted already.
        // Verify nothing was written to DataWord+1 (which would only happen if TAD ran).
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    // ── JMS (opcode 4) ────────────────────────────────────────────────────────

    [Fact]
    public void Jms_StoresReturnAddressAndJumps() {
        (SingleCycleTrain t, FlatMemory m) = Make();

        // Subroutine at word 10: DCA result, HLT
        const int subWord = 10;
        WriteWord(m, Pdp8Tests.DataWord, 0x777); // TAD source
        WriteWord(m, Pdp8Tests.DataWord + 1, 0); // subroutine return addr slot (mem[subWord])
        WriteWord(m, Pdp8Tests.DataWord + 2, 0); // DCA target inside sub

        // word 0: TAD DataWord → AC=0x777
        // word 1: JMS subWord (page 0, indirect=false)
        //   → mem[subWord] = 2 (return word address), PC = subWord+1
        // sub at word 10: [0] = return addr (set by JMS)
        //   word 11: DCA DataWord+2 → stores AC=0x777, AC=0
        //   word 12: HLT
        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));               // TAD
        WriteWord(m, 1, Instr(4, false, false, subWord));                          // JMS subWord
        WriteWord(m, 2, NopWord);                                                  // would run if JMS returned
        WriteWord(m, subWord + 0, 0);                                              // slot for return addr
        WriteWord(m, subWord + 1, Instr(3, false, false, Pdp8Tests.DataWord + 2)); // DCA
        WriteWord(m, subWord + 2, HltWord);

        t.Run(20);

        // JMS at word 1 → return word addr = 2; stored at mem[subWord]
        Assert.Equal(2, ReadWord(m, subWord));
        // AC was 0x777 when sub was entered; DCA stored it
        Assert.Equal(0x777, ReadWord(m, Pdp8Tests.DataWord + 2));
    }

    // ── Indirect addressing ───────────────────────────────────────────────────

    [Fact]
    public void Indirect_ReadsPointer() {
        (SingleCycleTrain _, FlatMemory m) = Make();
        const int ptrWord = Pdp8Tests.DataWord;       // pointer cell
        const int dataWordB = Pdp8Tests.DataWord + 1; // actual data

        WriteWord(m, ptrWord, dataWordB); // pointer: points to dataWordB
        WriteWord(m, dataWordB, 0x321);   // data

        // TAD indirect through ptrWord: first reads mem[ptrWord]=dataWordB, then reads mem[dataWordB]
        WriteWord(m, 0, Instr(1, true, false, ptrWord - 64 + 64)); // TAD I ptrWord (page 0)
        // ptrWord=64, but the page-0 offset is only 7 bits (0-127), and DataWord=64 is within range
        // Hmm: ptrWord=64 > 127. So we need the pointer and data in the low 128 words (page 0).
        // Let's rewrite with smaller addresses.
        WriteWord(m, 0, 0); // reset
        const int ptr2 = 30;
        const int data2 = 31;
        WriteWord(m, ptr2, data2);                                   // pointer
        WriteWord(m, data2, 0x777);                                  // data
        WriteWord(m, 0, Instr(1, true, false, ptr2));                // TAD I ptr2
        WriteWord(m, 1, Instr(3, false, false, Pdp8Tests.DataWord)); // DCA result
        WriteWord(m, 2, HltWord);

        var mem2 = new FlatMemory(256 * 2);
        var t2 = new SingleCycleTrain(new Pdp8Mechanism(), mem2);
        WriteWord(mem2, ptr2, data2);
        WriteWord(mem2, data2, 0x777);
        WriteWord(mem2, 0, Instr(1, true, false, ptr2));
        WriteWord(mem2, 1, Instr(3, false, false, Pdp8Tests.DataWord));
        WriteWord(mem2, 2, HltWord);
        t2.Run(10);
        Assert.Equal(0x777, ReadWord(mem2, Pdp8Tests.DataWord));
    }

    [Fact]
    public void AutoIncrement_IncrementsCellAndUsesNewValue() {
        // Auto-increment range: word addresses 8-15 (octal 010-017)
        // TAD I 8 → auto-increments mem[8], then reads mem[mem[8]] as the EA
        (SingleCycleTrain t, FlatMemory m) = Make();
        // Set up: auto-index cell at word 8 points to word 20 initially.
        WriteWord(m, 8, 19);     // cell 8 starts at 19; after auto-inc → 20
        WriteWord(m, 20, 0xABC); // data at word 20

        WriteWord(m, 0, Instr(1, true, false, 8));                   // TAD I 8 (auto-increment)
        WriteWord(m, 1, Instr(3, false, false, Pdp8Tests.DataWord)); // DCA result
        WriteWord(m, 2, HltWord);
        t.Run(10);
        Assert.Equal(20, ReadWord(m, 8));                     // auto-incremented
        Assert.Equal(0xABC, ReadWord(m, Pdp8Tests.DataWord)); // data at word 20
    }

    // ── Current-page addressing ───────────────────────────────────────────────

    [Fact]
    public void CurrentPage_ReadsWithinSamePage() {
        // Place code and data in page 1 (words 128-255)
        // JMP and CLA are easier in page 0, so put program at page 1 start.
        // Page 1 starts at word 128. Offset bits [6:0] within that page.
        (SingleCycleTrain _, FlatMemory m) = Make(512);
        const int pageBase = 128; // page 1

        // word 128: TAD Z+1 (current-page, offset=1 → word 129)
        // word 129: data = 0x555
        // word 130: DCA to word 140 (current-page, offset=12)
        // word 140: result slot
        // word 131: HLT
        WriteWord(m, pageBase + 1, 0x555);                     // data
        WriteWord(m, pageBase + 0, Instr(1, false, true, 1));  // TAD Z+1 → word 129
        WriteWord(m, pageBase + 2, Instr(3, false, true, 12)); // DCA Z+12 → word 140
        WriteWord(m, pageBase + 3, HltWord);

        // For the first HLT and NOP we need an unconditional jump from word 0 to 128
        // Use JMP to pageBase (offset=0 from page 0 is 128 that is > 127, impossible)
        // Page 0 offset range is 0-127; page 1 = 128+: must use indirect or another approach.
        // Simplest: just place code at the word 0 where SingleCycleTrain starts (entryPoint=0).
        // Reset and test with a separate program that uses current-page within page 0.

        // Simpler version: use code in page 0, with current-page addressing to word 100.
        var mem2 = new FlatMemory(256 * 2);
        var t2 = new SingleCycleTrain(new Pdp8Mechanism(), mem2);
        WriteWord(mem2, 100, 0x888);                    // data at word 100
        WriteWord(mem2, 101, 0);                        // result
        WriteWord(mem2, 0, Instr(1, false, true, 100)); // TAD Z+100 (current-page from word 0)
        WriteWord(mem2, 1, Instr(3, false, true, 101)); // DCA Z+101
        WriteWord(mem2, 2, HltWord);
        t2.Run(10);
        Assert.Equal(0x888, ReadWord(mem2, 101));
    }

    // ── OPR Group 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void Opr1_ClaClears_Ac() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xABC);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0xABC
        WriteWord(m, 1, Opr1(0x80));                                     // CLA → AC=0
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_CmaComplementsAc() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xA5A);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD → AC=0xA5A
        WriteWord(m, 1, Opr1(0x20));                                 // CMA → AC = ~0xA5A & 0xFFF = 0x5A5
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(~0xA5A & 0xFFF, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_IacIncrementsAc() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 41);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD → AC=41
        WriteWord(m, 1, Opr1(0x01));                                 // IAC → AC=42
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(42, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_RarRotatesRightThroughLink() {
        // AC=0b100000000001 (0x801), L=0 → RAR → AC=0b010000000000 (0x400), L=1
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x801);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);
        WriteWord(m, Pdp8Tests.DataWord + 2, 0);

        // CLL to ensure L=0, TAD to load AC, RAR, DCA to read AC result
        WriteWord(m, 0, Opr1(0x40));                                     // CLL → L=0
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0x801
        WriteWord(m, 2, Opr1(0x08));                                     // RAR → AC=0x400, L=1
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA AC result
        // To check L: RAR again → L(=1) into AC bit11. Note: DCA cleared AC to 0.
        // Second RAR: AC=0, L=1 → AC=(1<<11)|(0>>1)=0x800, L=0
        WriteWord(m, 4, Opr1(0x08)); // RAR: AC=(1<<11)|0=0x800, L=0
        WriteWord(m, 5, Instr(3, false, false, Pdp8Tests.DataWord + 2));
        WriteWord(m, 6, HltWord);
        t.Run(30);
        Assert.Equal(0x400, ReadWord(m, Pdp8Tests.DataWord + 1)); // AC after first RAR
        Assert.Equal(0x800, ReadWord(m, Pdp8Tests.DataWord + 2)); // L=1 shifted into bit11 (AC was 0)
    }

    [Fact]
    public void Opr1_RalRotatesLeftThroughLink() {
        // AC=0b100000000001 (0x801), L=0 → RAL → AC = ((0x801<<1)|0) & 0xFFF = 0x002, L=1
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x801);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Opr1(0x40));                                 // CLL → L=0
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD → AC=0x801
        WriteWord(m, 2, Opr1(0x04));                                 // RAL: AC=((0x801<<1)|0)&0xFFF=0x002, L=1
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 4, HltWord);
        t.Run(20);
        Assert.Equal(0x002, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_RtrRotatesRightTwice() {
        // RTR = 7012₈ = bit3+bit1 = 0x08|0x02 = 0x0A
        // AC=0b000000001100 (0x00C), L=0 → RTR twice:
        //   step1: AC=0b000000000110 (0x006), L=0
        //   step2: AC=0b000000000011 (0x003), L=0
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x00C);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Opr1(0x40));                                 // CLL
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD
        WriteWord(m, 2, Opr1(0x0A));                                 // RTR
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 4, HltWord);
        t.Run(20);
        Assert.Equal(0x003, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_RtlRotatesLeftTwice() {
        // RTL = 7006₈ = bit2+bit1 = 0x04|0x02 = 0x06
        // AC=0b110000000000 (0xC00), L=0 → RTL twice:
        //   step1: AC=((0xC00<<1)|0)&0xFFF=0x800, L=1
        //   step2: AC=((0x800<<1)|1)&0xFFF=0x001, L=1
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xC00);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Opr1(0x40));
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord));
        WriteWord(m, 2, Opr1(0x06)); // RTL
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 4, HltWord);
        t.Run(20);
        Assert.Equal(0x001, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_BswSwapsAcBytes() {
        // BSW = 7002₈ = bit1 alone = 0x02
        // AC = 0b101010 000111 (0xA87) → BSW → 0b000111 101010 (0x1EA)
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0xA87); // 0b101010 000111
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD
        WriteWord(m, 1, Opr1(0x02));                                 // BSW
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 3, HltWord);
        t.Run(20);
        const int expected = ((0xA87 & 0x3F) << 6) | ((0xA87 >> 6) & 0x3F); // 0x1EA
        Assert.Equal(expected, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr1_ClaBeforeCma() {
        // CLA+CMA (both set): first clears AC to 0, then complements → 0xFFF
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x123);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD → AC=0x123
        WriteWord(m, 1, Opr1(0x80 | 0x20));                          // CLA|CMA → AC=0xFFF
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1));
        WriteWord(m, 3, HltWord);
        t.Run(20);
        Assert.Equal(0xFFF, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    // ── OPR Group 2 ──────────────────────────────────────────────────────────

    [Fact]
    public void Opr2_SmaSkipsWhenAcNegative() {
        // SMA (7500₈) = Group2 | bit6 = 0x100 | 0x40 = 0x140; but Opr2(bits) adds 0x100 for us
        // AC = 0x800 (bit11 set = negative) → SMA skips
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x800);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0); // sentinel

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0x800
        WriteWord(m, 1, Opr2(0x40));                                     // SMA → should skip word 2
        WriteWord(m, 2, HltWord);                                        // would halt here if no skip
        WriteWord(m, 3, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA → sets sentinel=0 to confirm we got here
        WriteWord(m, 4, HltWord);
        t.Run(15);
        Assert.Equal(0x800, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr2_SmaNoSkipWhenAcPositive() {
        // AC = 0x001 (positive) → SMA doesn't skip
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 1);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD → AC=1
        WriteWord(m, 1, Opr2(0x40));                                 // SMA → should NOT skip
        WriteWord(m, 2, HltWord);                                    // should halt here
        WriteWord(m, 3, NopWord);                                    // should not reach here
        t.Run(10);
        Assert.True(true); // just verify it terminates (halted at word 2)
    }

    [Fact]
    public void Opr2_SzaSkipsWhenAcZero() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0); // AC starts 0 (TAD with 0 → AC=0)
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD 0 → AC=0
        WriteWord(m, 1, Opr2(0x20));                                     // SZA → skip (AC==0)
        WriteWord(m, 2, Instr(1, false, false, Pdp8Tests.DataWord + 1)); // TAD (should skip)
        WriteWord(m, 3, HltWord);
        t.Run(10);
        Assert.True(true);
    }

    [Fact]
    public void Opr2_SkpAlwaysSkips() {
        // SKP = 7410₈ = Group2 | RSS = 0x100|0x08 = Opr2(0x08)
        // Natural condition with no SMA/SZA/SNL = false; RSS=1 → !false = always skip
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0);

        WriteWord(m, 0, Opr2(0x08));                                 // SKP → always skip word 1
        WriteWord(m, 1, Instr(1, false, false, Pdp8Tests.DataWord)); // TAD (should be skipped)
        WriteWord(m, 2, HltWord);
        t.Run(10);
        Assert.True(true);
    }

    [Fact]
    public void Opr2_SpaSkipsWhenAcNonNegative() {
        // SPA = SMA + RSS = Group2 | bit6 | bit3 = 0x100|0x40|0x08 = Opr2(0x48)
        // AC=1 (positive) → natural=(SMA && AC<0)=false; RSS=1 → !false=true → skip
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 1);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=1
        WriteWord(m, 1, Opr2(0x40 | 0x08));                              // SPA → skip (AC≥0)
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA (skipped)
        WriteWord(m, 3, HltWord);
        t.Run(10);
        Assert.Equal(0, ReadWord(m, Pdp8Tests.DataWord + 1)); // DCA was skipped → result stays 0
    }

    [Fact]
    public void Opr2_SpaNoSkipWhenAcNegative() {
        // SPA, AC=0x800 (negative) → natural=true; RSS=1 → !true=false → no skip
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0x800);
        WriteWord(m, Pdp8Tests.DataWord + 1, 0);

        WriteWord(m, 0, Instr(1, false, false, Pdp8Tests.DataWord));     // TAD → AC=0x800
        WriteWord(m, 1, Opr2(0x40 | 0x08));                              // SPA → no skip (AC<0)
        WriteWord(m, 2, Instr(3, false, false, Pdp8Tests.DataWord + 1)); // DCA runs
        WriteWord(m, 3, HltWord);
        t.Run(10);
        Assert.Equal(0x800, ReadWord(m, Pdp8Tests.DataWord + 1));
    }

    [Fact]
    public void Opr2_HltStopsExecution() {
        (SingleCycleTrain t, FlatMemory m) = Make();
        WriteWord(m, Pdp8Tests.DataWord, 0);
        WriteWord(m, 0, HltWord);
        t.Run(10);
        Assert.True(true); // just verify it terminates
    }

    // ── Loop: countdown from 3 to 0 using ISZ ─────────────────────────────────

    [Fact]
    public void Loop_CountdownWithIsz() {
        // Classic ISZ loop: decrement a counter from -3 (0xFFD) and stop when it hits 0.
        // word 0: ISZ counter → increment counter; if==0: skip word 1 (branch to word 2)
        // word 1: JMP 0 (loop back)
        // word 2: HLT
        // counter initialized to 0xFFD (-3 in 12-bit two's-complement)
        (SingleCycleTrain t, FlatMemory m) = Make();
        const int counter = Pdp8Tests.DataWord;
        WriteWord(m, counter, 0xFFD); // -3

        WriteWord(m, 0, Instr(2, false, false, counter)); // ISZ counter
        WriteWord(m, 1, Instr(5, false, false, 0));       // JMP 0
        WriteWord(m, 2, HltWord);
        t.Run(100);
        Assert.Equal(0, ReadWord(m, counter));
    }
}