using Mechanism;
using Mechanism.BranchPredictModels;

namespace Tests.Mechanism;

public class LlbpBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new LlbpPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new LlbpPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 8; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new LlbpPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 8; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void LlbpOverride_FiresAfterAllocation() {
        // After one predict+update cycle, TAGE allocates at table 0 and LLBP
        // allocates at table 0 on misprediction. On the second Predict, LLBP
        // matches at t=0 >= provider=0, so LlbpOverrides increments.
        var p = new LlbpPredictor();
        ulong pc = 0x1000;
        // Warm RCR to full window with a taken branch at a distinct PC.
        ulong warmPc = 0x8000;
        for (var i = 0; i < 120; i++) p.Update(warmPc, true, warmPc + 4);

        // Now let TAGE train on pc.
        for (var i = 0; i < 16; i++) p.Update(pc, true, 0x2000);

        int before = p.LlbpOverrides;
        p.Predict(pc);
        int after = p.LlbpOverrides;
        Assert.True(after > before, $"Expected LlbpOverrides to increase (was {before}, still {after})");
    }
}