#region

using Mechanism;
using Orrery.Cache;
using Orrery.Train;

#endregion

namespace Pipeline;

/// <summary>
///     Systematic-sampling parameters for one SMARTS measurement run (Wunderlich, Wenisch,
///     Falsafi &amp; Hoe, "SMARTS: Accelerating Microarchitecture Simulation via Rigorous
///     Statistical Sampling", ISCA 2003, Table 2 / Section 5.1).
/// </summary>
/// <param name="U">Sampling unit size: instructions measured per unit (paper default: 1000).</param>
/// <param name="W">
///     Detailed-warming instructions run (unmeasured) immediately before each unit's measured
///     window, to rebuild pipeline-internal state a functional pass cannot warm (paper: 2000 for
///     the 8-way config; Section 4.4's worst-case bound is store-buffer depth × memory latency ×
///     max IPC — zero for a detailed train with no store buffer and no other cross-instruction
///     short-term state).
/// </param>
/// <param name="K">
///     Systematic sampling interval in instructions (population size / n) — consecutive units'
///     measured windows start <c>K</c> instructions apart.
/// </param>
/// <param name="J">Starting offset of the first unit's measured window.</param>
/// <param name="N">Number of sampling units to measure.</param>
public sealed record SmartsParameters(long U, long W, long K, long J, int N);

/// <summary>One systematic sampling unit's measured cycles/instructions.</summary>
public sealed record SmartsUnitResult(int Index, long Instructions, long Cycles) {
    /// <summary>Cycles per instruction over this unit's measured window.</summary>
    public double Cpi => Instructions > 0 ? Cycles / (double)Instructions : double.NaN;
}

/// <summary>
///     Whole-run SMARTS estimate: one <see cref="SmartsUnitResult" /> per measured sampling unit,
///     plus the derived whole-program CPI estimate, its coefficient of variation, and
///     confidence-interval helpers (<see cref="SmartsStatistics" />).
/// </summary>
/// <param name="Units">
///     Measured units, in sampling order. May number fewer than the requested
///     <see cref="SmartsParameters.N" /> if the workload halted first (see <see cref="Halted" />).
/// </param>
/// <param name="Halted">True if the workload halted before every requested unit was measured.</param>
/// <param name="FinalPosition">
///     The absolute instruction count actually reached by the end of the run. Matches
///     <c>J + (N-1)*K + U</c> only when nothing drained extra in-flight instructions on the way
///     (always true for a train with no cross-instruction short-term state); an OoO detailed
///     train's final <see cref="OooTrain.Drain" /> call can retire a few more instructions than
///     the nominal window, which are real, already-committed progress and are reflected here.
/// </param>
public sealed record SmartsResult(IReadOnlyList<SmartsUnitResult> Units, bool Halted, long FinalPosition) {
    private IReadOnlyList<double> Cpis { get; } = [..Units.Select(u => u.Cpi),];

    /// <summary>Sample mean CPI across measured units (paper: x̄).</summary>
    public double MeanCpi => SmartsStatistics.Mean(Cpis);

    /// <summary>Coefficient of variation of the per-unit CPI sample (paper: V̂_CPI).</summary>
    public double CoefficientOfVariation => SmartsStatistics.CoefficientOfVariation(Cpis);

    /// <summary>The relative confidence interval ±ε this sample achieves at confidence level <paramref name="z" />.</summary>
    public double ConfidenceInterval(double z) =>
        SmartsStatistics.ConfidenceInterval(CoefficientOfVariation, Units.Count, z);

    /// <summary>
    ///     The sample size needed to achieve confidence interval ±<paramref name="epsilon" /> at
    ///     confidence level <paramref name="z" />, given this run's measured coefficient of variation.
    /// </summary>
    public int RequiredSampleSize(double z, double epsilon) =>
        SmartsStatistics.RequiredSampleSize(CoefficientOfVariation, z, epsilon);
}

/// <summary>
///     Builds the detailed pipeline train for one SMARTS sampling unit, sharing
///     <paramref name="iLayers" />/<paramref name="dLayers" /> (so cache/TLB state carries over
///     from the functional-warming pass) and <paramref name="predictor" /> (so branch-predictor
///     state does too). <paramref name="entryPoint" /> is the exact instruction the train must
///     resume at — the factory must pass it straight through as the train's own entry-point
///     constructor argument, since a Train's <c>Wind()</c> seeds <c>Pc</c> from that argument
///     unconditionally at <c>BeginStepping()</c>, regardless of any state
///     <see cref="SmartsDriver" /> copies in beforehand. <paramref name="counter" /> must be wired
///     up as the train's commit observer.
/// </summary>
public delegate ISteppableTrain SmartsDetailedTrainFactory(
    IMechanism mechanism,
    MemoryLayers iLayers,
    MemoryLayers dLayers,
    IBranchPredictor? predictor,
    ulong entryPoint,
    InstructionCounter counter
);

/// <summary>
///     Drives SMARTS systematic sampling (Wunderlich et al., ISCA 2003, Section 3.1): alternates a
///     functional fast-forward train with a detailed warm-then-measure window per sampling unit.
///     <para>
///         Architectural state is live-switched between the two trains at every boundary
///         (<see cref="ArchStateTransfer" />) rather than serialized through a full
///         <see cref="ArchitecturalCheckpoint" /> — the paper's own n (thousands of switches per
///         run) would make repeated full-memory snapshots prohibitive. Both trains instead share
///         the same <see cref="MemoryLayers" /> (cache/TLB) and <see cref="IBranchPredictor" />
///         instances throughout the run, so only register-level state needs to move.
///     </para>
///     <para>
///         Functional warming keeps cache/TLB/branch-predictor state continuously accurate through
///         the fast-forwarded majority of the stream: <see cref="SingleCycleTrain" /> already ticks
///         a shared cache/TLB on every access, and — given a <c>predictor</c> — trains it on every
///         resolved branch (see <see cref="SingleCycleTrain" />'s doc comment). Only the
///         <c>W</c>-instruction detailed-warming window needs to rebuild state a purely functional
///         pass cannot warm; for a detailed train with no store buffer and no other
///         cross-instruction short-term state, that is nothing, so <c>W</c> only needs to cover
///         pipeline-latch depth. The return-address stack is not warmed by the functional pass
///         (it lives outside <see cref="IBranchPredictor" />) — a known, documented gap, not a
///         silent one.
///     </para>
/// </summary>
public static class SmartsDriver {
    /// <param name="mechanism">
    ///     The single, long-lived mechanism instance for the whole run — reused (not recreated)
    ///     across every train construction so any state it owns outside <see cref="IArchState" />
    ///     (e.g. LR/SC reservation state) persists across switches exactly like the shared
    ///     <paramref name="iLayers" />/<paramref name="dLayers" />/<paramref name="predictor" />.
    /// </param>
    /// <param name="entryPoint">The workload's real entry point (first instruction of the whole run).</param>
    /// <param name="iLayers">Shared instruction-side cache/TLB, built once for the whole run.</param>
    /// <param name="dLayers">Shared data-side cache/TLB, built once for the whole run.</param>
    /// <param name="predictor">
    ///     Shared branch predictor, built once for the whole run, or null to warm no predictor
    ///     (a detailed train given a different predictor instance would simply cold-start it).
    /// </param>
    /// <param name="parameters">Sampling unit size, warmup length, systematic interval, offset, and unit count.</param>
    /// <param name="detailedTrainFactory">Builds the detailed train for each unit's warm-then-measure window.</param>
    /// <param name="onUnitEntry">
    ///     Diagnostic hook fired with each unit's index, its absolute instruction position (where the
    ///     detailed-warming window begins), and the architectural state handed to that unit's detailed
    ///     train, immediately before it is copied in. Lets a caller verify handoff fidelity directly —
    ///     e.g. against a reference functional trace's state at the same absolute instruction counts —
    ///     independent of whatever CPI the sampled windows happen to measure. Not fired for a unit
    ///     whose entry position is the workload's very start (nothing carried yet to inspect).
    /// </param>
    public static SmartsResult Run(
        IMechanism mechanism,
        ulong entryPoint,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        IBranchPredictor? predictor,
        SmartsParameters parameters,
        SmartsDetailedTrainFactory detailedTrainFactory,
        Action<int, long, IArchState>? onUnitEntry = null
    ) {
        var units = new List<SmartsUnitResult>(parameters.N);
        long globalPos = 0;
        IArchState? carried = null;
        var halted = false;

        for (var i = 0; i < parameters.N && !halted; i++) {
            long targetStart = parameters.J + (long)i * parameters.K;

            // Warming should start W instructions before the measured window, clamped so it
            // never precedes instruction 0 (early units) and never rewinds behind wherever the
            // stream actually is (small K relative to W+U — degrades systematic spacing
            // gracefully instead of erroring or double-measuring).
            long warmStart = Math.Max(globalPos, targetStart - Math.Min(parameters.W, targetStart));

            long ffNeeded = warmStart - globalPos;
            if (ffNeeded > 0) {
                var ffCounter = new InstructionCounter();
                ulong resumePc = carried?.Pc ?? entryPoint;
                var functional = new SingleCycleTrain(mechanism, iLayers, dLayers, resumePc, ffCounter, predictor);
                if (carried is not null) ArchStateTransfer.CopyInto(carried, functional.ArchState);

                functional.BeginStepping();
                while (ffCounter.Count < ffNeeded && functional.StepCycle()) { }
                functional.FinishStepping();

                globalPos += ffCounter.Count;
                carried = functional.ArchState;

                if (ffCounter.Count < ffNeeded) {
                    halted = true;
                    break;
                }
            }

            long warmupLen = Math.Max(0, targetStart - globalPos);
            var detailedCounter = new InstructionCounter();
            ulong detailedEntryPc = carried?.Pc ?? entryPoint;
            if (carried is not null) onUnitEntry?.Invoke(i, globalPos, carried);
            ISteppableTrain detailed = detailedTrainFactory(
                mechanism, iLayers, dLayers, predictor, detailedEntryPc, detailedCounter
            );
            if (carried is not null) ArchStateTransfer.CopyInto(carried, detailed.ArchState!);

            RevolutionResult rev = WarmupMeasureDriver.RunWarmupThenMeasure(
                detailed, detailedCounter, warmupLen, parameters.U
            );
            long measuredCount = detailedCounter.Count;
            bool completedWindow = measuredCount >= warmupLen + parameters.U;

            // OoO trains can still hold in-flight (not yet retired) instructions at the exact
            // tick the measured window ends — drain them so their stores land in the shared
            // memory and the state handed to the next train reflects real committed progress,
            // not a mid-flight snapshot. Drained after FinishStepping (not before), so the
            // drained instructions' cycles never leak into this unit's CPI measurement — the
            // Escapement this steps through doesn't care that the Train's own lifecycle already
            // transitioned to Finished. Only attempted if the window actually completed: a train
            // that already halted (program exit) mid-window has nothing left to drain, and
            // Drain() correctly refuses — same halt-boundary caveat as
            // --checkpoint-save-micro's own Drain() call in the Runner CLI.
            if (completedWindow && detailed is OooTrain ooo) {
                try { ooo.Drain(); }
                catch (InvalidOperationException) { completedWindow = false; }
            }

            long measured = Math.Max(0, measuredCount - warmupLen);
            if (measured > 0) units.Add(new SmartsUnitResult(i, measured, rev.TotalTicks));

            globalPos += detailedCounter.Count;
            carried = detailed.ArchState;

            if (!completedWindow) halted = true;
        }

        return new SmartsResult(units, halted, globalPos);
    }

    /// <summary>
    ///     A <see cref="SmartsDetailedTrainFactory" /> that builds a <see cref="FiveStageTrain" />
    ///     via its <c>MemoryLayers</c>-based constructor — <c>internal</c> to this assembly, so this
    ///     is the blessed entry point for callers outside <c>Pipeline</c> (Runner, RiscV32.Analysis,
    ///     tests) that need one without reaching for <c>InternalsVisibleTo</c>.
    /// </summary>
    public static SmartsDetailedTrainFactory FiveStage(bool forwardingEnabled = true, int storeBufferCapacity = 0) =>
        (mechanism, iLayers, dLayers, predictor, entryPoint, counter) =>
            new FiveStageTrain(
                mechanism, iLayers, dLayers, entryPoint, forwardingEnabled, predictor, storeBufferCapacity,
                commitObserver: counter
            );

    /// <summary>
    ///     A <see cref="SmartsDetailedTrainFactory" /> that builds an <see cref="OooTrain" /> via
    ///     its <c>MemoryLayers</c>-based constructor (see <see cref="FiveStage" /> for why this
    ///     indirection exists). <see cref="SmartsParameters.W" /> must exceed the in-flight
    ///     lifetime of an instruction in this configuration (bounded by ROB capacity, matching the
    ///     paper's Section 4.4 store-buffer/memory-latency/IPC bound) — unlike a 5-stage in-order
    ///     train, an OoO train's ROB/IQ/LQ/SQ hold real cross-instruction short-term state a purely
    ///     functional pass cannot warm, so an undersized <c>W</c> leaves the measured window
    ///     starting mid-drain-recovery from the previous unit's boundary.
    /// </summary>
    public static SmartsDetailedTrainFactory Ooo(
        int issueWidth = 2, int robCapacity = 32, int iqCapacity = 8, int lqCapacity = 0, int sqCapacity = 0
    ) =>
        (mechanism, iLayers, dLayers, predictor, entryPoint, counter) =>
            new OooTrain(
                mechanism, iLayers, dLayers, entryPoint, issueWidth, robCapacity, iqCapacity,
                predictor: predictor, commitObserver: counter, lqCapacity: lqCapacity, sqCapacity: sqCapacity
            );
}
