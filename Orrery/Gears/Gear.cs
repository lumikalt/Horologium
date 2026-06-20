using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Orrery.Gears;

public abstract class Gear {
    private readonly List<object> _outArbors = [];
    private readonly List<object> _inArbors = [];
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
        var arbor = new OutArbor<T>(name, Escapement);
        _outArbors.Add(arbor);
        return arbor;
    }

    protected InArbor<T> AddInArbor<T>(string name) {
        Node.AssertLifecycle(SimLifecycle.Building, "add an InArbor");
        var arbor = new InArbor<T>(name, Node);
        _inArbors.Add(arbor);
        return arbor;
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
    public void LockSettings() =>
        _settings.ToList().ForEach(s => {
                // Settings are erased to object — use reflection-free dynamic dispatch
                if (s is ILockable lockable) lockable.Lock();
            }
        );

    // ── Lifecycle hooks ───────────────────────────────────────────────────────

    public virtual void Initialize() { }
    public virtual void Finalize() { }
    public virtual void Reset() { Dials.Reset(); }
    public virtual void Tick() { }
}