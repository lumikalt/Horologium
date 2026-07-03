using Mechanism;
using Mechanism.BranchPredictModels;

namespace Tests.Mechanism;

public class VlaTageBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new VlaTagePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void NonVectorLoop_DoesNotGate() {
        var p = new VlaTagePredictor();
        ulong branch = 0x2000;
        ulong head = 0x1000;

        // Run 100 iterations with no vector instructions: PEN must never fire.
        for (var i = 0; i < 100; i++) {
            p.Predict(branch);
            // Simulate register values: a0 (variable) increments, a1 (bound) = 100.
            p.NotifyLoopBranchExecute(branch, head, (ulong)i, 100);
            p.Update(branch, true, head);
        }

        Assert.Equal(0, p.GatedPredictions);
    }

    [Fact]
    public void VectorLoop_GatesAfterLmEstimatesLongRemainder() {
        // Set up: loop with 200 iterations, vector instructions in body.
        // Variable a0 = i (counts up), bound a1 = 200.
        // After the first iteration the LM stores (rs1=0, rs2=200).
        // On the second iteration (rs1=1, rs2=200) it computes:
        //   variable=rs1=1, bound=rs2=200, increment=1, remaining=199 >= 32 → PenLatch=true.
        var p = new VlaTagePredictor();
        ulong branch = 0x2000;
        ulong head = 0x1000;
        ulong vecInst = 0x1800; // inside [head, branch]

        for (var i = 0; i < 200; i++) {
            p.Predict(branch);
            p.NotifyVectorInstruction(vecInst);
            p.NotifyLoopBranchExecute(branch, head, (ulong)i, 200);
            p.Update(branch, true, head);
        }

        // PEN should fire from iteration 2 onward (remaining ≥ 32 until last 5).
        // 200 iterations → PEN fires for roughly iterations 2..195 = ~194 gated predictions.
        Assert.True(
            p.GatedPredictions > 100,
            $"Expected many gated predictions for a 200-iteration vector loop (got {p.GatedPredictions})"
        );
    }

    [Fact]
    public void VectorLoop_DeassertsPenNearLoopEnd() {
        // A 40-iteration loop: PEN should eventually drop before the exit.
        var p = new VlaTagePredictor();
        ulong branch = 0x2000;
        ulong head = 0x1000;
        ulong vecInst = 0x1800;

        var gatedAt35 = 0;
        for (var i = 0; i < 40; i++) {
            p.Predict(branch);
            p.NotifyVectorInstruction(vecInst);
            p.NotifyLoopBranchExecute(branch, head, (ulong)i, 40);
            p.Update(branch, true, head);
            if (i == 35) gatedAt35 = p.GatedPredictions;
        }

        int totalGated = p.GatedPredictions;
        // PEN must have been active at some point (remaining ≥ 32 early on).
        Assert.True(totalGated > 0, $"Expected some gating (got {totalGated})");
        // And must have deasserted before the end (LoopTermThreshold=5):
        // gatedAt35 should equal totalGated if PEN deasserted before i=36.
        Assert.Equal(gatedAt35, totalGated);
    }

    [Fact]
    public void VectorLoop_ResetsAfterExit() {
        // After a loop exits (not-taken) and re-enters, the second run must
        // also gate once estimation fires.
        var p = new VlaTagePredictor();
        ulong branch = 0x2000;
        ulong head = 0x1000;
        ulong vecInst = 0x1800;

        // Run 1: 60 iterations taken, then exit.
        for (var i = 0; i < 60; i++) {
            p.Predict(branch);
            p.NotifyVectorInstruction(vecInst);
            p.NotifyLoopBranchExecute(branch, head, (ulong)i, 60);
            p.Update(branch, true, head);
        }

        p.Predict(branch);
        p.Update(branch, false, branch + 4); // exit

        int afterRun1 = p.GatedPredictions;
        Assert.True(afterRun1 > 0, $"Run 1 should gate (got {afterRun1})");

        // Run 2: same loop again from variable=0 to bound=60.
        for (var i = 0; i < 60; i++) {
            p.Predict(branch);
            p.NotifyVectorInstruction(vecInst);
            p.NotifyLoopBranchExecute(branch, head, (ulong)i, 60);
            p.Update(branch, true, head);
        }

        p.Predict(branch);
        p.Update(branch, false, branch + 4);

        Assert.True(
            p.GatedPredictions > afterRun1,
            $"Run 2 should also gate (total {p.GatedPredictions}, after run 1: {afterRun1})"
        );
    }

    [Fact]
    public void VectorLoop_AccuracyCloseToTageScL() {
        // On a stable taken-only backward branch VLA-TAGE (gated via bimodal) should
        // match or approach TAGE-SC-L since bimodal quickly learns "always taken."
        var tsl = new TageScLPredictor();
        var vlat = new VlaTagePredictor();

        ulong branch = 0x2000;
        ulong head = 0x1000;
        ulong vecInst = 0x1800;

        int tslMisses = 0, vlatMisses = 0;
        for (var iter = 0; iter < 500; iter++) {
            if (tsl.Predict(branch).PredictedTaken != true) tslMisses++;
            if (vlat.Predict(branch).PredictedTaken != true) vlatMisses++;

            vlat.NotifyVectorInstruction(vecInst);
            vlat.NotifyLoopBranchExecute(branch, head, (ulong)(iter % 200), 200);

            tsl.Update(branch, true, head);
            vlat.Update(branch, true, head);
        }

        // VLA-TAGE should not be dramatically worse than TSL on a trivial trace.
        Assert.True(
            vlatMisses <= tslMisses + 10,
            $"VLA-TAGE ({vlatMisses} misses) should be close to TAGE-SC-L ({tslMisses})"
        );
    }
}