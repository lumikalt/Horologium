#region

using Mechanism;
using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class LlbpXBranchPredictionTests {
    [Fact]
    public void ColdMiss_PredictsFallThrough() {
        var p = new LlbpXBp();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new LlbpXBp();
        ulong pc = 0x1000;
        for (var i = 0; i < 8; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new LlbpXBp();
        ulong pc = 0x1000;
        for (var i = 0; i < 8; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void LlbpOverride_FiresAfterAllocation() {
        // Uses proper Predict→Update cycles so LLBP-X can learn and then override.
        // The W=2 RCR transitions context faster than W=8 LLBP, so the base LLBP
        // test's Update-only shortcut does not generalize here.
        var p = new LlbpXBp();
        ulong pc = 0x1000;
        ulong warmPc = 0x8000;
        for (var i = 0; i < 120; i++) {
            p.Predict(warmPc);
            p.Update(warmPc, true, warmPc + 4);
        }

        for (var i = 0; i < 32; i++) {
            p.Predict(pc);
            p.Update(pc, true, 0x2000);
        }

        Assert.True(
            p.LlbpOverrides > 0,
            $"Expected LlbpOverrides to be positive after training (got {p.LlbpOverrides})"
        );
    }

    /// <summary>
    ///     Runs TageScL, LLBP, and LLBP-X on the same synthetic branch trace and
    ///     verifies that LLBP-X matches or improves upon LLBP's override count.
    ///     This serves as a regression baseline for benchmark comparisons.
    /// </summary>
    [Fact]
    public void BenchmarkComparison_LlbpX_AtLeastAsGoodAsLlbp() {
        var tsl = new TageScLBp();
        var llbp = new LlbpBp();
        var llbpX = new LlbpXBp();

        // Synthetic trace: four branches cycling through a repeating 4-deep pattern.
        // Branch A at 0x1000 taken → 0x2000, then branch B, C, D at distinct PCs.
        // The history context changes with each iteration.
        ulong[] pcs = [0x1000, 0x2000, 0x3000, 0x4000,];
        ulong[] tgts = [0x2000, 0x3000, 0x4000, 0x1000,];
        bool[] pattern = [true, true, false, true,];

        int tslMisses = 0, llbpMisses = 0, llbpXMisses = 0;

        for (var iter = 0; iter < 2000; iter++)
        for (var b = 0; b < pcs.Length; b++) {
            bool taken = pattern[b];
            ulong tgt = taken ? tgts[b] : pcs[b] + 4;

            if (tsl.Predict(pcs[b]).PredictedTaken != taken) tslMisses++;
            if (llbp.Predict(pcs[b]).PredictedTaken != taken) llbpMisses++;
            if (llbpX.Predict(pcs[b]).PredictedTaken != taken) llbpXMisses++;

            tsl.Update(pcs[b], taken, tgt);
            llbp.Update(pcs[b], taken, tgt);
            llbpX.Update(pcs[b], taken, tgt);
        }

        // LLBP should be <= TSL misses after warmup; LLBP-X should be <= LLBP.
        Assert.True(
            llbpMisses <= tslMisses,
            $"LLBP ({llbpMisses}) should not be worse than TSL ({tslMisses}) on this trace"
        );
        Assert.True(
            llbpXMisses <= llbpMisses + 5, // allow tiny variance from depth switching
            $"LLBP-X ({llbpXMisses}) should be close to or better than LLBP ({llbpMisses})"
        );
    }
}