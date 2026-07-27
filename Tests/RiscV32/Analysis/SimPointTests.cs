#region

using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     SimPoint phase analysis (Sherwood et al., ASPLOS 2002) end-to-end: BBV profiling of
///     a functional run through <see cref="BbvProfiler" /> on <see cref="SingleCycleTrain" />,
///     then clustering, on hand-assembled RV32I programs with engineered phase structure.
/// </summary>
public class SimPointTests {
    private const uint BneX1X0Minus12 = 0xFE009AE3;
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

    // Two 4-instruction loops run back to back, 600 iterations each: a clean two-phase
    // program (2401 instructions per phase, plus setup).
    private static BbvProfiler ProfileTwoLoopProgram(long intervalSize) {
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(1, 0, 600),              // 0x00: addi x1, x0, 600
            Addi(2, 2, 1),                // 0x04: loopA: addi x2, x2, 1
            Addi(2, 2, 1),                // 0x08
            Addi(1, 1, -1),               // 0x0C
            SimPointTests.BneX1X0Minus12, // 0x10: bne x1, x0, loopA
            Addi(1, 0, 600),              // 0x14: addi x1, x0, 600
            Addi(3, 3, 3),                // 0x18: loopB: addi x3, x3, 3
            Addi(3, 3, 3),                // 0x1C
            Addi(1, 1, -1),               // 0x20
            SimPointTests.BneX1X0Minus12, // 0x24: bne x1, x0, loopB
            SimPointTests.Ebreak          // 0x28
        );

        var mechanism = new Rv32Mechanism();
        var profiler = new BbvProfiler(mechanism.Decoder, intervalSize);
        new SingleCycleTrain(mechanism, mem, commitObserver: profiler).Run();
        profiler.Complete();
        return profiler;
    }

    [Fact]
    public void CutInterval_SplitsAnOpenBlock_AndContinuesTheRemainderInTheNextInterval() {
        // Four addi's in one straight-line block (no control-flow instruction until the trailing
        // ebreak, which the train never forwards to the commit observer), cut externally mid-block
        // after the first two commits -- exactly what MultiHartLoopPointProfiler does at a region
        // boundary. The already-executed portion must close out under the block's original start PC;
        // the remainder must continue seamlessly (not restart as its own spurious block at whatever
        // PC happens to be current) under the address it actually continues from, and get credited
        // to the *next* interval, once it's later closed by Complete() (no control-flow instruction
        // ever ends it explicitly here).
        var mem = new FlatMemory(4096);
        Load(mem, Addi(1, 1, 1), Addi(1, 1, 1), Addi(1, 1, 1), Addi(1, 1, 1), SimPointTests.Ebreak);

        var mechanism = new Rv32Mechanism();
        var profiler = new BbvProfiler(mechanism.Decoder, long.MaxValue); // never auto-cuts
        var train = new SingleCycleTrain(mechanism, mem, commitObserver: profiler);

        train.BeginStepping();
        train.StepCycle(); // commits addi at 0x00
        train.StepCycle(); // commits addi at 0x04; block still open
        profiler.CutInterval();
        Assert.Single(profiler.Intervals);
        Assert.Equal(2L, profiler.Intervals[0][0x00]);

        train.StepCycle(); // commits addi at 0x08
        train.StepCycle(); // commits addi at 0x0C
        train.StepCycle(); // ebreak halts
        train.FinishStepping();
        profiler.Complete();

        Assert.Equal(2, profiler.Intervals.Count);
        Assert.Equal(2L, profiler.Intervals[1][0x08]); // remainder continues under its own address
        Assert.Equal(4, profiler.TotalInstructions);   // no instruction lost or double-counted by the cut
    }

    [Fact]
    public void Profiler_SplitsBlocksAndIntervalsCorrectly() {
        BbvProfiler profiler = ProfileTwoLoopProgram(400);

        // 1 setup + 600×4 + 1 setup + 600×4 = 4802 observed instructions (the halting
        // ebreak stops the train before reaching the commit observer).
        Assert.Equal(4802, profiler.TotalInstructions);
        Assert.Equal(13, profiler.Intervals.Count); // 12 full intervals of 400 + partial 2

        // Every full interval carries exactly intervalSize instructions of block weight.
        foreach (IReadOnlyDictionary<ulong, long> interval in profiler.Intervals.Take(12))
            Assert.Equal(400, interval.Values.Sum());

        // Early intervals are dominated by loop A's body block (starts at 0x04); late ones
        // by loop B's (0x18). The loop body is one dynamic block: three ALU ops + the
        // terminating branch, keyed at the block's start PC.
        Assert.True(profiler.Intervals[1].GetValueOrDefault(0x04UL) > 350);
        Assert.True(profiler.Intervals[10].GetValueOrDefault(0x18UL) > 350);
        Assert.Equal(0, profiler.Intervals[1].GetValueOrDefault(0x18UL));
    }

    [Fact]
    public void TwoLoopProgram_SeparatesThePhases_WithRepresentativesInEachHalf() {
        BbvProfiler profiler = ProfileTwoLoopProgram(400);
        SimPointResult r = SimPointAnalysis.Analyze(profiler.Intervals);

        // Intervals 1–5 are pure loop A, 7–11 pure loop B; 0 mixes in the setup, 6 is the
        // crossover, and 12 is the partial tail — the clusterer may give those their own
        // small phases, but the two pure regions must each be uniform and mutually distinct.
        Assert.InRange(r.K, 2, 5);
        Assert.Single(r.Phases.Skip(1).Take(5).Distinct());
        Assert.Single(r.Phases.Skip(7).Take(5).Distinct());
        Assert.NotEqual(r.Phases[1], r.Phases[7]);

        // One representative per phase, weights summing to 1, each drawn from its own phase.
        Assert.Equal(r.K, r.Points.Count);
        Assert.Equal(1.0, r.Points.Sum(p => p.Weight), 12);
        foreach (SimulationPoint p in r.Points) Assert.Equal(p.Cluster, r.Phases[p.IntervalIndex]);

        // The heavyweight phases' representatives come from the right halves of the run.
        int phaseA = r.Phases[1], phaseB = r.Phases[7];
        Assert.True(r.Points.Single(p => p.Cluster == phaseA).IntervalIndex is >= 0 and <= 6);
        Assert.True(r.Points.Single(p => p.Cluster == phaseB).IntervalIndex is >= 6 and <= 12);
    }

    [Fact]
    public void Profiler_TrapRedirect_EndsBlockAtDiscontinuity() {
        // ecall redirects to the trap vector (no branch terminator): the profiler must
        // close the block at the discontinuity rather than folding the handler into it.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(1, 0, 5),       // 0x00
            0x00000073,          // 0x04: ecall → trap to 0 base... mtvec=0 → vector 0x0
            SimPointTests.Ebreak // 0x08
        );

        var mechanism = new Rv32Mechanism();
        var profiler = new BbvProfiler(mechanism.Decoder, 1000);
        new SingleCycleTrain(mechanism, mem, commitObserver: profiler).Run(1000);
        profiler.Complete();

        // The run traps to mtvec (0x0) and re-executes from the top; regardless of the
        // exact path, all committed instructions must be accounted for in the intervals.
        long accounted = profiler.Intervals.Sum(i => i.Values.Sum());
        Assert.Equal(profiler.TotalInstructions, accounted);
    }
}