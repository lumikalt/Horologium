#region

using System.Text.Json;
using Mechanism;
using Mechanism.BranchPred;
using RiscV32.Config;

#endregion

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
        const ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void AlwaysNotTaken_DirectionConverges() {
        var p = new IttagePredictor();
        const ulong pc = 0x1000;
        for (var i = 0; i < 16; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void IndirectTarget_LearnedAfterTraining() {
        var p = new IttagePredictor();
        const ulong pc = 0x2000;
        const ulong target = 0xDEAD_0000;
        for (var i = 0; i < 32; i++) p.Update(pc, true, target);
        Assert.Equal(target, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void DirectBranch_KnownTargetPassedThrough() {
        var p = new IttagePredictor();
        const ulong pc = 0x3000, knownTarget = 0x3800;
        for (var i = 0; i < 8; i++) p.Update(pc, true, knownTarget);
        BranchPrediction pred = p.Predict(pc, (knownTarget, true));
        Assert.Equal(knownTarget, pred.PredictedTarget);
    }

    [Fact]
    public void IndirectTarget_TwoHistoryContexts_LearnDistinctTargets() {
        // ITTAGE's whole point: the same PC resolves to different targets depending on
        // execution history (e.g., virtual dispatch). Prime two distinct global-history
        // contexts with different not-taken branches, then train the shared indirect PC
        // with a different target under each context; both must be predicted correctly,
        // which is only possible via tagged-table entries (the tagless BTB alone can't
        // distinguish them since it's overwritten on every taken update).
        var p = new IttagePredictor();
        const ulong pc = 0x2100, targetA = 0xBEEF_0000, targetB = 0xFEED_0000;

        for (var i = 0; i < 64; i++) {
            ContextA();
            p.Update(pc, true, targetA);
            ContextB();
            p.Update(pc, true, targetB);
        }

        ContextA();
        Assert.Equal(targetA, p.Predict(pc).PredictedTarget);
        ContextB();
        Assert.Equal(targetB, p.Predict(pc).PredictedTarget);
        return;

        void ContextA() {
            for (var i = 0; i < 24; i++) p.Update(0x100, false, 0x104);
        }

        void ContextB() {
            for (var i = 0; i < 24; i++) p.Update(0x200, true, 0x204);
        }
    }

    [Fact]
    public void Config_RoundTrip() {
        var cfg = new IttageConfig();
        string json = JsonSerializer.Serialize<BranchPredictorConfig>(cfg);
        var rt = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<IttageConfig>(rt);
    }
}