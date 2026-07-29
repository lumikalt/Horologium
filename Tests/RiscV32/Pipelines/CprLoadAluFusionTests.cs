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
///     Load+ALU micro-fusion on <see cref="CprTrain" /> — the same idiom
///     <see cref="OooLoadAluFusionTests" /> covers on <see cref="OooTrain" />, fused at
///     Rename into a single checkpoint-entry + IQ entry instead of two, reusing
///     <c>FusedSecondInstrId</c> exactly as <see cref="CprMacroFusionTests" />'s
///     compare+branch fusion does.
/// </summary>
public class CprLoadAluFusionTests {
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

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint[] LoadUseProgram() => [
        Addi(1, 0, 200),
        Lw(5, 1, 0),
        Addi(5, 5, 4),
        CprLoadAluFusionTests.Ebreak,
    ];

    private static void RunWithSeededData(uint[] program, bool enableMacroFusion, out CprTrain train) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(200, 10, 4);
        train = new CprTrain(new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: 4);
        train.Run();
    }

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
    public void LoadThenAddi_ProducesSameResultRegardlessOfFusion(bool fusion) {
        RunWithSeededData(LoadUseProgram(), fusion, out CprTrain train);
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void LoadAluFusion_ActuallyFiresOnTheLoadUseIdiom() {
        RunWithSeededData(LoadUseProgram(), true, out CprTrain train);
        Assert.True(train.SnapshotPipeline().Counters["macro_fusions"] > 0, "expected the load+addi pair to fuse");
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        RunWithSeededData(LoadUseProgram(), false, out CprTrain unfused);
        RunWithSeededData(LoadUseProgram(), true, out CprTrain fused);

        Assert.True(fused.SnapshotPipeline().Counters["macro_fusions"] > 0, "expected the pair to fuse");
        Assert.Equal(
            unfused.SnapshotPipeline().Counters["retired"],
            fused.SnapshotPipeline().Counters["retired"]
        );
    }

    [Fact]
    public void DoesNotFuse_WhenAluDestinationDiffersFromLoadDestination() {
        uint[] program = [
            Addi(1, 0, 200),
            Lw(5, 1, 0),
            Addi(6, 5, 4),
            CprLoadAluFusionTests.Ebreak,
        ];
        RunWithSeededData(program, true, out CprTrain train);
        Assert.Equal(0, train.SnapshotPipeline().Counters["macro_fusions"]);
        Assert.Equal(10UL, train.ArchState.IntegerRegisters.Read(5));
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(6));
    }

    // Mirrors CprMacroFusionTests.BuildCheckpointPressureProgram: a cold-miss load holds the
    // head checkpoint (nothing retires — strict FIFO commit — until it resolves), followed by
    // `copies` independent lw+addi pairs that pile onto the tail checkpoint until it hits
    // checkpointMaxInstructions. Fused pairs cost one entry instead of two, so the tail fills
    // more slowly and rename stalls less.
    private static uint[] BuildCheckpointPressureProgram(int copies) {
        var program = new List<uint> {
            Lw(10, 0, 0), // cold D-cache miss: nothing behind this retires until it resolves
            Addi(1, 0, 2000), // address base for the repeated pairs (safely beyond the program's own bytes)
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Lw(5, 1, 0)); // x5 = mem[2000] — hits cache after the first iteration
            program.Add(Addi(5, 5, 1)); // x5 += 1 — fusible read-modify pair
        }

        program.Add(CprLoadAluFusionTests.Ebreak);
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

        Assert.True(fused.Counters["macro_fusions"] > 0, "expected at least one load+ALU fusion");
        Assert.Equal(0, unfused.Counters["macro_fusions"]);

        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
        Assert.True(
            fused.Counters["cycles"] < unfused.Counters["cycles"],
            $"expected fusion to reduce cycles: unfused={unfused.Counters["cycles"]}, fused={fused.Counters["cycles"]}"
        );
    }
}
