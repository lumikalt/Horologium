#region

using Mechanism;
using Mechanism.ValuePred;

#endregion

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="DynamicClassificationVp" /> (Rychlik et al.,
///     CMuART-1998-01, §3.2.3) in isolation from pipeline complexity: the 3-value learning window,
///     delta-based classification, routing, and the adapted eviction/reclassification rules. See
///     that class's own doc comment for the paper's 3-way-to-2-way fold and the eviction-trigger
///     adaptation.
/// </summary>
public class DynamicClassificationValuePredictionTests {
    [Fact]
    public void Unclassified_NoPredictionDuringLearningWindow() {
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x1000;
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
        p.Update(pc, default(ValueHistoryCheckpoint), 10); // 1st of 3 learning values
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
        p.Update(pc, default(ValueHistoryCheckpoint), 20); // 2nd of 3
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
        // The 3rd Update classifies, but the assigned component (cold, never trained) still
        // needs its own warmup before it ever predicts confidently.
        p.Update(pc, default(ValueHistoryCheckpoint), 30);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void ConstantDeltaHistory_ClassifiesToComputational_AndEventuallyPredicts() {
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x2000;

        // Learning window: deltas 10, 10 (equal) -> classified Computational (Stride).
        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);

        // The classified Stride component starts cold (no history replay from the learning
        // window -- see the class doc comment) and needs its own 2-confirmation bootstrap.
        p.Update(pc, default(ValueHistoryCheckpoint), 30); // Stride: seed, Init
        p.Update(pc, default(ValueHistoryCheckpoint), 40); // stride=10 vs 0: mismatch, stays Init, stride now 10
        p.Update(pc, default(ValueHistoryCheckpoint), 50); // stride=10 vs 10: match, Init -> Transient
        p.Update(pc, default(ValueHistoryCheckpoint), 60); // stride=10 vs 10: match, Transient -> Steady

        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(70UL, predicted); // lastValue(60) + stride(10) * depth(1)
    }

    [Fact]
    public void NonConstantDeltaHistory_ClassifiesToContext_AndEventuallyPredicts() {
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x3000;

        // Learning window: deltas 94, -96 (not equal) -> classified Context (VTAGE), playing the
        // paper's FCM/general-catch-all role.
        p.Update(pc, default(ValueHistoryCheckpoint), 5);
        p.Update(pc, default(ValueHistoryCheckpoint), 99);
        p.Update(pc, default(ValueHistoryCheckpoint), 3);

        // The classified VTAGE component starts cold; converges via its tagless LVP base on a
        // repeated value, same convergence pattern as VtageValuePredictionTests.
        const ulong steady = 77;
        for (var i = 0; i < 5000; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);

        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(steady, predicted);
    }

    [Fact]
    public void Update_OnlyTrainsTheAssignedComponent() {
        var context = new VtageVp();
        var computational = new StrideVp();
        var p = new DynamicClassificationVp(context, computational);
        const ulong pc = 0x4000;

        // Classifies to Computational.
        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);
        for (var i = 0; i < 10; i++) p.Update(pc, default(ValueHistoryCheckpoint), 20 + 10UL * (ulong)(i + 1));

        // The context component was never given this PC's data at all -- not even the 3 learning
        // values -- since classification routes every subsequent call to exactly one component.
        Assert.False(context.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void EvictionFromComputational_ReturnsToUnclassified_AndReclassifiesToContext() {
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x5000;

        // Classify to Computational and bring it to a confident, Steady prediction.
        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);
        p.Update(pc, default(ValueHistoryCheckpoint), 30);
        p.Update(pc, default(ValueHistoryCheckpoint), 40);
        p.Update(pc, default(ValueHistoryCheckpoint), 50);
        p.Update(pc, default(ValueHistoryCheckpoint), 60);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(70UL, predicted); // arms eviction

        // Break the stride: Stride's own FSM drops Steady -> Init on a single mismatch. With the
        // default evictThreshold of 2, that single miss below must NOT evict yet -- see
        // SingleMiss_DoesNotEvict_ButTwoConsecutiveMissesDo for the threshold boundary itself.
        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss

        // A second, consecutive non-confident Predict (no intervening Update) crosses the
        // threshold and evicts. Eviction from Computational (not the FCM/general role) returns
        // it to Unclassified rather than permanent Don't Predict.
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 2nd -> evicts

        // Relearn against a brand new pattern: non-constant deltas this time -> classifies to
        // Context instead, proving eviction genuinely reset classification rather than just
        // suppressing Computational's own (now-broken) prediction. (If eviction hadn't actually
        // fired, these Updates would keep training the still-assigned Stride component instead --
        // and its own FSM would eventually reconverge on the repeated `steady` tail below, so this
        // is the one assertion in this file that would silently pass for the wrong reason if
        // eviction ever regressed back to a lower effective threshold.)
        p.Update(pc, default(ValueHistoryCheckpoint), 5);
        p.Update(pc, default(ValueHistoryCheckpoint), 99);
        p.Update(pc, default(ValueHistoryCheckpoint), 3);
        const ulong steady = 77;
        for (var i = 0; i < 5000; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);

        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong reclassified));
        Assert.Equal(steady, reclassified);
    }

    [Fact]
    public void EvictionFromContext_BecomesPermanentlyDontPredict() {
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x6000;

        // Classify to Context (non-constant deltas) and converge to a confident prediction.
        p.Update(pc, default(ValueHistoryCheckpoint), 5);
        p.Update(pc, default(ValueHistoryCheckpoint), 99);
        p.Update(pc, default(ValueHistoryCheckpoint), 3);
        const ulong steady = 77;
        for (var i = 0; i < 5000; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(steady, predicted); // arms eviction

        // A single real mismatch immediately drops confidence here too (same established
        // behavior as VtageValuePredictionTests/LvpValuePredictionTests' own mispredict tests),
        // but the default evictThreshold of 2 means this first miss alone must not evict yet.
        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 2nd -> evicts

        // Context is the paper's FCM/general-catch-all role: eviction from it is permanent, so
        // even feeding a brand new, cleanly-classifiable pattern never predicts again.
        for (var i = 0; i < 20; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void SingleMiss_DoesNotEvict_ButTwoConsecutiveMissesDo() {
        // Regression test for the evict-on-first-miss -> evict-on-N-consecutive-misses tightening
        // a lone ordinary misprediction below the threshold must leave classification
        // intact, so the assigned component can simply recover on its own.
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x7000;

        // Classify to Computational (constant delta 10) and converge to Steady -- arms eviction.
        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);
        p.Update(pc, default(ValueHistoryCheckpoint), 30);
        p.Update(pc, default(ValueHistoryCheckpoint), 40);
        p.Update(pc, default(ValueHistoryCheckpoint), 50);
        p.Update(pc, default(ValueHistoryCheckpoint), 60);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));

        // Break the stride once (Stride's own FSM: Steady -> Init, new stride baseline 939) --
        // exactly one miss, strictly below the default threshold of 2.
        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss

        // Re-establish the new stride (939) via two matching Updates, deliberately without
        // querying TryPredict in between (so the miss streak isn't touched again). If the single
        // miss above had already evicted this PC to Unclassified, these two Updates would only
        // accumulate learning history instead of driving Stride's FSM -- and it would take a full
        // fresh 3-value learning window plus its own 2-match bootstrap to predict again, not just
        // these two matching Updates.
        p.Update(pc, default(ValueHistoryCheckpoint), 1938); // newStride 939 matches: Init -> Transient
        p.Update(pc, default(ValueHistoryCheckpoint), 2877); // matches again: Transient -> Steady

        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(2877UL + 939, predicted);
    }

    [Fact]
    public void TwoConsecutiveMisses_EvictFromComputational_EvenAcrossDifferentStrideBreaks() {
        // Complements SingleMiss_DoesNotEvict_ButTwoConsecutiveMissesDo: crossing the threshold
        // (two non-confident TryPredict calls in a row, no intervening confident one) must evict,
        // routing the PC back through Unclassified to a fresh classification.
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x7100;

        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);
        p.Update(pc, default(ValueHistoryCheckpoint), 30);
        p.Update(pc, default(ValueHistoryCheckpoint), 40);
        p.Update(pc, default(ValueHistoryCheckpoint), 50);
        p.Update(pc, default(ValueHistoryCheckpoint), 60);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));

        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 2nd -> evicts

        // Evicted from Computational returns to Unclassified: a brand-new, non-constant-delta
        // pattern must now go through the 3-value learning window and reclassify to Context.
        p.Update(pc, default(ValueHistoryCheckpoint), 5);
        p.Update(pc, default(ValueHistoryCheckpoint), 99);
        p.Update(pc, default(ValueHistoryCheckpoint), 3);
        const ulong steady = 77;
        for (var i = 0; i < 5000; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong reclassified));
        Assert.Equal(steady, reclassified);
    }

    [Fact]
    public void ConfidentPredictionBetweenMisses_ResetsTheStreak() {
        // A recovered guess between two misses must reset the consecutive-miss counter, so two
        // misses separated by a confident prediction must not evict -- only two truly consecutive
        // misses should.
        var p = new DynamicClassificationVp(new VtageVp(), new StrideVp());
        const ulong pc = 0x7200;

        p.Update(pc, default(ValueHistoryCheckpoint), 0);
        p.Update(pc, default(ValueHistoryCheckpoint), 10);
        p.Update(pc, default(ValueHistoryCheckpoint), 20);
        p.Update(pc, default(ValueHistoryCheckpoint), 30);
        p.Update(pc, default(ValueHistoryCheckpoint), 40);
        p.Update(pc, default(ValueHistoryCheckpoint), 50);
        p.Update(pc, default(ValueHistoryCheckpoint), 60);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));

        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss

        // Re-establish Steady (resets the streak on the next confident prediction below).
        p.Update(pc, default(ValueHistoryCheckpoint), 1938);
        p.Update(pc, default(ValueHistoryCheckpoint), 2877);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // confident: streak -> 0

        // Break the (new) stride again: one fresh miss, still below the threshold on its own.
        p.Update(pc, default(ValueHistoryCheckpoint), 1);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _)); // 1st consecutive miss again

        // Recover once more without ever having accumulated two misses in a row -- proves the
        // intervening confident prediction actually reset the streak rather than merely delaying
        // eviction by one call.
        var newStride = unchecked((long)(1UL - 2877));
        ulong v1 = unchecked(1UL + (ulong)newStride);
        ulong v2 = unchecked(v1 + (ulong)newStride);
        p.Update(pc, default(ValueHistoryCheckpoint), v1);
        p.Update(pc, default(ValueHistoryCheckpoint), v2);
        Assert.True(p.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(unchecked(v2 + (ulong)newStride), predicted);
    }
}