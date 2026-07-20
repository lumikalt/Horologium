#region

using Mechanism;
using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class ImliBranchPredictionTests {
    // ── Cold-miss / base behaviour ────────────────────────────────────────────

    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new ImliPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new ImliPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 4; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new ImliPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 4; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── IMLI counter mechanics ────────────────────────────────────────────────

    [Fact]
    public void BodyBranch_CorrelatesWithLoopIteration_ConvergesAfterTraining() {
        // The discriminating IMLI test: a body branch that is taken only at loop
        // iteration 2 (0-indexed). After one training pass the PHT entries
        // PHT[hash(bodyPc, 0..4)] encode "not-taken" for all iterations except
        // PHT[hash(bodyPc, 2)] which encodes "taken". A plain 2-bit predictor
        // cannot learn this; IMLI can.
        var p = new ImliPredictor();
        const ulong lcbPc = 0x2000; // loop-closing backward branch
        const ulong lcbTarget = 0x1000;
        const ulong bodyPc = 0x1800; // body branch (forward, taken only at iter=2)
        const ulong bodyTarget = 0x1900;
        const int tripCount = 5;

        // Training pass — one complete loop execution
        for (var iter = 0; iter < tripCount; iter++) {
            bool bodyTaken = iter == 2;
            p.Update(bodyPc, bodyTaken, bodyTaken ? bodyTarget : bodyPc + 4);
            bool lastIter = iter == tripCount - 1;
            bool lcbTaken = !lastIter;
            p.Update(lcbPc, lcbTaken, lcbTaken ? lcbTarget : lcbPc + 4);
        }

        // Second pass — should have zero body-branch mispredictions
        var misses = 0;
        for (var iter = 0; iter < tripCount; iter++) {
            BranchPrediction pred = p.Predict(bodyPc);
            bool bodyTaken = iter == 2;
            if (pred.PredictedTaken != bodyTaken) misses++;
            p.Update(bodyPc, bodyTaken, bodyTaken ? bodyTarget : bodyPc + 4);
            bool lastIter = iter == tripCount - 1;
            bool lcbTaken = !lastIter;
            p.Update(lcbPc, lcbTaken, lcbTaken ? lcbTarget : lcbPc + 4);
        }

        Assert.Equal(0, misses);
    }

    [Fact]
    public void ImliCounter_ResetsOnLoopExit() {
        // A not-taken backward branch resets _imli to 0, so predictions for
        // a second loop execution start at iter-index 0 again.
        var p = new ImliPredictor();
        const ulong lcbPc = 0x2000;
        const ulong lcbTarget = 0x1000;

        // Run one 3-iteration loop (3 taken, then 1 not-taken = exit)
        for (var i = 0; i < 3; i++) p.Update(lcbPc, true, lcbTarget);
        p.Update(lcbPc, false, lcbPc + 4); // exit → _imli reset to 0

        // Prediction for another branch now uses _imli=0, same as loop start
        ulong probePc = 0x1500;
        for (var i = 0; i < 4; i++) p.Update(probePc, true, 0x1600);
        Assert.True(p.Predict(probePc).PredictedTaken);
    }

    // ── Two branches do not interfere ─────────────────────────────────────────

    [Fact]
    public void TwoBranches_DoNotInterfere() {
        var p = new ImliPredictor();
        ulong pcA = 0x1000; // always not-taken
        ulong pcB = 0x3000; // always taken to 0x4000

        for (var i = 0; i < 8; i++) {
            p.Update(pcA, false, pcA + 4);
            p.Update(pcB, true, 0x4000);
        }

        Assert.False(p.Predict(pcA).PredictedTaken);
        BranchPrediction predB = p.Predict(pcB);
        Assert.True(predB.PredictedTaken);
        Assert.Equal(0x4000UL, predB.PredictedTarget);
    }
}