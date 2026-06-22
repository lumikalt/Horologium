using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
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

    // ── Load-use hazard ───────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_LoadUseHazard_CorrectResult() {
        // lw followed immediately by a dependent addi — should stall one cycle
        // addi x2, x0, 256   base address
        // lw   x1, 0(x2)     load from addr 256
        // addi x3, x1, 1     load-use hazard: depends on x1
        // ebreak
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x10000113, // addi x2, x0, 256
            0x00012083, // lw   x1, 0(x2)
            0x00108193, // addi x3, x1, 1
            0x00100073
        );
        mem.Write(256, 42, 4); // value at the load address
        train.Run();
        Assert.Equal(43u, (uint)train.ArchState.IntegerRegisters.Read(3));
    }

    [Fact]
    public void Pipeline_LoadUseHazard_InsertsOneStall() {
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x10000113, // addi x2, x0, 256
            0x00012083, // lw   x1, 0(x2)
            0x00108193, // addi x3, x1, 1
            0x00100073
        );
        mem.Write(256, 42, 4);
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(1L, snap.Counters["stalls"]);
    }

    // ── Branch correctness ────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_BranchNotTaken_ZeroMispredictions() {
        // AlwaysNotTaken predictor is correct when the branch is not taken.
        // addi x1, x0, 1
        // addi x2, x0, 2
        // beq  x1, x2, +8  →  not taken (1 != 2), predictor is correct → 0 misses
        // addi x3, x0, 42
        // ebreak
        (FiveStageTrain train, FlatMemory mem) = Make(predictor: new AlwaysNotTakenPredictor());
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00200113, // addi x2, x0, 2
            0x00208463, // beq  x1, x2, +8  (not taken)
            0x02A00193, // addi x3, x0, 42
            0x00100073
        );
        RevolutionResult result = train.Run();

        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(3));
        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(0L, snap.Counters["branch_misses"]);
    }

    [Fact]
    public void Pipeline_Jal_CorrectResult() {
        // jal x1, +8   →  jump to addr 8, x1 = 4 (return addr)
        // addi x2, x0, 99  ← skipped (addr 4)
        // addi x2, x0, 42  ← executed (addr 8)
        // ebreak
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x008000EF, // jal x1, +8
            0x06300113, // addi x2, x0, 99  ← skipped
            0x02A00113, // addi x2, x0, 42
            0x00100073
        );
        train.Run();
        Assert.Equal(4u, (uint)train.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(2));
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_RetiredCount_MatchesInstructions() {
        // 3 instructions + ebreak — ebreak does not retire
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x00A00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081B3, // add  x3, x1, x2
            0x00100073
        );
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(3L, snap.Counters["retired"]);
    }

    [Fact]
    public void Pipeline_NoForwarding_StallsCounterIsPositive() {
        // A RAW chain with no forwarding must insert stall cycles.
        uint[] program = [
            0x00A00093, // addi x1, x0, 10
            0x00108113, // addi x2, x1, 1   ← RAW on x1
            0x00210193, // addi x3, x2, 2   ← RAW on x2
            0x00100073,
        ];
        (FiveStageTrain train, FlatMemory mem) = Make(false);
        Load(mem, program);
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters["stalls"] > 0, "No-forwarding pipeline must stall on RAW hazards");
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

    // ── Cache / TLB integration ───────────────────────────────────────────────

    private static MemoryConfig SmallICache(int missLatency = 5) =>
        new(64, 4, 16, missLatency);

    private static MemoryConfig SmallDCache(int missLatency = 5) =>
        new(64, 4, 16, missLatency);

    [Fact]
    public void WithICache_CorrectResultStillProduced() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            SmallICache()
        );
        Load(
            mem,
            0x00A00093, // addi x1, x0, 10
            0x02000113, // addi x2, x0, 32
            0x002081b3, // add  x3, x1, x2
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(3));
    }

    [Fact]
    public void WithICache_ColdFetchesTriggerMisses() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            SmallICache(5)
        );
        Load(
            mem,
            0x00A00093, // addi x1, x0, 10
            0x00100073  // ebreak
        );
        train.Run();

        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Misses > 0, "I-cache should have at least one miss for a cold run");
    }

    [Fact]
    public void WithICache_MissesCauseStallCycles() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            SmallICache(10)
        );
        Load(
            mem,
            0x00100073 // ebreak
        );
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters["cache_miss_stalls"] > 0,
            "At least one stall cycle should be charged for cold I-cache misses"
        );
    }

    [Fact]
    public void WithDCache_LoadHitAfterStore_CorrectValue() {
        // SW to address 0x100 then LW from the same address through a D-cache.
        // The SW is a write-through so backing stays consistent; the LW should
        // fill the cache and return the stored value.
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            dMemConfig: SmallDCache()
        );
        Load(
            mem,
            0x10000093, // addi x1, x0, 256   (x1 = 0x100 — base address)
            0x00A00113, // addi x2, x0, 10    (x2 = 10 — value to store)
            0x0020a023, // sw   x2, 0(x1)     (mem[0x100] = 10)
            0x0000a183, // lw   x3, 0(x1)     (x3 = mem[0x100])
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(10u, (uint)train.ArchState.IntegerRegisters.Read(3));
    }

    [Fact]
    public void WithDCache_LoadInstructions_RecordDCacheMisses() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            dMemConfig: SmallDCache(5)
        );
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0x0000a103, // lw   x2, 0(x1)     (cold D-cache miss)
            0x00100073  // ebreak
        );
        train.Run();

        Assert.NotNull(train.DCache);
        Assert.True(train.DCache!.Misses > 0, "D-cache should record a miss for the load");
    }

    [Fact]
    public void WithTlb_IdentityMapping_CorrectResult() {
        var mem = new FlatMemory(4096);
        var iTlbConfig = new MemoryConfig(
            0,
            TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 8
        );
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            iTlbConfig
        );
        Load(
            mem,
            0x00500093, // addi x1, x0, 5
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(5u, (uint)train.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void WithTlb_ColdMiss_RecordedInDialBoard() {
        var mem = new FlatMemory(4096);
        var iTlbConfig = new MemoryConfig(
            0,
            TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 8
        );
        var train = new FiveStageTrain(
            new RvMechanism(), mem, 0, true, null,
            iTlbConfig
        );
        Load(mem, 0x00100073);
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.ContainsKey("itlb_misses"), "itlb_misses counter should exist when I-TLB is configured"
        );
        Assert.True(snap.Counters["itlb_misses"] > 0, "I-TLB should record at least one cold miss");
    }
}