#region

using Pipeline;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     LoopPoint's Eq. 1/2 runtime extrapolation (<see cref="LoopPointRuntimeExtrapolation" />), on a
///     hand-verifiable oracle first, then the self-consistency invariant that generalizes beyond any
///     one hand-picked case: <c>sum(multiplier_j * inscount_j) == total filtered instruction count</c>
///     (Eq. 2's own definition — every non-representative region's count is folded into exactly one
///     representative's multiplier, so the weighted sum must reconstruct the whole).
/// </summary>
public class LoopPointRuntimeExtrapolationTests {
    // Four regions, inscounts [100, 300, 200, 400]. Clusters: {0, 2} -> represented by region 0;
    // {1, 3} -> represented by region 3. (SimPointResult's Phases[i] is region i's cluster label;
    // Points has one SimulationPoint per cluster, IntervalIndex naming the representative.)
    private static SimPointResult FourRegionTwoClusterFixture() =>
        new(
            IntervalCount: 4,
            K: 2,
            Phases: [0, 1, 0, 1,],
            Points: [new SimulationPoint(0, 0, 0.5), new SimulationPoint(3, 1, 0.5),],
            SingleSimulationPoint: 0
        );

    [Fact]
    public void ComputeMultipliers_HandVerifiableFourRegionCase_MatchesHandComputedValues() {
        SimPointResult sp = FourRegionTwoClusterFixture();
        long[] inscounts = [100, 300, 200, 400,];

        IReadOnlyDictionary<int, double> multipliers = LoopPointRuntimeExtrapolation.ComputeMultipliers(sp, inscounts);

        // Cluster 0 = {region 0 (100), region 2 (200)}, represented by region 0: (100+200)/100 = 3.0
        // Cluster 1 = {region 1 (300), region 3 (400)}, represented by region 3: (300+400)/400 = 1.75
        Assert.Equal(2, multipliers.Count);
        Assert.Equal(3.0, multipliers[0], 1e-9);
        Assert.Equal(1.75, multipliers[3], 1e-9);
    }

    [Fact]
    public void ComputeMultipliers_WeightedSumOfInscounts_ReconstructsTotalInstructionCount() {
        // The invariant Eq. 2 guarantees by construction: every region's count is folded into
        // exactly one cluster total, so summing (multiplier_j * inscount_j) over every representative
        // must equal the sum of every region's own instruction count.
        SimPointResult sp = FourRegionTwoClusterFixture();
        long[] inscounts = [100, 300, 200, 400,];

        IReadOnlyDictionary<int, double> multipliers = LoopPointRuntimeExtrapolation.ComputeMultipliers(sp, inscounts);

        double reconstructed = multipliers.Sum(kv => kv.Value * inscounts[kv.Key]);
        Assert.Equal(inscounts.Sum(), reconstructed, 1e-6);
    }

    [Fact]
    public void ExtrapolateTotalRuntime_WeightsEachRepresentativesOwnRuntimeByItsMultiplier() {
        SimPointResult sp = FourRegionTwoClusterFixture();
        long[] inscounts = [100, 300, 200, 400,];
        IReadOnlyDictionary<int, double> multipliers = LoopPointRuntimeExtrapolation.ComputeMultipliers(sp, inscounts);

        var runtimes = new Dictionary<int, double> { [0] = 10.0, [3] = 20.0, };
        double total = LoopPointRuntimeExtrapolation.ExtrapolateTotalRuntime(multipliers, runtimes);

        // 10.0*3.0 + 20.0*1.75 = 30 + 35 = 65
        Assert.Equal(65.0, total, 1e-9);
    }

    [Fact]
    public void ExtrapolateTotalRuntime_MissingRepresentativeRuntime_Throws() {
        SimPointResult sp = FourRegionTwoClusterFixture();
        long[] inscounts = [100, 300, 200, 400,];
        IReadOnlyDictionary<int, double> multipliers = LoopPointRuntimeExtrapolation.ComputeMultipliers(sp, inscounts);

        var incompleteRuntimes = new Dictionary<int, double> { [0] = 10.0, }; // missing region 3
        Assert.Throws<ArgumentException>(
            () => LoopPointRuntimeExtrapolation.ExtrapolateTotalRuntime(multipliers, incompleteRuntimes)
        );
    }
}
