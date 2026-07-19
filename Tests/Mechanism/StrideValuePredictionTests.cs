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
    public void BackToBackTryPredict_ScalesByInFlightDepthWithoutAnInterveningUpdate() {
        // Simulates several in-flight occurrences of the same PC being predicted (at Rename)
        // before the earliest of them has retired (at Commit calling Update) -- the scenario
        // StridePredictor's in-flight depth tracking exists for (see its doc comment). Each
        // successive TryPredict call, with no Update in between, must multiply the stride by how
        // many occurrences are now unresolved, not keep re-predicting a single stride step past
        // the same stale last-committed value.
        var p = new StridePredictor();
        const ulong pc = 0x1000;
        p.Update(pc, default, 0);
        p.Update(pc, default, 10); // stride=10 vs 0: mismatch, stays Init, stride now 10
        p.Update(pc, default, 20); // stride=10 vs 10: match, Init -> Transient
        p.Update(pc, default, 30); // stride=10 vs 10: match, Transient -> Steady

        Assert.True(p.TryPredict(pc, default, out ulong first));
        Assert.Equal(40UL, first); // lastValue(30) + stride(10) * depth(1)
        Assert.True(p.TryPredict(pc, default, out ulong second));
        Assert.Equal(50UL, second); // lastValue(30) + stride(10) * depth(2), not chained off `first`
        Assert.True(p.TryPredict(pc, default, out ulong third));
        Assert.Equal(60UL, third); // lastValue(30) + stride(10) * depth(3)
    }

    [Fact]
    public void Update_DecrementsInFlightDepth_WithoutClobberingFurtherSpeculation() {
        // A commit must only ever signal "one fewer occurrence is unresolved" -- never "reset
        // speculation to here" -- or it would clobber legitimate further-ahead predictions that
        // already happened for younger, still-in-flight occurrences (the bug this design fixes;
        // see StridePredictor's doc comment for the pipeline measurement that caught it).
        var p = new StridePredictor();
        const ulong pc = 0x2000;
        p.Update(pc, default, 0);
        p.Update(pc, default, 10);
        p.Update(pc, default, 20);
        p.Update(pc, default, 30); // Steady, stride=10

        // Three occurrences renamed back-to-back before any of them commit.
        Assert.True(p.TryPredict(pc, default, out ulong spec1));
        Assert.Equal(40UL, spec1);
        Assert.True(p.TryPredict(pc, default, out ulong spec2));
        Assert.Equal(50UL, spec2);
        Assert.True(p.TryPredict(pc, default, out ulong spec3));
        Assert.Equal(60UL, spec3);

        // The first of those three commits (matches its own prediction, 40): in-flight depth
        // drops from 3 to 2, but the two still-unresolved occurrences (50, 60) are not clobbered
        // -- the *next* prediction correctly continues the sequence at 70, not at 40+10=50.
        p.Update(pc, default, 40);
        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(70UL, predicted); // lastValue(40) + stride(10) * depth(2 in-flight + 1)
    }

    [Fact]
    public void RecoverSpeculativeHistory_ResetsInFlightDepth_AfterSquash() {
        // A squash discards every younger in-flight instruction without ever calling Update for
        // them, so the in-flight counter must be reset explicitly (there is no commit to
        // decrement it) -- otherwise it would leak upward forever across repeated squashes.
        var p = new StridePredictor();
        const ulong pc = 0x6000;
        p.Update(pc, default, 0);
        p.Update(pc, default, 10);
        p.Update(pc, default, 20);
        p.Update(pc, default, 30); // Steady, stride=10

        Assert.True(p.TryPredict(pc, default, out _)); // in-flight depth now 1
        Assert.True(p.TryPredict(pc, default, out _)); // in-flight depth now 2

        p.RecoverSpeculativeHistory(); // simulates a full-flush squash discarding both

        Assert.True(p.TryPredict(pc, default, out ulong predicted));
        Assert.Equal(40UL, predicted); // lastValue(30) + stride(10) * depth(1), not depth(3)
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
