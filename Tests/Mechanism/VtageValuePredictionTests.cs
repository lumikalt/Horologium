using Mechanism;
using Mechanism.ValuePredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="VtagePredictor" /> (Perais &amp; Seznec, HPCA 2014, VTAGE) in
///     isolation from pipeline complexity.
/// </summary>
public class VtageValuePredictionTests {
    [Fact]
    public void ColdMiss_NoPrediction() {
        var p = new VtagePredictor();
        Assert.False(p.TryPredict(0x1000, out _));
    }

    [Fact]
    public void RepeatedValue_ConvergesToConfidentPrediction() {
        var p = new VtagePredictor();
        const ulong pc = 0x1000;
        const ulong value = 123;
        for (var i = 0; i < 5000; i++) p.Update(pc, value);

        Assert.True(p.TryPredict(pc, out ulong predicted));
        Assert.Equal(value, predicted);
    }

    [Fact]
    public void Mispredict_ResetsConfidenceAndReplacesValue() {
        var p = new VtagePredictor();
        const ulong pc = 0x3000;
        for (var i = 0; i < 5000; i++) p.Update(pc, 10);
        Assert.True(p.TryPredict(pc, out ulong before));
        Assert.Equal(10UL, before);

        p.Update(pc, 99);
        Assert.False(p.TryPredict(pc, out _));

        for (var i = 0; i < 5000; i++) p.Update(pc, 99);
        Assert.True(p.TryPredict(pc, out ulong after));
        Assert.Equal(99UL, after);
    }

    [Fact]
    public void HistoryDependentIndex_AllowsRetrainingAfterHistoryShift() {
        // VTAGE indexes tagged components off (PC, global history), so a shift in committed
        // history changes which slot a PC's training lands in — it may or may not be the same
        // slot as before. Either way, the predictor must still converge cleanly to whatever
        // value is actually observed under the new history.
        var p = new VtagePredictor();
        const ulong pc = 0x4000;

        for (var i = 0; i < 5000; i++) {
            p.OnBranchFetched(true);
            p.AdvanceCommittedHistory(true);
            p.Update(pc, 111);
        }

        Assert.True(p.TryPredict(pc, out ulong first));
        Assert.Equal(111UL, first);

        for (var i = 0; i < 5000; i++) {
            p.OnBranchFetched(false);
            p.AdvanceCommittedHistory(false);
            p.Update(pc, 222);
        }

        Assert.True(p.TryPredict(pc, out ulong second));
        Assert.Equal(222UL, second);
    }

    /// <summary>
    ///     The risk this test targets: an execute-time partial squash must rewind VTAGE's
    ///     speculative history to <em>exactly</em> what it would have been had the wrong-path
    ///     branch never been fetched — not just to the last commit. A predictor that only
    ///     recovers to the committed shadow (ignoring the checkpoint) would silently corrupt its
    ///     index after every ordinary branch misprediction, with no crash and no failing
    ///     assertion anywhere else. Verified by comparing a predictor that took a wrong-path
    ///     detour and restored against one that walked the correct path directly — both must
    ///     end up in the same predicting state.
    /// </summary>
    [Fact]
    public void RestoreHistory_AfterWrongPathDetour_MatchesNeverDivergedPredictor() {
        var diverged = new VtagePredictor();
        var clean = new VtagePredictor();
        const ulong pc = 0x5000;

        // Common setup: both predictors walk an identical sequence of correctly-predicted
        // branches (speculative == committed throughout, as in an in-order pipeline) and train
        // several PCs so their tables are non-trivial and identical.
        bool[] commonPath = [true, false, true, true, false, false, true, false, true, false,];
        foreach (bool taken in commonPath) {
            diverged.OnBranchFetched(taken);
            diverged.AdvanceCommittedHistory(taken);
            clean.OnBranchFetched(taken);
            clean.AdvanceCommittedHistory(taken);
        }

        for (var i = 0; i < 5000; i++) {
            diverged.Update(pc, 77);
            clean.Update(pc, 77);
        }

        ValueHistoryCheckpoint checkpoint = diverged.CaptureHistory();

        // Wrong-path speculation on `diverged` only — never applied to `clean`, and never
        // advances the committed shadow (a real branch predictor wouldn't know it's wrong yet).
        diverged.OnBranchFetched(true);
        diverged.OnBranchFetched(false); // a second speculative branch down the wrong path

        // The redirecting branch actually resolved not-taken: restore to the checkpoint and
        // fold the true outcome, mirroring OooeTrain.StepPartialSquash.
        diverged.RestoreHistory(checkpoint, actualTaken: false);

        // `clean` takes the same real branch directly, with no detour.
        clean.OnBranchFetched(false);
        clean.AdvanceCommittedHistory(false);

        // Both predictors must now be indexing identically: same confident/unconfident
        // verdict and, when confident, the same value, for a range of PCs (the trained one
        // and several untrained ones, to also exercise cold-miss/base-fallback agreement).
        foreach (ulong testPc in new[] { pc, 0x5004UL, 0x6000UL, 0x7000UL, }) {
            bool divergedHit = diverged.TryPredict(testPc, out ulong divergedValue);
            bool cleanHit = clean.TryPredict(testPc, out ulong cleanValue);
            Assert.Equal(cleanHit, divergedHit);
            if (cleanHit) Assert.Equal(cleanValue, divergedValue);
        }
    }

    [Fact]
    public void RecoverSpeculativeHistory_OnFullFlush_RestoresCommittedShadow() {
        var p = new VtagePredictor();
        const ulong pc = 0x8000;

        for (var i = 0; i < 5000; i++) {
            p.OnBranchFetched(false);
            p.AdvanceCommittedHistory(false);
            p.Update(pc, 55);
        }

        Assert.True(p.TryPredict(pc, out ulong predicted));
        Assert.Equal(55UL, predicted);

        // Speculate down a wrong path, then fully recover (a real StepFlush call).
        p.OnBranchFetched(true);
        p.OnBranchFetched(true);
        p.RecoverSpeculativeHistory();

        // History is back to the all-not-taken committed shadow, so the same PC still predicts.
        Assert.True(p.TryPredict(pc, out ulong afterRecover));
        Assert.Equal(55UL, afterRecover);
    }
}
