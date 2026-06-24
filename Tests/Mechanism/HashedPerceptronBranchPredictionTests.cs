using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV.Config;

namespace Tests.Mechanism;

public class HashedPerceptronBranchPredictionTests {
    // ── Cold state ────────────────────────────────────────────────────────────

    [Fact]
    public void Cold_AllWeightsZero_PredictsWeaklyTaken_TargetFallsThrough() {
        // y = 0 (all weights zero) → 0 >= 0 → predicted taken, but no BTB entry.
        var p = new HashedPerceptronPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new HashedPerceptronPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new HashedPerceptronPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── Pattern learning ──────────────────────────────────────────────────────

    [Fact]
    public void AlternatingPattern_LearnsTakenAfterNotTakenHistory() {
        // T N T N … pattern: geometric history tables capture the regularity.
        var p = new HashedPerceptronPredictor();
        ulong pc = 0x400;
        bool[] pattern = [true, false, true, false, true, false, true, false,];

        for (var cycle = 0; cycle < 60; cycle++)
            foreach (bool t in pattern)
                p.Update(pc, t, 0x800);

        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Shared history across correlated PCs ─────────────────────────────────

    [Fact]
    public void TwoBranches_CorrelatedHistory_BothConverge() {
        // Interleaved training: pcA always taken, pcB always not-taken.
        // Both branches share history tables, but their PC-based hashes differ,
        // so each converges to its own direction.
        var p = new HashedPerceptronPredictor(512);
        ulong pcA = 0x1000, pcB = 0x1004;
        for (var i = 0; i < 80; i++) {
            p.Update(pcA, true, 0x2000);
            p.Update(pcB, false, pcB + 4);
        }

        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    // ── Saturation stability ──────────────────────────────────────────────────

    [Fact]
    public void WeightsReachSaturation_RemainsStable() {
        var p = new HashedPerceptronPredictor();
        ulong pc = 0x80;
        for (var i = 0; i < 200; i++) p.Update(pc, true, 0x100);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Custom history lengths ────────────────────────────────────────────────

    [Fact]
    public void CustomHistLengths_ConvergesAlwaysTaken() {
        var p = new HashedPerceptronPredictor(256, [0, 4, 16,]);
        ulong pc = 0x1000;
        for (var i = 0; i < 40; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Config_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.HashedPerceptron(1024);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<HashedPerceptronConfig>(result);
        Assert.Equal(1024, typed.TableSize);
    }
}