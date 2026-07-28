#region

using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) slot accounting on
///     <see cref="DaeTrain" />. DAE has a single-dispatch front end (issueWidth = 1 in TMA
///     terms — at most one lane-instruction or barrier enters the pipeline per cycle), so
///     TotalSlots accrues one per real cycle rather than issueWidth × cycles, and DAE has no
///     branch speculation (barriers, including branches, execute in-order against precise
///     state) so Bad Speculation, when present, comes entirely from precise-trap rollbacks
///     and is attributed to Machine Clears rather than Branch Mispredicts.
/// </summary>
public class DaeTopDownTests {
    private const uint Ebreak = 0x00100073;

    private static (DaeTrain train, FlatMemory mem) Make(
        int laneQueueDepth = 8,
        int memSize = 65536,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) {
        var mem = new FlatMemory(memSize);
        var train = new DaeTrain(
            new Rv32Mechanism(), mem, laneQueueDepth: laneQueueDepth, iMemConfig: iMemConfig, dMemConfig: dMemConfig
        );
        return (train, mem);
    }

    private static void Load(FlatMemory mem, ulong address, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(address, bytes);
    }

    // addi x{rd}, x0, {imm}
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static TopDownBreakdown RunAndAnalyze(DaeTrain train, long maxTicks = 100_000) {
        RevolutionResult result = train.Run(maxTicks);
        DialBoardSnapshot? snap = result.Find("dae.pipeline");
        Assert.NotNull(snap);
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(snap);
        Assert.NotNull(breakdown);
        return breakdown;
    }

    private static DialBoardSnapshot Snapshot(RevolutionResult result) {
        DialBoardSnapshot? snap = result.Find("dae.pipeline");
        Assert.NotNull(snap);
        return snap;
    }

    // ── Slot-accounting invariants ─────────────────────────────────────────────

    [Fact]
    public void Level1_FractionsSumToOne_AndCountersAreConsistent() {
        (DaeTrain train, FlatMemory mem) = Make();
        var program = new uint[65];
        for (var i = 0; i < 64; i++) program[i] = Addi(1 + i % 8, 0, i);
        program[64] = DaeTopDownTests.Ebreak;
        Load(mem, 0, program);

        RevolutionResult result = train.Run(100_000);
        DialBoardSnapshot snap = Snapshot(result);
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(snap);
        Assert.NotNull(breakdown);

        // TotalSlots is exactly cycles (issueWidth = 1 for DAE's single-dispatch front end).
        Assert.Equal(snap.Counters["cycles"], snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        // Everything retired was previously issued.
        Assert.True(
            snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters[TopDownBreakdown.SlotsRetiredCounter]
        );

        double sum = breakdown.FrontendBound + breakdown.BadSpeculation
                                             + breakdown.Retiring + breakdown.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.05);

        Assert.Equal(breakdown.FrontendBound, snap.Dials["td_frontend_bound"], 12);
        Assert.Equal(breakdown.BadSpeculation, snap.Dials["td_bad_speculation"], 12);
        Assert.Equal(breakdown.Retiring, snap.Dials["td_retiring"], 12);
        Assert.Equal(breakdown.BackendBound, snap.Dials["td_backend_bound"], 12);
    }

    // ── Category dominance ─────────────────────────────────────────────────────

    [Fact]
    public void Retiring_DominatesOnIndependentAluCode() {
        // 128 independent single-cycle ALU ops, all address-untainted (Execute lane only):
        // the single-dispatch front end sustains one instruction per cycle with no stalls.
        (DaeTrain train, FlatMemory mem) = Make();
        var program = new uint[129];
        for (var i = 0; i < 128; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[128] = DaeTopDownTests.Ebreak;
        Load(mem, 0, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(breakdown.Retiring > 0.5, $"Retiring {breakdown.Retiring:P1} should exceed 50%");
        Assert.True(breakdown.Retiring > breakdown.FrontendBound);
        Assert.True(breakdown.Retiring > breakdown.BadSpeculation);
        Assert.True(breakdown.Retiring > breakdown.BackendBound);
    }

    [Fact]
    public void FrontendBound_FlagsIcacheMisses() {
        // 512 straight-line instructions (2 KiB) through a 256-byte I-cache with a 20-cycle
        // miss penalty: fetch starvation dominates.
        (DaeTrain train, FlatMemory mem) = Make(
            iMemConfig: new MemoryConfig(256, CacheBlockBytes: 32, CacheMissLatency: 20)
        );
        var program = new uint[513];
        for (var i = 0; i < 512; i++) program[i] = Addi(1 + i % 8, 0, i % 512);
        program[512] = DaeTopDownTests.Ebreak;
        Load(mem, 0, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.FrontendBound > 0.5, $"Frontend Bound {breakdown.FrontendBound:P1} should dominate"
        );
        Assert.True(breakdown.FrontendBound > breakdown.BackendBound);
        Assert.True(breakdown.FrontendBound > breakdown.Retiring);
        Assert.True(breakdown.FetchLatencyBound > breakdown.FetchBandwidthBound);
    }

    [Fact]
    public void MemoryBound_FlagsLoadMissChain() {
        // Pointer chase across 32 distinct cache lines with a 50-cycle miss penalty: every
        // load misses and depends on the previous one, so the Access lane starves waiting on
        // D-side stalls — Backend Bound, attributed to memory.
        (DaeTrain train, FlatMemory mem) = Make(
            dMemConfig: new MemoryConfig(512, CacheBlockBytes: 32, CacheMissLatency: 50)
        );

        for (var i = 0; i < 32; i++) {
            uint address = 0x1000u + 64u * (uint)i;
            uint next = address + 64;
            mem.Load(address, [(byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)(next >> 24),]);
        }

        var program = new uint[35];
        program[0] = 0x00001097; // auipc x1, 0x1  -> x1 = pc + 0x1000 = 0x1000
        program[1] = Addi(1, 1, 0);
        for (var i = 0; i < 32; i++) program[2 + i] = 0x0000A083; // lw x1, 0(x1)
        program[34] = DaeTopDownTests.Ebreak;
        Load(mem, 0, program);

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(
            breakdown.BackendBound > 0.5, $"Backend Bound {breakdown.BackendBound:P1} should dominate"
        );
        Assert.True(breakdown.MemoryBound > breakdown.CoreBound);
        Assert.True(breakdown.MemoryBound > 0.3, $"Memory Bound {breakdown.MemoryBound:P1} should be significant");
    }

    [Fact]
    public void BadSpeculation_FlagsPreciseTrapRollback_AsMachineClear() {
        // Same fault scenario as DaeTrainTests.FaultingAccessLoad_RollsBackYoungerExecuteLaneWrite:
        // an Access-lane load faults after several younger Execute-lane instructions have
        // already retired speculatively-out-of-order; the undo-log rollback is DAE's only
        // source of Bad Speculation, and since DAE never mispredicts a branch (barriers are
        // precise), it must land entirely on Machine Clears, not Branch Mispredicts.
        uint[] program = [
            0x00003137, // lui  x2, 3      -> x2 = 0x3000 (mapped filler-data page)
            0x000040B7, // lui  x1, 4      -> x1 = 0x4000 (unmapped -> page fault)
            0x00012503, // lw   x10, 0(x2)
            0x00012583, // lw   x11, 0(x2)
            0x00012603, // lw   x12, 0(x2)
            0x00012683, // lw   x13, 0(x2)
            0x00012703, // lw   x14, 0(x2)
            0x0000A183, // lw   x3, 0(x1)  -- FAULTS: LoadPageFault
            0x02A00293, // addi x5, x0, 42 -- rolled back
            DaeTopDownTests.Ebreak,
        ];

        var mem = new FlatMemory(0x10000);
        var train = new DaeTrain(new Rv32Mechanism(), mem, laneQueueDepth: 8);
        Load(mem, 0, program);

        mem.Write(0x1000UL, 0x801u, 4);
        mem.Write(0x2000UL, 0x5Fu, 4);
        mem.Write(0x200CUL, (3u << 10) | 0b1101_0111u, 4);
        Load(mem, 0x3000, 0xABCD1234u);
        Load(mem, 0x8000, 0x0000006Fu); // jal x0, 0 (self-loop halt idiom)

        train.ArchState.SystemRegisters.Write(CsrFile.Satp, 0x80000001u, RvPrivilege.Machine);
        train.ArchState.SystemRegisters.Write(CsrFile.Mtvec, 0x8000u, RvPrivilege.Machine);
        train.ArchState.PrivilegeLevel = RvPrivilege.User;

        TopDownBreakdown breakdown = RunAndAnalyze(train);
        Assert.True(train.IsIdle);
        Assert.True(
            breakdown.BadSpeculation > 0.0, $"Bad Speculation {breakdown.BadSpeculation:P1} should be nonzero"
        );
        Assert.Equal(0.0, breakdown.BranchMispredicts);
        Assert.True(breakdown.MachineClears > 0.0);
        Assert.Equal(breakdown.BadSpeculation, breakdown.MachineClears, 12);

        // Level 1 must still sum to ~1 under a rollback: "retired" counts the rolled-back
        // Execute-lane write (incremented before the trap check, never decremented on undo)
        // as well as the faulting instruction itself, and both also feed BadSpeculation via
        // the recovery-bubble cycles — so this specifically checks the two don't double-count
        // past what Backend Bound's residual can absorb.
        double sum = breakdown.FrontendBound + breakdown.BadSpeculation
                                             + breakdown.Retiring + breakdown.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.05);
    }
}