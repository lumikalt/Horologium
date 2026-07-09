using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Orrery.Gears;

public abstract class Gear {
    private readonly List<object> _settings = [];

    // ── Identity ──────────────────────────────────────────────────────────────

    public SimNode Node { get; }
    public string Name => Node.Name;
    public string Path => Node.Path;

    // ── Infrastructure ────────────────────────────────────────────────────────

    protected Escapement Escapement { get; }

    /// <summary>This gear's observable metrics.</summary>
    public DialBoard Dials { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    protected Gear(string name, SimNode parent, Escapement escapement) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(escapement);

        Node = new SimNode(name, parent);
        Escapement = escapement;
        Dials = new DialBoard(Node.Path);
    }

    // ── Arbor registration ────────────────────────────────────────────────────

    protected OutArbor<T> AddOutArbor<T>(string name) {
        Node.AssertLifecycle(SimLifecycle.Building, "add an OutArbor");
        return new OutArbor<T>(name, Escapement);
    }

    protected InArbor<T> AddInArbor<T>(string name) {
        Node.AssertLifecycle(SimLifecycle.Building, "add an InArbor");
        return new InArbor<T>(name, Node);
    }

    // ── Setting registration ──────────────────────────────────────────────────

    /// <summary>
    /// Declares a typed configuration parameter with a default value.
    /// Call from Initialize().
    /// </summary>
    protected Setting<T> AddSetting<T>(string name, T defaultValue, string description = "") {
        Node.AssertLifecycle(SimLifecycle.Building, "add a Setting");
        var setting = new Setting<T>(name, defaultValue, description);
        _settings.Add(setting);
        return setting;
    }

    /// <summary>
    /// Locks all settings on this gear. Called by the Train before BeginRunning().
    /// </summary>
    public void LockSettings() {
        foreach (object s in _settings)
            if (s is ILockable lockable)
                lockable.Lock();
    }

    // ── Lifecycle hooks ───────────────────────────────────────────────────────

    public virtual void Initialize() { }
    public virtual void Seal() { }
    public virtual void Reset() { Dials.Reset(); }
    public virtual void Wind() { }
}