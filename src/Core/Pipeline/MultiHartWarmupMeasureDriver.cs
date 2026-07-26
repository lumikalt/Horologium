#region

using Mechanism;
using Orrery.Observation;
using Orrery.Train;

#endregion

namespace Pipeline;

/// <summary>
///     <see cref="WarmupMeasureDriver" />'s multi-hart counterpart: drives N trains through a
///     <em>global</em> (summed across every hart, matching <c>MultiHartLoopPointProfiler</c>'s own
///     accounting) instruction-count-bounded warmup phase, then a global instruction-count-bounded
///     measurement phase, one round-robin cycle per hart per tick — the same interleaving order
///     <c>MultiHartPipeline.Run</c> uses.
///     <para>
///         A halted train's <see cref="ISteppableTrain.StepCycle" /> is never called again once it
///         first returns <c>false</c> (mirroring <c>MultiHartPipeline.Run</c>'s own halted-tracking),
///         so a hart that finishes early simply stops contributing further instructions to the
///         global count for the rest of the run, rather than being stepped past its own halt.
///     </para>
/// </summary>
public static class MultiHartWarmupMeasureDriver {
    /// <summary>
    ///     A hart still spinning on a still-blocked <see cref="ExecuteResult.RequestBlock" /> (e.g.
    ///     futex(FUTEX_WAIT)) never halts — its <see cref="ISteppableTrain.StepCycle" /> keeps returning
    ///     <c>true</c> forever, since it's retrying, not halted. If every hart that could ever clear that
    ///     wait has already halted, the global instruction count can never advance again, and a loop that
    ///     only checks "is any hart still active" would spin forever. This bounds consecutive ticks with
    ///     zero global progress instead of trusting halted-state alone — generous enough that no
    ///     legitimate detailed-pipeline stall (cache misses, etc.) could ever trip it, since it only
    ///     triggers when NOT A SINGLE hart retires anything, machine-wide, for this many consecutive ticks.
    /// </summary>
    private const long StallTickLimit = 100_000;

    /// <param name="trains">
    ///     Freshly built, not-yet-stepped trains, each with the corresponding entry in
    ///     <paramref name="counters" /> wired as its commit observer.
    /// </param>
    /// <param name="counters">One <see cref="InstructionCounter" /> per hart, same order as <paramref name="trains" />.</param>
    /// <param name="warmupInstructions">Global (all-harts) instructions to run before measurement starts.</param>
    /// <param name="measureInstructions">Global (all-harts) instructions to run and measure after warmup.</param>
    /// <returns>Baseline-subtracted <see cref="RevolutionResult" /> per hart, same order as <paramref name="trains" />.</returns>
    public static RevolutionResult[] RunWarmupThenMeasure(
        IReadOnlyList<ISteppableTrain> trains,
        IReadOnlyList<InstructionCounter> counters,
        long warmupInstructions,
        long measureInstructions
    ) {
        if (trains.Count != counters.Count) {
            throw new ArgumentException(
                $"trains.Count ({trains.Count}) must equal counters.Count ({counters.Count}).", nameof(counters)
            );
        }

        foreach (ISteppableTrain t in trains) t.BeginStepping();
        var halted = new bool[trains.Count];

        long GlobalCount() {
            var total = 0L;
            foreach (InstructionCounter c in counters) total += c.Count;
            return total;
        }

        bool StepAllActive() {
            var any = false;
            for (var i = 0; i < trains.Count; i++) {
                if (halted[i]) continue;
                if (trains[i].StepCycle()) any = true;
                else halted[i] = true;
            }

            return any;
        }

        // Runs until either the global instruction count reaches target, every hart has halted,
        // or no hart retires anything for StallTickLimit consecutive ticks (a permanent deadlock —
        // the waker a still-blocked hart needs has already halted, so global count can never move
        // again). The last case returns early with whatever progress was made, rather than hanging.
        void RunUntil(long target) {
            long lastCount = GlobalCount();
            var stalledTicks = 0L;
            while (GlobalCount() < target) {
                if (!StepAllActive()) return;
                long count = GlobalCount();
                if (count == lastCount) {
                    if (++stalledTicks >= StallTickLimit) return;
                }
                else {
                    stalledTicks = 0;
                    lastCount = count;
                }
            }
        }

        RunUntil(warmupInstructions);

        var baselines = new IReadOnlyList<DialBoardSnapshot>[trains.Count];
        for (var i = 0; i < trains.Count; i++) baselines[i] = trains[i].SnapshotDials();

        RunUntil(warmupInstructions + measureInstructions);

        var results = new RevolutionResult[trains.Count];
        for (var i = 0; i < trains.Count; i++) results[i] = trains[i].FinishStepping(baselines[i]);
        return results;
    }
}
