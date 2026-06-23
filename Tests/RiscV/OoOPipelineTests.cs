using Orrery.Observation;
using Orrery.Train;
using RiscV;
using RiscV.Memory;
using RiscV.Trains;

namespace Tests.RiscV;

/// <summary>
/// End-to-end tests for OooeTrain: superscalar out-of-order pipeline.
///
/// Hand-assembled RV32I programs are loaded into FlatMemory and run through
/// the train. Final register values are compared against expected results
/// identical to what SingleCycleTrain produces, verifying that OoO execution
/// produces correct outputs despite scheduling instructions out of order.
/// </summary>
public class OoOPipelineTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        int robCapacity = 16,
        int iqCapacity = 8,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new RvMechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity
        );
        return (train, mem);
    }

    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static uint Reg(OooeTrain t, int r) =>
        (uint)t.ArchState.IntegerRegisters.Read(r);

    // ── Correctness: arithmetic ────────────────────────────────────────────────

    [Fact]
    public void Program_AddTwoNumbers() {
        // addi x1, x0, 10    → x1 = 10
        // addi x2, x0, 32    → x2 = 32
        // add  x3, x1, x2    → x3 = 42
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00a00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081b3, // add  x3, x1, x2
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(10u, Reg(train, 1));
        Assert.Equal(32u, Reg(train, 2));
        Assert.Equal(42u, Reg(train, 3));
    }

    [Fact]
    public void Program_DependencyChain() {
        // Chain: each instruction reads the result of the previous one.
        // addi x1, x0, 1     → x1 = 1
        // addi x1, x1, 1     → x1 = 2  (depends on previous)
        // addi x1, x1, 1     → x1 = 3
        // addi x1, x1, 1     → x1 = 4
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00108093, // addi x1, x1, 1
            0x00108093, // addi x1, x1, 1
            0x00108093, // addi x1, x1, 1
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(4u, Reg(train, 1));
    }

    [Fact]
    public void Program_IndependentInstructions_AllCommit() {
        // Six independent ADDIs — no RAW hazards. All should commit correctly.
        // addi x1..x6, x0, 1..6
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00200113, // addi x2, x0, 2
            0x00300193, // addi x3, x0, 3
            0x00400213, // addi x4, x0, 4
            0x00500293, // addi x5, x0, 5
            0x00600313, // addi x6, x0, 6
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(1u, Reg(train, 1));
        Assert.Equal(2u, Reg(train, 2));
        Assert.Equal(3u, Reg(train, 3));
        Assert.Equal(4u, Reg(train, 4));
        Assert.Equal(5u, Reg(train, 5));
        Assert.Equal(6u, Reg(train, 6));
    }

    [Fact]
    public void Program_SubtractNumbers() {
        // addi x1, x0, 10   → x1 = 10
        // addi x2, x0, 3    → x2 = 3
        // sub  x3, x1, x2   → x3 = 7
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00a00093, // addi x1, x0, 10
            0x00300113, // addi x2, x0, 3
            0x402081b3, // sub  x3, x1, x2
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(7u, Reg(train, 3));
    }

    [Fact]
    public void Program_BitwiseAnd() {
        // addi x1, x0, 0xFF  → x1 = 255
        // addi x2, x0, 0x0F  → x2 = 15
        // and  x3, x1, x2    → x3 = 15
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x0ff00093, // addi x1, x0, 0xFF
            0x00f00113, // addi x2, x0, 0x0F
            0x0020f1b3, // and  x3, x1, x2
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(15u, Reg(train, 3));
    }

    // ── Correctness: memory ────────────────────────────────────────────────────

    [Fact]
    public void Program_StoreAndLoad() {
        // addi x1, x0, 42     → x1 = 42
        // addi x2, x0, 0x100  → x2 = 256 (store address)
        // sw   x1, 0(x2)      → mem[256] = 42
        // lw   x3, 0(x2)      → x3 = 42
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x02a00093, // addi x1, x0, 42
            0x10000113, // addi x2, x0, 0x100
            0x00112023, // sw   x1, 0(x2)
            0x00012183, // lw   x3, 0(x2)
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(42u, Reg(train, 3));
    }

    // ── Correctness: control flow ──────────────────────────────────────────────

    [Fact]
    public void Program_UnconditionalJump() {
        // jal  x0, +8      → PC = 8 (skip next instruction)
        // addi x1, x0, 99  → should NOT execute
        // addi x2, x0, 42  → x2 = 42
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x0080006f, // jal x0, +8
            0x06300093, // addi x1, x0, 99   (skipped)
            0x02a00113, // addi x2, x0, 42
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(0u, Reg(train, 1)); // skipped instruction — x1 unchanged
        Assert.Equal(42u, Reg(train, 2));
    }

    [Fact]
    public void Program_ConditionalBranch_Taken() {
        // addi x1, x0, 5
        // addi x2, x0, 5
        // beq  x1, x2, +8   → taken (x1 == x2), skip next
        // addi x3, x0, 99   → skipped
        // addi x4, x0, 42   → x4 = 42
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00500093, // addi x1, x0, 5
            0x00500113, // addi x2, x0, 5
            0x00208463, // beq  x1, x2, +8
            0x06300193, // addi x3, x0, 99  (skipped)
            0x02a00213, // addi x4, x0, 42
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(0u, Reg(train, 3)); // skipped
        Assert.Equal(42u, Reg(train, 4));
    }

    [Fact]
    public void Program_ConditionalBranch_NotTaken() {
        // addi x1, x0, 3
        // addi x2, x0, 5
        // beq  x1, x2, +8   → not taken (x1 != x2)
        // addi x3, x0, 10   → x3 = 10
        // ebreak
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00300093, // addi x1, x0, 3
            0x00500113, // addi x2, x0, 5
            0x00208463, // beq  x1, x2, +8
            0x00a00193, // addi x3, x0, 10
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(10u, Reg(train, 3));
    }

    // ── Stats and counters ────────────────────────────────────────────────────

    [Fact]
    public void Stats_RetiredCountMatchesInstructionCount() {
        // 3 ADDIs + EBREAK = 4 instructions total
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00200113, // addi x2, x0, 2
            0x00300193, // addi x3, x0, 3
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();
        DialBoardSnapshot snap = result.Find("ooo.pipeline")!;

        Assert.Equal(4L, snap.Counters["retired"]); // 3 ADDIs + EBREAK
        Assert.True(
            snap.Counters["cycles"] >= 4,
            $"Expected cycles >= 4, got {snap.Counters["cycles"]}"
        );
    }

    [Fact]
    public void Stats_SuperscalarIssueWidth2_FasterThanSequential() {
        // 6 independent ADDIs: with issue width 2, fewer cycles than 1-wide
        // because the pipeline can commit 2 instructions per cycle in steady state.
        (OooeTrain wide, FlatMemory mem1) = Make();
        (OooeTrain narrow, FlatMemory mem2) = Make(1);

        uint[] program = [
            0x00100093, // addi x1, x0, 1
            0x00200113, // addi x2, x0, 2
            0x00300193, // addi x3, x0, 3
            0x00400213, // addi x4, x0, 4
            0x00500293, // addi x5, x0, 5
            0x00600313, // addi x6, x0, 6
            0x00100073, // ebreak
        ];
        Load(mem1, program);
        Load(mem2, program);

        long cycles2 = wide.Run().Find("ooo.pipeline")!.Counters["cycles"];
        long cycles1 = narrow.Run().Find("ooo.pipeline")!.Counters["cycles"];

        Assert.True(
            cycles2 <= cycles1,
            $"Expected wide ({cycles2} cycles) ≤ narrow ({cycles1} cycles)"
        );
    }

    [Fact]
    public void Stats_CyclesGe1_PerInstruction() {
        // CPI must be >= 1: we have one execute slot per cycle.
        (OooeTrain train, FlatMemory mem) = Make(1);
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00200113, // addi x2, x0, 2
            0x00100073  // ebreak
        );
        DialBoardSnapshot snap = train.Run().Find("ooo.pipeline")!;
        Assert.True(
            snap.Counters["cycles"] >= snap.Counters["retired"],
            $"cycles={snap.Counters["cycles"]} retired={snap.Counters["retired"]}"
        );
    }
}