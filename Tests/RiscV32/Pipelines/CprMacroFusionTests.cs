#region

using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Macro-fusion on <see cref="CprTrain" />: the same RV32 SLT(U)/SLTI(U) + BEQ/BNE-against-
///     zero idiom <c>SuperscalarMacroFusionTests</c>/<c>OooMacroFusionTests</c> cover, fused at
///     Rename (before RAT allocation) into a single checkpoint-entry + IQ entry instead of two.
///     <para>
///         CPR has no monolithic ROB — its per-instruction resource is a checkpoint-entry slot
///         (bounded by <c>checkpointMaxInstructions</c>) plus a per-class Issue-Queue slot, both
///         consumed at Rename/Dispatch. A cold D-cache load sits at the head checkpoint (nothing
///         retires — checkpoints commit in strict FIFO order — until it resolves); enough
///         low-confidence branches then open more checkpoints than the (small) checkpoint buffer
///         holds, so once it's exhausted, further instructions pile onto the still-open tail
///         checkpoint until *it* hits <c>checkpointMaxInstructions</c> and rename stalls (a
///         mandatory re-open blocked by a full buffer). Fusing halves the entries each pair costs,
///         so the tail checkpoint fills more slowly and rename stalls less.
///     </para>
/// </summary>
public class CprMacroFusionTests {
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

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static uint[] TakenProgram() => [
        Addi(1, 0, 5),
        Addi(2, 0, 3),
        Slt(5, 2, 1), // 3 < 5 = 1
        Bne(5, 0, 12), // taken, jumps straight to ebreak
        Addi(3, 0, 111), // skipped
        Addi(3, 0, 222), // skipped
        CprMacroFusionTests.Ebreak,
    ];

    private static uint[] NotTakenProgram() => [
        Addi(1, 0, 3),
        Addi(2, 0, 5),
        Slt(5, 2, 1), // 5 < 3 = 0
        Bne(5, 0, 12), // not taken, falls through both ADDIs
        Addi(3, 0, 111),
        Addi(3, 0, 222),
        CprMacroFusionTests.Ebreak,
    ];

    private static CprTrain Run(
        uint[] program,
        bool enableMacroFusion,
        int checkpointCount = 8,
        int checkpointMaxInstructions = 256,
        MemoryConfig? dMemConfig = null
    ) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new CprTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem,
            issueWidth: 4, checkpointCount: checkpointCount,
            checkpointMaxInstructions: checkpointMaxInstructions, dMemConfig: dMemConfig
        );
        train.Run();
        return train;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TakenBranch_SkipsFallthroughRegardlessOfFusion(bool fusion) {
        CprTrain train = Run(TakenProgram(), fusion);
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(1UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotTakenBranch_RunsFallthroughRegardlessOfFusion(bool fusion) {
        CprTrain train = Run(NotTakenProgram(), fusion);
        Assert.Equal(222UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        DialBoardSnapshot unfused = Run(TakenProgram(), false).SnapshotPipeline();
        DialBoardSnapshot fused = Run(TakenProgram(), true).SnapshotPipeline();

        // A fused checkpoint entry is still 2 architectural instructions at retire (RetireEntry
        // scales by ITooth.ArchInstructionCount) — if it silently collapsed to 1, instret-derived
        // stats (IPC, benchmark instruction budgets) would under-count. Assert fusion actually
        // fired first: without it, two unfused runs would also trivially satisfy the equality below.
        Assert.True(fused.Counters["macro_fusions"] > 0, "expected the pair to fuse");
        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
    }

    // A cold-miss load sits at the head checkpoint (nothing retires until it resolves — checkpoints
    // commit strictly FIFO), followed by `copies` independent slt+bne pairs that don't touch the
    // load's destination. Each is a cold (never-before-seen) branch PC, so the JRS confidence
    // estimator reads it as low-confidence and opens a fresh checkpoint at every pair regardless of
    // fusion — checkpoint *count* is unaffected by fusion here. The pressure instead shows up once
    // the small checkpoint buffer is exhausted: further opens are silently skipped (the paper's
    // no-stall rule for a merely-wanted open) and instructions pile onto the still-open tail
    // checkpoint, which stalls rename once it hits checkpointMaxInstructions — fused pairs cost it
    // one entry instead of two, so the tail fills slower and rename stalls less.
    private static uint[] BuildCheckpointPressureProgram(int copies) {
        var program = new List<uint> {
            Lw(10, 0, 0), // cold D-cache miss: nothing behind this retires until it resolves
            Addi(1, 0, 1), // x1 = 1, independent of the load
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Slt(5, 0, 1)); // x5 = (0 < 1) = 1, always — independent of x10
            program.Add(Bne(5, 0, 4)); // always taken, straight to the fall-through PC (no refill)
        }

        program.Add(CprMacroFusionTests.Ebreak);
        return program.ToArray();
    }

    [Fact]
    public void ReducesCyclesWhenDenseWorkOutgrowsTheCheckpointBuffer() {
        const int copies = 24;
        const int checkpointCount = 4;
        const int checkpointMaxInstructions = 3;
        uint[] program = BuildCheckpointPressureProgram(copies);
        var dMemConfig = new MemoryConfig(CacheCapacityBytes: 4096, CacheWays: 4, CacheBlockBytes: 32, CacheMissLatency: 60);

        DialBoardSnapshot unfused =
            Run(program, false, checkpointCount, checkpointMaxInstructions, dMemConfig).SnapshotPipeline();
        DialBoardSnapshot fused =
            Run(program, true, checkpointCount, checkpointMaxInstructions, dMemConfig).SnapshotPipeline();

        // At least some pairs actually fused — not required to be every pair (StepRename drains
        // the decode queue greedily one at a time, so fetch-window alignment can split a pair
        // across cycles), just enough to matter.
        Assert.True(fused.Counters["macro_fusions"] > 0, "expected at least one macro-fusion");
        Assert.Equal(0, unfused.Counters["macro_fusions"]);

        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
        Assert.True(
            fused.Counters["cycles"] < unfused.Counters["cycles"],
            $"expected fusion to reduce cycles: unfused={unfused.Counters["cycles"]}, fused={fused.Counters["cycles"]}"
        );
    }
}
