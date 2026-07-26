namespace Pipeline;

/// <summary>
///     Statistical-sampling math for SMARTS (Wunderlich, Wenisch, Falsafi &amp; Hoe, "SMARTS:
///     Accelerating Microarchitecture Simulation via Rigorous Statistical Sampling", ISCA 2003,
///     Section 2): the mean and coefficient of variation of a set of per-sampling-unit
///     measurements, the confidence interval a given sample achieves, and the sample size needed
///     to reach a target confidence.
/// </summary>
public static class SmartsStatistics {
    /// <summary>z for a 95% confidence level (paper Section 5.1).</summary>
    public const double Z95 = 1.97;

    /// <summary>z for a 99.7% ("3σ", virtually-certain) confidence level (paper Section 5.1).</summary>
    public const double Z997 = 3.0;

    public static double Mean(IReadOnlyList<double> samples) => samples.Average();

    /// <summary>
    ///     Coefficient of variation of <paramref name="samples" />: Bessel-corrected sample
    ///     standard deviation normalized by the sample mean (paper Section 2: V_x = σ_x / X̄).
    ///     Zero for fewer than two samples or a zero mean.
    /// </summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> samples) {
        if (samples.Count < 2) return 0.0;
        double mean = SmartsStatistics.Mean(samples);
        if (mean == 0.0) return 0.0;

        double sumSq = samples.Sum(x => (x - mean) * (x - mean));
        double stdDev = Math.Sqrt(sumSq / (samples.Count - 1));
        return stdDev / mean;
    }

    /// <summary>
    ///     The relative confidence interval ±ε achieved by a sample of size <paramref name="n" />
    ///     with coefficient of variation <paramref name="cv" />, at confidence level <paramref name="z" />
    ///     (paper Section 2: ε = z·V_x / √n).
    /// </summary>
    public static double ConfidenceInterval(double cv, int n, double z) =>
        n <= 0 ? double.PositiveInfinity : z * cv / Math.Sqrt(n);

    /// <summary>
    ///     The sample size <c>n_tuned</c> needed to achieve confidence interval ±<paramref name="epsilon" />
    ///     at confidence level <paramref name="z" />, given coefficient of variation <paramref name="cv" />
    ///     (paper Section 2: n ≥ (z·V̂_x/ε)²).
    /// </summary>
    public static int RequiredSampleSize(double cv, double z, double epsilon) {
        if (epsilon <= 0) throw new ArgumentOutOfRangeException(nameof(epsilon));
        return (int)Math.Ceiling(Math.Pow(z * cv / epsilon, 2));
    }
}
