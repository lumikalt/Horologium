#region

using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV32.Config;

#endregion

namespace Tests.Mechanism;

public class HypreBranchPredictionTests {
    // ── Cold state ────────────────────────────────────────────────────────────

    [Fact]
    public void Cold_DoesNotThrow_TargetFallsThroughWhenNotInBtb() {
        var p = new HyprePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        if (!pred.PredictedTaken) Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new HyprePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 64; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new HyprePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 64; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── Pattern learning ──────────────────────────────────────────────────────

    [Fact]
    public void PeriodicPattern_ConvergesToHighAccuracy() {
        var p = new HyprePredictor();
        ulong pc = 0x4000;
        bool[] period = [true, true, false, true, false, false,];

        for (var i = 0; i < 2000; i++) {
            bool taken = period[i % period.Length];
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        var correct = 0;
        const int trials = 300;
        for (var i = 0; i < trials; i++) {
            bool taken = period[i % period.Length];
            bool pred = p.Predict(pc).PredictedTaken;
            if (pred == taken) correct++;
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        Assert.True(correct >= trials * 0.9, $"only {correct}/{trials} correct after training");
    }

    // ── Shared history across correlated PCs ─────────────────────────────────

    [Fact]
    public void TwoBranches_DifferentDirections_BothConverge() {
        var p = new HyprePredictor();
        ulong pcA = 0x1000, pcB = 0x1040;
        for (var i = 0; i < 80; i++) {
            p.Update(pcA, true, 0x2000);
            p.Update(pcB, false, pcB + 4);
        }

        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    // ── Custom history lengths ────────────────────────────────────────────────

    [Fact]
    public void CustomHistLengths_ConvergesAlwaysTaken() {
        var p = new HyprePredictor([2, 8, 24,]);
        ulong pc = 0x1000;
        for (var i = 0; i < 64; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Robustness under load ─────────────────────────────────────────────────

    [Fact]
    public void RandomPatternAcrossManyPcs_StaysStable() {
        var p = new HyprePredictor();
        var rng = new Random(12345);
        const int pcCount = 64;
        var pcs = new ulong[pcCount];
        for (var i = 0; i < pcCount; i++) pcs[i] = 0x8000UL + (ulong)i * 4;

        for (var iter = 0; iter < 2000; iter++) {
            ulong pc = pcs[rng.Next(pcCount)];
            bool taken = rng.Next(2) == 0;
            p.Predict(pc);
            p.Update(pc, taken, taken ? pc + 0x40 : pc + 4);
        }
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Config_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.Hypre();
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<HypreConfig>(result);
    }
}