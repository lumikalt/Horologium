#region

using Mechanism;
using Orrery.Cache;
using Orrery.Train;

#endregion

namespace Pipeline;

/// <summary>
///     Coordinates N independent pipeline trains in round-robin cycle-interleaved order,
///     analogous to <c>MultiHartKernel</c> but for full pipeline trains.
///     <para>
///         Each hart owns its own <see cref="ISteppableTrain" /> instance (and typically its
///         own <c>MoesifCache</c> backed by a shared <c>MoesifBus</c>). The coordinator advances
///         harts one tick at a time in hart-0 → hart-1 → … → hart-(N-1) order within each
///         logical cycle, so cross-hart coherence effects are interleaved at instruction
///         granularity just like <c>MultiHartKernel</c>.
///     </para>
///     <para>
///         Usage:
///         <code>
///   var pipeline = new MultiHartPipeline(train0, train1);
///   RevolutionResult[] results = pipeline.Run(maxTicks: 100_000);
/// </code>
///     </para>
///     <para>
///         Optionally supports <c>clone()</c>-driven dynamic hart activation (mirroring
///         <c>MultiHartKernel</c>'s dormant-slot <see cref="IHartSpawner" /> support, unlike which
///         this coordinator has no pre-allocated slots to fill — a spawn always builds and appends a
///         brand-new train). Unlike <c>MultiHartKernel</c> (a shared <see cref="IMechanism" /> plus a
///         plain <see cref="IArchState" /> per hart), each hart here owns a whole
///         <see cref="ISteppableTrain" /> — its own pipeline latches, PRF/rename state, fetch-address
///         tracking — so spawning one can't just assign architectural state into a pre-built slot; a
///         genuinely new train must be constructed. <see cref="SpawnHart" /> needs a caller-supplied
///         <c>spawnTrainFactory</c> (constructor parameter) that builds a fresh, workload-appropriate
///         train (own <see cref="IMechanism" /> sharing the parent's syscall handler and memory, own
///         <c>entryPoint</c> read from the spawned hart's own <see cref="IArchState.Pc" /> — the same
///         "read PC before construction, thread it through as the constructor argument" pattern every
///         other checkpoint-restore path in this codebase uses, since a train's own fetch-address
///         state is seeded once at construction and never re-read from <see cref="IArchState" />
///         afterward); <see cref="SpawnHart" /> then copies the rest of the spawned hart's register/ISA
///         state into the new train's own (otherwise zero-valued) <see cref="IArchState" /> via
///         <see cref="ArchStateTransfer.CopyInto" />.
///     </para>
///     <para>
///         Only <see cref="Run" /> supports dynamic activation. <see cref="RunConcurrent" /> steps
///         every hart's <see cref="ISteppableTrain.StepCycle" /> from real, concurrent host threads
///         inside a <c>Parallel.For</c> — appending to the shared hart list from inside that region
///         (as a spawn would) races with the other threads' concurrent reads of it, so
///         <see cref="SpawnHart" /> throws if called while a <see cref="RunConcurrent" /> call is in
///         flight, rather than silently corrupting the list. Because the offending call happens on one
///         of <c>Parallel.For</c>'s own worker threads, the thrown <see cref="InvalidOperationException" />
///         surfaces wrapped in an <see cref="AggregateException" /> from <see cref="RunConcurrent" />,
///         not bare the way it does from <see cref="Run" />.
///     </para>
/// </summary>
public sealed class MultiHartPipeline : IHartSpawner {
    private readonly List<bool> _halted = [];
    private readonly Func<IArchState, ISteppableTrain>? _spawnTrainFactory;
    private readonly List<ISteppableTrain> _trains;
    private volatile bool _runConcurrentInFlight;

    public MultiHartPipeline(params ISteppableTrain[] trains) : this(null, trains) { }

    /// <param name="spawnTrainFactory">
    ///     Builds a fresh detailed-pipeline train for a hart spawned via <see cref="SpawnHart" />
    ///     (i.e. a real <c>clone()</c> call), given that hart's already-derived initial
    ///     <see cref="IArchState" /> (Pc/sp/tp already set by the caller's <c>clone()</c>
    ///     implementation) — read its <see cref="IArchState.Pc" /> and pass that as the new train's
    ///     own constructor <c>entryPoint</c> argument. Null (the default) means this pipeline has a
    ///     fixed hart count and <see cref="SpawnHart" /> throws, matching every existing caller's
    ///     behavior before dynamic activation existed.
    /// </param>
    public MultiHartPipeline(Func<IArchState, ISteppableTrain>? spawnTrainFactory, params ISteppableTrain[] trains) {
        ArgumentNullException.ThrowIfNull(trains);
        if (trains.Length == 0) throw new ArgumentException("At least one train required.", nameof(trains));
        _trains = [..trains,];
        _halted.AddRange(new bool[trains.Length]);
        _spawnTrainFactory = spawnTrainFactory;
    }

    public int HartCount => _trains.Count;

    /// <inheritdoc />
    public int SpawnHart(IArchState initialState) {
        if (_spawnTrainFactory is null) {
            throw new InvalidOperationException(
                "MultiHartPipeline.SpawnHart: no spawnTrainFactory was supplied at construction — this " +
                "pipeline has a fixed hart count."
            );
        }

        if (_runConcurrentInFlight) {
            throw new InvalidOperationException(
                "MultiHartPipeline.SpawnHart: dynamic hart activation is not supported during RunConcurrent " +
                "— appending to the shared hart list would race with other harts' concurrent StepCycle calls."
            );
        }

        ISteppableTrain train = _spawnTrainFactory(initialState);
        ArchStateTransfer.CopyInto(initialState, train.ArchState!);
        train.BeginStepping();

        _trains.Add(train);
        _halted.Add(false);
        return _trains.Count - 1;
    }

    /// <summary>
    ///     Runs all trains for up to <paramref name="maxTicks" /> ticks in round-robin
    ///     order.  Stops when every train has halted (IsIdle) or the tick limit is
    ///     reached, whichever comes first.
    ///     <para>
    ///         A hart spawned mid-run (via <see cref="SpawnHart" />, i.e. a real <c>clone()</c> call
    ///         from one of the currently-driven harts) is appended to the round-robin set and — since
    ///         it's always appended past every index the current tick's <c>for</c> loop has already
    ///         reached — starts stepping in that same tick, one tick "younger" than its parent, the
    ///         same same-tick-if-the-slot-lands-later asymmetry <c>MultiHartKernel.SpawnHart</c> already
    ///         documents and accepts.
    ///     </para>
    /// </summary>
    /// <returns>One <see cref="RevolutionResult" /> per hart, in hart-index order as of when this
    /// call returns — including any hart spawned during the run.</returns>
    public RevolutionResult[] Run(long maxTicks = long.MaxValue) {
        foreach (ISteppableTrain t in _trains) t.BeginStepping();
        for (var i = 0; i < _halted.Count; i++) _halted[i] = false;

        long ticks = 0;

        while (ticks < maxTicks) {
            var anyActive = false;
            for (var i = 0; i < _trains.Count; i++) {
                if (_halted[i]) continue;
                bool stillRunning = _trains[i].StepCycle();
                if (!stillRunning)
                    _halted[i] = true;
                else
                    anyActive = true;
            }

            ticks++;
            if (!anyActive) break;
        }

        var results = new RevolutionResult[_trains.Count];
        for (var i = 0; i < _trains.Count; i++) results[i] = _trains[i].FinishStepping();
        return results;
    }

    /// <summary>
    ///     Runs all trains for up to <paramref name="maxTicks" /> ticks with per-tick two-phase
    ///     parallelism: phase 1 advances every hart's <see cref="ISteppableTrain.StepCycle" /> in
    ///     parallel threads; phase 2 drains <paramref name="buses" /> in hart-0 → hart-N order,
    ///     applying deferred bus operations and correcting cache-coherence state.
    ///     <para>
    ///         Results are bit-identical to <see cref="Run" /> for well-synchronized programs — those
    ///         where no hart reads a cache line in the same outer tick that another hart writes it
    ///         (guaranteed by correct use of LR/SC or memory fences).  Same-tick cross-hart
    ///         write-then-read is a data race with undefined behavior in this mode; use <see cref="Run" />
    ///         as the correctness reference for any program that requires that ordering.
    ///     </para>
    /// </summary>
    /// <param name="buses">
    ///     One <see cref="DeferredBus" /> per hart, in hart-index order.
    ///     Each hart's <see cref="MoesifCache" /> must have been constructed with the corresponding
    ///     <see cref="DeferredBus" /> as its <c>IBus</c> argument.
    /// </param>
    /// <param name="maxTicks">Max ticks to run for.</param>
    public RevolutionResult[] RunConcurrent(DeferredBus[] buses, long maxTicks = long.MaxValue) {
        ArgumentNullException.ThrowIfNull(buses);
        if (buses.Length != _trains.Count)
            throw new ArgumentException(
                $"buses.Length ({buses.Length}) must equal HartCount ({_trains.Count}).",
                nameof(buses)
            );

        foreach (ISteppableTrain t in _trains) t.BeginStepping();
        for (var i = 0; i < _halted.Count; i++) _halted[i] = false;

        long ticks = 0;
        _runConcurrentInFlight = true;
        try {
            while (ticks < maxTicks) {
                // Phase 1: all non-halted harts advance one cycle in parallel. Hart count is fixed
                // for the duration of this call (SpawnHart throws while _runConcurrentInFlight), so
                // indexing _trains/_halted from concurrent tasks here is safe.
                Parallel.For(
                    0, _trains.Count, i => {
                        if (!_halted[i] && !_trains[i].StepCycle()) _halted[i] = true;
                    }
                );

                // Phase 2: drain deferred bus queues in hart-0 → hart-N order.
                foreach (DeferredBus bus in buses) {
                    bus.Drain();
                    bus.Clear();
                }

                ticks++;
                var anyActive = false;
                for (var i = 0; i < _trains.Count; i++)
                    if (!_halted[i]) {
                        anyActive = true;
                        break;
                    }

                if (!anyActive) break;
            }
        }
        finally {
            _runConcurrentInFlight = false;
        }

        var results = new RevolutionResult[_trains.Count];
        for (var i = 0; i < _trains.Count; i++) results[i] = _trains[i].FinishStepping();
        return results;
    }
}