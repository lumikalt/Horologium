#region

using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Pipelines;

public class FiveStagePipelineTests {
    private static (FiveStageTrain train, FlatMemory mem) Make(
        bool forwarding = true,
        IBranchPredictor? predictor = null,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem,
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

    [Fact]
    public void Pipeline_AmocasDPair_SecondaryDestConsumedByLaterInstruction() {
        // amocas.d's high half (rd+1) commits via SideEffect in WB with no forwarding
        // path — this exercises SecondaryDestRawHazard, which must stall the consumer
        // in ID until the producer reaches WB, rather than letting it read x5 stale.
        // addi x5, x0, 999      → poison x5 (the atomic's future secondary dest)
        // addi x8, x0, 256      → address
        // addi x6, x0, 0x111    → new value, low half
        // addi x7, x0, 0x222    → new value, high half
        // amocas.d x4, x6, (x8) → comparand (x4,x5)=(0,999) != mem (0,0) → CAS fails,
        //                         but x5 still gets overwritten with old high half (0).
        // add  x9, x5, x0       → must read the corrected value (0), not the stale poison (999)
        // ebreak
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0x3e700293, // addi x5, x0, 999
            0x10000413, // addi x8, x0, 256
            0x11100313, // addi x6, x0, 0x111
            0x22200393, // addi x7, x0, 0x222
            0x2864322f, // amocas.d x4, x6, (x8)
            0x000284b3, // add x9, x5, x0
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(0u, Reg(train, 4));     // old low half returned via normal dest path
        Assert.Equal(0u, Reg(train, 5));     // old high half delivered via SideEffect
        Assert.Equal(0u, Reg(train, 9));     // consumer must see the corrected value, not 999
        Assert.Equal(0UL, mem.Read(256, 8)); // CAS failed (comparand mismatch) — mem unchanged
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

    [Fact]
    public void Pipeline_EcallImmediatelyAfterArgWrite_SeesWrittenValue_NotStaleState() {
        // ecall reads a7 (and a0-a5) straight from architectural state — not through any
        // decoded SourceRegisters — so neither the load-use stall nor forwarding (hardwired
        // to 3 operand slots) has any decoded operand to key off. Zero-instruction gap: the
        // preceding addi's write to a7 hasn't reached WB by the time ecall would (without the
        // drain fix) read state in EX. Without the fix this reads a7=0, not 220.
        var handler = new RecordingSyscallHandler();
        var mem = new FlatMemory(4096);
        var mech = new Rv32Mechanism(syscallHandler: handler);
        var train = new FiveStageTrain(mech, mem);
        Load(
            mem,
            0x0DC00893, // addi a7, x0, 220  (SYS_clone — arbitrary distinguishing sentinel)
            0x00000073  // ecall             (zero-instruction gap)
        );
        train.Run();
        Assert.Equal(220UL, handler.LastSyscallNum);
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
        (FiveStageTrain twoB, FlatMemory memTwoB) = Make(predictor: new NBitBp());
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

    [Fact]
    public void Pipeline_RasPredictsReturn_AndDirectJalResolved_ZeroMisses() {
        // jal ra → func is a direct unconditional jump: fetch resolves it straight to its
        // statically known target, bypassing the (AlwaysNotTaken) direction predictor → 0 misses.
        // jalr x0, ra (return) is predicted by RAS → 0 misses for the return.
        // Total branch_misses == 0
        //
        //   0x00: addi x10, x0, 0         (x10 = 0; x10/a0 is result reg; ra/x1 is link)
        //   0x04: jal  ra, +8             (ra = 0x08, jump to 0x0C)
        //   0x08: ebreak
        //   0x0C: addi x10, x10, 42       (x10 = 42)
        //   0x10: jalr x0, ra, 0          (return to ra = 0x08)
        uint[] program = [
            0x00000513, // addi x10, x0, 0
            0x008000EF, // jal  ra, +8
            0x00100073, // ebreak
            0x02A50513, // addi x10, x10, 42
            0x00008067, // jalr x0, ra, 0
        ];
        (FiveStageTrain train, FlatMemory mem) = Make(predictor: new AlwaysNotTakenPredictor());
        Load(mem, program);
        RevolutionResult result = train.Run();

        Assert.Equal(42u, Reg(train, 10));

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(0L, snap.Counters["branch_misses"]);
    }

    // ── DoCache / TLB integration ───────────────────────────────────────────────

    private static MemoryConfig SmallICache(int missLatency = 5) =>
        new(64, 4, 16, missLatency);

    private static MemoryConfig SmallDCache(int missLatency = 5) =>
        new(64, 4, 16, missLatency);

    [Fact]
    public void WithICache_CorrectResultStillProduced() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem, 0, true, null,
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
            new Rv32Mechanism(), mem, 0, true, null,
            SmallICache()
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
            new Rv32Mechanism(), mem, 0, true, null,
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
        // The SW is a write-through, so backing stays consistent; the LW should
        // fill the cache and return the stored value.
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem,
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
            new Rv32Mechanism(), mem,
            dMemConfig: SmallDCache()
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

    // De-risks the shared BΔI counter-wiring pattern hand-edited into all five trains
    // (SingleCycle/FiveStage/Superscalar/Ooo/Cpr): the guard-widen + BdiCache? UpdateCacheStat
    // overload + call was only exercised end to end by an OoO-specific test
    // (MicroCheckpointTests.L2Bdi_Equivalence...) before this — a green suite otherwise doesn't
    // prove the identical edit works on the other four trains too.
    [Fact]
    public void WithCompressedL2_LoadInstructions_RecordL2BdiDialCounters() {
        var mem = new FlatMemory(4096);
        // 1 way, 1 line D-L1 so alternating between two lines forces every access to be an L1
        // miss, keeping L2Bdi genuinely exercised (not just touched once on a cold fill).
        MemoryConfig dCfg = new(
            32, 1, 32, 4,
            128, 2, 32, 8,
            L2Compression: CompressionKind.Bdi
        );
        // Configure both I and D paths (matching the "unified" convention the L2Cache/L2Bdi
        // properties assume — see their doc comments) so the D-specific L2Bdi is reachable
        // through the train's own accessor rather than needing a D-only one.
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, iMemConfig: dCfg, dMemConfig: dCfg);
        Load(
            mem,
            0x00000093, // addi x1, x0, 0
            0x0000a203, // lw x4, 0(x1)
            0x400a283,  // lw x5, 64(x1)
            0x0000a303, // lw x6, 0(x1)
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();

        Assert.NotNull(train.L2Bdi);
        Assert.True(train.L2Bdi!.Misses > 0, "L2Bdi should record misses for the cold lines");
        Assert.True(train.L2Bdi.Hits > 0, "L2Bdi should record a hit on the re-accessed line");
        Assert.Null(train.L2Cache); // compressed slot leaves the typed field null

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.GetValueOrDefault("l2_dcache_misses") > 0,
            "l2_dcache_misses dial counter should reflect L2Bdi activity"
        );
        Assert.True(
            snap.Counters.GetValueOrDefault("l2_dcache_hits") > 0,
            "l2_dcache_hits dial counter should reflect L2Bdi activity"
        );
    }

    // Same de-risking rationale as WithCompressedL2_LoadInstructions_RecordL2BdiDialCounters,
    // for the parallel CeaserCache wiring hand-edited into all five trains.
    [Fact]
    public void WithCeaserL2_LoadInstructions_RecordL2CeaserDialCounters() {
        var mem = new FlatMemory(4096);
        MemoryConfig dCfg = new(
            32, 1, 32, 4,
            128, 2, 32, 8,
            L2Variant: CacheVariantKind.Ceaser
        );
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, iMemConfig: dCfg, dMemConfig: dCfg);
        Load(
            mem,
            0x00000093, // addi x1, x0, 0
            0x0000a203, // lw x4, 0(x1)
            0x400a283,  // lw x5, 64(x1)
            0x0000a303, // lw x6, 0(x1)
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();

        Assert.NotNull(train.L2Ceaser);
        Assert.True(train.L2Ceaser!.Misses > 0, "L2Ceaser should record misses for the cold lines");
        Assert.True(train.L2Ceaser.Hits > 0, "L2Ceaser should record a hit on the re-accessed line");
        Assert.Null(train.L2Cache); // Ceaser-variant slot leaves the typed field null

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.GetValueOrDefault("l2_dcache_misses") > 0,
            "l2_dcache_misses dial counter should reflect L2Ceaser activity"
        );
        Assert.True(
            snap.Counters.GetValueOrDefault("l2_dcache_hits") > 0,
            "l2_dcache_hits dial counter should reflect L2Ceaser activity"
        );
    }

    [Fact]
    public void WithTlb_IdentityMapping_CorrectResult() {
        var mem = new FlatMemory(4096);
        var iTlbConfig = new MemoryConfig(
            TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 8
        );
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem, 0, true, null,
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

    // ── Store buffer ──────────────────────────────────────────────────────────

    [Fact]
    public void StoreBuffer_StoreFollowedByLoad_Forwards() {
        // sw x2, 0(x1) then immediately lw x3, 0(x1) at the same address.
        // With D-cache (write-through, no-write-allocate) and a store buffer:
        // the load should forward from the buffer → store_forwards == 1, and
        // x3 holds the stored value.
        //
        //   addi x1, x0, 256   (base address)
        //   addi x2, x0, 77    (value to store)
        //   sw   x2, 0(x1)     → buffered in StoreBuffer
        //   lw   x3, 0(x1)     → forwarded from StoreBuffer
        //   ebreak
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem,
            dMemConfig: SmallDCache(),
            storeBufferCapacity: 8
        );
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0x04D00113, // addi x2, x0, 77
            0x0020a023, // sw   x2, 0(x1)
            0x0000a183, // lw   x3, 0(x1)
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();

        Assert.Equal(77u, (uint)train.ArchState.IntegerRegisters.Read(3));

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters.ContainsKey("store_forwards"), "store_forwards counter should exist");
        Assert.Equal(1L, snap.Counters["store_forwards"]);
    }

    [Fact]
    public void StoreBuffer_WithoutCache_CorrectResult() {
        // Store buffer works without D-cache too: stores go to FlatMemory on drain,
        // and forwards happen within the 1-tick window.
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem,
            storeBufferCapacity: 4
        );
        Load(
            mem,
            0x10000093, // addi x1, x0, 256
            0x02A00113, // addi x2, x0, 42
            0x0020a023, // sw   x2, 0(x1)
            0x0000a183, // lw   x3, 0(x1)
            0x00100073  // ebreak
        );
        train.Run();
        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(3));
    }

    [Fact]
    public void StoreBuffer_NoForward_WhenNoDependency() {
        // A store to 0x100 and a load from 0x200 should NOT forward.
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem,
            storeBufferCapacity: 8
        );
        mem.Write(0x200, 55, 4);
        Load(
            mem,
            0x10000093, // addi x1, x0, 256   (0x100)
            0x20000113, // addi x2, x0, 512   (0x200)
            0x02A00193, // addi x3, x0, 42
            0x0030a023, // sw   x3, 0(x1)     (store to 0x100)
            0x00012203, // lw   x4, 0(x2)     (load from 0x200 — no forward)
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();

        Assert.Equal(55u, (uint)train.ArchState.IntegerRegisters.Read(4));

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(0L, snap.Counters["store_forwards"]);
    }

    [Fact]
    public void OpcodeHistogram_CountsRetiredInstructions() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        Load(
            mem,
            0x00100093, // addi x1, x0, 1
            0x00100113, // addi x2, x0, 1
            0x002081b3, // add  x3, x1, x2
            0x00100073  // ebreak
        );
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Histograms.ContainsKey("opcodes"), "opcodes histogram should exist");
        IReadOnlyDictionary<string, long> opcodes = snap.Histograms["opcodes"];
        Assert.Equal(2, opcodes["RvAddi"]);
        Assert.Equal(1, opcodes["RvAdd"]);
        Assert.False(opcodes.ContainsKey("RvEbreak"), "EBREAK should not retire");
    }

    [Fact]
    public void WithTlb_ColdMiss_RecordedInDialBoard() {
        var mem = new FlatMemory(4096);
        var iTlbConfig = new MemoryConfig(
            TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 8
        );
        var train = new FiveStageTrain(
            new Rv32Mechanism(), mem, 0, true, null,
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

    // ── Interrupt dispatch (end-to-end) ──────────────────────────────────────

    // Helpers shared by the interrupt tests.
    private static ulong ReadCsr(FiveStageTrain t, uint addr) =>
        t.ArchState.SystemRegisters.Read(addr, RvPrivilege.Machine);

    private static void WriteCsr(FiveStageTrain t, uint addr, uint val) =>
        t.ArchState.SystemRegisters.Write(addr, val, RvPrivilege.Machine);

    // Layout used by interrupt tests:
    //   0x0000..0x001F  five NOPs, then EBREAK (fallback halt if no interrupt)
    //   0x0100          handler: EBREAK (halts the pipeline in the handler)
    private static FiveStageTrain MakeInterruptFixture() {
        var mem = new FlatMemory(4096);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        const uint nop = 0x00000013u; // addi x0, x0, 0
        const uint ebreak = 0x00100073u;
        const uint jalSelf = 0x0000006Fu; // jal x0, 0 — halt via self-jump backstop
        Load(mem, nop, nop, nop, nop, nop, ebreak);
        // Handler uses a self-jump so the backstop halts cleanly regardless of mtvec.
        // (ebreak with mtvec≠0 now generates a Breakpoint exception, not a halt.)
        mem.Load(0x100, BitConverter.GetBytes(jalSelf));
        return train;
    }

    [Fact]
    public void FiveStage_MachineTimerInterrupt_EntersHandler_AndSetsCorrectMepc() {
        FiveStageTrain train = MakeInterruptFixture();

        WriteCsr(train, CsrFile.Mtvec, 0x0100);               // handler at 0x0100
        WriteCsr(train, CsrFile.Mip, 1u << 7);                // MTI pending
        WriteCsr(train, CsrFile.Mie, 1u << 7);                // MTI enabled
        WriteCsr(train, CsrFile.Mstatus, CsrFile.MstatusMie); // MIE=1

        train.Run();

        // The interrupt fires after the first NOP (at PC=0x0000) retires.
        // mepc must be the PC of the first unretired instruction = 0x0004.
        Assert.Equal(0x0004uL, ReadCsr(train, CsrFile.Mepc));
        Assert.Equal(
            unchecked((uint)RvTrapCause.MachineTimerInterrupt),
            (uint)ReadCsr(train, CsrFile.Mcause)
        );
        // After trap entry: MIE=0, MPIE=1 (old MIE), MPP=3 (M-mode).
        ulong mstatus = ReadCsr(train, CsrFile.Mstatus);
        Assert.Equal(0uL, (mstatus >> 3) & 1);  // MIE = 0 (disabled during handler)
        Assert.Equal(1uL, (mstatus >> 7) & 1);  // MPIE = 1 (saved MIE)
        Assert.Equal(3uL, (mstatus >> 11) & 3); // MPP = 3 (was M-mode)
    }

    [Fact]
    public void FiveStage_InterruptDisabled_MIE_Clear_DoesNotFire() {
        FiveStageTrain train = MakeInterruptFixture();

        // MTI pending and enabled in mie, but mstatus.MIE = 0.
        WriteCsr(train, CsrFile.Mip, 1u << 7);
        WriteCsr(train, CsrFile.Mie, 1u << 7);
        // Do NOT set mstatus.MIE.

        train.Run();

        // Pipeline must retire through all NOPs and halt at the EBREAK in the main sequence.
        // mepc stays 0 (no interrupt taken).
        Assert.Equal(0uL, ReadCsr(train, CsrFile.Mepc));
    }

    [Fact]
    public void Pipeline_TrueOraclePredictor_ZeroBranchMisses() {
        uint[] program = [
            0x00000093, // addi x1, x0, 0
            0x00A00113, // addi x2, x0, 10
            0x00108093, // addi x1, x1, 1
            0xFE20CEE3, // blt  x1, x2, -4  (loops 9 times, then falls through)
            0x00100073, // ebreak
        ];

        // Pre-pass: collect the dynamic branch trace via functional simulation
        var preMem = new FlatMemory(4096);
        Load(preMem, program);
        var mechanism = new Rv32Mechanism();
        var recorder = new BranchTraceRecorder(mechanism.Decoder);
        new SingleCycleTrain(mechanism, preMem, commitObserver: recorder).Run();

        // Main pass: replay the oracle trace — every prediction must be correct
        (FiveStageTrain train, FlatMemory mem) = Make(predictor: new OracleBp(recorder.Trace));
        Load(mem, program);
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.Equal(0L, snap.Counters["branch_misses"]);
    }

    // ── Vector-crypto element-group hazard tracking ─────────────────────────────
    // FiveStageTrain widens VectorRawHazard with a runtime LMUL-derived register span for these
    // instructions rather than rejecting them outright — see ITooth.RuntimeVectorRegisterSpan /
    // MaxRuntimeVectorRegisterSpan.

    [Fact]
    public void Pipeline_ElementGroupVectorCryptoInstruction_ProducesCorrectResult() {
        // vsetivli x0, 16, e32,m4,ta,ma (vtypei=0xD2); vaesem.vv v8, v12 (funct6=0x28,vs1=2,
        // opcode=0x77 — the vector-crypto major opcode, NOT the standard OP-V 0x57). LMUL=4 means
        // this writes v8..v11, one AES block (element group) per register.
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0xCD287057, // vsetivli x0, 16, e32,m4,ta,ma
            0xA2C12477, // vaesem.vv v8, v12
            0x00100073
        );

        var rv32 = (Rv32ArchState)train.ArchState;
        var states = new byte[4][];
        var keys = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = new byte[16];
            keys[g] = new byte[16];
            for (var i = 0; i < 16; i++) {
                states[g][i] = (byte)(0x10 + g * 16 + i);
                keys[g][i] = (byte)(0xA0 + g * 16 + i);
            }

            rv32.VectorRegisters.Write(8 + g, states[g]);
            rv32.VectorRegisters.Write(12 + g, keys[g]);
        }

        train.Run();

        for (var g = 0; g < 4; g++) Assert.Equal(AesEmReference(states[g], keys[g]), rv32.VectorRegisters.Read(8 + g));
    }

    [Fact]
    public void Pipeline_ElementGroupVectorCrypto_StallsForNonBaseRegisterConsumer() {
        // vaesem.vv v8, v12 at LMUL=4 writes v8..v11 via a deferred (WB-time) SideEffect. The very
        // next instruction reads v11 — the producer's *last*, non-base register — via a
        // whole-register move (vmv1r.v, which ignores vl/vtype entirely, so it can immediately
        // follow the crypto op without also needing a vl compatible with a single, non-LMUL-aware
        // register). A hazard check that only compares the producer's base register (vd=8) against
        // the consumer's read (11) would miss this RAW entirely and let the move read v11's stale
        // pre-existing value instead of the freshly computed AES output — confirmed by temporarily
        // reverting VectorRawHazard to the single-register check, which fails this test.
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0xCD287057, // vsetivli x0, 16, e32,m4,ta,ma
            0xA2C12477, // vaesem.vv v8, v12
            0x9EB03A57, // vmv1r.v v20, v11
            0x00100073
        );

        var rv32 = (Rv32ArchState)train.ArchState;
        var state = new byte[16];
        var key = new byte[16];
        for (var i = 0; i < 16; i++) {
            state[i] = (byte)(0x10 + i);
            key[i] = (byte)(0xA0 + i);
        }

        for (var g = 0; g < 4; g++) {
            rv32.VectorRegisters.Write(8 + g, state);
            rv32.VectorRegisters.Write(12 + g, key);
        }

        train.Run();

        Assert.Equal(AesEmReference(state, key), rv32.VectorRegisters.Read(20));
    }

    [Fact]
    public void Pipeline_ElementGroupVectorCrypto_ConsumerReadsNonBaseProducerRegister() {
        // vaesem.vv v16, v8 at LMUL=4 reads its round-key group from vs2=v8, spanning v8..v11 —
        // ITooth.VectorSourceRegisters only lists the base (v8), so a hazard check that doesn't
        // widen the CONSUMER's read span (MaxRuntimeVectorRegisterSpan) would only ever compare
        // producer writes against v8 and miss a producer that instead writes v11, v10, or v9. The
        // preceding vmv1r.v v11, v5 is exactly that: it writes only v11 (single register), the
        // non-base register vaesem.vv's group-3 key comes from. Without the widened consumer span,
        // vaesem.vv would read v11's stale pre-existing (wrong) key instead of the fresh one just
        // moved in from v5, and group 3's ciphertext (v19) would come out wrong.
        (FiveStageTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            0xCD287057, // vsetivli x0, 16, e32,m4,ta,ma
            0x9E5035D7, // vmv1r.v v11, v5
            0xA2812877, // vaesem.vv v16, v8
            0x00100073
        );

        var rv32 = (Rv32ArchState)train.ArchState;
        var state = new byte[16];
        var correctKey = new byte[16];
        var staleKey = new byte[16];
        for (var i = 0; i < 16; i++) {
            state[i] = (byte)(0x10 + i);
            correctKey[i] = (byte)(0xA0 + i);
            staleKey[i] = (byte)(0xFF - i);
        }

        for (var g = 0; g < 3; g++) {
            rv32.VectorRegisters.Write(8 + g, correctKey); // groups 0-2's keys, unrelated to the hazard
            rv32.VectorRegisters.Write(16 + g, state);
        }

        rv32.VectorRegisters.Write(11, staleKey);  // v11 starts wrong; vmv1r.v must overwrite it in time
        rv32.VectorRegisters.Write(5, correctKey); // the real group-3 key, moved into v11
        rv32.VectorRegisters.Write(19, state);

        train.Run();

        Assert.Equal(AesEmReference(state, correctKey), rv32.VectorRegisters.Read(19));
    }

    private static byte GfMul(byte a, byte b) {
        byte result = 0;
        for (var i = 0; i < 8; i++) {
            if ((b & 1) != 0) result ^= a;
            bool hi = (a & 0x80) != 0;
            a <<= 1;
            if (hi) a ^= 0x1B;
            b >>= 1;
        }

        return result;
    }

    private static byte GfInv(byte a) {
        if (a == 0) return 0;
        for (var c = 1; c < 256; c++)
            if (GfMul(a, (byte)c) == 1)
                return (byte)c;
        throw new InvalidOperationException();
    }

    private static byte Rotl8(byte x, int n) => (byte)((x << n) | (x >> (8 - n)));

    private static byte SboxFwd(byte x) {
        byte inv = GfInv(x);
        return (byte)(inv ^ Rotl8(inv, 1) ^ Rotl8(inv, 2) ^
                      Rotl8(inv, 3) ^ Rotl8(inv, 4) ^ 0x63);
    }

    private static byte GfMulSmall(byte x, int y) {
        byte Xtime(byte v) => (byte)((v << 1) ^ ((v & 0x80) != 0 ? 0x1B : 0));
        byte r = 0;
        if ((y & 0x1) != 0) r ^= x;
        if ((y & 0x2) != 0) r ^= Xtime(x);
        if ((y & 0x4) != 0) r ^= Xtime(Xtime(x));
        if ((y & 0x8) != 0) r ^= Xtime(Xtime(Xtime(x)));
        return r;
    }

    // Independent reference — SubBytes/ShiftRows/MixColumns then XOR the round key, computed by
    // hand from the FIPS-197 AES S-box/MixColumns matrix rather than by calling any of the
    // executor's own AES helpers.
    private static byte[] AesEmReference(byte[] state, byte[] key) {
        var sb = new byte[16];
        for (var i = 0; i < 16; i++) sb[i] = SboxFwd(state[i]);
        var sr = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            sr[i] = sb[4 * ((c + r) % 4) + r];
        }

        var mix = new byte[16];
        for (var c = 0; c < 4; c++) {
            byte s0 = sr[4 * c], s1 = sr[4 * c + 1], s2 = sr[4 * c + 2], s3 = sr[4 * c + 3];
            mix[4 * c] = (byte)(GfMulSmall(s0, 0x2) ^ GfMulSmall(s1, 0x3) ^ s2 ^ s3);
            mix[4 * c + 1] = (byte)(s0 ^ GfMulSmall(s1, 0x2) ^ GfMulSmall(s2, 0x3) ^ s3);
            mix[4 * c + 2] = (byte)(s0 ^ s1 ^ GfMulSmall(s2, 0x2) ^ GfMulSmall(s3, 0x3));
            mix[4 * c + 3] = (byte)(GfMulSmall(s0, 0x3) ^ s1 ^ s2 ^ GfMulSmall(s3, 0x2));
        }

        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = (byte)(mix[i] ^ key[i]);
        return result;
    }

    // ── Ecall implicit-register hazard ──────────────────────────────────────────

    private sealed class RecordingSyscallHandler : ISyscallHandler {
        public ulong? LastSyscallNum { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            LastSyscallNum = syscallNum;
            return new ExecuteResult { RequestHalt = true, };
        }
    }
}