#region

using Mechanism;
using Mechanism.BranchPredictModels;

#endregion

namespace Tests.Mechanism;

public class LvcpBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new LvcpPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void NoLoadActivity_BehavesLikeTageScL() {
        // With no NotifyRegisterResult(isLoad: true) calls, the LTQ is always empty, so
        // TryLvcpPredict never finds a candidate and ResolvePrediction falls through to
        // the TAGE-SC-L baseline unchanged, regardless of H2P classification.
        var tsl = new TageScLPredictor();
        var lvcp = new LvcpPredictor();
        ulong branch = 0x3000;

        for (var i = 0; i < 200; i++) {
            bool taken = i % 7 < 4; // arbitrary periodic pattern, learnable by TAGE alone
            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pl = lvcp.Predict(branch);
            Assert.Equal(pt.PredictedTaken, pl.PredictedTaken);
            tsl.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
            lvcp.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
        }

        Assert.Equal(0, lvcp.LvcpOverrides);
    }

    [Fact]
    public void LoadValueCorrelation_BeatsHistoryOnlyPredictorForPseudorandomPattern() {
        // A branch whose direction is a pseudorandom function of loop iteration (defeats
        // history-based TAGE-SC-L) but is perfectly signaled by a load value produced just
        // before the branch is fetched — exactly the correlation LVCP is designed to exploit.
        // First drive enough mispredicted iterations against the TAGE-SC-L baseline to push
        // the branch's H2P counter to saturation, then confirm the correlation table takes
        // over and substantially reduces misses.
        var tsl = new TageScLPredictor();
        var lvcp = new LvcpPredictor();
        ulong branch = 0x4000;
        ulong loadPc = branch - 4;
        const int destReg = 5;
        const ulong takenValue = 0xAAAAAAAAAAAAAAAAUL;
        const ulong notTakenValue = 0x5555555555555555UL;

        var rng = new Random(1234);
        int tslMisses = 0, lvcpMisses = 0;

        for (var i = 0; i < 600; i++) {
            bool taken = rng.Next(2) == 0;

            // Load producer executes before the branch is fetched.
            lvcp.NotifyRegisterResult(loadPc, destReg, taken ? takenValue : notTakenValue, true);

            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pl = lvcp.Predict(branch);
            if (pt.PredictedTaken != taken) tslMisses++;
            if (pl.PredictedTaken != taken) lvcpMisses++;

            ulong target = taken ? branch - 0x100 : branch + 4;
            tsl.Update(branch, taken, target);
            lvcp.Update(branch, taken, target);
        }

        Assert.True(
            lvcpMisses < tslMisses / 2,
            $"Expected LVCP to substantially beat history-only TAGE-SC-L on a load-value-correlated " +
            $"pseudorandom pattern (LVCP misses={lvcpMisses}, TAGE-SC-L misses={tslMisses})"
        );
        Assert.True(lvcp.LvcpOverrides > 0, "Expected LVCP to have overridden the baseline at least once");
    }

    [Fact]
    public void WrongDirectionHit_PermanentlyRetiresCorrelationEntry() {
        // Once a correlation-table entry is trained taken and then observes the opposite
        // outcome for the same (branch PC, load PC, load value) key, DirChanged retires it
        // permanently — it must never again participate in a prediction override, even
        // though the branch stays H2P and the same load value keeps recurring.
        var lvcp = new LvcpPredictor();
        ulong branch = 0x6000;
        ulong loadPc = branch - 4;
        const int destReg = 7;
        const ulong loadValue = 0xC0FFEEUL;

        // Saturate the H2P Branch Table by repeatedly mispredicting against TAGE-SC-L's
        // fall-through-only baseline: alternate taken/not-taken so the static baseline
        // (which settles on a single direction) keeps missing.
        for (var i = 0; i < 8; i++) {
            bool taken = i % 2 == 0;
            lvcp.Predict(branch);
            lvcp.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
        }

        // Train the correlation entry for (branch, loadPc, loadValue) as strongly "taken",
        // saturating its confidence counter.
        for (var i = 0; i < 40; i++) {
            lvcp.NotifyRegisterResult(loadPc, destReg, loadValue, true);
            lvcp.Predict(branch);
            lvcp.Update(branch, true, branch - 0x100);
        }

        int overridesAfterTraining = lvcp.LvcpOverrides;
        Assert.True(
            overridesAfterTraining > 0, "Expected the saturated correlation entry to have overridden at least once"
        );

        // Now observe the opposite outcome once for the same key — this must retire the
        // entry (DirChanged) rather than merely flip its stored direction.
        lvcp.NotifyRegisterResult(loadPc, destReg, loadValue, true);
        lvcp.Predict(branch);
        lvcp.Update(branch, false, branch + 4);

        int overridesBefore = lvcp.LvcpOverrides;
        for (var i = 0; i < 20; i++) {
            lvcp.NotifyRegisterResult(loadPc, destReg, loadValue, true);
            lvcp.Predict(branch);
            lvcp.Update(branch, true, branch - 0x100);
        }

        Assert.Equal(overridesBefore, lvcp.LvcpOverrides);
    }
}