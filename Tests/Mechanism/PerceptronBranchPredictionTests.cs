#region

using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV32.Config;

#endregion

namespace Tests.Mechanism;

public class PerceptronBranchPredictionTests {
    // ── Cold state ────────────────────────────────────────────────────────────

    [Fact]
    public void ColdMiss_AllWeightsZero_PredictsWeaklyTaken_TargetFallsThrough() {
        // y = 0 (all weights zero) → 0 >= 0 → predicted taken,
        // but BTB is empty so target is still pc+4.
        var p = new PerceptronPredictor(4, 64);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget); // no BTB entry yet
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new PerceptronPredictor(8, 64);
        ulong pc = 0x1000;
        for (var i = 0; i < 40; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new PerceptronPredictor(8, 64);
        ulong pc = 0x1000;
        for (var i = 0; i < 40; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── Pattern learning ─────────────────────────────────────────────────────

    [Fact]
    public void AlternatingPattern_LearnsTakenAfterNotTakenHistory() {
        // TNTNTNT… pattern: after enough training, with history "1010…" the
        // predictor should reliably predict T when the last branch was N.
        var p = new PerceptronPredictor(8, 64);
        ulong pc = 0x400;
        bool[] pattern = [true, false, true, false, true, false, true, false,];

        // Warm up for many cycles.
        for (var cycle = 0; cycle < 40; cycle++)
            foreach (bool t in pattern)
                p.Update(pc, t, 0x800);

        // After last false, history ends in N — next should be T.
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Two branches don't interfere ─────────────────────────────────────────

    [Fact]
    public void TwoBranches_DifferentTableSlots_DoNotInterfere() {
        // historyLength=0 eliminates GHR coupling; each perceptron is a pure bias.
        // pcA and pcB map to different slots (they differ in bits [9:2]).
        var p = new PerceptronPredictor(0);

        ulong pcA = 0x0000, pcB = 0x0100;
        for (var i = 0; i < 20; i++) p.Update(pcA, true, 0x200);
        for (var i = 0; i < 20; i++) p.Update(pcB, false, pcB + 4);

        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Config_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.Perceptron(16, 128);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<PerceptronConfig>(result);
        Assert.Equal(16, typed.HistoryLength);
        Assert.Equal(128, typed.TableSize);
    }

    // ── Threshold drives retraining ───────────────────────────────────────────

    [Fact]
    public void WeightsReachSaturation_AndPredictor_RemainsStable() {
        // After very heavy training on always-taken the bias weight should saturate
        // at 127 (sbyte.MaxValue) and the predictor should remain confidently taken.
        var p = new PerceptronPredictor(4, 64);
        ulong pc = 0x80;
        for (var i = 0; i < 200; i++) p.Update(pc, true, 0x100);
        Assert.True(p.Predict(pc).PredictedTaken);
    }
}