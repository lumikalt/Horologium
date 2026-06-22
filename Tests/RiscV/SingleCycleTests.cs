using Orrery.Observation;
using Orrery.Train;
using RiscV;
using RiscV.Memory;
using RiscV.Trains;

namespace Tests.RiscV;

/// <summary>
/// End-to-end tests: hand-assembled RV32I programs run through the
/// SingleCycleTrain. These validate the full fetch → decode → execute →
/// writeback loop against known-correct results.
/// </summary>
public class SingleCycleTests {
    private static (SingleCycleTrain train, FlatMemory mem) Make(int memSize = 4096) {
        var mem = new FlatMemory(memSize);
        var train = new SingleCycleTrain(new RvMechanism(), mem);
        return (train, mem);
    }

    // Helper: load a word-array program at address 0
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

    private static uint Reg(SingleCycleTrain t, int r) =>
        (uint)t.ArchState.IntegerRegisters.Read(r);

    // ── Arithmetic ────────────────────────────────────────────────────────────

    [Fact]
    public void Program_AddTwoNumbers() {
        // addi x1, x0, 10    → x1 = 10
        // addi x2, x0, 32    → x2 = 32
        // add  x3, x1, x2    → x3 = 42
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00a00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081b3, // add  x3, x1, x2
            0x00100073
        ); // ebreak
        train.Run();
        Assert.Equal(10u, Reg(train, 1));
        Assert.Equal(32u, Reg(train, 2));
        Assert.Equal(42u, Reg(train, 3));
    }

    [Fact]
    public void Program_SubtractNumbers() {
        // addi x1, x0, 100
        // addi x2, x0, 37
        // sub  x3, x1, x2   → x3 = 63
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x06400093, // addi x1, x0, 100
            0x02500113, // addi x2, x0, 37
            0x402081b3, // sub  x3, x1, x2
            0x00100073
        );
        train.Run();
        Assert.Equal(63u, Reg(train, 3));
    }

    // ── Memory ────────────────────────────────────────────────────────────────

    [Fact]
    public void Program_StoreAndLoad() {
        // addi x1, x0, 0x100   → x1 = 256 (base address)
        // addi x2, x0, 0x5A    → x2 = 0x5A
        // sw   x2, 0(x1)       → mem[256] = 0x5A
        // lw   x3, 0(x1)       → x3 = 0x5A
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0x05A00113, // addi x2, x0, 90
            0x00212023, // sw   x2, 0(x1)
            0x00012183, // lw   x3, 0(x1)
            0x00100073
        );
        train.Run();
        Assert.Equal(90u, Reg(train, 3));
    }

    [Fact]
    public void Program_ByteLoadSignExtend() {
        // addi x1, x0, 0x100
        // addi x2, x0, -1      → x2 = 0xFFFFFFFF
        // sb   x2, 0(x1)       → mem[256] = 0xFF
        // lb   x3, 0(x1)       → x3 = -1 (sign extended)
        // lbu  x4, 0(x1)       → x4 = 255 (zero extended)
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0xFFF00113, // addi x2, x0, -1
            0x00208023, // sb   x2, 0(x1)
            0x00008183, // lb   x3, 0(x1)
            0x0000c203, // lbu  x4, 0(x1)
            0x00100073
        );
        train.Run();
        Assert.Equal(0xFFFFFFFFu, Reg(train, 3)); // sign extended
        Assert.Equal(0xFFu, Reg(train, 4));       // zero extended
    }

    // ── Control flow ──────────────────────────────────────────────────────────

    [Fact]
    public void Program_ConditionalBranch_Taken() {
        // addi x1, x0, 5
        // addi x2, x0, 5
        // beq  x1, x2, +8    → skip next instruction
        // addi x3, x0, 99    → should be skipped
        // addi x3, x0, 42    → x3 = 42
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00500093, // addi x1, x0, 5
            0x00500113, // addi x2, x0, 5
            0x00208463, // beq  x1, x2, +8
            0x06300193, // addi x3, x0, 99  ← skipped
            0x02A00193, // addi x3, x0, 42
            0x00100073
        );
        train.Run();
        Assert.Equal(42u, Reg(train, 3));
    }

    [Fact]
    public void Program_ConditionalBranch_NotTaken() {
        // addi x1, x0, 5
        // addi x2, x0, 6
        // beq  x1, x2, +8    → not taken
        // addi x3, x0, 99    → x3 = 99
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00500093, // addi x1, x0, 5
            0x00600113, // addi x2, x0, 6
            0x00208463, // beq  x1, x2, +8
            0x06300193, // addi x3, x0, 99
            0x00100073
        );
        train.Run();
        Assert.Equal(99u, Reg(train, 3));
    }

    [Fact]
    public void Program_Jal_AndLink() {
        // jal x1, +8         → x1 = PC+4 = 4, jump to 8
        // addi x2, x0, 99    → skipped (at address 4)
        // addi x2, x0, 42    → x2 = 42 (at address 8)
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x008000EF, // jal x1, +8
            0x06300113, // addi x2, x0, 99  ← skipped
            0x02A00113, // addi x2, x0, 42
            0x00100073
        );
        train.Run();
        Assert.Equal(4u, Reg(train, 1)); // return address
        Assert.Equal(42u, Reg(train, 2));
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Program_CountingLoop() {
        // Count from 0 to 5 using a loop:
        // addi x1, x0, 0     → counter = 0
        // addi x2, x0, 5     → limit = 5
        // loop:
        //   addi x1, x1, 1   → counter++
        //   blt  x1, x2, -4  → if counter < limit, branch back
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00000093, // addi x1, x0, 0
            0x00500113, // addi x2, x0, 5
            0x00108093, // addi x1, x1, 1   ← loop start (addr 8)
            0xfe20cee3, // blt  x1, x2, -4  → back to addr 8
            0x00100073
        ); // ebreak
        train.Run();
        Assert.Equal(5u, Reg(train, 1));
    }

    // ── Logical ───────────────────────────────────────────────────────────────

    [Fact]
    public void Program_BitwiseOps() {
        // addi x1, x0, 0xFF
        // addi x2, x0, 0x0F
        // and  x3, x1, x2    → 0x0F
        // or   x4, x1, x2    → 0xFF
        // xor  x5, x1, x2    → 0xF0
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x0FF00093, // addi x1, x0, 255
            0x00F00113, // addi x2, x0, 15
            0x0020f1b3, // and  x3, x1, x2
            0x0020e233, // or   x4, x1, x2
            0x0020c2b3, // xor  x5, x1, x2
            0x00100073
        );
        train.Run();
        Assert.Equal(0x0Fu, Reg(train, 3));
        Assert.Equal(0xFFu, Reg(train, 4));
        Assert.Equal(0xF0u, Reg(train, 5));
    }

    // ── JALR ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Program_Jalr_WithLink() {
        // addi x2, x0, 16  →  x2 = 16 (jump target)
        // jalr x1, 0(x2)   →  jump to addr 16, x1 = PC+4 = 8
        // addi x3, x0, 99  ← skipped (addr 8)
        // addi x3, x0, 99  ← skipped (addr 12)
        // addi x3, x0, 42  ← executed (addr 16)
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x01000113, // addi x2, x0, 16
            0x000100E7, // jalr x1, 0(x2)
            0x06300193, // addi x3, x0, 99  ← skipped
            0x06300193, // addi x3, x0, 99  ← skipped
            0x02A00193, // addi x3, x0, 42  (at addr 16)
            0x00100073
        );
        train.Run();
        Assert.Equal(8u, Reg(train, 1));  // return addr = jalr_pc(4) + 4
        Assert.Equal(42u, Reg(train, 3));
    }

    // ── AUIPC ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Program_Auipc() {
        // addi x0, x0, 0   (NOP, addr 0)
        // auipc x1, 1      (addr 4) →  x1 = 4 + (1 << 12) = 0x1004
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00000013, // addi x0, x0, 0  (NOP)
            0x00001097, // auipc x1, 1
            0x00100073
        );
        train.Run();
        Assert.Equal(0x1004u, Reg(train, 1));
    }

    // ── Half-word load/store ──────────────────────────────────────────────────

    [Fact]
    public void Program_HalfWordLoadStore() {
        // addi x1, x0, 256  →  base address
        // addi x2, x0, -1   →  x2 = 0xFFFFFFFF
        // sh   x2, 0(x1)    →  store low 16 bits (0xFFFF) at addr 256
        // lh   x3, 0(x1)    →  x3 = 0xFFFFFFFF (sign extended)
        // lhu  x4, 0(x1)    →  x4 = 0x0000FFFF (zero extended)
        // ebreak
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0xFFF00113, // addi x2, x0, -1
            0x00209023, // sh   x2, 0(x1)
            0x00009183, // lh   x3, 0(x1)
            0x0000D203, // lhu  x4, 0(x1)
            0x00100073
        );
        train.Run();
        Assert.Equal(0xFFFFFFFFu, Reg(train, 3));
        Assert.Equal(0x0000FFFFu, Reg(train, 4));
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Program_StatsAreCorrect() {
        // 3 instructions + ebreak = 4 retired
        (SingleCycleTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00A00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081b3, // add  x3, x1, x2
            0x00100073
        ); // ebreak
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("single_cycle.core");
        Assert.NotNull(snap);
        Assert.Equal(3L, snap.Counters["retired"]); // ebreak doesn't retire
        Assert.Equal(3L, snap.Counters["cycles"]);
    }
}