using Mechanism;
using Mechanism.BranchPredictModels;

namespace Tests.Mechanism;

public class RunltsBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new RunltsPredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void NoRegisterActivity_BehavesLikeTageScL() {
        // With no NotifyRegisterResult calls, sR never has a fresh digest to score, so
        // ResolvePrediction should fall through to the TAGE-SC-L baseline unchanged.
        var tsl = new TageScLPredictor();
        var runlts = new RunltsPredictor();
        ulong branch = 0x3000;

        for (var i = 0; i < 200; i++) {
            bool taken = i % 7 < 4; // arbitrary periodic pattern, learnable by TAGE alone
            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pr = runlts.Predict(branch);
            Assert.Equal(pt.PredictedTaken, pr.PredictedTaken);
            tsl.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
            runlts.Update(branch, taken, taken ? branch - 0x100 : branch + 4);
        }

        Assert.Equal(0, runlts.SrOverrides);
    }

    [Fact]
    public void RegisterCorrelation_BeatsHistoryOnlyPredictorForPseudorandomPattern() {
        // A branch whose direction is a pseudorandom function of loop iteration (defeats
        // history-based TAGE-SC-L) but is perfectly signaled by a register value produced
        // just before the branch is fetched (as if by an earlier instruction in the loop
        // body) — exactly the correlation sR is designed to exploit.
        var tsl = new TageScLPredictor();
        var runlts = new RunltsPredictor();
        ulong branch = 0x4000;
        const int destReg = 5;
        const ulong takenValue = 0xAAAAAAAAAAAAAAAAUL;
        const ulong notTakenValue = 0x5555555555555555UL;

        var rng = new Random(1234);
        int tslMisses = 0, runltsMisses = 0;

        for (var i = 0; i < 400; i++) {
            bool taken = rng.Next(2) == 0;

            // Register producer executes before the branch is fetched.
            runlts.NotifyRegisterResult(branch - 4, destReg, taken ? takenValue : notTakenValue, false);

            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pr = runlts.Predict(branch);
            if (pt.PredictedTaken != taken) tslMisses++;
            if (pr.PredictedTaken != taken) runltsMisses++;

            ulong target = taken ? branch - 0x100 : branch + 4;
            tsl.Update(branch, taken, target);
            runlts.Update(branch, taken, target);
        }

        Assert.True(
            runltsMisses < tslMisses / 2,
            $"Expected sR to substantially beat history-only TAGE-SC-L on a register-correlated " +
            $"pseudorandom pattern (RUNLTS misses={runltsMisses}, TAGE-SC-L misses={tslMisses})"
        );
        Assert.True(runlts.SrOverrides > 0, "Expected sR to have overridden the baseline at least once");
    }

    [Fact]
    public void StaleDigest_DoesNotParticipate() {
        // A digest older than the 256-notification staleness window must not be used —
        // flood the freshness table with unrelated writes to age the tracked register out,
        // then confirm the predictor no longer diverges from TAGE-SC-L for that branch.
        var tsl = new TageScLPredictor();
        var runlts = new RunltsPredictor();
        ulong branch = 0x5000;
        const int destReg = 3;

        // Prime a correlated digest once (keep the TAGE-SC-L baseline instance in lock-step
        // so later comparisons isolate sR's contribution, not baseline training drift).
        runlts.NotifyRegisterResult(branch - 4, destReg, 0xDEADBEEFUL, false);
        tsl.Predict(branch);
        runlts.Predict(branch);
        tsl.Update(branch, true, branch - 0x100);
        runlts.Update(branch, true, branch - 0x100);

        // Age it out by advancing the notification clock past the staleness window
        // without touching any tracked register slot (destReg out of range is ignored).
        for (var i = 0; i < 300; i++) runlts.NotifyRegisterResult(0, 999, (ulong)i, false);

        int overridesBefore = runlts.SrOverrides;
        for (var i = 0; i < 50; i++) {
            bool taken = i % 3 == 0;
            BranchPrediction pt = tsl.Predict(branch);
            BranchPrediction pr = runlts.Predict(branch);
            Assert.Equal(pt.PredictedTaken, pr.PredictedTaken);
            ulong target = taken ? branch - 0x100 : branch + 4;
            tsl.Update(branch, taken, target);
            runlts.Update(branch, taken, target);
        }

        Assert.Equal(overridesBefore, runlts.SrOverrides);
    }
}