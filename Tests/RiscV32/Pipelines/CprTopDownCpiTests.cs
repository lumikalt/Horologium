using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     TMA slot accounting (Yasin, ISPASS 2014) and interval-analysis CPI stacks
///     (Eyerman et al., ASPLOS 2006) on <see cref="CprTrain" /> — the same dominance-style
///     scenarios as the OooeTrain suites, exercising the CPR-specific hooks: checkpoint
///     rollback recovery bubbles and misprediction windows, rename backpressure as a
///     backend stall, and the head checkpoint's first uncommitted entry as the blocked head.
/// </summary>
public class CprTopDownCpiTests {
    private const uint Ebreak = 0x00100073;

    private static (CprTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null
    ) {
        var mem = new FlatMemory(65536);
        var train = new CprTrain(
            new Rv32Mechanism(), mem,
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
        Assert.True(snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters["retired"]);
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