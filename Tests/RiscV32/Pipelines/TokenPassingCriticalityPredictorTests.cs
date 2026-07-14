using Mechanism;
using Pipeline.Ooo;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Unit tests for <see cref="TokenPassingCriticalityPredictor" /> (Fields, Rubin &amp; Bodík,
///     "Focusing Processor Policies via Critical-Path Prediction", ISCA 2001). Feeds synthetic
///     <see cref="CriticalityCommitInfo" /> sequences directly, bypassing the pipeline, to exercise
///     the token-propagation and hysteresis-training logic in isolation.
/// </summary>
public class TokenPassingCriticalityPredictorTests {
    private const int RobCapacity = 8;

    // propagationDistance = 500 + robCapacity (paper's formula); a training cycle (plant -> die
    // or plant -> survive -> train) never takes more than that plus the 0-9 replant jitter.
    private const ulong CycleUpperBound = 500 + RobCapacity + 10;

    // A single token (tokenCount=1) is guaranteed to plant on the very first commit ever fed to
    // a fresh predictor (initial _replantAt[0] == 0), which keeps these tests independent of the
    // predictor's internal RNG.
    private static TokenPassingCriticalityPredictor NewPredictor() =>
        new(RobCapacity, tokenCount: 1);

    private static CriticalityCommitInfo Commit(
        ulong instrId,
        ulong pc,
        CpNode eSourceNode = CpNode.D,
        ulong eSourceInstrId = 0
    ) =>
        new() {
            InstrId = instrId,
            Pc = pc,
            DSourceNode = CpNode.D,
            DSourceInstrId = instrId > 0 ? instrId - 1 : 0,
            ESourceNode = eSourceNode,
            ESourceInstrId = eSourceInstrId,
            CSourceNode = CpNode.C,
            CSourceInstrId = instrId > 0 ? instrId - 1 : 0,
        };

    /// <summary>
    ///     Feeds a continuous chain of <paramref name="totalCommits" /> commits, all at
    ///     <paramref name="pc" />, where each instruction's E-node last-arrives from the
    ///     immediately preceding instruction's E-node. Because every commit shares the same PC,
    ///     whichever commit the (possibly re-planted) token happens to land on is always trained
    ///     under <paramref name="pc" /> — independent of the predictor's replant-delay RNG. Because
    ///     the E-chain never breaks, every token planted during the run survives to
    ///     propagationDistance and trains critical.
    /// </summary>
    private static void RunCriticalChain(TokenPassingCriticalityPredictor p, ulong pc, ulong totalCommits) {
        for (ulong id = 0; id < totalCommits; id++)
            p.OnCommit(id == 0 ? Commit(id, pc) : Commit(id, pc, CpNode.E, id - 1));
    }

    /// <summary>
    ///     Feeds a continuous chain of <paramref name="totalCommits" /> commits, all at
    ///     <paramref name="pc" />, where no instruction's E-node ever references another's — so
    ///     any token planted here sits untouched until its ROB slot is recycled (within
    ///     <see cref="RobCapacity" /> commits) and then dies, training non-critical.
    /// </summary>
    private static void RunDeadChain(TokenPassingCriticalityPredictor p, ulong pc, ulong totalCommits) {
        for (ulong id = 0; id < totalCommits; id++)
            p.OnCommit(Commit(id, pc));
    }

    [Fact]
    public void ColdPc_PredictsNotCritical() {
        var p = NewPredictor();
        Assert.False(p.PredictCritical(0x1000));
    }

    [Fact]
    public void TokenChain_SurvivesToPropagationDistance_TrainsSeedCritical() {
        var p = NewPredictor();
        ulong seedPc = 0x1000;

        // Long enough for at least two full plant -> survive -> train cycles: a single +8 from a
        // cold hysteresis of 0 lands exactly at 8, which does not clear the ">8" threshold.
        RunCriticalChain(p, seedPc, CycleUpperBound * 3);

        Assert.True(p.PredictCritical(seedPc));
    }

    [Fact]
    public void TokenChain_DiesImmediately_TrainsSeedNonCritical() {
        var p = NewPredictor();
        ulong seedPc = 0x1000;

        RunDeadChain(p, seedPc, CycleUpperBound);

        // Hysteresis starts at the floor (0) and non-critical training decrements via
        // Math.Max(0, x - 1), so it remains 0 — never crosses the >8 threshold.
        Assert.False(p.PredictCritical(seedPc));
    }

    [Fact]
    public void Hysteresis_SaturatesAtSixtyThree() {
        var p = NewPredictor();
        ulong seedPc = 0x1000;

        // Comfortably more than 63/8 = ~8 training cycles' worth of commits.
        RunCriticalChain(p, seedPc, CycleUpperBound * 12);

        Assert.True(p.PredictCritical(seedPc));
    }

    [Fact]
    public void Hysteresis_FloorsAtZero_NeverGoesNegative() {
        var p = NewPredictor();
        ulong seedPc = 0x1000;

        // Several dead-chain cycles back to back must not underflow the hysteresis counter.
        RunDeadChain(p, seedPc, CycleUpperBound * 5);

        Assert.False(p.PredictCritical(seedPc));
    }

    [Fact]
    public void DifferentPcs_TrainIndependently() {
        var p = NewPredictor();
        ulong criticalPc = 0x1000;
        ulong nonCriticalPc = 0x4000;

        RunCriticalChain(p, criticalPc, CycleUpperBound * 3);
        RunDeadChain(p, nonCriticalPc, CycleUpperBound * 3);

        Assert.True(p.PredictCritical(criticalPc));
        Assert.False(p.PredictCritical(nonCriticalPc));
    }
}
