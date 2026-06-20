using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Orrery.Train;

/// <summary>
/// The result of a completed Revolution — a snapshot of every
/// Gear's DialBoard at the moment the simulation finished.
/// </summary>
public sealed record RevolutionResult(
    long TotalTicks,
    long TotalEvents,
    IReadOnlyList<DialBoardSnapshot> Snapshots
) {
    /// <summary>
    /// Finds a snapshot by the owning gear's full path.
    /// Returns null if not found.
    /// </summary>
    public DialBoardSnapshot? Find(string ownerPath) =>
        Snapshots.FirstOrDefault(s => s.OwnerPath == ownerPath);

    public override string ToString() {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Revolution complete — {TotalTicks} ticks, {TotalEvents} events");
        foreach (DialBoardSnapshot snap in Snapshots) sb.Append(snap);
        return sb.ToString();
    }
}

/// <summary>
/// The Train — the topology builder and lifecycle orchestrator.
/// <p/>
/// A Train owns a collection of Gears, wires them together through
/// typed Arbor bindings, and drives the full simulation lifecycle:
/// <p/>
///
///   1. AddGear()     — register gears (Building phase)
///   2. Build()       — Initialize all gears, then transition to Finalizing,
///                      then Finalize all gears, then lock all settings
///   3. Run(ticks)    — transition to Running, Tick all gears, run Escapement,
///                      transition to Finished, return RevolutionResult
///   4. Reset()       — reset Escapement and all gears for another Revolution
///
/// <p/>
/// The Train does not know about ISAs, pipelines, or instruction semantics.
/// It is purely a lifecycle and topology manager.
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
    ///   1. Initialize() all Gears        (Building phase)
    ///   2. Transition tree to Finalizing
    ///   3. Finalize() all Gears          (Finalizing phase — bind arbors here)
    ///   4. Lock all Settings
    ///
    /// After Build(), the Train is ready for Run().
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

        // Step 3 — Finalize (arbor binding happens here, in subclass overrides)
        foreach (Gear gear in _gears) gear.Finalize();

        // Step 4 — Lock settings
        foreach (Gear gear in _gears) gear.LockSettings();

        _built = true;
    }

    /// <summary>
    /// Runs the simulation for up to <paramref name="maxTicks"/> ticks.
    ///
    ///   1. Transition tree to Running
    ///   2. Tick() all Gears             (each gear schedules its first event)
    ///   3. Run the Escapement
    ///   4. Transition tree to Finished
    ///   5. Snapshot all DialBoards
    ///   6. Return RevolutionResult
    /// </summary>
    public RevolutionResult Run(long maxTicks = long.MaxValue) {
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

        // Step 2 — Tick all gears (each schedules its first event)
        foreach (Gear gear in _gears) gear.Tick();

        // Step 3 — Run the Escapement
        long events = _escapement.Run(maxTicks);
        long ticks = _escapement.CurrentTick;

        // Step 4 — Transition to Finished
        Root.BeginFinished();

        // Step 5 — Snapshot all DialBoards
        List<DialBoardSnapshot> snapshots = _gears
                                           .Select(g => g.Dials.Snapshot())
                                           .ToList();

        return new RevolutionResult(ticks, events, snapshots);
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

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Dumps the full tree topology for debugging.</summary>
    public string DumpTopology() => Root.DumpTree();
}