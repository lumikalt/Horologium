using Mechanism;

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
    ///     Runs the train for up to <paramref name="maxTicks" /> ticks.
    ///     <paramref name="warmupTicks" /> ticks run before measurement begins (counters reset at that point).
    ///     <paramref name="snapshotInterval" /> controls periodic time-series capture (0 = disabled).
    /// </summary>
    RevolutionResult Run(long maxTicks = long.MaxValue, long warmupTicks = 0, long snapshotInterval = 0);
}