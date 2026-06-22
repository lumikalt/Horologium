namespace Orrery.Scheduling;

/// <summary>
/// The phase of an event within a single tick.
/// Events at the same tick are executed in ascending phase order.
/// This ordering is the contract — pipeline stages depend on it.
/// </summary>
public enum Phase {
    /// <summary>Instruction fetch logic.</summary>
    Fetch = 0,

    /// <summary>Main pipeline computation — decode, execute, etc.</summary>
    Execute = 1,

    /// <summary>Delivery of data sent through Arbors.</summary>
    ArborUpdate = 2,

    /// <summary>Result writeback to architectural state.</summary>
    Writeback = 3,

    /// <summary>In-order retirement and commit.</summary>
    Commit = 4,

    /// <summary>Pipeline flush and squash — always runs after commit.</summary>
    Flush = 5,

    /// <summary>Dial snapshot and stat collection.</summary>
    Collection = 6,
}

/// <summary>
/// A scheduled work item: a callback to invoke at a specific tick and phase.
/// </summary>
internal readonly record struct SimEvent(long Tick, Phase Phase)
    : IComparable<SimEvent> {
    public int CompareTo(SimEvent other) {
        int t = Tick.CompareTo(other.Tick);
        return t != 0 ? t : Phase.CompareTo(other.Phase);
    }
}

/// <summary>
/// The Escapement — the discrete-event scheduler that drives time forward.
///
/// Like the escapement in a mechanical clock, it releases work in discrete,
/// ordered steps. Nothing in the simulation happens except through the
/// Escapement scheduling it.
///
/// Thread safety: not thread-safe. The simulation runs on a single thread.
/// </summary>
public sealed class Escapement {
    private readonly PriorityQueue<Action, SimEvent> _queue = new();

    // ── Time ─────────────────────────────────────────────────────────────────

    /// <summary>The current simulation tick.</summary>
    public long CurrentTick { get; private set; }

    /// <summary>The current phase within the current tick.</summary>
    public Phase CurrentPhase { get; private set; } = Phase.Fetch;

    /// <summary>True if there are no pending events.</summary>
    public bool IsIdle => _queue.Count == 0;

    // ── Scheduling ───────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a callback at an absolute tick and phase.
    /// May be called from within a running event callback.
    /// </summary>
    public void Schedule(Action callback, long atTick, Phase phase) {
        ArgumentNullException.ThrowIfNull(callback);

        if (atTick < CurrentTick)
            throw new ArgumentOutOfRangeException(
                nameof(atTick),
                $"Cannot schedule an event in the past. " +
                $"Requested tick {atTick}, current tick is {CurrentTick}."
            );

        if (atTick == CurrentTick && phase < CurrentPhase)
            throw new ArgumentOutOfRangeException(
                nameof(phase),
                $"Cannot schedule an event in a phase that has already passed. " +
                $"Requested phase {phase} at tick {atTick}, " +
                $"current phase is {CurrentPhase}."
            );

        _queue.Enqueue(callback, new SimEvent(atTick, phase));
    }

    /// <summary>
    /// Schedules a callback a given number of ticks from now, at the specified phase.
    /// </summary>
    public void ScheduleAfter(Action callback, long delay, Phase phase) {
        switch (delay) {
            case < 0:
                throw new ArgumentOutOfRangeException(
                    nameof(delay),
                    "Delay must be non-negative."
                );
            case 0 when phase <= CurrentPhase:
                throw new ArgumentOutOfRangeException(
                    nameof(phase),
                    $"A zero-delay event must target a phase later than the current " +
                    $"phase ({CurrentPhase})."
                );
            default: Schedule(callback, CurrentTick + delay, phase); break;
        }
    }

    /// <summary>
    /// Schedules a callback at the next tick, at the specified phase.
    /// Convenience wrapper for the common case of a one-cycle latency.
    /// </summary>
    public void ScheduleNextTick(Action callback, Phase phase) =>
        Schedule(callback, CurrentTick + 1, phase);

    // ── Execution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the simulation until the queue is empty or the tick limit is reached.
    /// </summary>
    /// <param name="untilTick">
    /// Inclusive upper bound on ticks to process.
    /// Pass <see cref="long.MaxValue"/> to run until idle.
    /// </param>
    /// <returns>The number of events processed.</returns>
    public long Run(long untilTick = long.MaxValue) {
        long processed = 0;

        while (_queue.Count > 0) {
            _queue.TryPeek(out _, out SimEvent next);

            if (next.Tick > untilTick) break;

            CurrentTick = next.Tick;
            CurrentPhase = next.Phase;

            _queue.Dequeue().Invoke();
            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Runs exactly one tick (all events at <see cref="CurrentTick"/> + 1),
    /// then stops. Useful for step-by-step debugging.
    /// </summary>
    public void Step() => Run(CurrentTick + 1);

    /// <summary>
    /// Discards all pending events and resets the clock to zero.
    /// Used between Revolution runs.
    /// </summary>
    public void Reset() {
        _queue.Clear();
        CurrentTick = 0;
        CurrentPhase = Phase.Fetch;
    }
}