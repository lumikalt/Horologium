using Mechanism.ValuePredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="StridePredictor" />'s 2-delta-style confidence FSM, in isolation
///     from pipeline complexity.
/// </summary>
public class StrideValuePredictionTests {
    [Fact]
    public void ColdMiss_NoPrediction() {
        var p = new StridePredictor();
        Assert.False(p.TryPredict(0x1000, default, out _));
    }

    [Fact]
    public void SingleStrideMatch_StillNotConfident() {
        // 2-delta: one matching stride (Init -> Transient) is not enough to predict yet.
        var p = new StridePredictor();
        const ulong pc = 0x1000;
        p.Update(pc, default, 10); // seeds last value, state = Init
        p.Update(pc, default, 20); // stride = 10, first ever comparison (0 != 10) -> stays Init
        Assert.False(p.TryPredict(pc, default, out _));
    }

    [Fact]
    public void TwoConsecutiveMatchingStrides_ReachesSteadyAndPredicts() {
        var p = new StridePredictor();
        const ulong pc = 0x1000;
        p.Update(pc, default, 10); // Init, last=10, stride=0
        p.Update(pc, default, 20); // stride=10 vs 0: mismatch, stays Init, stride now 10
        p.Update(pc, default, 30); // stride=10 vs 10: match -> Transient
        Assert.False(p.TryPredict(pc, default, out _));
        p.Update(pc, default, 40); // stride=10 vs 10: match -> Steady
        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(50UL, predicted); // lastValue(40) + stride(10)
    }

    [Fact]
    public void SteadyState_PredictsIndefinitelyWhileStrideHolds() {
        var p = new StridePredictor();
        const ulong pc = 0x2000;
        ulong value = 5;
        for (var i = 0; i < 10; i++) {
            p.Update(pc, default, value);
            value += 3;
        }

        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(value, predicted); // last recorded value (value - 3) + stride (3)
    }

    [Fact]
    public void MismatchInSteady_DropsToInit_RequiringTwoFreshConfirmations() {
        var p = new StridePredictor();
        const ulong pc = 0x3000;
        ulong value = 0;
        for (var i = 0; i < 5; i++) {
            p.Update(pc, default, value);
            value += 4;
        }

        Assert.True(p.TryPredict(pc, default, out ulong steadyPrediction));
        Assert.Equal(value, steadyPrediction); // last recorded value (16) + stride (4)

        // Break the pattern: a single mismatch drops Steady straight back to Init (not merely
        // one step down to Transient), so recovering costs two fresh confirmations of a new
        // stride — the same cost as bootstrapping from a cold predictor.
        ulong afterBreak = value + 999;
        p.Update(pc, default, afterBreak); // stride jumps to 999+4=1003: mismatch, Steady -> Init
        Assert.False(p.TryPredict(pc, default, out _));

        const long newStride = 12;
        ulong v1 = unchecked(afterBreak + (ulong)newStride);
        p.Update(pc, default, v1); // stride=12 vs the just-primed 1003: mismatch, stays Init
        Assert.False(p.TryPredict(pc, default, out _));

        ulong v2 = unchecked(v1 + (ulong)newStride);
        p.Update(pc, default, v2); // stride=12 vs 12: match, Init -> Transient
        Assert.False(p.TryPredict(pc, default, out _));

        ulong v3 = unchecked(v2 + (ulong)newStride);
        p.Update(pc, default, v3); // stride=12 vs 12: match, Transient -> Steady
        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(unchecked(v3 + (ulong)newStride), predicted);
    }

    [Fact]
    public void BrokenPatternAfterTransient_DropsToNoPred_AndRecoveryNeedsTwoMatches() {
        var p = new StridePredictor();
        const ulong pc = 0x4000;

        p.Update(pc, default, 100); // seed: Init, last=100, stride=0
        p.Update(pc, default, 110); // stride=10 vs 0: mismatch, stays Init, stride now 10
        p.Update(pc, default, 120); // stride=10 vs 10: match, Init -> Transient
        Assert.False(p.TryPredict(pc, default, out _));

        p.Update(pc, default, 500); // stride=380 vs 10: mismatch, Transient -> NoPred
        Assert.False(p.TryPredict(pc, default, out _));

        p.Update(pc, default, 1000); // stride=500 vs 380: mismatch, stays NoPred
        Assert.False(p.TryPredict(pc, default, out _));

        // Recovery requires two consecutive matching strides, same as bootstrapping from Init.
        p.Update(pc, default, 1500); // stride=500 vs 500: match, NoPred -> Transient
        Assert.False(p.TryPredict(pc, default, out _));

        p.Update(pc, default, 2000); // stride=500 vs 500: match, Transient -> Steady
        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(2500UL, predicted);
    }

    [Fact]
    public void Tagless_DistinctPcsAliasingToSameSlot_ShareState() {
        var p = new StridePredictor(1);
        p.Update(0x1000, default, 10);
        p.Update(0x1000, default, 20);
        p.Update(0x1000, default, 30);
        p.Update(0x1000, default, 40);
        Assert.True(p.TryPredict(0x1000, default, out _));

        // A different PC aliasing to the same (single-entry) table slot inherits and then
        // disrupts that state, exactly as LvpPredictor's aliasing test documents.
        p.Update(0x2000, default, 999);
        Assert.False(p.TryPredict(0x2000, default, out _));
    }
}
