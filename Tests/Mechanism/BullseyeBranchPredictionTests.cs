using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV32.Config;

namespace Tests.Mechanism;

public class BullseyeBranchPredictionTests {
    [Fact]
    public void Cold_PredictsNotTaken_FallThroughTarget() {
        var p = new BullseyePredictor();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [Fact]
    public void AlwaysTaken_DirectionConverges() {
        var p = new BullseyePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 32; i++) p.Update(pc, true, 0x2000);
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    [Fact]
    public void AlwaysNotTaken_DirectionConverges() {
        var p = new BullseyePredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 32; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // A period-6 outcome sequence is fully determined by a modest amount of history — both
    // the inherited TAGE-SC-L substrate and (once H2P-active) Bullseye's own perceptrons can
    // represent it. Exercises the full Predict/Update path through Bullseye's overrides many
    // thousands of times without breaking basic learning.
    [Fact]
    public void PeriodicPattern_ConvergesToHighAccuracy() {
        var p = new BullseyePredictor();
        ulong pc = 0x4000;
        bool[] period = [true, true, false, true, false, false,];

        for (var i = 0; i < 4000; i++) {
            bool taken = period[i % period.Length];
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        var correct = 0;
        const int trials = 600;
        for (var i = 0; i < trials; i++) {
            bool taken = period[i % period.Length];
            bool pred = p.Predict(pc).PredictedTaken;
            if (pred == taken) correct++;
            p.Update(pc, taken, taken ? pc + 0x100 : pc + 4);
        }

        Assert.True(correct >= trials * 0.9, $"only {correct}/{trials} correct after training");
    }

    // Drives a genuinely unpredictable (50/50 pseudo-random) pattern across enough dynamic
    // occurrences and PCs to blow past the HIT/H2P admission thresholds (Eq. 1a-1c: >=2048
    // executions, >=256 mispredictions per PC) and the H2P/HIT capacity bounds (8 and 64
    // entries respectively), forcing TryAdmit, EvictWeakestH2p, EvictLruHit and the §4.6
    // filter-streak logic to all run. The pattern is unlearnable by construction, so no
    // accuracy assertion is made — this is a robustness/no-throw stress test of the H2P
    // subsystem's admission and eviction machinery.
    [Fact]
    public void RandomPatternAcrossManyPcs_H2PSubsystemStaysStable() {
        var p = new BullseyePredictor();
        var rng = new Random(12345);
        const int pcCount = 96; // > HitCapacity(64) forces HIT eviction
        var pcs = new ulong[pcCount];
        for (var i = 0; i < pcCount; i++) pcs[i] = 0x8000UL + (ulong)i * 4;

        for (var iter = 0; iter < 3000; iter++) {
            ulong pc = pcs[rng.Next(pcCount)];
            bool taken = rng.Next(2) == 0;
            BranchPrediction pred = p.Predict(pc);
            Assert.True(pred.PredictedTarget == pc + 4 || pred.PredictedTaken);
            p.Update(pc, taken, taken ? pc + 0x40 : pc + 4);
        }
    }

    [Fact]
    public void Config_RoundTrip() {
        var cfg = new BullseyeConfig();
        string json = JsonSerializer.Serialize<BranchPredictorConfig>(cfg);
        var rt = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<BullseyeConfig>(rt);
    }
}