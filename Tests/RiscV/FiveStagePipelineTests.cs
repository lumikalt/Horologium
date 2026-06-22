using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using RiscV;
using RiscV.Memory;
using RiscV.Trains;

namespace Tests.RiscV;

public class FiveStagePipelineTests {
    private static (FiveStageTrain train, FlatMemory mem) Make(
        bool forwarding = true,
        IBranchPredictor? predictor = null,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new FiveStageTrain(
            new RvMechanism(), mem,
            0, forwarding, predictor
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

    private static uint Reg(FiveStageTrain t, int r) =>
        (uint)t.ArchState.IntegerRegisters.Read(r);

    // ── Correctness ───────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_AddTwoNumbers_ProducesCorrectResult() {
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00A00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081b3, // add  x3, x1, x2
            0x00100073
        );
        train.Run();
        Assert.Equal(42u, Reg(train, 3));
    }

    [Fact]
    public void Pipeline_StoreAndLoad_ProducesCorrectResult() {
        (FiveStageTrain train, FlatMemory mem) = Make();
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
    public void Pipeline_BranchTaken_SkipsInstruction() {
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00500093, // addi x1, x0, 5
            0x00500113, // addi x2, x0, 5
            0x00208463, // beq  x1, x2, +8
            0x06300193, // addi x3, x0, 99  ← flushed
            0x02A00193, // addi x3, x0, 42
            0x00100073
        );
        train.Run();
        Assert.Equal(42u, Reg(train, 3));
    }

    [Fact]
    public void Pipeline_CountingLoop_ProducesCorrectResult() {
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00000093, // addi x1, x0, 0
            0x00500113, // addi x2, x0, 5
            0x00108093, // addi x1, x1, 1   ← loop (addr 8)
            0xFE20CEE3, // blt  x1, x2, -4
            0x00100073
        );
        train.Run();
        Assert.Equal(5u, Reg(train, 1));
    }

    // ── Forwarding vs no-forwarding: same result, different CPI ──────────────

    [Fact]
    public void Pipeline_WithAndWithoutForwarding_SameResult() {
        uint[] program = [
            0x00A00093, // addi x1, x0, 10
            0x00108113, // addi x2, x1, 1   ← RAW on x1
            0x00210193, // addi x3, x2, 2   ← RAW on x2
            0x00100073,
        ];

        (FiveStageTrain fwd, FlatMemory memFwd) = Make();
        (FiveStageTrain noFwd, FlatMemory memNoFwd) = Make(false);
        Load(memFwd, program);
        Load(memNoFwd, program);

        fwd.Run();
        noFwd.Run();

        Assert.Equal(Reg(fwd, 3), Reg(noFwd, 3)); // same answer
    }

    [Fact]
    public void Pipeline_WithoutForwarding_HasMoreCycles() {
        // RAW chain: each instruction depends on the previous
        uint[] program = [
            0x00A00093, // addi x1, x0, 10
            0x00108113, // addi x2, x1, 1
            0x00210193, // addi x3, x2, 2
            0x00100073,
        ];

        (FiveStageTrain fwd, FlatMemory memFwd) = Make();
        (FiveStageTrain noFwd, FlatMemory memNoFwd) = Make(false);
        Load(memFwd, program);
        Load(memNoFwd, program);

        RevolutionResult rFwd = fwd.Run();
        RevolutionResult rNoFwd = noFwd.Run();

        DialBoardSnapshot? snapFwd = rFwd.Find("five_stage.pipeline");
        DialBoardSnapshot? snapNoFwd = rNoFwd.Find("five_stage.pipeline");

        Assert.NotNull(snapFwd);
        Assert.NotNull(snapNoFwd);
        Assert.True(
            snapNoFwd.Counters["cycles"] > snapFwd.Counters["cycles"],
            "No-forwarding pipeline should take more cycles on a RAW chain"
        );
    }

    // ── Branch predictor ──────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_TwoBitPredictor_FewerMissesThanAlwaysNotTaken() {
        // A loop that branches back 10 times — 2-bit predictor learns quickly
        uint[] program = [
            0x00000093, // addi x1, x0, 0
            0x00A00113, // addi x2, x0, 10
            0x00108093, // addi x1, x1, 1   ← loop (addr 8)
            0xFE20CEE3, // blt  x1, x2, -4
            0x00100073,
        ];

        (FiveStageTrain ant, FlatMemory memAnt) = Make(predictor: new AlwaysNotTakenPredictor());
        (FiveStageTrain twoB, FlatMemory memTwoB) = Make(predictor: new TwoBitPredictor());
        Load(memAnt, program);
        Load(memTwoB, program);

        RevolutionResult rAnt = ant.Run();
        RevolutionResult rTwoB = twoB.Run();

        DialBoardSnapshot? snapAnt = rAnt.Find("five_stage.pipeline");
        DialBoardSnapshot? snapTwoB = rTwoB.Find("five_stage.pipeline");

        Assert.NotNull(snapAnt);
        Assert.NotNull(snapTwoB);
        Assert.True(
            snapTwoB.Counters["branch_misses"] < snapAnt.Counters["branch_misses"],
            "2-bit predictor should have fewer mispredictions on a loop"
        );
    }
}