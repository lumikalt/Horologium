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
///     Macro-fusion on <see cref="OooTrain" />: the same RV32 SLT(U)/SLTI(U) + BEQ/BNE-against-
///     zero idiom <c>SuperscalarMacroFusionTests</c> covers, fused at Rename (before RAT
///     allocation) into a single ROB+IQ entry instead of at Issue.
///     <para>
///         Unlike the in-order <see cref="Pipeline.SuperscalarTrain" />, an OoO machine shares one
///         issue-width parameter across fetch/rename/issue/commit, so a dense stream of
///         independent fusible pairs is fetch-bound at that width regardless of fusion — fusion
///         has nowhere to show a win there (nothing post-fetch can lift throughput above the fetch
///         ceiling). The place fusion actually pays in an OoO machine is ROB occupancy: a fused
///         pair costs one ROB entry instead of two, so more independent work fits in the shadow of
///         a stalled ROB head. <see cref="ReducesCyclesWhenDenseWorkOutgrowsTheRob" /> is built
///         specifically to force that: a cold D-cache load miss sits at the ROB head, with more
///         independent fusible pairs behind it than an unfused stream could fit in a deliberately
///         small ROB — forcing dispatch to stall waiting for retirement (which can't happen until
///         the load resolves) before it can enqueue the backlog. Fused, everything fits and
///         dispatch never stalls.
///     </para>
/// </summary>
public class OooMacroFusionTests {
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
        OooMacroFusionTests.Ebreak,
    ];

    private static uint[] NotTakenProgram() => [
        Addi(1, 0, 3),
        Addi(2, 0, 5),
        Slt(5, 2, 1), // 5 < 3 = 0
        Bne(5, 0, 12), // not taken, falls through both ADDIs
        Addi(3, 0, 111),
        Addi(3, 0, 222),
        OooMacroFusionTests.Ebreak,
    ];

    private static OooTrain Run(
        uint[] program,
        bool enableMacroFusion,
        int robCapacity = 32,
        int iqCapacity = 32,
        MemoryConfig? dMemConfig = null
    ) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new OooTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem,
            issueWidth: 4, robCapacity: robCapacity, iqCapacity: iqCapacity, dMemConfig: dMemConfig
        );
        train.Run();
        return train;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TakenBranch_SkipsFallthroughRegardlessOfFusion(bool fusion) {
        OooTrain train = Run(TakenProgram(), fusion);
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(1UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotTakenBranch_RunsFallthroughRegardlessOfFusion(bool fusion) {
        OooTrain train = Run(NotTakenProgram(), fusion);
        Assert.Equal(222UL, train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        DialBoardSnapshot unfused = Run(TakenProgram(), false).SnapshotPipeline();
        DialBoardSnapshot fused = Run(TakenProgram(), true).SnapshotPipeline();

        // A fused ROB entry is still 2 architectural instructions at retire (FinishRetire scales
        // by ITooth.ArchInstructionCount) — if it silently collapsed to 1, instret-derived stats
        // (IPC, benchmark instruction budgets) would under-count. Assert fusion actually fired
        // first: without it, two unfused runs would also trivially satisfy the equality below.
        Assert.True(fused.Counters["macro_fusions"] > 0, "expected the pair to fuse");
        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
    }

    // A cold-miss load sits at the ROB head (nothing can retire until it resolves), followed by
    // `copies` independent slt+bne pairs that don't touch the load's destination — free to
    // dispatch/execute out of order, but each still costs a ROB entry until the load retires.
    // robCapacity is sized so the *unfused* instruction count (2*copies) exceeds it (forcing
    // dispatch to stall on ROB-full mid-backlog) while the *fused* count (copies) fits entirely.
    private static uint[] BuildRobPressureProgram(int copies) {
        var program = new List<uint> {
            Lw(10, 0, 0), // cold D-cache miss: nothing behind this retires until it resolves
            Addi(1, 0, 1), // x1 = 1, independent of the load
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Slt(5, 0, 1)); // x5 = (0 < 1) = 1, always — independent of x10
            program.Add(Bne(5, 0, 4)); // always taken, straight to the fall-through PC (no refill)
        }

        program.Add(OooMacroFusionTests.Ebreak);
        return program.ToArray();
    }

    [Fact]
    public void ReducesCyclesWhenDenseWorkOutgrowsTheRob() {
        const int copies = 12;
        const int robCapacity = 16; // 2*copies=24 > 16 (unfused stalls); copies=12 <= 16 (fused fits)
        uint[] program = BuildRobPressureProgram(copies);
        var dMemConfig = new MemoryConfig(CacheCapacityBytes: 4096, CacheWays: 4, CacheBlockBytes: 32, CacheMissLatency: 60);

        DialBoardSnapshot unfused = Run(program, false, robCapacity, robCapacity * 2, dMemConfig).SnapshotPipeline();
        DialBoardSnapshot fused = Run(program, true, robCapacity, robCapacity * 2, dMemConfig).SnapshotPipeline();

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
