#region

using Mechanism;
using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class StaticBranchPredictionTests {
    [Fact]
    public void BackwardNotForwards_ColdMiss_PredictsFallThrough() {
        var p = new AlwaysBackwardNotForwards();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void BackwardNotForwards_BackwardBranch_PredictsTaken() {
        var p = new AlwaysBackwardNotForwards();
        ulong pc = 0x1000;
        p.Update(pc, true, 0x800); // backward: target < pc
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x800UL, pred.PredictedTarget);
    }

    [Fact]
    public void BackwardNotForwards_ForwardBranch_PredictsNotTaken() {
        var p = new AlwaysBackwardNotForwards();
        ulong pc = 0x1000;
        p.Update(pc, true, 0x2000); // forward: target > pc
        BranchPrediction pred = p.Predict(pc);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void BackwardNotForwards_NotTakenUpdate_DoesNotPopulateBtb() {
        var p = new AlwaysBackwardNotForwards();
        ulong pc = 0x1000;
        p.Update(pc, false, 0x800);
        // BTB is unpopulated, so still a cold miss
        BranchPrediction pred = p.Predict(pc);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }
}