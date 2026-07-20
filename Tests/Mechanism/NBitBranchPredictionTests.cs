#region

using Mechanism;
using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class NBitBranchPredictionTests {
    // ── 1-bit predictor ───────────────────────────────────────────────────────

    [Fact]
    public void OneBit_ColdMiss_PredictsNotTaken() {
        var p = new NBitBp(1);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void OneBit_AfterTakenUpdate_PredictsTaken() {
        var p = new NBitBp(1);
        p.Update(0x1000, true, 0x2000);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void OneBit_SingleNotTakenFlipsImmediately() {
        var p = new NBitBp(1);
        p.Update(0x1000, true, 0x2000);  // now taken
        p.Update(0x1000, false, 0x2000); // one miss flips to not-taken
        Assert.False(p.Predict(0x1000).PredictedTaken);
    }

    // ── 2-bit predictor ───────────────────────────────────────────────────────

    [Fact]
    public void TwoBit_ColdMiss_PredictsNotTaken() {
        var p = new NBitBp();
        Assert.False(p.Predict(0x1000).PredictedTaken);
    }

    [Fact]
    public void TwoBit_AlwaysTaken_ConvergesAfterTwoUpdates() {
        var p = new NBitBp();
        ulong pc = 0x1000;
        p.Update(pc, true, 0x2000);
        p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
        Assert.Equal(0x2000UL, p.Predict(pc).PredictedTarget);
    }

    [Fact]
    public void TwoBit_SingleMiss_DoesNotFlipFromStronglyTaken() {
        var p = new NBitBp();
        ulong pc = 0x100;
        // Drive to strongly taken (counter = 3)
        for (var i = 0; i < 4; i++) p.Update(pc, true, 0x200);
        // One not-taken miss should not flip prediction
        p.Update(pc, false, 0x200);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── 3-bit predictor ───────────────────────────────────────────────────────

    [Fact]
    public void ThreeBit_RequiresThreeMissesToFlip() {
        var p = new NBitBp(3);
        ulong pc = 0x400;
        // Drive to strongly taken (counter = 7)
        for (var i = 0; i < 8; i++) p.Update(pc, true, 0x500);
        // Two misses: still taken
        p.Update(pc, false, 0x500);
        p.Update(pc, false, 0x500);
        Assert.True(p.Predict(pc).PredictedTaken);
        // Third miss: still taken (weakly taken at 4, threshold = 4 → taken)
        p.Update(pc, false, 0x500);
        Assert.True(p.Predict(pc).PredictedTaken);
        // Fourth miss: now not-taken (counter falls below threshold)
        p.Update(pc, false, 0x500);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── N-bit general properties ──────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NBit_AlwaysTaken_ConvergesForAnyBitWidth(int bits) {
        var p = new NBitBp(bits);
        ulong pc = 0x1000;
        int warmup = 1 << bits; // saturate the counter
        for (var i = 0; i < warmup; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void TwoBit_TwoPcsDoNotInterfere() {
        var p = new NBitBp();
        ulong pcA = 0x100, pcB = 0x200;
        for (var i = 0; i < 4; i++) p.Update(pcA, true, 0x300);
        for (var i = 0; i < 4; i++) p.Update(pcB, false, pcB + 4);
        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }
}