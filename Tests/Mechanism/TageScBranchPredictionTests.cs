#region

using System.Text.Json;
using Mechanism;
using Mechanism.BranchPred;
using RiscV32.Config;

#endregion

namespace Tests.Mechanism;

public class TageScBranchPredictionTests {
    // ── Cold state ────────────────────────────────────────────────────────────

    [Fact]
    public void Cold_PredictsNotTaken_FallThroughTarget() {
        // Cold TAGE base = weakly not-taken; SC tables = zero (no override).
        var p = new TageScLBp();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new TageScLBp();
        const ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new TageScLBp();
        const ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── Pattern learning ──────────────────────────────────────────────────────

    [Fact]
    public void AlternatingPattern_LearnsTakenAfterNotTakenHistory() {
        var p = new TageScLBp();
        const ulong pc = 0x400;
        bool[] pattern = [true, false, true, false, true, false, true, false,];

        for (var cycle = 0; cycle < 60; cycle++)
            foreach (bool t in pattern)
                p.Update(pc, t, 0x800);

        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── SC can override TAGE ──────────────────────────────────────────────────

    [Fact]
    public void ScOverrides_Tage_AfterHeavyTraining() {
        // Heavy training biases SC tables; SC override should produce a stable result.
        var p = new TageScLBp();
        const ulong pc = 0x800;
        for (var i = 0; i < 100; i++) p.Update(pc, true, 0x1000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Loop predictor still active ───────────────────────────────────────────

    [Fact]
    public void LoopPredictor_StillActive_AfterScTraining() {
        // A loop with trip count 3: SC trains alongside TAGE; loop takes over.
        var p = new TageScLBp();
        const ulong pc = 0xC00;
        const ulong target = 0xD00;

        // Warm up enough for loop predictor to reach confidence.
        for (var iteration = 0; iteration < 20; iteration++) {
            p.Update(pc, true, target);  // iter 1
            p.Update(pc, true, target);  // iter 2
            p.Update(pc, false, pc + 4); // exit
        }

        // Loop is confident: next prediction should be taken (iter 1 of 3).
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Config_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.TageScL();
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<TageScLConfig>(result);
    }
}