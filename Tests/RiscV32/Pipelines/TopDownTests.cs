using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) slot accounting on OooeTrain.
///     <para>
///         Each test runs a hand-assembled RV32I program engineered to stress one TMA
///         category and asserts that the breakdown flags that category as dominant — the
///         same validation style the paper uses (matrix-multiply → Backend, branchy code
///         → Bad Speculation, …). Fractions are asserted as dominance relations rather
///         than exact values so the tests survive microarchitectural tuning.
///     </para>
/// </summary>
public class TopDownTests {
    private const uint Ebreak = 0x00100073;

    private static (OooeTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        int robCapacity = 32,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null
    ) {
        var mem = new FlatMemory(65536);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
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

    private static TopDownBreakdown RunAndAnalyze(OooeTrain train) {
        train.Run();
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(train.SnapshotPipeline());
        Assert.NotNull(breakdown);
        return breakdown;
    }

    // addi x{rd}, x0, {imm}
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    // ── Slot-accounting invariants ─────────────────────────────────────────────

    [Fact]
    public void Level1_FractionsSumToOne_AndCountersAreConsistent() {
        (OooeTrain train, FlatMemory mem) = Make();
        var program = new uint[65];
        for (var i = 0; i < 64; i++) program[i] = Addi(1 + i % 8, 0, i);
        program[64] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        DialBoardSnapshot snap = train.SnapshotPipeline();

        // TotalSlots is exactly issueWidth × cycles, including lump-sum stall cycles.
        Assert.Equal(snap.Counters["cycles"] * 2, snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        // Everything retired was previously issued.
        Assert.True(snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters["retired"]);

        // Level 1 sums to 1: exact when backend-bound absorbs the residual; end-of-run
        // in-flight leftovers can only overshoot slightly on a short program.
        double sum = breakdown.FrontendBound + breakdown.BadSpeculation
                                             + breakdown.Retiring + breakdown.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.05);

        // The four dials mirror the snapshot-derived breakdown.
        Assert.Equal(breakdown.FrontendBound, snap.Dials["td_frontend_bound"], 12);
        Assert.Equal(breakdown.BadSpeculation, snap.Dials["td_bad_speculation"], 12);
        Assert.Equal(breakdown.Retiring, snap.Dials["td_retiring"], 12);
        Assert.Equal(breakdown.BackendBound, snap.Dials["td_backend_bound"], 12);
    }

    // ── Category dominance ─────────────────────────────────────────────────────

    [Fact]
    public void Retiring_DominatesOnIndependentAluCode() {
        // 256 independent single-cycle ALU ops on a 2-wide machine with 2 ALU ports:
        // the machine sustains full width, so Retiring should dwarf every stall category.
        (OooeTrain train, FlatMemory mem) = Make();
        var program = new uint[257];
        for (var i = 0; i < 256; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[256] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(breakdown.Retiring > 0.5, $"Retiring {breakdown.Retiring:P1} should exceed 50%");
        Assert.True(breakdown.Retiring > breakdown.FrontendBound);
        Assert.True(breakdown.Retiring > breakdown.BadSpeculation);
        Assert.True(breakdown.Retiring > breakdown.BackendBound);
    }

    [Fact]
    public void BadSpeculation_FlagsMispredictedLoop() {
        // Countdown loop whose backward branch is taken 199 times against the default
        // always-not-taken predictor: every iteration mispredicts, so wrong-path slots
        // plus recovery bubbles must be flagged, and attributed to branches rather than
        // machine clears (there are no memory-order violations or traps in the loop).
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            Addi(1, 0, 200), // addi x1, x0, 200
            Addi(1, 1, -1),  // loop: addi x1, x1, -1
            0xFE009EE3,      // bne x1, x0, -4
            TopDownTests.Ebreak
        );

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.BadSpeculation > 0.15,
            $"Bad Speculation {breakdown.BadSpeculation:P1} should be significant"
        );
        Assert.True(breakdown.BadSpeculation > breakdown.BackendBound);
        Assert.True(breakdown.BranchMispredicts > breakdown.MachineClears);
    }

    [Fact]
    public void FrontendBound_FlagsIcacheMisses() {
        // 512 straight-line instructions (2 KiB) through a 256-byte I-cache with a
        // 20-cycle miss penalty: fetch starvation dominates, and since every starved
        // cycle delivers zero uops it lands under Fetch Latency, not Fetch Bandwidth.
        (OooeTrain train, FlatMemory mem) = Make(
            iMemConfig: new MemoryConfig(256, CacheBlockBytes: 32, CacheMissLatency: 20)
        );
        var program = new uint[513];
        for (var i = 0; i < 512; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[512] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.FrontendBound > 0.5,
            $"Frontend Bound {breakdown.FrontendBound:P1} should dominate"
        );
        Assert.True(breakdown.FrontendBound > breakdown.BackendBound);
        Assert.True(breakdown.FrontendBound > breakdown.Retiring);
        Assert.True(breakdown.FetchLatencyBound > breakdown.FetchBandwidthBound);
    }

    [Fact]
    public void BackendBound_FlagsDependentDivChain() {
        // Serialized 20-cycle divides: dispatch keeps up but the backend can only start
        // one uop every 20 cycles → Backend Bound, attributed to the core (no loads at
        // all, so Memory Bound must stay at zero).
        (OooeTrain train, FlatMemory mem) = Make(fuLatency: new FuLatencyConfig(DivLatency: 20));
        var program = new uint[23];
        program[0] = Addi(1, 0, 1000);
        program[1] = Addi(2, 0, 3);
        for (var i = 0; i < 20; i++) program[2 + i] = 0x0220C0B3; // div x1, x1, x2
        program[22] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.BackendBound > 0.5,
            $"Backend Bound {breakdown.BackendBound:P1} should dominate"
        );
        Assert.True(breakdown.BackendBound > breakdown.FrontendBound);
        Assert.True(breakdown.CoreBound > breakdown.MemoryBound);
        Assert.Equal(0.0, breakdown.MemoryBound);
    }

    [Fact]
    public void MemoryBound_FlagsDependentLoadMissChain() {
        // Pointer chase across 64 distinct cache lines with a 50-cycle miss penalty:
        // every load misses and depends on the previous one, so execution starves with
        // a load in flight — Backend Bound, attributed to memory rather than the core.
        // The chain is longer than the 32-entry ROB so the backend stall (full ROB)
        // stays asserted; a shorter chain would let dispatch drain early and count the
        // idle slots as fetch bubbles instead.
        (OooeTrain train, FlatMemory mem) = Make(
            dMemConfig: new MemoryConfig(512, CacheBlockBytes: 32, CacheMissLatency: 50)
        );

        // Seed the pointer chain: word at 0x1000 + 64i points to 0x1000 + 64(i+1).
        for (var i = 0; i < 64; i++) {
            uint address = 0x1000u + 64u * (uint)i;
            uint next = address + 64;
            mem.Load(address, [(byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)(next >> 24),]);
        }

        var program = new uint[67];
        program[0] = 0x00001097; // auipc x1, 0x1  → x1 = pc + 0x1000 = 0x1000
        program[1] = Addi(1, 1, 0);
        for (var i = 0; i < 64; i++) program[2 + i] = 0x0000A083; // lw x1, 0(x1)
        program[66] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.BackendBound > 0.5,
            $"Backend Bound {breakdown.BackendBound:P1} should dominate"
        );
        Assert.True(breakdown.MemoryBound > breakdown.CoreBound);
        Assert.True(breakdown.MemoryBound > 0.3, $"Memory Bound {breakdown.MemoryBound:P1} should be significant");
    }
}