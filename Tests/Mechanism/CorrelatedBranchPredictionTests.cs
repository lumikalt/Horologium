#region

using Mechanism;
using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class CorrelatedBranchPredictionTests {
    // ── CorrelatedBp (m, n) ────────────────────────────────────────────

    [Fact]
    public void Correlated_ColdMiss_PredictsFallThrough() {
        var p = new CorrelatedBp();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void Correlated_AlwaysTaken_ConvergesAfterTraining() {
        var p = new CorrelatedBp();
        const ulong pc = 0x1000;
        // Warmup: push the 2-bit counter past weakly-not-taken
        for (var i = 0; i < 4; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
        Assert.Equal(0x2000UL, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void Correlated_AlternatingPattern_Learns() {
        // The (2,2) predictor has a 2-bit history; a TNTNT pattern produces
        // history "01" or "10" alternating. After enough training the PHT
        // entries for those patterns saturate correctly.
        var p = new CorrelatedBp();
        const ulong pc = 0x100;
        bool[] pattern = [true, false, true, false, true, false,];
        // Warmup for several full cycles
        for (var cycle = 0; cycle < 8; cycle++)
            foreach (bool t in pattern)
                p.Update(pc, t, 0x200);

        // After training, predict the next two outcomes in the pattern.
        // Current GHR after the last 'false' update is "10" (last two: T, F).
        // The predictor should now predict T (because history "10" → WT/ST).
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken); // expects T after TF history
    }

    [Fact]
    public void Correlated_TwoDifferentPcsDoNotInterfere() {
        // PAg: two distinct PCs that hash to different BHT slots are independent.
        var p = new CorrelatedBp();
        const ulong pcA = 0x100, pcB = 0x200;
        for (var i = 0; i < 8; i++) p.Update(pcA, true, 0x300);
        for (var i = 0; i < 8; i++) p.Update(pcB, false, 0x400);
        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    // ── GshareBp ───────────────────────────────────────────────────────

    [Fact]
    public void Gshare_ColdMiss_PredictsFallThrough() {
        var p = new GshareBp(4);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void Gshare_AlwaysTaken_ConvergesAfterTraining() {
        var p = new GshareBp(4);
        const ulong pc = 0x1000;
        for (var i = 0; i < 6; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
        Assert.Equal(0x2000UL, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void Gshare_GhrShiftsOnUpdate() {
        // Two branches on different PCs, both always taken.
        // Because GHR is global, each update shifts it. After 4 taken updates
        // the GHR should be all-ones (for 4 history bits) → XOR still maps to
        // a valid PHT slot.
        var p = new GshareBp(4);
        for (var i = 0; i < 10; i++) {
            p.Update(0x100, true, 0x200);
            p.Update(0x200, true, 0x300);
        }

        // Both branches should be predicted taken after training.
        Assert.True(p.Predict(0x100).PredictedTaken);
        Assert.True(p.Predict(0x200).PredictedTaken);
    }

    [Fact]
    public void Gshare_TakenFlagFlipsCounterCorrectly() {
        var p = new GshareBp(4);
        const ulong pc = 0x10;
        // Drive the counter to strongly not-taken
        for (var i = 0; i < 8; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
        // Now push it to taken
        for (var i = 0; i < 8; i++) p.Update(pc, true, 0x500);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── GselectPredictor ─────────────────────────────────────────────────────

    [Fact]
    public void Gselect_ColdMiss_PredictsFallThrough() {
        var p = new GselectPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void Gselect_AlwaysTaken_ConvergesAfterTraining() {
        var p = new GselectPredictor();
        const ulong pc = 0x1000;
        for (var i = 0; i < 10; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
        Assert.Equal(0x2000UL, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void Gselect_TwoBranchesWithDifferentPcBits_UseDistinctSlots() {
        // pcA and pcB differ in the lower pcBits, so their GHR=0 initial state
        // maps to different PHT slots even before any history is built up.
        var p = new GselectPredictor();
        const ulong pcA = 0x00, pcB = 0x10; // differ in pc bits [5:2]
        for (var i = 0; i < 8; i++) p.Update(pcA, true, 0x100);
        for (var i = 0; i < 8; i++) p.Update(pcB, false, 0x200);
        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }
}