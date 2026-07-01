using Orrery.Train;

namespace Pipeline;

/// <summary>
/// Coordinates N independent pipeline trains in round-robin cycle-interleaved order,
/// analogous to <c>MultiHartKernel</c> but for full pipeline trains.
///
/// Each hart owns its own <see cref="ISteppableTrain"/> instance (and typically its
/// own <c>MesiCache</c> backed by a shared <c>MesiBus</c>). The coordinator advances
/// harts one tick at a time in hart-0 → hart-1 → … → hart-(N-1) order within each
/// logical cycle, so cross-hart coherence effects are interleaved at instruction
/// granularity just like <c>MultiHartKernel</c>.
///
/// Usage:
/// <code>
///   var pipeline = new MultiHartPipeline(train0, train1);
///   RevolutionResult[] results = pipeline.Run(maxTicks: 100_000);
/// </code>
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
}