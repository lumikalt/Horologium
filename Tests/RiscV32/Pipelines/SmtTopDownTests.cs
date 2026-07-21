#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) slot accounting on
///     <see cref="SmtTrain" />. This is an in-order barrel processor with no branch
///     speculation (each hart resolves its own PC synchronously), so Bad Speculation is
///     always zero — a correct TMA reading for this machine, not a missing category. The
///     only source of unutilized issue slots is thread starvation: fewer runnable harts than
///     issueWidth, which reads as Frontend Bound (no ROB/IQ-style backend resource
///     in this design to structurally block dispatch).
/// </summary>
public class SmtTopDownTests {
    private const uint Ebreak = 0x00100073;

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    // addi x{rd}, x0, {imm}
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static DialBoardSnapshot Snapshot(RevolutionResult result) {
        DialBoardSnapshot? snap = result.Find("smt.pipeline");
        Assert.NotNull(snap);
        return snap;
    }

    [Fact]
    public void Level1_FractionsSumToOne_AndCountersAreConsistent() {
        // Two harts, issueWidth 2: each hart alone can't fill both slots, so both must run
        // every cycle to sustain full width.
        var mem0 = new FlatMemory(4096);
        var mem1 = new FlatMemory(4096);
        var program0 = new uint[65];
        var program1 = new uint[65];
        for (var i = 0; i < 64; i++) {
            program0[i] = Addi(1 + i % 8, 0, i);
            program1[i] = Addi(1 + i % 8, 0, i);
        }

        program0[64] = SmtTopDownTests.Ebreak;
        program1[64] = SmtTopDownTests.Ebreak;
        mem0.Load(0, ToBytes(program0));
        mem1.Load(0, ToBytes(program1));

        var smt = new SmtTrain([new Rv32Mechanism(), new Rv32Mechanism(),], [mem0, mem1,], issueWidth: 2);
        RevolutionResult result = smt.Run(100_000);
        DialBoardSnapshot snap = Snapshot(result);
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(snap);
        Assert.NotNull(breakdown);

        Assert.Equal(snap.Counters["cycles"] * 2, snap.Counters[TopDownBreakdown.TotalSlotsCounter]);
        Assert.True(snap.Counters[TopDownBreakdown.SlotsIssuedCounter] >= snap.Counters["retired"]);

        double sum = breakdown.FrontendBound + breakdown.BadSpeculation
                                             + breakdown.Retiring + breakdown.BackendBound;
        Assert.InRange(sum, 1.0 - 1e-9, 1.05);

        Assert.Equal(breakdown.FrontendBound, snap.Dials["td_frontend_bound"], 12);
        Assert.Equal(breakdown.BadSpeculation, snap.Dials["td_bad_speculation"], 12);
        Assert.Equal(breakdown.Retiring, snap.Dials["td_retiring"], 12);
        Assert.Equal(breakdown.BackendBound, snap.Dials["td_backend_bound"], 12);

        // No speculation exists in this in-order design.
        Assert.Equal(0.0, breakdown.BadSpeculation);
    }

    [Fact]
    public void Retiring_DominatesWhenHartCountMatchesIssueWidth() {
        // Two harts on a 2-wide machine, both with enough independent work to always be
        // runnable: every cycle fills both slots, so Retiring should dwarf Frontend Bound.
        var mem0 = new FlatMemory(4096);
        var mem1 = new FlatMemory(4096);
        var program0 = new uint[129];
        var program1 = new uint[129];
        for (var i = 0; i < 128; i++) {
            program0[i] = Addi(1 + i % 8, 0, i % 512);
            program1[i] = Addi(1 + i % 8, 0, i % 512);
        }

        program0[128] = SmtTopDownTests.Ebreak;
        program1[128] = SmtTopDownTests.Ebreak;
        mem0.Load(0, ToBytes(program0));
        mem1.Load(0, ToBytes(program1));

        var smt = new SmtTrain([new Rv32Mechanism(), new Rv32Mechanism(),], [mem0, mem1,], issueWidth: 2);
        RevolutionResult result = smt.Run(100_000);
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(Snapshot(result));
        Assert.NotNull(breakdown);

        Assert.True(breakdown.Retiring > 0.5, $"Retiring {breakdown.Retiring:P1} should exceed 50%");
        Assert.True(breakdown.Retiring > breakdown.FrontendBound);
        Assert.True(breakdown.Retiring > breakdown.BackendBound);
    }

    // jal x{rd}, {imm}: J-type encoding (RoundRobinFetchPolicy lets a single hart refill
    // every issue slot itself when its retired instructions never "cut" its own turn — see
    // FrontendBound_FlagsThreadStarvation for why straight-line ALU code can't demonstrate
    // starvation on this machine).
    private static uint Jal(int rd, int imm) {
        uint bit20 = (uint)(imm >> 20) & 1;
        uint bits19To12 = (uint)(imm >> 12) & 0xFF;
        uint bit11 = (uint)(imm >> 11) & 1;
        uint bits10To1 = (uint)(imm >> 1) & 0x3FF;
        return (bit20 << 31) | (bits19To12 << 12) | (bit11 << 20) | (bits10To1 << 21)
             | ((uint)rd << 7) | 0b1101111;
    }

    [Fact]
    public void FrontendBound_FlagsThreadStarvation() {
        // A single hart on a 2-wide machine. RoundRobinFetchPolicy lets one hart fill every
        // slot in a cycle by itself as long as it keeps being selected — so straight-line ALU
        // code doesn't starve (the lone hart just issues issueWidth instructions per cycle).
        // Genuine thread starvation shows up only on a cycle where the hart's own retiring
        // instruction "cuts" its further participation that cycle (branch/jump/trap/halt),
        // leaving the remaining slot(s) structurally unfillable — no other hart exists to
        // take them. A forward-jump trampoline (every instruction is an unconditional jump
        // to the next one, so it never trips the same-PC self-loop halt check) makes every
        // single cycle look like that: 1 slot filled, 1 slot starved.
        var mem = new FlatMemory(4096);
        var program = new uint[65];
        for (var i = 0; i < 64; i++) program[i] = Jal(0, 4);
        program[64] = SmtTopDownTests.Ebreak;
        mem.Load(0, ToBytes(program));

        var smt = new SmtTrain([new Rv32Mechanism(),], [mem,], issueWidth: 2);
        RevolutionResult result = smt.Run(100_000);
        TopDownBreakdown? breakdown = TopDownBreakdown.FromSnapshot(Snapshot(result));
        Assert.NotNull(breakdown);

        Assert.True(
            breakdown.FrontendBound > 0.4, $"Frontend Bound {breakdown.FrontendBound:P1} should be significant"
        );
        Assert.True(breakdown.FrontendBound > breakdown.BackendBound);
        Assert.True(breakdown.FrontendBound > breakdown.BadSpeculation);
    }

    // Note: SmtTrain hardcodes MemoryConfig.None per hart (no public constructor path to
    // attach a cache), so the I/D-stall TMA wiring in SmtCore.RunCycle cannot be exercised
    // by a dominance test today. It mirrors OooeTrain/CprTrain's DrainStalls split and will
    // become reachable if per-hart cache configuration is ever added to SmtTrain's public API.
}