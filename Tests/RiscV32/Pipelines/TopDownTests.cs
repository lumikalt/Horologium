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
///     Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) slot accounting on OooTrain.
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

    private static (OooTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        int robCapacity = 32,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        bool enableMacroFusion = false
    ) {
        var mem = new FlatMemory(65536);
        var train = new OooTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem,
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

    private static TopDownBreakdown RunAndAnalyze(OooTrain train) {
        train.Run();
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(train.SnapshotPipeline());
        Assert.NotNull(breakdown);
        return breakdown;
    }

    // addi x{rd}, x0, {imm}
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

    // ── Slot-accounting invariants ─────────────────────────────────────────────

    [Fact]
    public void Level1_FractionsSumToOne_AndCountersAreConsistent() {
        (OooTrain train, FlatMemory mem) = Make();
        var program = new uint[65];
        for (var i = 0; i < 64; i++) program[i] = Addi(1 + i % 8, 0, i);
        program[64] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        DialBoardSnapshot snap = train.SnapshotPipeline();

        // TotalSlots is exactly issueWidth × cycles, including lump-sum stall cycles.
        Assert.Equal(snap.Counters["cycles"] * 2, snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        // Every retired slot was previously issued (compared slot-to-slot, not against the
        // architectural "retired" counter, which scales by ArchInstructionCount under fusion).
        Assert.True(
            snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters[TopDownBreakdown.SlotsRetiredCounter]
        );

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

    [Fact]
    public void MacroFusion_KeepsSlotsRetiredAtOnePerFusedPair() {
        // Same regression as SuperscalarTopDownTests: a fused SLT+BNE pair retires as 2
        // architectural instructions (see ITooth.ArchInstructionCount) but dispatches and
        // retires through exactly one ROB entry/slot. Feeding the architectural "retired"
        // count into TMA's slotsRetired term would make SlotsRetired exceed SlotsIssued once
        // fusion fires, inflating Retiring and clamping Bad Speculation to zero.
        //
        // The branch is engineered NOT taken (matching the default AlwaysNotTakenPredictor)
        // so this OoO machine's speculative dispatch never has a wrong path to squash —
        // isolating the fusion/slot-accounting question from unrelated bad-speculation noise.
        (OooTrain train, FlatMemory mem) = Make(enableMacroFusion: true);
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

        program[blockWords * 20] = TopDownTests.Ebreak;
        Load(mem, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        DialBoardSnapshot snap = train.SnapshotPipeline();

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
            breakdown.Retiring, 12
        );
        // Ties the live train dial (backed by ComputeTopDown, not FromSnapshot) to the same
        // value: a regression that swapped only ComputeTopDown back to _retiredCounter would
        // otherwise slip past every assertion above.
        Assert.Equal(breakdown.Retiring, snap.Dials["td_retiring"], 12);
    }

    // ── Category dominance ─────────────────────────────────────────────────────

    [Fact]
    public void Retiring_DominatesOnIndependentAluCode() {
        // 256 independent single-cycle ALU ops on a 2-wide machine with 2 ALU ports:
        // the machine sustains full width, so Retiring should dwarf every stall category.
        (OooTrain train, FlatMemory mem) = Make();
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
        (OooTrain train, FlatMemory mem) = Make();
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
        (OooTrain train, FlatMemory mem) = Make(
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
        (OooTrain train, FlatMemory mem) = Make(fuLatency: new FuLatencyConfig(DivLatency: 20));
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
        (OooTrain train, FlatMemory mem) = Make(
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