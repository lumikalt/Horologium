#region

using Mechanism;
using Orrery.Observation;

#endregion

namespace Orrery.Train;

/// <summary>
///     A pipeline train that can be driven one cycle at a time.
///     Implemented by all train types so a
///     <see>
///         <cref>MultiHartPipeline</cref>
///     </see>
///     -like coordinator can step N trains in round-robin without knowing their internals.
/// </summary>
public interface ISteppableTrain {
    /// <summary>
    ///     The single-hart architectural state, or null for multi-hart trains.
    ///     Valid after <see cref="BeginStepping" /> or <see cref="Run" />; reflects the committed state.
    /// </summary>
    IArchState? ArchState => null;

    /// <summary>Transitions to Running and winds all gears. Call once before the first StepCycle.</summary>
    void BeginStepping();

    /// <summary>Advances by one tick. Returns true while events remain false when the train has halted.</summary>
    bool StepCycle();

    /// <summary>Finalizes the run and returns accumulated statistics.</summary>
    RevolutionResult FinishStepping();

    /// <summary>
    ///     Snapshots every Gear's DialBoard at the current point in a step-by-step run, without
    ///     ending the lifecycle — lets a caller drive an instruction-count-bounded (rather than
    ///     tick-bounded) warmup/measurement split externally via <see cref="StepCycle" />. Only
    ///     implemented by trains that also support a commit observer (<c>SingleCycleTrain</c>,
    ///     <c>FiveStageTrain</c>, <c>OooeTrain</c>) — the trains an instruction-count driver is
    ///     meaningful for. Others throw <see cref="NotSupportedException" />.
    /// </summary>
    IReadOnlyList<DialBoardSnapshot> SnapshotDials() =>
        throw new NotSupportedException($"{GetType().Name} does not support mid-step dial snapshots.");

    /// <summary>
    ///     Finalizes a step-by-step run like <see cref="FinishStepping()" />, subtracting
    ///     <paramref name="baseline" /> (from an earlier <see cref="SnapshotDials" /> call) from
    ///     every Gear's final DialBoard snapshot. See <see cref="SnapshotDials" /> for which trains
    ///     support this.
    /// </summary>
    RevolutionResult FinishStepping(IReadOnlyList<DialBoardSnapshot> baseline) =>
        throw new NotSupportedException($"{GetType().Name} does not support baseline-relative FinishStepping.");

    /// <summary>
    ///     Runs the train for up to <paramref name="maxTicks" /> ticks.
    ///     <paramref name="warmupTicks" /> ticks run before measurement begins (counters reset at that point).
    ///     <paramref name="snapshotInterval" /> controls periodic time-series capture (0 = disabled).
    /// </summary>
    RevolutionResult Run(long maxTicks = long.MaxValue, long warmupTicks = 0, long snapshotInterval = 0);
}