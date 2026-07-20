#region

using Orrery.Observation;
using Pipeline;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Unit tests for the CPI-stack arithmetic (Eyerman et al., ASPLOS 2006) — pure math
///     over lost-cycle counts, no simulation involved.
/// </summary>
public class CpiStackAnalysisTests {
    [Fact]
    public void Compute_ComponentsArePerRetiredInstruction_AndBaseIsResidual() {
        CpiStack s = CpiStack.Compute(
            1000, 500,
            50, 10, 0, 5,
            100,
            20, 200, 0, 15,
            30, 70
        );

        Assert.Equal(2.0, s.Total, 12); // 1000 / 500
        Assert.Equal(0.1, s.L1ICache, 12);
        Assert.Equal(0.2, s.BranchMisprediction, 12);
        Assert.Equal(0.4, s.L2DCache, 12);
        Assert.Equal(0.14, s.ResourceStall, 12);

        // base = (1000 − 500 component cycles) / 500 retired
        Assert.Equal(1.0, s.Base, 12);
        // The stack is additive: base + all components == total CPI.
        Assert.Equal(s.Total, s.Base + s.MissComponents, 12);
    }

    [Fact]
    public void Compute_ComponentsExceedingCycles_ClampBaseAtZero() {
        CpiStack s = CpiStack.Compute(
            100, 10,
            80, 0, 0, 0,
            40,
            0, 0, 0, 0,
            0, 0
        );
        Assert.Equal(0.0, s.Base);
    }

    [Fact]
    public void Compute_ZeroCyclesOrRetired_ReturnsAllZero() {
        Assert.Equal(0.0, CpiStack.Compute(0, 10, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1).Total);
        Assert.Equal(0.0, CpiStack.Compute(100, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1).Total);
    }

    [Fact]
    public void FromSnapshot_WithoutCpiCounters_ReturnsNull() {
        var snapshot = new DialBoardSnapshot(
            "some.gear",
            new Dictionary<string, long> { ["cycles"] = 100, ["retired"] = 50, },
            new Dictionary<string, double>(),
            new Dictionary<string, IReadOnlyDictionary<string, long>>()
        );
        Assert.Null(CpiStack.FromSnapshot(snapshot));
    }

    [Fact]
    public void FromSnapshot_ReadsSharedCounterNames() {
        var snapshot = new DialBoardSnapshot(
            "ooo.pipeline",
            new Dictionary<string, long> {
                ["cycles"] = 1000,
                ["retired"] = 500,
                [CpiStack.BpredCounter] = 100,
                [CpiStack.L2DCounter] = 200,
            },
            new Dictionary<string, double>(),
            new Dictionary<string, IReadOnlyDictionary<string, long>>()
        );

        CpiStack? s = CpiStack.FromSnapshot(snapshot);
        Assert.NotNull(s);
        Assert.Equal(0.2, s.BranchMisprediction, 12);
        Assert.Equal(0.4, s.L2DCache, 12);
        Assert.Equal(1.4, s.Base, 12); // (1000 − 300) / 500
    }
}