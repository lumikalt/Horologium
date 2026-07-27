#region

using Pipeline;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Unit tests for the SMARTS sampling-theory math (Wunderlich et al., ISCA 2003, Section 2) —
///     mean, coefficient of variation, confidence interval, and required sample size — against
///     hand-computed values, independent of any simulated workload.
/// </summary>
public class SmartsStatisticsTests {
    [Fact]
    public void Mean_IsArithmeticMean() { Assert.Equal(2.5, SmartsStatistics.Mean([1, 2, 3, 4,]), 10); }

    [Fact]
    public void CoefficientOfVariation_MatchesHandComputedValue() {
        // mean = 11, deviations ±1, Bessel-corrected variance = (1+1)/(2-1) = 2, stddev = √2.
        double cv = SmartsStatistics.CoefficientOfVariation([10, 12,]);
        Assert.Equal(Math.Sqrt(2) / 11, cv, 10);
    }

    [Fact]
    public void CoefficientOfVariation_SingleSample_IsZero() {
        Assert.Equal(0.0, SmartsStatistics.CoefficientOfVariation([42,]));
    }

    [Fact]
    public void CoefficientOfVariation_IdenticalSamples_IsZero() {
        Assert.Equal(0.0, SmartsStatistics.CoefficientOfVariation([5, 5, 5, 5,]));
    }

    [Fact]
    public void ConfidenceInterval_MatchesPaperFormula() {
        // ε = z·V/√n (paper Section 2).
        double epsilon = SmartsStatistics.ConfidenceInterval(0.5, 100, SmartsStatistics.Z95);
        Assert.Equal(1.97 * 0.5 / 10.0, epsilon, 10);
    }

    [Fact]
    public void ConfidenceInterval_ZeroSamples_IsInfinite() {
        Assert.Equal(double.PositiveInfinity, SmartsStatistics.ConfidenceInterval(0.5, 0, SmartsStatistics.Z997));
    }

    [Fact]
    public void RequiredSampleSize_MatchesPaperFormula() {
        // n ≥ (z·V/ε)² (paper Section 2), rounded up.
        int n = SmartsStatistics.RequiredSampleSize(0.5, SmartsStatistics.Z95, 0.03);
        double exact = Math.Pow(1.97 * 0.5 / 0.03, 2);
        Assert.Equal((int)Math.Ceiling(exact), n);
    }

    [Fact]
    public void RequiredSampleSize_TighterConfidence_NeedsMoreSamples() {
        int loose = SmartsStatistics.RequiredSampleSize(0.5, SmartsStatistics.Z95, 0.05);
        int tight = SmartsStatistics.RequiredSampleSize(0.5, SmartsStatistics.Z95, 0.01);
        Assert.True(tight > loose);
    }

    [Fact]
    public void SmartsResult_DerivedPropertiesMatchStatisticsHelpers() {
        SmartsUnitResult[] units = [
            new(0, 1000, 1000),
            new(1, 1000, 1200),
            new(2, 1000, 1100),
        ];
        var result = new SmartsResult(units, false, 3000);

        double[] cpis = [1.0, 1.2, 1.1,];
        Assert.Equal(SmartsStatistics.Mean(cpis), result.MeanCpi, 10);
        Assert.Equal(SmartsStatistics.CoefficientOfVariation(cpis), result.CoefficientOfVariation, 10);
        Assert.Equal(
            SmartsStatistics.ConfidenceInterval(result.CoefficientOfVariation, 3, SmartsStatistics.Z997),
            result.ConfidenceInterval(SmartsStatistics.Z997), 10
        );
        Assert.Equal(
            SmartsStatistics.RequiredSampleSize(result.CoefficientOfVariation, SmartsStatistics.Z997, 0.01),
            result.RequiredSampleSize(SmartsStatistics.Z997, 0.01)
        );
    }
}