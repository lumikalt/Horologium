using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

namespace Tests.RiscV32;

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
        (FiveStageTrain twoB, FlatMemory memTwoB) = Make(predictor: new NBitPredictor());
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
    public void Pipeline_RasPredictsReturn_CorrectResultAndOneJalMiss() {
        // jal ra → func causes 1 miss (AlwaysNotTaken predicts not-taken)
        // jalr x0, ra (return) is predicted by RAS → 0 misses for the return
        // Total branch_misses == 1
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
        Assert.Equal(1L, snap.Counters["branch_misses"]);
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
        Load(mem, nop, nop, nop, nop, nop, ebreak);
        // Write EBREAK to handler address using unchecked byte truncation.
        mem.Load(0x100, BitConverter.GetBytes(ebreak));
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
}