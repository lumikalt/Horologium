#region

using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     TMA slot accounting (Yasin, ISPASS 2014) and interval-analysis CPI stacks
///     (Eyerman et al., ASPLOS 2006) on <see cref="CprTrain" /> — the same dominance-style
///     scenarios as the OooTrain suites, exercising the CPR-specific hooks: checkpoint
///     rollback recovery bubbles and misprediction windows, rename backpressure as a
///     backend stall, and the head checkpoint's first uncommitted entry as the blocked head.
/// </summary>
public class CprTopDownCpiTests {
    private const uint Ebreak = 0x00100073;

    private static (CprTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        bool enableMacroFusion = false
    ) {
        var mem = new FlatMemory(65536);
        var train = new CprTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem,
            issueWidth: issueWidth,
            iMemConfig: iMemConfig,
            dMemConfig: dMemConfig,
            fuLatency: fuLatency
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

    private static (TopDownBreakdown Td, CpiStack Cpi, DialBoardSnapshot Snap) RunAndAnalyze(CprTrain train) {
        train.Run();
        DialBoardSnapshot snap = train.SnapshotPipeline();
        TopDownBreakdown? td = TopDownBreakdown.FromSnapshot(snap);
        CpiStack? cpi = CpiStack.FromSnapshot(snap);
        Assert.NotNull(td);
        Assert.NotNull(cpi);
        // Structural invariants shared by every scenario.
        Assert.True(cpi.Base >= 0.0);
        Assert.Equal(cpi.Total, cpi.Base + cpi.MissComponents, 6);
        double sum = td.FrontendBound + td.BadSpeculation + td.Retiring + td.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.1);
        return (td, cpi, snap);
    }

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Slt(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0110011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    [Fact]
    public void MacroFusion_KeepsSlotsRetiredAtOnePerFusedPair() {
        // Same regression as SuperscalarTopDownTests/TopDownTests: a fused SLT+BNE pair
        // retires as 2 architectural instructions (ITooth.ArchInstructionCount) but through
        // exactly one checkpoint entry/slot. Feeding the architectural "retired" count into
        // TMA's slotsRetired term would make SlotsRetired exceed SlotsIssued once fusion
        // fires, inflating Retiring and clamping Bad Speculation to zero.
        //
        // The branch is engineered NOT taken (matching the default AlwaysNotTakenPredictor)
        // so this OoO machine's speculative dispatch never has a wrong path to squash —
        // isolating the fusion/slot-accounting question from unrelated bad-speculation noise.
        (CprTrain train, FlatMemory mem) = Make(enableMacroFusion: true);
        const int blockWords = 6;
        var program = new uint[blockWords * 20 + 1];
        for (var i = 0; i < 20; i++) {
            int b = i * blockWords;
            program[b + 0] = Addi(1, 0, 3);
            program[b + 1] = Addi(2, 0, 5);
            program[b + 2] = Slt(5, 2, 1);  // x5 = (5 < 3) = 0
            program[b + 3] = Bne(5, 0, 12); // not taken: falls through, as predicted
            program[b + 4] = Addi(3, 0, 111);
            program[b + 5] = Addi(3, 0, 222);
        }

        program[blockWords * 20] = CprTopDownCpiTests.Ebreak;
        Load(mem, program);

        (TopDownBreakdown td, _, DialBoardSnapshot snap) = RunAndAnalyze(train);

        Assert.True(snap.Counters["macro_fusions"] > 0, "expected the SLT+BNE idiom to fuse");
        // The bug this guards against: "retired" scales by ArchInstructionCount (2 per fused
        // pair), so on a fusion-heavy program it exceeds SlotsIssued — exactly the condition
        // that would have broken SlotsIssued >= SlotsRetired had "retired" been fed into TMA's
        // slotsRetired term directly (as it was before td_slots_retired existed).
        Assert.True(
            snap.Counters["retired"] > snap.Counters[TopDownBreakdown.SlotsIssuedCounter],
            "expected fusion to inflate the architectural retired count past SlotsIssued"
        );
        Assert.True(
            snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters[TopDownBreakdown.SlotsRetiredCounter],
            "SlotsRetired must never exceed SlotsIssued, even with fusion inflating the architectural retired count"
        );
        // Ties the computed breakdown back to the slot counter, not the architectural one:
        // this is what would actually fail if ComputeTopDown/FromSnapshot ever regressed to
        // reading "retired" again.
        Assert.Equal(
            snap.Counters[TopDownBreakdown.SlotsRetiredCounter]
          / (double)snap.Counters[TopDownBreakdown.TotalSlotsCounter],
            td.Retiring, 12
        );
        // Ties the live train dial (backed by ComputeTopDown, not FromSnapshot) to the same
        // value: a regression that swapped only ComputeTopDown back to _retiredCounter would
        // otherwise slip past every assertion above.
        Assert.Equal(td.Retiring, snap.Dials["td_retiring"], 12);
    }

    [Fact]
    public void Retiring_AndBase_DominateOnIndependentAluCode() {
        (CprTrain train, FlatMemory mem) = Make();
        var program = new uint[257];
        for (var i = 0; i < 256; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[256] = CprTopDownCpiTests.Ebreak;
        Load(mem, program);

        (TopDownBreakdown td, CpiStack cpi, DialBoardSnapshot snap) = RunAndAnalyze(train);
        Assert.True(td.Retiring > 0.5, $"Retiring {td.Retiring:P1} should exceed 50%");
        Assert.True(cpi.Base > 0.5 * cpi.Total, $"base {cpi.Base:F3} should dominate CPI {cpi.Total:F3}");
        Assert.Equal(snap.Counters["cycles"] * 2, snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        Assert.True(
            snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters[TopDownBreakdown.SlotsRetiredCounter]
        );
    }

    [Fact]
    public void BadSpeculation_FlagsMispredictedLoop_ViaCheckpointRecovery() {
        // Countdown loop against the default always-not-taken predictor: every taken
        // backward branch forces a checkpoint rollback (CPR's recovery path), so wrong-path
        // slots + recovery bubbles land in Bad Speculation, attributed to branches, and the
        // CPI stack's bpred component (window + refill) must dominate its base.
        (CprTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            Addi(1, 0, 200), // addi x1, x0, 200
            Addi(1, 1, -1),  // loop: addi x1, x1, -1
            0xFE009EE3,      // bne x1, x0, -4
            CprTopDownCpiTests.Ebreak
        );

        (TopDownBreakdown td, CpiStack cpi, _) = RunAndAnalyze(train);
        Assert.True(
            td.BadSpeculation > 0.15,
            $"Bad Speculation {td.BadSpeculation:P1} should be significant"
        );
        Assert.True(td.BranchMispredicts > td.MachineClears);
        Assert.True(
            cpi.BranchMisprediction > cpi.Base,
            $"bpred {cpi.BranchMisprediction:F3} should exceed base {cpi.Base:F3}"
        );
    }

    [Fact]
    public void FrontendBound_AndL1I_FlagIcacheMisses() {
        (CprTrain train, FlatMemory mem) = Make(
            iMemConfig: new MemoryConfig(256, CacheBlockBytes: 32, CacheMissLatency: 20)
        );
        var program = new uint[513];
        for (var i = 0; i < 512; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[512] = CprTopDownCpiTests.Ebreak;
        Load(mem, program);

        (TopDownBreakdown td, CpiStack cpi, _) = RunAndAnalyze(train);
        Assert.True(td.FrontendBound > 0.5, $"Frontend Bound {td.FrontendBound:P1} should dominate");
        Assert.True(td.FetchLatencyBound > td.FetchBandwidthBound);
        Assert.True(cpi.L1ICache > 0.3 * cpi.Total, $"L1I {cpi.L1ICache:F3} should be significant");
        Assert.True(cpi.L1ICache > cpi.Base);
    }

    [Fact]
    public void BackendBound_AndResource_FlagDependentDivChain() {
        (CprTrain train, FlatMemory mem) = Make(fuLatency: new FuLatencyConfig(DivLatency: 20));
        var program = new uint[43];
        program[0] = Addi(1, 0, 1000);
        program[1] = Addi(2, 0, 3);
        for (var i = 0; i < 40; i++) program[2 + i] = 0x0220C0B3; // div x1, x1, x2
        program[42] = CprTopDownCpiTests.Ebreak;
        Load(mem, program);

        (TopDownBreakdown td, CpiStack cpi, _) = RunAndAnalyze(train);
        Assert.True(td.BackendBound > 0.5, $"Backend Bound {td.BackendBound:P1} should dominate");
        Assert.True(td.CoreBound > td.MemoryBound);
        Assert.True(cpi.ResourceStall > 0.3 * cpi.Total, $"resource {cpi.ResourceStall:F3} should be significant");
        Assert.Equal(0.0, cpi.L2DCache);
    }

    [Fact]
    public void MemoryBound_AndL2D_FlagDependentLoadMissChain() {
        // Pointer chase across 64 lines missing both D-cache levels; longer than the LQ/IQ
        // window so the backend backpressures dispatch while the head load's miss is
        // outstanding — long L2 backend miss in both accountings.
        (CprTrain train, FlatMemory mem) = Make(
            dMemConfig: new MemoryConfig(
                512, CacheBlockBytes: 32, CacheMissLatency: 10,
                L2CapacityBytes: 1024, L2BlockBytes: 32, L2MissLatency: 50
            )
        );

        for (var i = 0; i < 64; i++) {
            uint address = 0x1000u + 64u * (uint)i;
            uint next = address + 64;
            mem.Load(address, [(byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)(next >> 24),]);
        }

        var program = new uint[67];
        program[0] = 0x00001097; // auipc x1, 0x1 → x1 = 0x1000
        program[1] = Addi(1, 1, 0);
        for (var i = 0; i < 64; i++) program[2 + i] = 0x0000A083; // lw x1, 0(x1)
        program[66] = CprTopDownCpiTests.Ebreak;
        Load(mem, program);

        (TopDownBreakdown td, CpiStack cpi, _) = RunAndAnalyze(train);
        Assert.True(td.BackendBound > 0.5, $"Backend Bound {td.BackendBound:P1} should dominate");
        Assert.True(td.MemoryBound > td.CoreBound);
        Assert.True(cpi.L2DCache > 0.3 * cpi.Total, $"L2D {cpi.L2DCache:F3} should be significant");
        Assert.True(cpi.L2DCache > cpi.L1DCache);
    }
}