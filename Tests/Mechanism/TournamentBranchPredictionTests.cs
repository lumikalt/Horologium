using System.Text.Json;
using Mechanism;
using Mechanism.BranchPredictModels;
using RiscV.Config;

namespace Tests.Mechanism;

public class TournamentBranchPredictionTests {
    // ── Cold state ────────────────────────────────────────────────────────────

    [Fact]
    public void Cold_LocalPhtWeaklyNotTaken_ChooserWeaklyLocal_PredictsNotTaken() {
        // Cold: local PHT = 3 (weakly not-taken, 3-bit: taken ≥ 4).
        // Chooser = 1 → local. Local not-taken → predict not-taken.
        var p = new TournamentPredictor();
        Assert.False(p.Predict(0x1000).PredictedTaken);
    }

    // ── Always-taken convergence ──────────────────────────────────────────────

    [Fact]
    public void AlwaysTaken_ConvergesAfterTraining() {
        var p = new TournamentPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, true, 0x2000);
        BranchPrediction pred = p.Predict(pc);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(0x2000UL, pred.PredictedTarget);
    }

    // ── Always-not-taken convergence ─────────────────────────────────────────

    [Fact]
    public void AlwaysNotTaken_ConvergesAfterTraining() {
        var p = new TournamentPredictor();
        ulong pc = 0x1000;
        for (var i = 0; i < 60; i++) p.Update(pc, false, pc + 4);
        Assert.False(p.Predict(pc).PredictedTaken);
    }

    // ── Local predictor benefits from per-branch history ─────────────────────

    [Fact]
    public void AlternatingPattern_ConvergesViaLocalPredictor() {
        // T N T N … repeating: local predictor can learn this via per-branch BHT.
        var p = new TournamentPredictor(4, 256, 4);
        ulong pc = 0x400;
        bool[] pattern = [true, false, true, false, true, false, true, false,];

        for (var cycle = 0; cycle < 60; cycle++)
            foreach (bool t in pattern)
                p.Update(pc, t, 0x800);

        // After last false in pattern, next should be true.
        Assert.True(p.Predict(pc).PredictedTaken);
    }

    // ── Two branches don't corrupt each other ────────────────────────────────

    [Fact]
    public void TwoBranches_LocalPredictor_ConvergesIndependently() {
        // Interleaved training: pcA always taken, pcB always not-taken.
        // pcA and pcB map to different BHT entries so their local PHT patterns diverge.
        // With enough rounds the local predictor saturates each branch independently.
        var p = new TournamentPredictor();
        ulong pcA = 0x0000, pcB = 0x0004; // BHT indices 0 and 1 — no aliasing
        for (var i = 0; i < 80; i++) {
            p.Update(pcA, true, 0x200);
            p.Update(pcB, false, pcB + 4);
        }

        Assert.True(p.Predict(pcA).PredictedTaken);
        Assert.False(p.Predict(pcB).PredictedTaken);
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Config_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.Tournament(8, 512, 10);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<TournamentConfig>(result);
        Assert.Equal(8, typed.LocalHistoryBits);
        Assert.Equal(512, typed.LocalTableSize);
        Assert.Equal(10, typed.GlobalHistoryBits);
    }
}