#region

using System.Text.Json;
using Mechanism;
using Mechanism.BranchPred;
using RiscV32.Config;

#endregion

namespace Tests.Mechanism;

public class MultiperspectivePerceptronBranchPredictionTests {
    [Fact]
    public void Cold_PredictsNotTaken_FallThroughTarget() {
        var p = new MultiperspectivePerceptronBp();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_DirectionConverges() {
        var p = new MultiperspectivePerceptronBp();
        ulong pc = 0x1000;
        for (var i = 0; i < 64; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void AlwaysNotTaken_DirectionConverges() {
        var p = new MultiperspectivePerceptronBp();
        ulong pc = 0x1000;
        for (var i = 0; i < 64; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // A period-6 outcome sequence is fully determined by a modest amount of history — both the
    // inherited TAGE-SC-L substrate and MPP's own five feature tables can represent it. Exercises
    // the full Predict/Update path through MPP's ResolvePrediction/OnAfterUpdate overrides many
    // thousands of times without breaking basic learning.
    [Fact]
    public void PeriodicPattern_ConvergesToHighAccuracy() {
        var p = new MultiperspectivePerceptronBp();
        ulong pc = 0x4000;
        bool[] period = [true, true, false, true, false, false,];

        for (var i = 0; i < 4000; i++) {
            bool taken = period[i % period.Length];
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        var correct = 0;
        const int trials = 600;
        for (var i = 0; i < trials; i++) {
            bool taken = period[i % period.Length];
            bool pred = p.Predict(pc).PredictedTaken;
            if (pred == taken) correct++;
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        Assert.True(correct >= trials * 0.9, $"only {correct}/{trials} correct after training");
    }

    // Exercises the IMLI features' backward-vs-forward branch classification (target < pc vs.
    // target >= pc), which nothing else in this test class distinguishes.
    [Fact]
    public void BackwardLoopBranch_ConvergesToTaken() {
        var p = new MultiperspectivePerceptronBp();
        ulong pc = 0x5000;
        ulong backwardTarget = 0x4000; // target < pc

        for (var i = 0; i < 64; i++) p.Update(pc, true, backwardTarget);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // Drives a genuinely unpredictable (50/50 pseudo-random) pattern across many PCs to stress
    // the recency stack, blurry-path dedup buffer, and modulo-gated GHISTMODPATH history without
    // an accuracy assertion (the pattern is unlearnable by construction) — a robustness/no-throw
    // test of the five feature tables' bookkeeping.
    [Fact]
    public void RandomPatternAcrossManyPcs_StaysStable() {
        var p = new MultiperspectivePerceptronBp();
        var rng = new Random(12345);
        const int pcCount = 64;
        var pcs = new ulong[pcCount];
        for (var i = 0; i < pcCount; i++) pcs[i] = 0x8000UL + (ulong)i * 4;

        for (var iter = 0; iter < 3000; iter++) {
            ulong pc = pcs[rng.Next(pcCount)];
            bool taken = rng.Next(2) == 0;
            BranchPrediction pred = p.Predict(pc);
            Assert.True(pred.PredictedTarget == pc + 4 || pred.PredictedTaken);
            p.Update(pc, taken, taken ? pc + 0x40 : pc + 4);
        }
    }

    [Fact]
    public void Config_RoundTrip() {
        var cfg = new MultiperspectivePerceptronConfig();
        string json = JsonSerializer.Serialize<BranchPredictorConfig>(cfg);
        var rt = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<MultiperspectivePerceptronConfig>(rt);
    }
}