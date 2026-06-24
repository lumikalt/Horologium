using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV.Config;

namespace Tests.Mechanism;

public class IttageBranchPredictionTests {
    [Fact]
    public void Cold_PredictsNotTaken_FallThroughTarget() {
        var p = new IttagePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_DirectionConverges() {
        var p = new IttagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void AlwaysNotTaken_DirectionConverges() {
        var p = new IttagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void IndirectTarget_LearnedAfterTraining() {
        var p = new IttagePredictor();
        ulong pc = 0x2000;
        ulong target = 0xDEAD_0000;
        for (var i = 0; i < 32; i++) p.Update(pc, true, target);
        Assert.Equal(target, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void DirectBranch_KnownTargetPassedThrough() {
        var p = new IttagePredictor();
        ulong pc = 0x3000, knownTarget = 0x3800;
        for (var i = 0; i < 8; i++) p.Update(pc, true, knownTarget);
        BranchPrediction pred = p.Predict(pc, knownTarget);
        Assert.Equal(knownTarget, pred.PredictedTarget);
    }

    [Fact]
    public void Config_RoundTrip() {
        var cfg = new IttageConfig();
        string json = System.Text.Json.JsonSerializer.Serialize<BranchPredictorConfig>(cfg);
        var rt = System.Text.Json.JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<IttageConfig>(rt);
    }
}