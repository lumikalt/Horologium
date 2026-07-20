#region

using Mechanism;
using Mechanism.BranchPredictModels;

#endregion

namespace Tests.Mechanism;

public class LTageBranchPredictionTests {
    // ── Cold-miss / base behaviour ────────────────────────────────────────────

    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new LTagePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new LTagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new LTagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── BTB ───────────────────────────────────────────────────────────────────

    [Fact]
    public void BtbColdMiss_FallsThrough() {
        var p = new LTagePredictor();
        // Drive predictor to taken but BTB not yet populated.
        // (We can't easily reach "predicted taken but no BTB" in LTage because
        //  the first update populates BTB at the same time as training. Instead
        //  verify that a taken prediction returns the recorded BTB target.)
        ulong pc = 0x200;
        for (var i = 0; i < 16; i++) p.Update(pc, true, 0x400);
        Assert.Equal(0x400UL, p.Predict(pc).PredictedTarget);
    }

    // ── Loop predictor ────────────────────────────────────────────────────────

    [Fact]
    public void LoopPredictor_LearnsFixedTripCount() {
        // A branch taken 3 times then not-taken, repeated many times.
        // After LoopConfidence (4) consistent exits the loop predictor should
        // be confident and predict the pattern exactly.
        var p = new LTagePredictor();
        ulong pc = 0x3000;
        const ulong target = 0x2F00;
        const int tripCount = 3;

        // Warm up enough full loop iterations for the loop predictor to gain
        // confidence and for TAGE to settle.
        for (var iter = 0; iter < 20; iter++) {
            for (var i = 0; i < tripCount; i++) p.Update(pc, true, target);
            p.Update(pc, false, pc + 4);
        }

        // Simulate one more iteration and verify mid-loop predictions are taken.
        for (var i = 0; i < tripCount; i++) {
            Assert.True(p.Predict(pc).PredictedTaken);
            p.Update(pc, true, target);
        }

        // Exit prediction: not taken.
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void LoopPredictor_TripCountChange_DropConfidence() {
        var p = new LTagePredictor();
        ulong pc = 0x4000;
        const ulong target = 0x3F00;

        // Train with trip count = 4.
        for (var iter = 0; iter < 20; iter++) {
            for (var i = 0; i < 4; i++) p.Update(pc, true, target);
            p.Update(pc, false, pc + 4);
        }

        // Switch to trip count = 2. The predictor must re-learn without crashing.
        // We don't assert a specific prediction here — just that it doesn't throw
        // and that after enough re-training it converges again.
        for (var iter = 0; iter < 30; iter++) {
            for (var i = 0; i < 2; i++) p.Update(pc, true, target);
            p.Update(pc, false, pc + 4);
        }

        // After re-training on trip count = 2, verify exit prediction.
        // Trip count = 2: two taken iterations then not-taken.
        p.Update(pc, true, target); // iter 1 → CurrentIter = 1, predict taken
        Assert.True(p.Predict(pc).PredictedTaken);
        p.Update(pc, true, target); // iter 2 → CurrentIter = 2 = LearnedIter, predict not-taken
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── History / aliasing ────────────────────────────────────────────────────

    [Fact]
    public void TwoBranches_DoNotInterfere() {
        var p = new LTagePredictor();
        ulong pcA = 0x100, pcB = 0x200;
        for (var i = 0; i < 20; i++) p.Update(pcA, true, 0x300);
        for (var i = 0; i < 20; i++) p.Update(pcB, false, pcB + 4);
        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }
}