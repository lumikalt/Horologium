using Mechanism.ValuePredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="LvpPredictor" /> (Lipasti &amp; Shen, MICRO 1996, LVPT) in
///     isolation from pipeline complexity.
/// </summary>
public class LvpValuePredictionTests {
    [Fact]
    public void ColdMiss_NoPrediction() {
        var p = new LvpPredictor();
        Assert.False(p.TryPredict(0x1000, out _));
    }

    [Fact]
    public void RepeatedValue_ConvergesToConfidentPrediction() {
        var p = new LvpPredictor();
        const ulong pc = 0x1000;
        const ulong value = 42;
        for (var i = 0; i < 5000; i++) p.Update(pc, value);

        Assert.True(p.TryPredict(pc, out ulong predicted));
        Assert.Equal(value, predicted);
    }

    [Fact]
    public void FirstEncounter_SeedsValueButDoesNotPredict() {
        var p = new LvpPredictor();
        p.Update(0x2000, 7);
        // A single training pass only seeds the value history; confidence has not
        // yet been demonstrated by a repeated correct prediction.
        Assert.False(p.TryPredict(0x2000, out _));
    }

    [Fact]
    public void Mispredict_ResetsConfidenceAndReplacesValue() {
        var p = new LvpPredictor();
        const ulong pc = 0x3000;
        for (var i = 0; i < 5000; i++) p.Update(pc, 10);
        Assert.True(p.TryPredict(pc, out ulong before));
        Assert.Equal(10UL, before);

        // A single differing value hard-resets confidence (FPC never gradually decays).
        p.Update(pc, 99);
        Assert.False(p.TryPredict(pc, out _));

        // Re-converges on the new value with enough repeated training.
        for (var i = 0; i < 5000; i++) p.Update(pc, 99);
        Assert.True(p.TryPredict(pc, out ulong after));
        Assert.Equal(99UL, after);
    }

    [Fact]
    public void Tagless_DistinctPcsAliasingToSameSlot_ShareState() {
        // A 1-entry table forces every PC to the same slot — the "tagless" design
        // accepts this as an ordinary misprediction source, never a correctness issue.
        var p = new LvpPredictor(1);
        for (var i = 0; i < 5000; i++) p.Update(0x1000, 5);
        Assert.True(p.TryPredict(0x1000, out ulong v1));
        Assert.Equal(5UL, v1);

        // A different PC that hashes to the same (only) slot aliases and clobbers it.
        p.Update(0x2000, 6);
        Assert.False(p.TryPredict(0x2000, out _));
    }
}