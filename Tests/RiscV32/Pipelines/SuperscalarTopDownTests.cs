#region

using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     TMA slot accounting (Yasin, ISPASS 2014) on the scoreboarded in-order
///     <see cref="SuperscalarTrain" />. In-order flavor: issue never speculates past an
///     unresolved branch, so SlotsIssued equals SlotsRetired and Bad Speculation consists
///     purely of post-flush recovery bubbles; Backend Bound is the scoreboard/port/LSU
///     backpressure residual.
/// </summary>
public class SuperscalarTopDownTests {
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

    private static uint Slt(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0110011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static (TopDownBreakdown Td, DialBoardSnapshot Snap) RunAndAnalyze(
        uint[] program,
        int issueWidth = 4,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        bool enableMacroFusion = false
    ) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new SuperscalarTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: issueWidth, predictor: predictor,
            iMemConfig: iMemConfig, dMemConfig: dMemConfig, fuLatency: fuLatency
        );
        train.Run();
        DialBoardSnapshot snap = train.SnapshotPipeline();
        TopDownBreakdown? td = TopDownBreakdown.FromSnapshot(snap);
        Assert.NotNull(td);

        // Structural invariants: slots account exactly, in-order issue retires all it issues.
        // Compared against SlotsRetired, not "retired": the latter scales by
        // ITooth.ArchInstructionCount (2 per macro-fused pair), while a slot is a pipeline-width
        // unit that a fused pair still only occupies once.
        Assert.Equal(snap.Counters["cycles"] * issueWidth, snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        Assert.Equal(snap.Counters[TopDownBreakdown.SlotsRetiredCounter], snap.Counters[TopDownBreakdown.SlotsIssuedCounter]);
        double sum = td.FrontendBound + td.BadSpeculation + td.Retiring + td.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.0 + 1e-9); // issued ≡ retired → never overshoots
        return (td, snap);
    }

    [Fact]
    public void MacroFusion_DoesNotInflateRetiringOrMaskBadSpeculation() {
        // A dense run of the fusible SLT+BNE idiom (see RvMacroFuser): each fused pair
        // retires as 2 architectural instructions but occupies exactly 1 issue slot. Before
        // wiring a dedicated SlotsRetired counter, TMA fed the architectural "retired" count
        // into the slot-retired term, so a fusion-heavy program made SlotsRetired exceed
        // SlotsIssued — inflating Retiring and clamping Bad Speculation to zero even when a
        // real bubble occurred. RunAndAnalyze's own SlotsRetired == SlotsIssued invariant
        // (and the level-1 fractions summing to exactly 1) is the regression check: it would
        // fail under fusion if the bug were still present.
        // 20 back-to-back copies of the classic fusible idiom (see SuperscalarMacroFusionTests'
        // TakenProgram): each block's taken branch jumps exactly to the next block's first
        // instruction, so blocks chain safely with no out-of-bounds target even for the last one
        // (whose branch lands squarely on the trailing Ebreak).
        const int blockWords = 6;
        var program = new uint[blockWords * 20 + 1];
        for (var i = 0; i < 20; i++) {
            int b = i * blockWords;
            program[b + 0] = Addi(1, 0, 5);
            program[b + 1] = Addi(2, 0, 3);
            program[b + 2] = Slt(5, 2, 1); // x5 = (3 < 5) = 1
            program[b + 3] = Bne(5, 0, 12); // taken: skip the next 2 addis, land on the next block
            program[b + 4] = Addi(3, 0, 111); // skipped
            program[b + 5] = Addi(3, 0, 222); // skipped
        }

        program[blockWords * 20] = SuperscalarTopDownTests.Ebreak;

        (TopDownBreakdown td, DialBoardSnapshot snap) = RunAndAnalyze(program, issueWidth: 2, enableMacroFusion: true);
        Assert.True(snap.Counters["macro_fusions"] > 0, "expected the SLT+BNE idiom to fuse");
        // The architectural "retired" count is provably inflated past SlotsIssued here — exactly
        // the condition that broke the SlotsIssued == SlotsRetired invariant (checked inside
        // RunAndAnalyze above) before td_slots_retired existed.
        Assert.True(
            snap.Counters["retired"] > snap.Counters[TopDownBreakdown.SlotsIssuedCounter],
            "expected fusion to inflate the architectural retired count past SlotsIssued"
        );
        Assert.True(td.Retiring is >= 0.0 and <= 1.0);
        // Ties the live train dial (backed by ComputeTopDown, not FromSnapshot) to the same
        // value: a regression that swapped only ComputeTopDown back to _retiredCounter would
        // otherwise slip past every assertion above.
        Assert.Equal(td.Retiring, snap.Dials["td_retiring"], 12);
    }

    [Fact]
    public void Retiring_DominatesOnIndependentAluCode() {
        var program = new uint[257];
        for (var i = 0; i < 256; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[256] = SuperscalarTopDownTests.Ebreak;

        (TopDownBreakdown td, DialBoardSnapshot snap) = RunAndAnalyze(
            program, fuLatency: new FuLatencyConfig(4)
        );
        Assert.True(td.Retiring > 0.5, $"Retiring {td.Retiring:P1}");
        // The paper's cross-check: Retiring == IPC / width, exactly, from the same snapshot.
        double ipcOverWidth = snap.Counters["retired"] / (double)snap.Counters["cycles"] / 4;
        Assert.Equal(ipcOverWidth, td.Retiring, 12);
    }

    [Fact]
    public void BackendBound_FlagsDependentChain() {
        // 64 chained addis at width 4: the RAW interlock at the queue head is backend
        // backpressure — three of four slots lost every cycle to the scoreboard.
        var program = new uint[66];
        program[0] = Addi(1, 0, 1);
        for (var i = 1; i < 65; i++) program[i] = Addi(1, 1, 1);
        program[65] = SuperscalarTopDownTests.Ebreak;

        (TopDownBreakdown td, _) = RunAndAnalyze(program);
        Assert.True(td.BackendBound > 0.5, $"Backend {td.BackendBound:P1}");
        Assert.True(td.BackendBound > td.FrontendBound);
        Assert.True(td.CoreBound > td.MemoryBound); // no memory ops at all
    }

    [Fact]
    public void BadSpeculation_FlagsMispredictedLoop() {
        // Countdown loop under the always-not-taken default: every taken back-edge
        // flushes the frontend, so the refill slots dominate as Bad Speculation, and
        // with no traps in the loop it is attributed to branches, not machine clears.
        uint[] program = [
            Addi(1, 0, 200),
            Addi(2, 2, 1),
            Addi(1, 1, -1),
            0xFE009CE3, // bne x1, x0, -8
            SuperscalarTopDownTests.Ebreak,
        ];

        (TopDownBreakdown td, _) = RunAndAnalyze(program);
        Assert.True(td.BadSpeculation > 0.3, $"Bad Speculation {td.BadSpeculation:P1}");
        Assert.True(td.BranchMispredicts > td.MachineClears);
        Assert.True(td.BadSpeculation > td.BackendBound);
    }

    [Fact]
    public void FrontendBound_FlagsIcacheMisses() {
        // 512 straight-line instructions through a 256-byte I-cache with a 20-cycle miss
        // penalty: fetch starves the issue stage while lines are fetched — whole-cycle
        // starvation, so Fetch Latency rather than Fetch Bandwidth.
        var program = new uint[513];
        for (var i = 0; i < 512; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[512] = SuperscalarTopDownTests.Ebreak;

        (TopDownBreakdown td, _) = RunAndAnalyze(
            program,
            iMemConfig: new MemoryConfig(256, CacheBlockBytes: 32, CacheMissLatency: 20),
            fuLatency: new FuLatencyConfig(4)
        );
        Assert.True(td.FrontendBound > 0.5, $"Frontend {td.FrontendBound:P1}");
        Assert.True(td.FetchLatencyBound > td.FetchBandwidthBound);
        Assert.True(td.FrontendBound > td.BackendBound);
    }

    [Fact]
    public void MemoryBound_FlagsDependentLoadMissChain() {
        // Pointer chase across 32 lines with a 50-cycle miss penalty: the machine idles
        // with a load in flight — execution stalls attributed to memory, not the core.
        var mem = new FlatMemory(65536);
        for (var i = 0; i < 32; i++) {
            uint address = 0x1000u + 64u * (uint)i;
            uint next = address + 64;
            mem.Load(address, [(byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)(next >> 24),]);
        }

        var program = new uint[35];
        program[0] = 0x00001097; // auipc x1, 0x1 → x1 = 0x1000
        program[1] = Addi(1, 1, 0);
        for (var i = 0; i < 32; i++) program[2 + i] = 0x0000A083; // lw x1, 0(x1)
        program[34] = SuperscalarTopDownTests.Ebreak;
        Load(mem, program);

        var train = new SuperscalarTrain(
            new Rv32Mechanism(), mem, issueWidth: 4,
            dMemConfig: new MemoryConfig(512, CacheBlockBytes: 32, CacheMissLatency: 50)
        );
        train.Run();
        TopDownBreakdown? td = TopDownBreakdown.FromSnapshot(train.SnapshotPipeline());
        Assert.NotNull(td);
        Assert.True(td.BackendBound > 0.5, $"Backend {td.BackendBound:P1}");
        Assert.True(td.MemoryBound > td.CoreBound);
        Assert.True(td.MemoryBound > 0.3, $"Memory {td.MemoryBound:P1}");
    }
}