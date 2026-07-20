#region

using Pipeline;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Unit tests for the SimPoint clustering pipeline (Sherwood et al., ASPLOS 2002) —
///     random projection, k-means, BIC selection — on synthetic basic-block vectors with
///     known phase structure, no simulation involved.
/// </summary>
public class SimPointAnalysisTests {
    // Builds an interval BBV concentrated on the given blocks (block start PC → weight).
    private static Dictionary<ulong, long> Bbv(params (ulong Block, long Weight)[] entries) =>
        entries.ToDictionary(e => e.Block, e => e.Weight);

    // Two clearly distinct phases: intervals dominated by block set A, then by block set B.
    private static List<IReadOnlyDictionary<ulong, long>> TwoPhaseIntervals(int perPhase) {
        var intervals = new List<IReadOnlyDictionary<ulong, long>>();
        for (var i = 0; i < perPhase; i++) intervals.Add(Bbv((0x1000, 800 + i), (0x1040, 200)));
        for (var i = 0; i < perPhase; i++) intervals.Add(Bbv((0x2000, 900 + i), (0x2080, 100)));
        return intervals;
    }

    [Fact]
    public void TwoPhases_FoundWithContiguousAssignment() {
        List<IReadOnlyDictionary<ulong, long>> intervals = TwoPhaseIntervals(10);
        SimPointResult r = SimPointAnalysis.Analyze(intervals);

        Assert.Equal(2, r.K);
        Assert.Equal(20, r.Phases.Count);
        // Each half is uniform, and the two halves differ.
        Assert.Single(r.Phases.Take(10).Distinct());
        Assert.Single(r.Phases.Skip(10).Distinct());
        Assert.NotEqual(r.Phases[0], r.Phases[19]);
    }

    [Fact]
    public void SimulationPoints_OnePerPhase_WeightsSumToOne() {
        List<IReadOnlyDictionary<ulong, long>> intervals = TwoPhaseIntervals(10);
        SimPointResult r = SimPointAnalysis.Analyze(intervals);

        Assert.Equal(r.K, r.Points.Count);
        Assert.Equal(1.0, r.Points.Sum(p => p.Weight), 12);
        // Each representative belongs to the phase it represents.
        foreach (SimulationPoint p in r.Points) Assert.Equal(p.Cluster, r.Phases[p.IntervalIndex]);
        // Representatives come from the two different halves.
        Assert.Equal(1, r.Points.Count(p => p.IntervalIndex < 10));
        Assert.Equal(1, r.Points.Count(p => p.IntervalIndex >= 10));
    }

    [Fact]
    public void UniformProgram_IsOnePhase() {
        var intervals = new List<IReadOnlyDictionary<ulong, long>>();
        for (var i = 0; i < 12; i++) intervals.Add(Bbv((0x1000, 1000), (0x1040, 500)));

        SimPointResult r = SimPointAnalysis.Analyze(intervals);
        Assert.Equal(1, r.K);
        Assert.Single(r.Points);
        Assert.Equal(1.0, r.Points[0].Weight, 12);
    }

    [Fact]
    public void SingleInterval_YieldsSinglePoint() {
        SimPointResult r = SimPointAnalysis.Analyze([Bbv((0x1000, 100)),]);
        Assert.Equal(1, r.K);
        Assert.Equal(0, r.SingleSimulationPoint);
        Assert.Equal(0, r.Points[0].IntervalIndex);
    }

    [Fact]
    public void Deterministic_ForFixedSeed() {
        List<IReadOnlyDictionary<ulong, long>> intervals = TwoPhaseIntervals(8);
        SimPointResult a = SimPointAnalysis.Analyze(intervals, seed: 7);
        SimPointResult b = SimPointAnalysis.Analyze(intervals, seed: 7);
        Assert.Equal(a.Phases, b.Phases);
        Assert.Equal(a.Points, b.Points);
        Assert.Equal(a.SingleSimulationPoint, b.SingleSimulationPoint);
    }

    [Fact]
    public void ThreePhases_Found() {
        var intervals = new List<IReadOnlyDictionary<ulong, long>>();
        for (var i = 0; i < 8; i++) intervals.Add(Bbv((0x1000, 1000)));
        for (var i = 0; i < 8; i++) intervals.Add(Bbv((0x2000, 1000)));
        for (var i = 0; i < 8; i++) intervals.Add(Bbv((0x3000, 1000)));

        SimPointResult r = SimPointAnalysis.Analyze(intervals);
        Assert.Equal(3, r.K);
        Assert.Equal(3, r.Points.Count);
        foreach (SimulationPoint p in r.Points) Assert.Equal(8 / 24.0, p.Weight, 12);
    }
}