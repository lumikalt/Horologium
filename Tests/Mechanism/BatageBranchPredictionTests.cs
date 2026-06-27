using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV32.Config;

namespace Tests.Mechanism;

public class BatageBranchPredictionTests {
    [Fact]
    public void Cold_PredictsNotTaken_FallThroughTarget() {
        var p = new BatagePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new BatagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 20; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
        Assert.Equal(0x2000UL, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new BatagePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 20; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void TwoBranches_DoNotInterfere() {
        var p = new BatagePredictor();
        ulong pcA = 0x100, pcB = 0x200;
        for (var i = 0; i < 20; i++) p.Update(pcA, true, 0x300);
        for (var i = 0; i < 20; i++) p.Update(pcB, false, pcB + 4);
        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    [Fact]
    public void BiasOverrides_AfterHeavyTraining() {
        // Train a branch heavily taken at one PC; the bias table should hold
        // enough positive weight to predict taken even after a cold TAGE state.
        var p = new BatagePredictor();
        ulong pc = 0x5000;
        for (var i = 0; i < 64; i++) p.Update(pc, true, 0x6000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void Config_RoundTrip() {
        var cfg = new BatageConfig();
        string json = JsonSerializer.Serialize<BranchPredictorConfig>(cfg);
        var rt = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<BatageConfig>(rt);
    }
}