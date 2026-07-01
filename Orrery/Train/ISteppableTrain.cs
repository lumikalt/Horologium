namespace Orrery.Train;

/// <summary>
/// A pipeline train that can be driven one cycle at a time.
/// Implemented by all train types so a <see cref="MultiHartPipeline"/>-like
/// coordinator can step N trains in round-robin without knowing their internals.
/// </summary>
public interface ISteppableTrain {
    /// <summary>Transitions to Running and winds all gears. Call once before the first StepCycle.</summary>
    void BeginStepping();

    /// <summary>Advances by one tick. Returns true while events remain, false when the train has halted.</summary>
    bool StepCycle();

    /// <summary>True when no pending events remain.</summary>
    bool IsIdle { get; }

    /// <summary>Finalizes the run and returns accumulated statistics.</summary>
    RevolutionResult FinishStepping();
}