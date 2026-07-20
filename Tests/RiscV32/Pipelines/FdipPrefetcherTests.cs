#region

using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Tests for Fetch Directed Instruction Prefetching (FDIP).
///     Three invariants are checked on each pipeline type:
///     (a) identical committed arch state — correctness;
///     (b) identical branch_misses count — verifies lookahead Predict() calls
///     do not corrupt predictor training state;
///     (c) ICache.Prefetches > 0 — FDIP is actually issuing prefetches.
/// </summary>
public class FdipPrefetcherTests {
    // Loop 10 times, taken branch on each iteration.
    // addi x1, x0, 0 / addi x2, x0, 10 / addi x1, x1, 1 / blt x1, x2, -4 / ebreak
    private static readonly uint[] LoopProgram = [
        0x00000093, // addi x1, x0, 0
        0x00A00113, // addi x2, x0, 10
        0x00108093, // addi x1, x1, 1   ← loop body (addr 8)
        0xFE20CEE3, // blt  x1, x2, -4  ← back-edge
        0x00100073, // ebreak
    ];

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

    // I-cache: 256 bytes, 4-way, 64-byte blocks, 10-cycle miss latency.
    // The loop fits in 2 blocks; cold misses are charged per block.
    private static MemoryConfig ICache() => new(256, 4, 64);

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Fact]
    public void Fdip_FiveStage_ArchStateIdenticalToWithout() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        Load(memOff, FdipPrefetcherTests.LoopProgram);
        Load(memOn, FdipPrefetcherTests.LoopProgram);

        var off = new FiveStageTrain(new Rv32Mechanism(), memOff, iMemConfig: ICache());
        var on = new FiveStageTrain(new Rv32Mechanism(), memOn, iMemConfig: ICache(), fdipFtqCapacity: 32);

        off.Run();
        on.Run();

        for (var r = 0; r < 32; r++)
            Assert.Equal(
                off.ArchState.IntegerRegisters.Read(r),
                on.ArchState.IntegerRegisters.Read(r)
            );
    }

    [Fact]
    public void Fdip_FiveStage_BranchMissCountIsIdentical() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        Load(memOff, FdipPrefetcherTests.LoopProgram);
        Load(memOn, FdipPrefetcherTests.LoopProgram);

        var off = new FiveStageTrain(new Rv32Mechanism(), memOff, predictor: Predictor(), iMemConfig: ICache());
        var on = new FiveStageTrain(
            new Rv32Mechanism(), memOn, predictor: Predictor(), iMemConfig: ICache(), fdipFtqCapacity: 32
        );

        RevolutionResult rOff = off.Run();
        RevolutionResult rOn = on.Run();

        DialBoardSnapshot? sOff = rOff.Find("five_stage.pipeline");
        DialBoardSnapshot? sOn = rOn.Find("five_stage.pipeline");
        Assert.NotNull(sOff);
        Assert.NotNull(sOn);
        Assert.Equal(sOff.Counters["branch_misses"], sOn.Counters["branch_misses"]);
        return;

        // branch_misses must be byte-identical: if FDIP's lookahead calls to
        // Predict() overwrite per-prediction carry state consumed by Update(),
        // the count would diverge. This test catches that class of bug.
        IBranchPredictor Predictor() => new LTagePredictor();
    }

    [Fact]
    public void Fdip_FiveStage_IssuesPrefetchesIntoICache() {
        var mem = new FlatMemory(4096);
        Load(mem, FdipPrefetcherTests.LoopProgram);

        var train = new FiveStageTrain(new Rv32Mechanism(), mem, iMemConfig: ICache(), fdipFtqCapacity: 32);
        train.Run();

        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Prefetches > 0, "FDIP should issue at least one I-cache prefetch");
    }

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fdip_OoO_ArchStateIdenticalToWithout() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        Load(memOff, FdipPrefetcherTests.LoopProgram);
        Load(memOn, FdipPrefetcherTests.LoopProgram);

        var off = new OooeTrain(new Rv32Mechanism(), memOff, iMemConfig: ICache());
        var on = new OooeTrain(new Rv32Mechanism(), memOn, iMemConfig: ICache(), fdipFtqCapacity: 32);

        off.Run();
        on.Run();

        for (var r = 0; r < 32; r++)
            Assert.Equal(
                off.ArchState.IntegerRegisters.Read(r),
                on.ArchState.IntegerRegisters.Read(r)
            );
    }

    [Fact]
    public void Fdip_OoO_BranchMissCountIsIdentical() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        Load(memOff, FdipPrefetcherTests.LoopProgram);
        Load(memOn, FdipPrefetcherTests.LoopProgram);

        var off = new OooeTrain(new Rv32Mechanism(), memOff, predictor: Predictor(), iMemConfig: ICache());
        var on = new OooeTrain(
            new Rv32Mechanism(), memOn, predictor: Predictor(), iMemConfig: ICache(), fdipFtqCapacity: 32
        );

        RevolutionResult rOff = off.Run();
        RevolutionResult rOn = on.Run();

        DialBoardSnapshot? sOff = rOff.Find("ooo.pipeline");
        DialBoardSnapshot? sOn = rOn.Find("ooo.pipeline");
        Assert.NotNull(sOff);
        Assert.NotNull(sOn);
        Assert.Equal(sOff.Counters["branch_misses"], sOn.Counters["branch_misses"]);
        return;

        IBranchPredictor Predictor() => new LTagePredictor();
    }

    [Fact]
    public void Fdip_OoO_IssuesPrefetchesIntoICache() {
        var mem = new FlatMemory(4096);
        Load(mem, FdipPrefetcherTests.LoopProgram);

        var train = new OooeTrain(new Rv32Mechanism(), mem, iMemConfig: ICache(), fdipFtqCapacity: 32);
        train.Run();

        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Prefetches > 0, "FDIP should issue at least one I-cache prefetch");
    }
}