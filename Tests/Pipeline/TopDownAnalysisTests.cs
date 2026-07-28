#region

using Orrery.Observation;
using Pipeline;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Unit tests for the Top-Down Microarchitecture Analysis formulas (Yasin, ISPASS 2014,
///     Table 2) — pure math over event counts, no simulation involved.
/// </summary>
public class TopDownAnalysisTests {
    [Fact]
    public void Compute_AppliesTable2Formulas() {
        TopDownBreakdown b = TopDownBreakdown.Compute(
            1000,
            400,
            380,
            100,
            40,
            500,
            30,
            200,
            120,
            10,
            5,
            8
        );

        Assert.Equal(0.10, b.FrontendBound, 12);
        Assert.Equal(0.06, b.BadSpeculation, 12); // (400 − 380 + 40) / 1000
        Assert.Equal(0.38, b.Retiring, 12);
        Assert.Equal(0.46, b.BackendBound, 12); // residual

        Assert.Equal(0.06, b.FetchLatencyBound, 12);   // 30 / 500
        Assert.Equal(0.04, b.FetchBandwidthBound, 12); // frontend − latency

        // 5 mispredicts + (8 − 5) machine clears → 5/8 of Bad Speculation is branches.
        Assert.Equal(0.06 * 5 / 8, b.BranchMispredicts, 12);
        Assert.Equal(0.06 * 3 / 8, b.MachineClears, 12);

        Assert.Equal(0.26, b.MemoryBound, 12); // (120 + 10) / 500
        Assert.Equal(0.14, b.CoreBound, 12);   // 200/500 − 0.26
    }

    [Fact]
    public void Compute_Level1SumsToOne() {
        TopDownBreakdown b = TopDownBreakdown.Compute(
            2000, 900, 850,
            300, 60,
            1000, 100, 400,
            250, 0,
            10, 12
        );
        Assert.Equal(1.0, b.FrontendBound + b.BadSpeculation + b.Retiring + b.BackendBound, 12);
    }

    [Fact]
    public void Compute_ZeroSlots_ReturnsAllZero() {
        TopDownBreakdown b = TopDownBreakdown.Compute(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, b.TotalSlots);
        Assert.Equal(0.0, b.Retiring);
        Assert.Equal(0.0, b.BackendBound);
    }

    [Fact]
    public void FromSnapshot_WithoutTopDownCounters_ReturnsNull() {
        var snapshot = new DialBoardSnapshot(
            "some.gear",
            new Dictionary<string, long> { ["cycles"] = 100, ["retired"] = 50, },
            new Dictionary<string, double>(),
            new Dictionary<string, IReadOnlyDictionary<string, long>>()
        );
        Assert.Null(TopDownBreakdown.FromSnapshot(snapshot));
    }

    [Fact]
    public void FromSnapshot_ReadsSharedCounterNames() {
        var snapshot = new DialBoardSnapshot(
            "ooo.pipeline",
            new Dictionary<string, long> {
                [TopDownBreakdown.TotalSlotsCounter] = 1000,
                [TopDownBreakdown.SlotsIssuedCounter] = 400,
                [TopDownBreakdown.SlotsRetiredCounter] = 380,
                ["retired"] = 380,
                [TopDownBreakdown.FetchBubblesCounter] = 100,
                [TopDownBreakdown.RecoveryBubblesCounter] = 40,
                ["cycles"] = 500,
            },
            new Dictionary<string, double>(),
            new Dictionary<string, IReadOnlyDictionary<string, long>>()
        );

        TopDownBreakdown? b = TopDownBreakdown.FromSnapshot(snapshot);
        Assert.NotNull(b);
        Assert.Equal(0.10, b.FrontendBound, 12);
        Assert.Equal(0.38, b.Retiring, 12);
    }

    [Fact]
    public void FromSnapshot_UsesSlotsRetiredNotArchInstructionRetired() {
        // A fused pair inflates the architectural "retired" count (2 per pair) without
        // inflating SlotsIssued/SlotsRetired (1 per pair) — Retiring must track the slot
        // counter, not "retired", or a fusion-heavy program would over-report Retiring and
        // under-report Bad Speculation (which clamps negative results to zero).
        var snapshot = new DialBoardSnapshot(
            "fused.pipeline",
            new Dictionary<string, long> {
                [TopDownBreakdown.TotalSlotsCounter] = 1000,
                [TopDownBreakdown.SlotsIssuedCounter] = 400,
                [TopDownBreakdown.SlotsRetiredCounter] = 400,
                ["retired"] = 700, // every issued slot was a fused pair: 2x architectural count
                [TopDownBreakdown.FetchBubblesCounter] = 100,
                [TopDownBreakdown.RecoveryBubblesCounter] = 0,
                ["cycles"] = 500,
            },
            new Dictionary<string, double>(),
            new Dictionary<string, IReadOnlyDictionary<string, long>>()
        );

        TopDownBreakdown? b = TopDownBreakdown.FromSnapshot(snapshot);
        Assert.NotNull(b);
        Assert.Equal(0.40, b.Retiring, 12); // 400/1000, not 700/1000
        Assert.Equal(0.0, b.BadSpeculation, 12); // slotsIssued == slotsRetired: no bad speculation
    }
}