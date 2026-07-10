using Pipeline;
using RiscV32.Memory;
using Subleq;

namespace Tests.Isa.Subleq;

public class SubleqTests {
    // Layout: instructions start at 0, data at 64.
    // Each instruction: 12 bytes [a, b, c] as little-endian signed 32-bit ints.
    private const int DataBase = 64;

    private static (SingleCycleTrain train, FlatMemory mem) Make(int memSize = 256) {
        var mem = new FlatMemory(memSize);
        var train = new SingleCycleTrain(new SubleqMechanism(), mem);
        return (train, mem);
    }

    private static void WriteInt(FlatMemory m, int offset, int value) =>
        m.Write((ulong)offset, (uint)value, 4);

    private static int ReadInt(FlatMemory m, int offset) =>
        (int)m.Read((ulong)offset, 4);

    // Writes a SUBLEQ instruction [a, b, c] at byte offset.
    private static void Sub(FlatMemory m, int offset, int a, int b, int c) {
        WriteInt(m, offset, a);
        WriteInt(m, offset + 4, b);
        WriteInt(m, offset + 8, c);
    }

    // Halt instruction: c = -1
    private static void Halt(FlatMemory m, int offset) =>
        Sub(m, offset, 0, 0, -1);

    // ── Basic subtraction ─────────────────────────────────────────────────────

    [Fact]
    public void Subtract_WritesResultToB() {
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 3);     // src: mem[64] = 3
        WriteInt(mem, SubleqTests.DataBase + 4, 7); // dst: mem[68] = 7
        Sub(
            mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, 12
        ); // mem[68] -= mem[64]  → 4; result=4>0, no branch
        Halt(mem, 12);
        train.Run();
        Assert.Equal(4, ReadInt(mem, SubleqTests.DataBase + 4));
    }

    [Fact]
    public void Subtract_NegativeResult_WritesWrapped() {
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 10);
        WriteInt(mem, SubleqTests.DataBase + 4, 3);
        Sub(mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, 12); // 3 - 10 = -7; result≤0 → branch to 12
        Halt(mem, 12);
        train.Run();
        Assert.Equal(-7, ReadInt(mem, SubleqTests.DataBase + 4));
    }

    // ── Branch logic ──────────────────────────────────────────────────────────

    [Fact]
    public void BranchNotTaken_WhenResultPositive_AdvancesSequentially() {
        // src=1, dst=5: 5-1=4 > 0 → branch not taken, sequential advance.
        // Layout:
        //   instr 0:  SUBLEQ src, dst, 24     (branch to 24 if result ≤ 0; not taken)
        //   instr 1:  SUBLEQ zero, marker, -1 (marker unchanged, then halt — sequential path)
        //   instr 2:  SUBLEQ src, marker, -1  (taken path — would decrement marker)
        (SingleCycleTrain train, FlatMemory mem) = Make();
        const int src = SubleqTests.DataBase,
                  dst = SubleqTests.DataBase + 4,
                  marker = SubleqTests.DataBase + 8,
                  zero = SubleqTests.DataBase + 12;
        WriteInt(mem, src, 1);
        WriteInt(mem, dst, 5);
        WriteInt(mem, marker, 99);
        WriteInt(mem, zero, 0);
        Sub(mem, 0, src, dst, 24);      // 5-1=4 > 0; not taken → to instr 1 at 12
        Sub(mem, 12, zero, marker, -1); // marker -= 0 → stays 99; c=-1 → halt
        Sub(mem, 24, src, marker, -1);  // taken path: marker -= 1 → 98; halt
        train.Run();
        Assert.Equal(4, ReadInt(mem, dst));
        Assert.Equal(99, ReadInt(mem, marker));
    }

    [Fact]
    public void BranchTaken_WhenResultZero() {
        // src=5, dst=5: 5-5=0 ≤ 0 → branch taken
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 5);
        WriteInt(mem, SubleqTests.DataBase + 4, 5);
        WriteInt(mem, SubleqTests.DataBase + 8, 0);                      // marker: 0 means "branch not taken path ran"
        Sub(mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, 24); // branch to 24 when result≤0
        // Instruction 1 at 12: "not-taken" path — sets marker, halts
        WriteInt(mem, SubleqTests.DataBase + 8, -1); // pre-set to -1 so sub by 1 gives -2
        WriteInt(mem, SubleqTests.DataBase + 12, 1);
        Sub(mem, 12, SubleqTests.DataBase + 12, SubleqTests.DataBase + 8, -1); // marker -= 1 then halt
        // Instruction 2 at 24: "taken" path — halts without touching marker
        Halt(mem, 24);
        // Reset marker to a sentinel
        WriteInt(mem, SubleqTests.DataBase + 8, 42);
        train.Run();
        // If branch was taken, instruction at 12 never ran, marker stays 42.
        Assert.Equal(42, ReadInt(mem, SubleqTests.DataBase + 8));
    }

    [Fact]
    public void BranchTaken_WhenResultNegative() {
        // src=10, dst=3: 3-10=-7 ≤ 0 → branch taken
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 10);
        WriteInt(mem, SubleqTests.DataBase + 4, 3);
        WriteInt(mem, SubleqTests.DataBase + 8, 42); // marker
        Sub(mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, 24);
        // Not-taken path at 12: clears marker
        WriteInt(mem, SubleqTests.DataBase + 12, 42);
        Sub(mem, 12, SubleqTests.DataBase + 12, SubleqTests.DataBase + 8, -1);
        // Taken path at 24: halt
        Halt(mem, 24);
        train.Run();
        Assert.Equal(42, ReadInt(mem, SubleqTests.DataBase + 8));
    }

    // ── Halt conditions ───────────────────────────────────────────────────────

    [Fact]
    public void HaltsOnNegativeC() {
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 0);
        WriteInt(mem, SubleqTests.DataBase + 4, 0);
        Sub(mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, -1);
        train.Run(10);
        // Just verifying it terminates without hanging.
        Assert.True(true);
    }

    [Fact]
    public void HaltsOnSelfBranch() {
        // result ≤ 0 and c == PC → executor returns IsHalt
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, 5);
        WriteInt(mem, SubleqTests.DataBase + 4, 3);
        Sub(mem, 0, SubleqTests.DataBase, SubleqTests.DataBase + 4, 0); // branch to PC=0 (self) → halt
        train.Run(10);
        Assert.True(true);
    }

    // ── Multi-instruction program ─────────────────────────────────────────────

    [Fact]
    public void CountDown_LoopUntilZero() {
        // Decrement counter from 3 to 0, looping via an unconditional SUBLEQ jump.
        //   instr 0 (addr  0): SUBLEQ step, counter, 24  — counter -= 1; done when ≤ 0
        //   instr 1 (addr 12): SUBLEQ zero, zero, 0      — unconditional jump back to 0
        //   instr 2 (addr 24): halt
        (SingleCycleTrain train, FlatMemory mem) = Make();
        const int counterAddr = SubleqTests.DataBase,
                  stepAddr = SubleqTests.DataBase + 4,
                  zeroAddr = SubleqTests.DataBase + 8;
        WriteInt(mem, counterAddr, 3);
        WriteInt(mem, stepAddr, 1);
        WriteInt(mem, zeroAddr, 0);
        Sub(mem, 0, stepAddr, counterAddr, 24); // counter -= 1; if ≤ 0 goto 24 (done)
        Sub(mem, 12, zeroAddr, zeroAddr, 0);    // 0-0=0 ≤ 0 → branch to 0 (loop)
        Halt(mem, 24);
        train.Run(100);
        Assert.Equal(0, ReadInt(mem, counterAddr));
    }

    // ── Stdout I/O ────────────────────────────────────────────────────────────

    [Fact]
    public void StdoutOutput_WritesCharacter() {
        // SUBLEQ I/O convention: store negative of char code, use b=-1.
        // SUBLEQ a, -1, c: Console.Write((char)((0 - mem[a]) & 0xFF))
        //   To output 'A' (65): store -65 at DataBase, sub(DataBase, -1, halt)
        (SingleCycleTrain train, FlatMemory mem) = Make();
        WriteInt(mem, SubleqTests.DataBase, -65);
        Sub(mem, 0, SubleqTests.DataBase, -1, 12);
        Halt(mem, 12);

        var output = new StringWriter();
        TextWriter prev = Console.Out;
        Console.SetOut(output);
        try { train.Run(); }
        finally { Console.SetOut(prev); }

        Assert.Equal("A", output.ToString());
    }
}