using Mechanism;
using Mechanism.ValuePredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="DynamicClassificationValuePredictor" /> (Rychlik et al.,
///     CMuART-1998-01, §3.2.3) in isolation from pipeline complexity: the 3-value learning window,
///     delta-based classification, routing, and the adapted eviction/reclassification rules. See
///     that class's own doc comment for the paper's 3-way-to-2-way fold and the eviction-trigger
///     adaptation.
/// </summary>
public class DynamicClassificationValuePredictionTests {
    [Fact]
    public void Unclassified_NoPredictionDuringLearningWindow() {
        var p = new DynamicClassificationValuePredictor(new VtagePredictor(), new StridePredictor());
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
        var p = new DynamicClassificationValuePredictor(new VtagePredictor(), new StridePredictor());
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
        var p = new DynamicClassificationValuePredictor(new VtagePredictor(), new StridePredictor());
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
        var context = new VtagePredictor();
        var computational = new StridePredictor();
        var p = new DynamicClassificationValuePredictor(context, computational);
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
        var p = new DynamicClassificationValuePredictor(new VtagePredictor(), new StridePredictor());
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

        // Break the stride: Stride's own FSM drops Steady -> Init on a single mismatch.
        p.Update(pc, default(ValueHistoryCheckpoint), 999);

        // The next Predict finds the assigned (Computational) component no longer confident,
        // and this PC was armed -- evicted. Eviction from Computational (not the FCM/general
        // role) returns it to Unclassified rather than permanent Don't Predict.
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));

        // Relearn against a brand new pattern: non-constant deltas this time -> classifies to
        // Context instead, proving eviction genuinely reset classification rather than just
        // suppressing Computational's own (now-broken) prediction.
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
        var p = new DynamicClassificationValuePredictor(new VtagePredictor(), new StridePredictor());
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
        // behavior as VtageValuePredictionTests/LvpValuePredictionTests' own mispredict tests).
        p.Update(pc, default(ValueHistoryCheckpoint), 999);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));

        // Context is the paper's FCM/general-catch-all role: eviction from it is permanent, so
        // even feeding a brand new, cleanly-classifiable pattern never predicts again.
        for (var i = 0; i < 20; i++) p.Update(pc, default(ValueHistoryCheckpoint), steady);
        Assert.False(p.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }
}