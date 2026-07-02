using Orrery.Cache;
using Orrery.Train;

namespace Pipeline;

/// <summary>
/// Coordinates N independent pipeline trains in round-robin cycle-interleaved order,
/// analogous to <c>MultiHartKernel</c> but for full pipeline trains.
/// <para>
/// Each hart owns its own <see cref="ISteppableTrain"/> instance (and typically its
/// own <c>MoesiCache</c> backed by a shared <c>MoesiBus</c>). The coordinator advances
/// harts one tick at a time in hart-0 → hart-1 → … → hart-(N-1) order within each
/// logical cycle, so cross-hart coherence effects are interleaved at instruction
/// granularity just like <c>MultiHartKernel</c>.
/// </para>
/// <para>
/// Usage:
/// <code>
///   var pipeline = new MultiHartPipeline(train0, train1);
///   RevolutionResult[] results = pipeline.Run(maxTicks: 100_000);
/// </code>
/// </para>
/// </summary>
public sealed class MultiHartPipeline {
    private readonly ISteppableTrain[] _trains;

    public int HartCount => _trains.Length;

    public MultiHartPipeline(params ISteppableTrain[] trains) {
        ArgumentNullException.ThrowIfNull(trains);
        if (trains.Length == 0) throw new ArgumentException("At least one train required.", nameof(trains));
        _trains = trains;
    }

    /// <summary>
    /// Runs all trains for up to <paramref name="maxTicks"/> ticks in round-robin
    /// order.  Stops when every train has halted (IsIdle) or the tick limit is
    /// reached, whichever comes first.
    /// </summary>
    /// <returns>One <see cref="RevolutionResult"/> per hart, in hart-index order.</returns>
    public RevolutionResult[] Run(long maxTicks = long.MaxValue) {
        foreach (ISteppableTrain t in _trains) t.BeginStepping();

        var halted = new bool[_trains.Length];
        long ticks = 0;

        while (ticks < maxTicks) {
            var anyActive = false;
            for (var i = 0; i < _trains.Length; i++) {
                if (halted[i]) continue;
                bool stillRunning = _trains[i].StepCycle();
                if (!stillRunning)
                    halted[i] = true;
                else
                    anyActive = true;
            }

            ticks++;
            if (!anyActive) break;
        }

        var results = new RevolutionResult[_trains.Length];
        for (var i = 0; i < _trains.Length; i++) results[i] = _trains[i].FinishStepping();
        return results;
    }

    /// <summary>
    /// Runs all trains for up to <paramref name="maxTicks"/> ticks with per-tick two-phase
    /// parallelism: phase 1 advances every hart's <see cref="ISteppableTrain.StepCycle"/> in
    /// parallel threads; phase 2 drains <paramref name="buses"/> in hart-0 → hart-N order,
    /// applying deferred bus operations and correcting cache-coherence state.
    /// <para>
    /// Results are bit-identical to <see cref="Run"/> for well-synchronized programs — those
    /// where no hart reads a cache line in the same outer tick that another hart writes it
    /// (guaranteed by correct use of LR/SC or memory fences).  Same-tick cross-hart
    /// write-then-read is a data race with undefined behavior in this mode; use <see cref="Run"/>
    /// as the correctness reference for any program that requires that ordering.
    /// </para>
    /// </summary>
    /// <param name="buses">
    /// One <see cref="DeferredBus"/> per hart, in hart-index order.
    /// Each hart's <see cref="MoesiCache"/> must have been constructed with the corresponding
    /// <see cref="DeferredBus"/> as its <c>IBus</c> argument.
    /// </param>
    /// <param name="maxTicks">Max ticks to run for.</param>
    public RevolutionResult[] RunConcurrent(DeferredBus[] buses, long maxTicks = long.MaxValue) {
        ArgumentNullException.ThrowIfNull(buses);
        if (buses.Length != _trains.Length)
            throw new ArgumentException(
                $"buses.Length ({buses.Length}) must equal HartCount ({_trains.Length}).",
                nameof(buses)
            );

        foreach (ISteppableTrain t in _trains) t.BeginStepping();

        var halted = new bool[_trains.Length];
        long ticks = 0;

        while (ticks < maxTicks) {
            // Phase 1: all non-halted harts advance one cycle in parallel.
            Parallel.For(
                0, _trains.Length, i => {
                    if (!halted[i] && !_trains[i].StepCycle()) halted[i] = true;
                }
            );

            // Phase 2: drain deferred bus queues in hart-0 → hart-N order.
            foreach (DeferredBus bus in buses) {
                bus.Drain();
                bus.Clear();
            }

            ticks++;
            var anyActive = false;
            for (var i = 0; i < _trains.Length; i++)
                if (!halted[i]) {
                    anyActive = true;
                    break;
                }

            if (!anyActive) break;
        }

        var results = new RevolutionResult[_trains.Length];
        for (var i = 0; i < _trains.Length; i++) results[i] = _trains[i].FinishStepping();
        return results;
    }
}