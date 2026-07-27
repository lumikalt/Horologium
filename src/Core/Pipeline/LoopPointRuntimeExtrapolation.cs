namespace Pipeline;

/// <summary>
///     LoopPoint's runtime extrapolation (Sabu, Patil, Heirman &amp; Carlson, HPCA 2022, Section III-G,
///     Eq. 1/2): reconstructs a whole-run estimate from only the representative regions'
///     ("looppoints'") own simulated results, weighted by how much of the run each one stands in for.
///     <para>
///         Deliberately not <c>SimulationPoint.Weight</c> (<c>members / intervalCount</c>): that
///         weighting assumes every interval is the same length, true for SimPoint's fixed-instruction-
///         count intervals but false for LoopPoint's regions, which are variable-length (bounded by
///         loop-header markers, not a fixed instruction count — see
///         <c>MultiHartLoopPointProfiler</c>). Reusing it would silently under- or over-weight a
///         representative relative to its cluster's real share of total work. Eq. 2's multiplier is
///         instead the ratio of *filtered instruction counts* — <c>MultiHartLoopPointProfiler.RegionInstructionCounts</c>,
///         which already excludes spin-loop/sync-library instructions, matching the paper's own
///         "instructions that contribute to spin-loops are not considered here" — of every region in
///         the representative's cluster (including the representative itself) to the representative's
///         own count.
///     </para>
/// </summary>
public static class LoopPointRuntimeExtrapolation {
    /// <summary>
    ///     Eq. 2: for each representative region (one per cluster in <paramref name="simPoints" />),
    ///     the ratio of its cluster's total filtered instruction count to its own — always &gt;= 1,
    ///     since the representative's own count is included in its cluster's total.
    /// </summary>
    /// <param name="simPoints">A <c>SimPointResult</c> computed from <c>MultiHartLoopPointProfiler.RegionBbvs</c>.</param>
    /// <param name="regionInstructionCounts">
    ///     <c>MultiHartLoopPointProfiler.RegionInstructionCounts</c> — same index order as
    ///     <paramref name="simPoints" />'s intervals (<c>Phases</c>/<c>IntervalIndex</c>).
    /// </param>
    /// <returns>Representative region index (<c>SimulationPoint.IntervalIndex</c>) to its Eq. 2 multiplier.</returns>
    public static IReadOnlyDictionary<int, double> ComputeMultipliers(
        SimPointResult simPoints,
        IReadOnlyList<long> regionInstructionCounts
    ) {
        if (regionInstructionCounts.Count != simPoints.IntervalCount)
            throw new ArgumentException(
                $"regionInstructionCounts.Count ({regionInstructionCounts.Count}) must equal " +
                $"simPoints.IntervalCount ({simPoints.IntervalCount}).", nameof(regionInstructionCounts)
            );

        var clusterTotals = new Dictionary<int, long>();
        for (var i = 0; i < simPoints.Phases.Count; i++) {
            int cluster = simPoints.Phases[i];
            clusterTotals[cluster] = clusterTotals.GetValueOrDefault(cluster) + regionInstructionCounts[i];
        }

        var multipliers = new Dictionary<int, double>();
        foreach (SimulationPoint p in simPoints.Points) {
            long ownCount = regionInstructionCounts[p.IntervalIndex];
            multipliers[p.IntervalIndex] = ownCount == 0 ? 0.0 : clusterTotals[p.Cluster] / (double)ownCount;
        }

        return multipliers;
    }

    /// <summary>
    ///     Eq. 1: the whole-run runtime estimate — each representative's own simulated runtime,
    ///     multiplier-weighted, summed across every representative.
    /// </summary>
    /// <param name="multipliers"><see cref="ComputeMultipliers" />'s result.</param>
    /// <param name="representativeRuntimes">
    ///     Each representative region's own measured runtime (e.g., detailed-pipeline ticks from
    ///     <c>MultiHartWarmupMeasureDriver</c>), keyed the same way as <paramref name="multipliers" />.
    ///     Must have an entry for every key in <paramref name="multipliers" />.
    /// </param>
    public static double ExtrapolateTotalRuntime(
        IReadOnlyDictionary<int, double> multipliers,
        IReadOnlyDictionary<int, double> representativeRuntimes
    ) {
        double total = 0;
        foreach ((int regionIndex, double multiplier) in multipliers) {
            if (!representativeRuntimes.TryGetValue(regionIndex, out double runtime))
                throw new ArgumentException(
                    $"No runtime supplied for representative region {regionIndex}.", nameof(representativeRuntimes)
                );

            total += runtime * multiplier;
        }

        return total;
    }
}