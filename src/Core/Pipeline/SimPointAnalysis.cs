#region

using System.Text;
using Mechanism;

#endregion

namespace Pipeline;

/// <summary>
///     Basic-block-vector profiler — the data-collection half of SimPoint phase analysis
///     (Sherwood, Perelman, Hamerly &amp; Calder, "Automatically Characterizing Large Scale
///     Program Behavior", ASPLOS 2002).
///     <para>
///         Attached to a (typically functional single-cycle) train as an
///         <see cref="ICommitObserver" />, it partitions the committed instruction stream
///         into fixed-length intervals and records, per interval, how many instructions ran
///         inside each basic block. (Block-entry count × block length, accumulated directly
///         as instructions — the paper's length weighting.) Blocks are identified
///         dynamically: a block starts at the first instruction after a control-flow
///         instruction or a discontinuous PC (trap redirect), is keyed by its start PC, and
///         ends at the next control-flow instruction. Static decode information is memoized
///         per PC, so profiling overhead stays negligible.
///     </para>
/// </summary>
public sealed class BbvProfiler(IDecoder decoder, long intervalSize) : ICommitObserver {
    private readonly List<IReadOnlyDictionary<ulong, long>> _intervals = [];
    private readonly Dictionary<ulong, (int Size, bool IsControlFlow)> _staticInfo = [];
    private long _blockLength;
    private ulong _blockStart;
    private Dictionary<ulong, long> _current = [];
    private ulong _expectedNextPc;
    private bool _inBlock;
    private long _intervalInstructions;

    /// <summary>Committed instructions per interval (the paper uses 100 M; tests use small values).</summary>
    private long IntervalSize { get; } = intervalSize > 0
        ? intervalSize
        : throw new ArgumentOutOfRangeException(nameof(intervalSize));

    /// <summary>Total committed instructions observed.</summary>
    public long TotalInstructions { get; private set; }

    /// <summary>
    ///     Completed interval BBVs (start-PC → instructions executed in that block). Call
    ///     <see cref="Complete" /> after the run to flush the trailing partial interval.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<ulong, long>> Intervals => _intervals;

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        (int size, bool isControlFlow) = StaticInfo(pc, rawEncoding);

        if (!_inBlock) {
            _blockStart = pc;
            _blockLength = 0;
            _inBlock = true;
        }
        else if (pc != _expectedNextPc) {
            // A control transfer landed here without a control-flow terminator (trap,
            // interrupt, mret): close the interrupted block and start a new one.
            EndBlock();
            _blockStart = pc;
            _blockLength = 0;
        }

        _blockLength++;
        _expectedNextPc = pc + (ulong)size;
        _intervalInstructions++;
        TotalInstructions++;

        if (isControlFlow) {
            EndBlock();
            _inBlock = false;
        }

        if (_intervalInstructions >= IntervalSize) {
            if (_inBlock) {
                // Split the in-progress block at the interval boundary; the remainder is
                // credited to the next interval under the same start PC.
                EndBlock();
                _blockStart = _expectedNextPc;
                _blockLength = 0;
            }

            _intervals.Add(_current);
            _current = [];
            _intervalInstructions = 0;
        }
    }

    /// <summary>Flushes the trailing partial interval (if any). Call once after the run.</summary>
    public void Complete() {
        if (_inBlock) {
            EndBlock();
            _inBlock = false;
        }

        if (_current.Count > 0) {
            _intervals.Add(_current);
            _current = [];
            _intervalInstructions = 0;
        }
    }

    private void EndBlock() {
        if (_blockLength == 0) return;
        _current[_blockStart] = _current.GetValueOrDefault(_blockStart) + _blockLength;
        _blockLength = 0;
    }

    private (int Size, bool IsControlFlow) StaticInfo(ulong pc, uint raw) {
        if (_staticInfo.TryGetValue(pc, out (int Size, bool IsControlFlow) info)) return info;
        ITooth decoded = decoder.Decode(pc, raw);
        info = (decoded.SizeBytes, decoder.GetFetchHint(pc, raw).IsBranch);
        _staticInfo[pc] = info;
        return info;
    }
}

/// <summary>One representative interval chosen for detailed simulation.</summary>
public sealed record SimulationPoint(int IntervalIndex, int Cluster, double Weight);

/// <summary>
///     Result of SimPoint phase analysis: a phase (cluster) label per interval, one
///     representative simulation point per phase with its weight (cluster share of the
///     full run), and the single best simulation point for one-sample estimation.
/// </summary>
public sealed record SimPointResult(
    int IntervalCount,
    int K,
    IReadOnlyList<int> Phases,
    IReadOnlyList<SimulationPoint> Points,
    int SingleSimulationPoint
) {
    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine($"SimPoint analysis: {IntervalCount} intervals → {K} phases");
        foreach (SimulationPoint p in Points.OrderBy(p => p.Cluster))
            sb.AppendLine($"  phase {p.Cluster}: interval {p.IntervalIndex} (weight {p.Weight:F4})");
        sb.Append($"  single simulation point: interval {SingleSimulationPoint}");
        return sb.ToString();
    }
}

/// <summary>
///     SimPoint phase analysis (Sherwood et al., ASPLOS 2002, section 4.2): random linear
///     projection of interval basic-block vectors to a low-dimensional space, k-means
///     clustering for k = 1…maxK, and Bayesian Information Criterion scoring to choose the
///     smallest k whose fit is close enough to the best. Simulation points are the intervals
///     closest to each cluster centroid (section 5.2); the single simulation point is the
///     interval closest to the whole-run centroid (section 5.1).
/// </summary>
public static class SimPointAnalysis {
    /// <param name="intervalBbvs">Per-interval basic-block vectors from a <see cref="BbvProfiler" />.</param>
    /// <param name="dimensions">Projected dimensionality (paper: 15).</param>
    /// <param name="maxK">Largest cluster count tried (paper: 10).</param>
    /// <param name="bicFraction">
    ///     Pick the smallest k whose BIC reaches this fraction of the spread between the
    ///     worst and best score seen (paper: 0.9).
    /// </param>
    /// <param name="seed">Seed for the projection matrix and k-means initialization.</param>
    public static SimPointResult Analyze(
        IReadOnlyList<IReadOnlyDictionary<ulong, long>> intervalBbvs,
        int dimensions = 15,
        int maxK = 10,
        double bicFraction = 0.9,
        int seed = 42
    ) {
        int n = intervalBbvs.Count;
        if (n == 0) throw new ArgumentException("No intervals to analyze.", nameof(intervalBbvs));

        double[][] projected = Project(intervalBbvs, dimensions, seed);

        // K-means for k = 1..maxK, each scored with the BIC; keep every clustering so the
        // chosen k's assignment is reused directly.
        int kLimit = Math.Min(maxK, n);
        var assignments = new int[kLimit + 1][];
        var centroids = new double[kLimit + 1][][];
        var bic = new double[kLimit + 1];
        var rng = new Random(seed);

        // Variance floor for the BIC: a clustering that reproduces the data exactly
        // (possible with duplicated interval vectors, unlike the paper's real-profile
        // regime) would otherwise earn unbounded likelihood and drag k to the maximum.
        var globalCentroid = new double[dimensions];
        foreach (double[] v in projected)
            for (var j = 0; j < dimensions; j++)
                globalCentroid[j] += v[j] / n;
        double globalSumSq = projected.Sum(v => SquaredDistance(v, globalCentroid));
        double varianceFloor = Math.Max(1e-30, globalSumSq / n * 1e-6);

        for (var k = 1; k <= kLimit; k++) {
            (assignments[k], centroids[k]) = KMeans(projected, k, rng);
            bic[k] = Bic(projected, assignments[k], centroids[k], k, varianceFloor);
        }

        double best = double.NegativeInfinity, worst = double.PositiveInfinity;
        for (var k = 1; k <= kLimit; k++) {
            best = Math.Max(best, bic[k]);
            worst = Math.Min(worst, bic[k]);
        }

        // Smallest k whose score covers bicFraction of the [worst, best] spread. A flat
        // spread (single k, or identical scores) selects k = 1.
        double threshold = worst + bicFraction * (best - worst);
        var chosenK = 1;
        for (var k = 1; k <= kLimit; k++)
            if (bic[k] >= threshold) {
                chosenK = k;
                break;
            }

        int[] phases = assignments[chosenK];
        double[][] centers = centroids[chosenK];

        // Representative per cluster: the interval closest to its centroid (Euclidean, in
        // the projected space); weight = cluster share of all intervals.
        var points = new List<SimulationPoint>();
        for (var c = 0; c < chosenK; c++) {
            int bestIdx = -1;
            double bestDist = double.PositiveInfinity;
            var members = 0;
            for (var i = 0; i < n; i++) {
                if (phases[i] != c) continue;
                members++;
                double d = SquaredDistance(projected[i], centers[c]);
                if (d < bestDist) {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            if (members > 0) points.Add(new SimulationPoint(bestIdx, c, members / (double)n));
        }

        // Single simulation point: closest interval to the whole-run centroid.
        var global = new double[dimensions];
        foreach (double[] v in projected)
            for (var j = 0; j < dimensions; j++)
                global[j] += v[j] / n;
        var single = 0;
        double singleDist = double.PositiveInfinity;
        for (var i = 0; i < n; i++) {
            double d = SquaredDistance(projected[i], global);
            if (d < singleDist) {
                singleDist = d;
                single = i;
            }
        }

        return new SimPointResult(n, chosenK, phases, points, single);
    }

    // Normalizes each BBV to sum 1 (the paper's proportion-of-time normalization) and
    // projects it through a random matrix with entries uniform in [-1, 1]. The matrix is
    // never materialized: entry (block, dim) is derived from a stable hash of the block's
    // start PC, the dimension, and the seed, so arbitrarily many static blocks cost nothing.
    private static double[][] Project(
        IReadOnlyList<IReadOnlyDictionary<ulong, long>> intervals,
        int dimensions,
        int seed
    ) {
        var projected = new double[intervals.Count][];
        for (var i = 0; i < intervals.Count; i++) {
            var v = new double[dimensions];
            double total = intervals[i].Values.Aggregate<long, double>(0, (current, weight) => current + weight);
            if (total > 0)
                foreach ((ulong block, long weight) in intervals[i]) {
                    double share = weight / total;
                    for (var j = 0; j < dimensions; j++) v[j] += share * ProjectionEntry(block, j, seed);
                }

            projected[i] = v;
        }

        return projected;
    }

    // SplitMix64 over (block, dimension, seed) → uniform double in [-1, 1].
    private static double ProjectionEntry(ulong block, int dimension, int seed) {
        ulong z = block + 0x9E3779B97F4A7C15UL * ((ulong)(uint)dimension + 1) + (uint)seed;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z / (double)ulong.MaxValue * 2.0 - 1.0;
    }

    // Standard k-means (paper section 4.2.2): centers initialized to k distinct random data
    // points, then alternate membership assignment and centroid update until stable.
    private static (int[] Assignment, double[][] Centroids) KMeans(double[][] data, int k, Random rng) {
        int n = data.Length, d = data[0].Length;

        // Distinct random starting points (indices sampled without replacement).
        int[] order = Enumerable.Range(0, n).ToArray();
        for (int i = n - 1; i > 0; i--) {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var centroids = new double[k][];
        for (var c = 0; c < k; c++) centroids[c] = (double[])data[order[c]].Clone();

        var assignment = new int[n];
        Array.Fill(assignment, -1);
        var changed = true;
        for (var iter = 0; iter < 200 && changed; iter++) {
            changed = false;
            for (var i = 0; i < n; i++) {
                var bestC = 0;
                double bestD = double.PositiveInfinity;
                for (var c = 0; c < k; c++) {
                    double dist = SquaredDistance(data[i], centroids[c]);
                    if (dist < bestD) {
                        bestD = dist;
                        bestC = c;
                    }
                }

                if (assignment[i] != bestC) {
                    assignment[i] = bestC;
                    changed = true;
                }
            }

            var counts = new int[k];
            var sums = new double[k][];
            for (var c = 0; c < k; c++) sums[c] = new double[d];
            for (var i = 0; i < n; i++) {
                counts[assignment[i]]++;
                for (var j = 0; j < d; j++) sums[assignment[i]][j] += data[i][j];
            }

            for (var c = 0; c < k; c++)
                if (counts[c] > 0)
                    for (var j = 0; j < d; j++)
                        centroids[c][j] = sums[c][j] / counts[c];
                else
                    // Empty cluster: reseed to a random point so k centers survive.
                    centroids[c] = (double[])data[rng.Next(n)].Clone();
        }

        return (assignment, centroids);
    }

    // BIC score (paper section 4.2.3, the Pelleg & Moore k-means formulation):
    // BIC = l(D|k) − (p_j / 2)·log R with p_j = (k−1) + d·k + 1, and the likelihood summed
    // per cluster under a spherical Gaussian with shared variance σ².
    private static double Bic(double[][] data, int[] assignment, double[][] centroids, int k, double varianceFloor) {
        int n = data.Length, d = data[0].Length;

        double sumSq = 0;
        var counts = new int[k];
        for (var i = 0; i < n; i++) {
            counts[assignment[i]]++;
            sumSq += SquaredDistance(data[i], centroids[assignment[i]]);
        }

        double variance = Math.Max(sumSq / Math.Max(1, n - k), varianceFloor);

        double likelihood = 0;
        for (var c = 0; c < k; c++) {
            if (counts[c] == 0) continue;
            double rc = counts[c];
            // Spherical d-dimensional Gaussian: every dimension contributes ½·log(2πσ²),
            // so both the constant and the variance term scale with d.
            likelihood += -rc * d / 2.0 * Math.Log(2 * Math.PI)
                        - rc * d / 2.0 * Math.Log(variance)
                        - (rc - 1) / 2.0
                        + rc * Math.Log(rc / n);
        }

        double parameters = k - 1 + d * k + 1;
        return likelihood - parameters / 2.0 * Math.Log(n);
    }

    private static double SquaredDistance(double[] a, double[] b) =>
        a.Select((t, i) => t - b[i]).Sum(diff => diff * diff);
}