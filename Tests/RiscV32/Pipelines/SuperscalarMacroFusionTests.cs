#region

using Mechanism;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Macro-fusion (TODO.md's "Macro-fusion" µops item): <see cref="SuperscalarTrain" />'s
///     opt-in fusion of RV32's SLT(U)/SLTI(U) + BEQ/BNE-against-zero idiom into a single
///     issue-slot µop (<see cref="RiscV32.Decode.RvMacroFuser" />).
///     <para>
///         Each test uses width-2 issue so a straight-line stream of dependent compare+branch
///         pairs structurally forces the interlock fusion eliminates: an in-order machine's
///         RAW bypass makes a producer of latency L visible to a consumer L cycles later, so
///         a branch immediately consuming its own preceding compare's result can never join
///         it in the same issue group without fusion — a deterministic 1-cycle bubble per
///         pair, every time, independent of predictor/cache behaviour.
///     </para>
/// </summary>
public class SuperscalarMacroFusionTests {
    private const uint Ebreak = 0x00100073;

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

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Slt(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0110011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    // x1 = 5, x2 = 3, slt x5,x2,x1 (3<5 = 1, taken), bne x5,x0,+12 jumps straight to ebreak —
    // x3 is never written when the branch fires.
    private static uint[] TakenProgram() => [
        Addi(1, 0, 5),
        Addi(2, 0, 3),
        Slt(5, 2, 1),
        Bne(5, 0, 12),
        Addi(3, 0, 111), // skipped
        Addi(3, 0, 222), // skipped
        SuperscalarMacroFusionTests.Ebreak,
    ];

    // x1 = 3, x2 = 5, slt x5,x2,x1 (5<3 = 0, not taken) falls through both ADDIs.
    private static uint[] NotTakenProgram() => [
        Addi(1, 0, 3),
        Addi(2, 0, 5),
        Slt(5, 2, 1),
        Bne(5, 0, 12),
        Addi(3, 0, 111),
        Addi(3, 0, 222),
        SuperscalarMacroFusionTests.Ebreak,
    ];

    private static SuperscalarTrain Run(uint[] program, bool enableMacroFusion) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new SuperscalarTrain(new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: 2);
        train.Run();
        return train;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TakenBranch_SkipsFallthroughRegardlessOfFusion(bool fusion) {
        SuperscalarTrain train = Run(TakenProgram(), fusion);
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(1UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotTakenBranch_RunsFallthroughRegardlessOfFusion(bool fusion) {
        SuperscalarTrain train = Run(NotTakenProgram(), fusion);
        Assert.Equal(222UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        DialBoardSnapshot unfused = Run(TakenProgram(), false).SnapshotPipeline();
        DialBoardSnapshot fused = Run(TakenProgram(), true).SnapshotPipeline();

        // Fusion changes issue-slot/cycle accounting, but the same architectural
        // instructions retire either way. If fusion collapsed a fused pair to a single
        // retirement, instret-derived stats (IPC, benchmark instruction budgets) would
        // silently under-count. Assert fusion actually fired first: without it, two unfused
        // runs would also trivially satisfy the equality below.
        Assert.True(fused.Counters["macro_fusions"] > 0, "expected the pair to fuse");
        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
    }

    // 64 unrolled copies of an independent (across copies) slt+bne pair that is never taken,
    // straight-line (no backward branch), so the only variable between fusion on/off is the
    // intra-pair RAW interlock itself — no predictor/frontend-refill noise to confound it.
    private static uint[] BuildDenseComparePairProgram(int copies) {
        var program = new List<uint> {
            Addi(1, 0, 1), // x1 = 1
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Slt(5, 0, 1)); // x5 = (0 < 1) = 1, always
            program.Add(Bne(5, 0, 4)); // always taken (x5 != 0) — but straight to the fall-through
            // PC, so it's never a misprediction/refill: the predicted-taken target from the decode
            // hint and the actual resolved target are identical either way.
        }

        program.Add(SuperscalarMacroFusionTests.Ebreak);
        return program.ToArray();
    }

    [Fact]
    public void ReducesCyclesOnDenseComparePairs() {
        const int copies = 64;
        uint[] program = BuildDenseComparePairProgram(copies);

        DialBoardSnapshot unfused = Run(program, false).SnapshotPipeline();
        DialBoardSnapshot fused = Run(program, true).SnapshotPipeline();

        // Every pair actually fused — not just "some pair fused and cycles happened to drop",
        // which the bare cycle inequality below can't distinguish from a partial-fusion
        // regression on its own.
        Assert.Equal(0, unfused.Counters["macro_fusions"]);
        Assert.Equal(copies, fused.Counters["macro_fusions"]);

        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
        Assert.True(
            fused.Counters["cycles"] < unfused.Counters["cycles"],
            $"expected fusion to reduce cycles: unfused={unfused.Counters["cycles"]}, fused={fused.Counters["cycles"]}"
        );
    }

    [Fact]
    public void DefaultMechanismDisablesFusion() {
        Assert.Null(new Rv32Mechanism().MacroFuser);
        Assert.NotNull(new Rv32Mechanism(enableMacroFusion: true).MacroFuser);
    }
}
