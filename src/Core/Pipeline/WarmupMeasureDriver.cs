#region

using Mechanism;
using Orrery.Observation;
using Orrery.Train;

#endregion

namespace Pipeline;

/// <summary>
///     Drives a train through an instruction-count-bounded warmup phase (unmeasured, structures
///     stay warm) followed by an instruction-count-bounded measurement phase, returning the
///     baseline-subtracted result for just the measured phase.
///     <para>
///         <see cref="Train.Run" />/<see cref="ISteppableTrain.Run" /> only support tick-bounded
///         warmup/measurement splits — the SimPoint methodology needs boundaries in dynamic
///         instruction counts instead, since that is what BBV profiling and clustering operate on.
///         A train's lifecycle is one-shot (<c>Run()</c>, and separately
///         <c>BeginStepping</c>→<c>FinishStepping</c>, cannot be re-entered without
///         <c>Reset()</c>, which wipes warmed-up microarchitectural state) so both phases must
///         happen within one continuous stepping session. This drives that session manually via
///         <see cref="ISteppableTrain.StepCycle" />, using <c>counter</c> to know when
///         each phase ends.
///     </para>
/// </summary>
public static class WarmupMeasureDriver {
    /// <param name="train">
    ///     A freshly built, not-yet-stepped train whose commit observer is <paramref name="counter" />
    ///     (e.g., built from a <c>PipelineSpec</c> with <c>CommitObserver: counter</c>). Must support
    ///     <see cref="ISteppableTrain.SnapshotDials" />/
    ///     <see cref="ISteppableTrain.FinishStepping(System.Collections.Generic.IReadOnlyList{DialBoardSnapshot})" />
    ///     — currently <c>SingleCycleTrain</c>, <c>FiveStageTrain</c>, and <c>OooTrain</c>.
    /// </param>
    /// <param name="counter">
    ///     The same <see cref="InstructionCounter" /> instance passed as <paramref name="train" />'s
    ///     commit observer at construction.
    /// </param>
    /// <param name="warmupInstructions">
    ///     Instructions to run before the measurement starts. Zero skips the warmup phase entirely.
    /// </param>
    /// <param name="measureInstructions">Instructions to run and measure after warmup.</param>
    /// <returns>
    ///     The baseline-subtracted <see cref="RevolutionResult" /> for the measurement phase only.
    ///     If the train halts on its own (<see cref="ISteppableTrain.StepCycle" /> returns false)
    ///     before either target count is reached, the phase ends early — the caller can tell from
    ///     <paramref name="counter" />'s final <see cref="InstructionCounter.Count" /> how many
    ///     instructions actually ran.
    /// </returns>
    public static RevolutionResult RunWarmupThenMeasure(
        ISteppableTrain train,
        InstructionCounter counter,
        long warmupInstructions,
        long measureInstructions
    ) {
        train.BeginStepping();

        while (counter.Count < warmupInstructions && train.StepCycle()) { }

        IReadOnlyList<DialBoardSnapshot> baseline = train.SnapshotDials();

        long measureTarget = warmupInstructions + measureInstructions;
        while (counter.Count < measureTarget && train.StepCycle()) { }

        return train.FinishStepping(baseline);
    }
}