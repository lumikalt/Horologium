using Mechanism;
using Mechanism.ValuePredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="HybridValuePredictor" />'s combination rule (Perais &amp; Seznec,
///     HPCA 2014 §7.1.2), using two <see cref="StridePredictor" /> stand-ins (rather than a real
///     context-based/computational pair) so each side's confidence can be driven directly and
///     independently by PC choice, isolating the combination logic from either component's own
///     convergence behavior (already covered by <see cref="StrideValuePredictionTests" /> and
///     <see cref="VtageValuePredictionTests" />).
///     <para>
///         <see cref="StridePredictor.TryPredict" /> is not side-effect-free — it advances its own
///         speculative chain on every confident call (see that class's doc comment). So these tests
///         never "peek" at a component that's wired into the <see cref="HybridValuePredictor" />
///         under test via a second, separate <c>TryPredict</c> call: doing so would consume a step of
///         that component's speculative chain before the real assertion, silently changing the value
///         the next call returns. Expected values are instead read from freshly-trained, untouched
///         oracle instances.
///     </para>
/// </summary>
public class HybridValuePredictionTests {
    private static void TrainToSteady(StridePredictor p, ulong pc, ulong start, long stride) {
        ulong v = start;
        p.Update(pc, default(ValueHistoryCheckpoint), v);
        v = unchecked(v + (ulong)stride);
        p.Update(pc, default(ValueHistoryCheckpoint), v); // primes the stride (likely a mismatch against the seed's 0)
        v = unchecked(v + (ulong)stride);
        p.Update(pc, default(ValueHistoryCheckpoint), v); // 1st confirmation: Init -> Transient
        v = unchecked(v + (ulong)stride);
        p.Update(pc, default(ValueHistoryCheckpoint), v); // 2nd confirmation: Transient -> Steady
    }

    [Fact]
    public void NeitherComponentConfident_NoPrediction() {
        var hybrid = new HybridValuePredictor(new StridePredictor(), new StridePredictor());
        Assert.False(hybrid.TryPredict(0x1000, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void OnlyContextConfident_UsesContextValue() {
        var context = new StridePredictor();
        var computational = new StridePredictor();
        var hybrid = new HybridValuePredictor(context, computational);
        const ulong pc = 0x1000;
        TrainToSteady(context, pc, 0, 5);

        var oracle = new StridePredictor();
        TrainToSteady(oracle, pc, 0, 5);
        Assert.True(oracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong expected));

        Assert.True(hybrid.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(expected, predicted);
        Assert.False(computational.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void OnlyComputationalConfident_UsesComputationalValue() {
        var context = new StridePredictor();
        var computational = new StridePredictor();
        var hybrid = new HybridValuePredictor(context, computational);
        const ulong pc = 0x2000;
        TrainToSteady(computational, pc, 100, 3);

        var oracle = new StridePredictor();
        TrainToSteady(oracle, pc, 100, 3);
        Assert.True(oracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong expected));

        Assert.True(hybrid.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(expected, predicted);
        Assert.False(context.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void BothConfidentAndAgree_PredictionProceeds() {
        var context = new StridePredictor();
        var computational = new StridePredictor();
        var hybrid = new HybridValuePredictor(context, computational);
        const ulong pc = 0x3000;

        // Same PC, same training on both components: they converge to the identical value.
        TrainToSteady(context, pc, 0, 4);
        TrainToSteady(computational, pc, 0, 4);

        var contextOracle = new StridePredictor();
        var computationalOracle = new StridePredictor();
        TrainToSteady(contextOracle, pc, 0, 4);
        TrainToSteady(computationalOracle, pc, 0, 4);
        Assert.True(contextOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong contextValue));
        Assert.True(computationalOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong computationalValue));
        Assert.Equal(contextValue, computationalValue); // sanity: test actually exercises agreement

        Assert.True(hybrid.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong predicted));
        Assert.Equal(contextValue, predicted);
    }

    [Fact]
    public void BothConfidentButDisagree_NoPrediction() {
        var context = new StridePredictor();
        var computational = new StridePredictor();
        var hybrid = new HybridValuePredictor(context, computational);
        const ulong pc = 0x4000;

        // Same PC, different strides trained into each component: both confident, disagreeing.
        TrainToSteady(context, pc, 0, 4);
        TrainToSteady(computational, pc, 0, 9);

        var contextOracle = new StridePredictor();
        var computationalOracle = new StridePredictor();
        TrainToSteady(contextOracle, pc, 0, 4);
        TrainToSteady(computationalOracle, pc, 0, 9);
        Assert.True(contextOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong contextValue));
        Assert.True(computationalOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong computationalValue));
        Assert.NotEqual(contextValue, computationalValue); // sanity: test actually exercises disagreement

        Assert.False(hybrid.TryPredict(pc, default(ValueHistoryCheckpoint), out _));
    }

    [Fact]
    public void Update_TrainsBothComponentsRegardlessOfWhichPredicted() {
        var context = new StridePredictor();
        var computational = new StridePredictor();
        var hybrid = new HybridValuePredictor(context, computational);
        const ulong pc = 0x5000;

        ulong v = 0;
        for (var i = 0; i < 4; i++) {
            hybrid.Update(pc, default(ValueHistoryCheckpoint), v);
            v += 6;
        }

        // Both components independently reached Steady from the same retire-time updates —
        // verified via untouched oracle instances trained identically.
        var contextOracle = new StridePredictor();
        var computationalOracle = new StridePredictor();
        TrainToSteady(contextOracle, pc, 0, 6);
        TrainToSteady(computationalOracle, pc, 0, 6);
        Assert.True(contextOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong contextValue));
        Assert.True(computationalOracle.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong computationalValue));
        Assert.Equal(v, contextValue);
        Assert.Equal(v, computationalValue);

        Assert.True(hybrid.TryPredict(pc, default(ValueHistoryCheckpoint), out ulong hybridValue));
        Assert.Equal(v, hybridValue);
    }
}