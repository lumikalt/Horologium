#region

using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     SMARTS systematic sampling (Wunderlich, Wenisch, Falsafi &amp; Hoe, "SMARTS: Accelerating
///     Microarchitecture Simulation via Rigorous Statistical Sampling", ISCA 2003) end-to-end
///     against <see cref="FiveStageTrain" />, using the shared workload in
///     <see cref="SmartsTestWorkload" />, sampled with <see cref="SmartsDriver" /> and compared
///     against a single full-length detailed run.
///     <para>
///         The workload's accumulator (<c>lw</c>/<c>addi</c>/<c>sw</c> against a fixed address,
///         incremented once per loop iteration) is the handoff-fidelity oracle: its final value in
///         the shared backing memory must match an independent, un-sampled functional reference for
///         the same total instruction count (see <see cref="SmartsTestWorkload.ExpectedTotalInstructions" />),
///         regardless of how many live functional/detailed train switches happened in between. A
///         dropped register in <see cref="ArchStateTransfer" />, a lost or duplicated memory write
///         across a switch, or an off-by-one in the fast-forward/warmup instruction counts would
///         all corrupt this value — unlike a pure-CPI comparison, which a uniform-cost loop can
///         satisfy even with a broken handoff (an earlier, weaker version of this test used exactly
///         that and would not have caught such a bug).
///     </para>
///     <para>
///         Also demonstrates the paper's core empirical claim (Section 3.1/4.5): sampling with a
///         branch predictor warmed continuously (shared, trained by both the functional
///         fast-forward and every detailed window) tracks the true CPI closely, while sampling
///         with no predictor warming at all (a fresh, always-not-taken predictor cold-started for
///         every detailed window) is measurably biased upward by the resulting misprediction-flush
///         penalty paid every iteration. The cache config is identical (and shared) in both
///         scenarios — this ablation isolates the branch-predictor's contribution to
///         stale-microarchitectural-state bias specifically, not cache warming.
///     </para>
/// </summary>
public class SmartsDriverTests {
    private static SmartsParameters Parameters() => new(U: 60, W: 12, K: 600, J: 60, N: 30);

    // The oracle for "did the handoff preserve state" is a continuous FiveStageTrain run of the
    // same total instruction count, not a functional SingleCycleTrain reference — see
    // SmartsTestWorkload.RunContinuousDetailedAccumulator's doc comment for why. Takes the run's
    // own SmartsResult.FinalPosition (not the nominal J+(N-1)*K+U formula) since that is the
    // exact total actually reached — always identical to the formula for a train with no
    // cross-instruction short-term state like FiveStageTrain, but this stays correct even if that
    // ever changes (see SmartsDriverOooTests, where it does not hold).
    private static int ExpectedAccumulator(long totalInstructions) =>
        SmartsTestWorkload.RunContinuousDetailedAccumulator(
            totalInstructions,
            (mechanism, mem, counter) => new FiveStageTrain(
                mechanism, mem, dMemConfig: SmartsTestWorkload.DCache(), commitObserver: counter
            )
        );

    // Single continuous detailed run of the whole program — the ground truth SMARTS estimates
    // are compared against. Uses the same (continuously trained, never reset) predictor
    // instance for its entire lifetime, same as the "warm" SMARTS scenario below.
    private static (double Cpi, FlatMemory Memory) RunFullDetailed() {
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        var counter = new InstructionCounter();
        var train = new FiveStageTrain(
            mechanism, mem, predictor: new NBitBp(), dMemConfig: SmartsTestWorkload.DCache(), commitObserver: counter
        );
        RevolutionResult result = train.Run();
        return (result.TotalTicks / (double)counter.Count, mem);
    }

    // sharedPredictor null models "no functional warming at all": SingleCycleCore never trains
    // any predictor during fast-forward, and FiveStageTrain's own null-predictor default
    // (a fresh AlwaysNotTakenPredictor) means every one of the N detailed windows starts cold
    // with no memory of prior branch history.
    private static (SmartsResult Result, FlatMemory Memory) RunSmarts(
        IBranchPredictor? sharedPredictor,
        Action<int, long, IArchState>? onUnitEntry = null
    ) {
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        MemoryLayers iLayers = MemoryLayers.Build(mem, MemoryConfig.None);
        MemoryLayers dLayers = MemoryLayers.Build(mem, SmartsTestWorkload.DCache());

        SmartsResult result = SmartsDriver.Run(
            mechanism, 0, iLayers, dLayers, sharedPredictor, SmartsDriverTests.Parameters(), SmartsDriver.FiveStage(),
            onUnitEntry
        );
        return (result, mem);
    }

    [Fact]
    public void WarmPredictor_TracksTrueCpi() {
        (double trueCpi, _) = RunFullDetailed();
        (SmartsResult warm, FlatMemory mem) = RunSmarts(new NBitBp());

        Assert.False(warm.Halted);
        Assert.Equal(30, warm.Units.Count);

        Assert.Equal(
            ExpectedAccumulator(warm.FinalPosition), (int)mem.Read(SmartsTestWorkload.AccumulatorAddress, 4)
        );

        double bias = Math.Abs(warm.MeanCpi - trueCpi) / trueCpi;
        Assert.True(bias < 0.05, $"warm SMARTS CPI {warm.MeanCpi:F4} vs true {trueCpi:F4} (bias {bias:P1})");
    }

    [Fact]
    public void ColdPredictor_IsMoreBiasedThanWarm() {
        (double trueCpi, _) = RunFullDetailed();
        (SmartsResult warm, _) = RunSmarts(new NBitBp());
        (SmartsResult cold, FlatMemory coldMem) = RunSmarts(null);

        // The accumulator must still be exactly right even under the "cold" (no-warming)
        // scenario — this ablation only changes predictor participation, never correctness.
        Assert.Equal(
            ExpectedAccumulator(cold.FinalPosition), (int)coldMem.Read(SmartsTestWorkload.AccumulatorAddress, 4)
        );

        double warmBias = Math.Abs(warm.MeanCpi - trueCpi);
        double coldBias = Math.Abs(cold.MeanCpi - trueCpi);

        Assert.True(
            coldBias > warmBias * 2,
            $"cold bias {coldBias:F4} should be well above warm bias {warmBias:F4} " +
            $"(true={trueCpi:F4}, warm={warm.MeanCpi:F4}, cold={cold.MeanCpi:F4})"
        );
    }

    // The decisive handoff-fidelity check: at each unit's entry position, the exact architectural
    // state SmartsDriver hands to the detailed train must match a plain functional trace's state
    // at that same absolute instruction count — independent of any timing model at all.
    [Fact]
    public void UnitEntryState_MatchesReferenceFunctionalTrace() {
        var capturedPositions = new List<long>();
        var captured = new Dictionary<long, (ulong Pc, ulong[] Regs)>();

        (SmartsResult result, _) = RunSmarts(
            new NBitBp(),
            (_, position, state) => {
                capturedPositions.Add(position);
                captured[position] = (state.Pc, [..Enumerable.Range(0, 32).Select(r => state.IntegerRegisters.Read(r)),]);
            }
        );

        Assert.False(result.Halted);
        Assert.NotEmpty(capturedPositions);

        // Reference: one plain functional pass, snapshotting the same register/PC state at the
        // same absolute instruction counts, entirely independent of SmartsDriver's machinery.
        FlatMemory refMem = SmartsTestWorkload.BuildProgram();
        var refMechanism = new Rv32Mechanism();
        var reference = new Dictionary<long, (ulong Pc, ulong[] Regs)>();
        SingleCycleTrain? trainRef = null;
        var counter = new InstructionCounter(
            capturedPositions,
            idx => reference[capturedPositions[idx]] = (
                trainRef!.ArchState.Pc,
                [..Enumerable.Range(0, 32).Select(r => trainRef!.ArchState.IntegerRegisters.Read(r)),]
            )
        );
        trainRef = new SingleCycleTrain(refMechanism, refMem, commitObserver: counter);
        trainRef.Run();

        foreach (long position in capturedPositions) {
            (ulong Pc, ulong[] Regs) expected = reference[position];
            (ulong Pc, ulong[] Regs) actual = captured[position];
            Assert.True(
                expected.Pc == actual.Pc, $"position {position}: expected Pc=0x{expected.Pc:X}, got 0x{actual.Pc:X}"
            );
            Assert.True(
                expected.Regs.SequenceEqual(actual.Regs),
                $"position {position}: register mismatch — expected [{string.Join(",", expected.Regs)}], " +
                $"got [{string.Join(",", actual.Regs)}]"
            );
        }
    }
}
