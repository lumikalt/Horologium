#region

using Mechanism;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Load+ALU micro-fusion on <see cref="SuperscalarTrain" />: an in-order, execute-at-issue
///     machine, so a load's destination becomes ready only <c>latency</c> cycles after it
///     issues (scoreboard, see <c>SuperscalarMacroFusionTests</c>'s own RAW-interlock
///     rationale for compare+branch). A dependent ALU consumer reading that same register
///     fails the RAW check and must issue a cycle later — fusion collapses both into one
///     issue slot/cycle, same mechanism as compare+branch, just triggered by
///     <see cref="RiscV32.Decode.RvMacroFuser" />'s second (load+ALU) pattern instead of its
///     first.
/// </summary>
public class SuperscalarLoadAluFusionTests {
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
        SuperscalarLoadAluFusionTests.Ebreak,
    ];

    private static void RunWithSeededData(uint[] program, bool enableMacroFusion, out SuperscalarTrain train) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(200, 10, 4);
        train = new SuperscalarTrain(new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: 2);
        train.Run();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadThenAddi_ProducesSameResultRegardlessOfFusion(bool fusion) {
        RunWithSeededData(LoadUseProgram(), fusion, out SuperscalarTrain train);
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        RunWithSeededData(LoadUseProgram(), false, out SuperscalarTrain unfused);
        RunWithSeededData(LoadUseProgram(), true, out SuperscalarTrain fused);

        Assert.True(fused.SnapshotPipeline().Counters["micro_fusions"] > 0, "expected the pair to fuse");
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
            SuperscalarLoadAluFusionTests.Ebreak,
        ];
        RunWithSeededData(program, true, out SuperscalarTrain train);
        Assert.Equal(0, train.SnapshotPipeline().Counters["micro_fusions"]);
    }

    // `copies` independent (across copies) lw+addi read-modify pairs, each reading from the
    // same already-cached address (so only the first pays a miss) — straight-line, no backward
    // branch, so the only variable between fusion on/off is the intra-pair RAW interlock.
    private static uint[] BuildDenseLoadUseProgram(int copies) {
        var program = new List<uint> {
            Addi(1, 0, 2000), // shared address base (safely beyond the program's own bytes)
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Lw(5, 1, 0)); // x5 = mem[2000]
            program.Add(Addi(5, 5, 1)); // x5 += 1 — fusible read-modify pair
        }

        program.Add(SuperscalarLoadAluFusionTests.Ebreak);
        return program.ToArray();
    }

    private static SuperscalarTrain Run(uint[] program, bool enableMacroFusion) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new SuperscalarTrain(new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: 2);
        train.Run();
        return train;
    }

    [Fact]
    public void ReducesCyclesOnDenseLoadUsePairs() {
        const int copies = 32;
        uint[] program = BuildDenseLoadUseProgram(copies);

        // Plain Run (not RunWithSeededData): the dense program is self-sufficient (reads
        // zero-initialized memory at address 2000), and RunWithSeededData's mem.Write(200, ...)
        // — sized for the small 4-word LoadUseProgram — would land inside this 66-word program's
        // own instruction bytes.
        DialBoardSnapshot unfused = Run(program, false).SnapshotPipeline();
        DialBoardSnapshot fused = Run(program, true).SnapshotPipeline();

        Assert.Equal(0, unfused.Counters["micro_fusions"]);
        Assert.True(fused.Counters["micro_fusions"] > 0, "expected at least one load+ALU fusion");

        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
        Assert.True(
            fused.Counters["cycles"] < unfused.Counters["cycles"],
            $"expected fusion to reduce cycles: unfused={unfused.Counters["cycles"]}, fused={fused.Counters["cycles"]}"
        );
    }
}
