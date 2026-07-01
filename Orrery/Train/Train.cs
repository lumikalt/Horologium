using System.Text;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Orrery.Train;

/// <summary>
/// A periodic snapshot taken during a Revolution for time-series analysis.
/// <see cref="Tick"/> is relative to the start of the measurement phase (after warmup).
/// Counter values are cumulative from measurement start.
/// </summary>
public sealed record TimeSeriesPoint(
    long Tick,
    IReadOnlyList<DialBoardSnapshot> Snapshots
);

/// <summary>
/// The result of a completed Revolution — a snapshot of every
/// Gear's DialBoard at the moment the simulation finished.
/// </summary>
public sealed record RevolutionResult(
    long TotalTicks,
    long TotalEvents,
    IReadOnlyList<DialBoardSnapshot> Snapshots,
    IReadOnlyList<TimeSeriesPoint>? TimeSeries = null
) {
    /// <summary>
    /// Finds a snapshot by the owning gear's full path.
    /// Returns null if not found.
    /// </summary>
    public DialBoardSnapshot? Find(string ownerPath) =>
        Snapshots.FirstOrDefault(s => s.OwnerPath == ownerPath);

    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine($"Revolution complete — {TotalTicks} ticks, {TotalEvents} events");
        foreach (DialBoardSnapshot snap in Snapshots) sb.Append(snap);
        return sb.ToString();
    }
}

/// <summary>
/// The Train — the topology builder and lifecycle orchestrator.
/// <para>
/// A Train owns a collection of Gears, wires them together through
/// typed Arbor bindings, and drives the full simulation lifecycle:
/// <list type="number">
///   <item><description>AddGear() — register gears (Building phase)</description></item>
///   <item><description>Build() — Initialize all gears, then transition to Finalizing,
///     then Seal all gears, then lock all settings</description></item>
///   <item><description>Run(ticks) — transition to Running, Wind all gears, run Escapement,
///     transition to Finished, return RevolutionResult</description></item>
///   <item><description>Reset() — reset Escapement and all gears for another Revolution</description></item>
/// </list>
/// </para>
/// <para>
/// The Train does not know about ISAs, pipelines, or instruction semantics.
/// It is purely a lifecycle and topology manager.
/// </para>
/// </summary>
public sealed class Train {
    private readonly List<Gear> _gears = new();
    private readonly Escapement _escapement;
    private bool _built;

    public string Name => Root.Name;
    public SimNode Root { get; }

    public Train(string name, Escapement escapement) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(escapement);

        _escapement = escapement;
        Root = new SimNode(name);
    }

    // ── Gear registration ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers a pre-constructed Gear with this Train.
    /// The Gear must have been constructed with this Train's root (or a
    /// descendant) as its parent node.
    /// Only valid before Build() is called.
    /// </summary>
    public T AddGear<T>(T gear) where T : Gear {
        ArgumentNullException.ThrowIfNull(gear);

        if (_built)
            throw new InvalidOperationException(
                $"Cannot add Gear '{gear.Name}' — Train '{Name}' has already been built."
            );

        _gears.Add(gear);
        return gear;
    }

    /// <summary>
    /// Convenience factory: constructs a Gear of type T using the canonical
    /// (name, parent, escapement) constructor and registers it.
    /// </summary>
    public T AddGear<T>(string name, SimNode parent) where T : Gear {
        if (_built)
            throw new InvalidOperationException(
                $"Cannot add Gear '{name}' — Train '{Name}' has already been built."
            );

        var gear = (T)Activator.CreateInstance(
            typeof(T), name, parent, _escapement
        )!;
        return AddGear(gear);
    }

    /// <summary>
    /// Convenience factory that parents the new Gear directly to the Train root.
    /// </summary>
    public T AddGear<T>(string name) where T : Gear =>
        AddGear<T>(name, Root);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full build sequence:
    /// <list type="number">
    ///   <item><description>Initialize() all Gears (Building phase)</description></item>
    ///   <item><description>Transition tree to Finalizing</description></item>
    ///   <item><description>Seal() all Gears (Finalizing phase — bind arbors here)</description></item>
    ///   <item><description>Lock all Settings</description></item>
    /// </list>
    /// <para>After Build(), the Train is ready for Run().</para>
    /// </summary>
    public void Build() {
        if (_built)
            throw new InvalidOperationException(
                $"Train '{Name}' has already been built. Call Reset() before rebuilding."
            );

        // Step 1 — Initialize
        foreach (Gear gear in _gears) gear.Initialize();

        // Step 2 — Transition to Finalizing
        Root.BeginFinalizing();

        // Step 3 — Seal (arbor binding happens here, in subclass overrides)
        foreach (Gear gear in _gears) gear.Seal();

        // Step 4 — Lock settings
        foreach (Gear gear in _gears) gear.LockSettings();

        _built = true;
    }

    /// <summary>
    /// Runs the simulation for up to <paramref name="maxTicks"/> ticks.
    /// <list type="number">
    ///   <item><description>Transition tree to Running</description></item>
    ///   <item><description>Wind() all Gears (each gear schedules its first event)</description></item>
    ///   <item><description>Run Escapement for warmupTicks (if any) — warms up caches / predictors</description></item>
    ///   <item><description>Snapshot DialBoards as baseline (warmup phase only)</description></item>
    ///   <item><description>Run Escapement for maxTicks — measurement phase</description></item>
    ///   <item><description>Transition tree to Finished</description></item>
    ///   <item><description>Snapshot all DialBoards; subtract baseline when warmup was used</description></item>
    ///   <item><description>Return RevolutionResult</description></item>
    /// </list>
    /// <para>
    /// When <paramref name="warmupTicks"/> &gt; 0 the returned counters and
    /// histograms reflect only the measurement phase. Dials (which are rates)
    /// are taken from the final snapshot and therefore approximate the full run;
    /// this is acceptable for long measurements where warmup is a small fraction.
    /// </para>
    /// </summary>
    /// <param name="warmupTicks">Warmup phase duration in ticks.
    /// </param>
    /// <param name="snapshotInterval">
    /// Ticks between periodic time-series snapshots. 0 disables time series.
    /// Snapshots are cumulative from measurement start and stored in
    /// <see cref="RevolutionResult.TimeSeries"/>.
    /// </param>
    /// <param name="maxTicks">
    /// Maximum number of ticks to run.
    /// </param>
    public RevolutionResult Run(long maxTicks = long.MaxValue, long warmupTicks = 0, long snapshotInterval = 0) {
        if (!_built)
            throw new InvalidOperationException(
                $"Train '{Name}' has not been built. Call Build() before Run()."
            );

        if (Root.Lifecycle != SimLifecycle.Finalizing)
            throw new InvalidOperationException(
                $"Train '{Name}' is in lifecycle '{Root.Lifecycle}'. " +
                $"Expected 'Finalizing'. Did you already call Run()?"
            );

        // Step 1 — Transition to Running
        Root.BeginRunning();

        // Step 2 — Wind all gears (each schedules its first event)
        foreach (Gear gear in _gears) gear.Wind();

        // Step 3 — Warmup phase (optional)
        DialBoardSnapshot[]? baseline = null;
        if (warmupTicks > 0) {
            _escapement.Run(warmupTicks);
            baseline = [.._gears.Select(g => g.Dials.Snapshot()),];
        }

        // Step 4 — Measurement phase
        long startTick = _escapement.CurrentTick;
        long totalEvents = 0;
        List<TimeSeriesPoint>? timeSeries = null;

        if (snapshotInterval > 0) {
            timeSeries = [];
            var halted = false;
            for (long boundary = startTick + snapshotInterval;
                 boundary <= startTick + maxTicks && !halted;
                 boundary += snapshotInterval) {
                long chunkEvents = _escapement.Run(boundary);
                totalEvents += chunkEvents;

                long relTick = _escapement.CurrentTick - startTick;
                IReadOnlyList<DialBoardSnapshot> tsSnaps = baseline is null
                    ? [.._gears.Select(g => g.Dials.Snapshot()),]
                    : [.._gears.Select((g, i) => g.Dials.Snapshot().Subtract(baseline[i])),];
                timeSeries.Add(new TimeSeriesPoint(relTick, tsSnaps));

                if (chunkEvents == 0) halted = true;
            }

            // Run remainder up to maxTicks if not already halted.
            if (!halted) totalEvents += _escapement.Run(startTick + maxTicks);
        }
        else { totalEvents = _escapement.Run(startTick + maxTicks); }

        long ticks = _escapement.CurrentTick - startTick;

        // Step 5 — Transition to Finished
        Root.BeginFinished();

        // Step 6 — Snapshot; subtract warmup baseline if present
        List<DialBoardSnapshot> snapshots = baseline is null
            ? [.._gears.Select(g => g.Dials.Snapshot()),]
            : [.._gears.Select((g, i) => g.Dials.Snapshot().Subtract(baseline[i])),];

        return new RevolutionResult(ticks, totalEvents, snapshots, timeSeries);
    }

    /// <summary>
    /// Resets the Train for another Revolution.
    /// Clears the Escapement and resets all Gears.
    /// The tree returns to Finalizing so Run() can be called again.
    /// </summary>
    public void Reset() {
        if (!_built)
            throw new InvalidOperationException(
                $"Train '{Name}' has not been built. Nothing to reset."
            );

        _escapement.Reset();

        foreach (Gear gear in _gears) gear.Reset();

        // Walk the tree lifecycle back to Finalizing manually —
        // we need a backdoor since SimNode only allows forward transitions.
        // The Train owns the root, so this is the one place this is valid.
        Root.ForceLifecycle(SimLifecycle.Finalizing);
    }

    // ── Step-by-step API ─────────────────────────────────────────────────────

    /// <summary>The current simulation tick (delegates to the Escapement).</summary>
    public long CurrentTick => _escapement.CurrentTick;

    /// <summary>True when no pending events remain — the simulation has halted.</summary>
    public bool IsIdle => _escapement.IsIdle;

    /// <summary>
    /// Transitions to Running and winds all Gears, readying the train for
    /// <see cref="StepCycle"/>. Equivalent to the first two steps of <see cref="Run"/>.
    /// </summary>
    public void BeginStepping() {
        if (!_built)
            throw new InvalidOperationException(
                $"Train '{Name}' has not been built. Call Build() before BeginStepping()."
            );
        if (Root.Lifecycle != SimLifecycle.Finalizing)
            throw new InvalidOperationException(
                $"Train '{Name}' is in lifecycle '{Root.Lifecycle}'. Expected 'Finalizing'."
            );
        Root.BeginRunning();
        foreach (Gear gear in _gears) gear.Wind();
    }

    /// <summary>
    /// Advances the simulation by exactly one tick.
    /// Returns <c>true</c> if the simulation is still running (more events pending),
    /// or <c>false</c> if it has halted (no events remain after this step).
    /// </summary>
    public bool StepCycle() {
        _escapement.Step();
        return !_escapement.IsIdle;
    }

    /// <summary>
    /// Finalizes a step-by-step run, transitioning to Finished and returning the
    /// accumulated statistics — equivalent to the tail of <see cref="Run"/>.
    /// </summary>
    public RevolutionResult FinishStepping() {
        Root.BeginFinished();
        return new RevolutionResult(
            _escapement.CurrentTick, 0,
            [.._gears.Select(g => g.Dials.Snapshot()),]
        );
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Dumps the full tree topology for debugging.</summary>
    public string DumpTopology() => Root.DumpTree();
}