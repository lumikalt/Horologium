using System.Collections.ObjectModel;
using System.Text;

namespace Orrery.Tree;

/// <summary>
/// The lifecycle of a node in the simulation tree.
/// Transitions are one-way and enforced — you cannot go backwards.
/// </summary>
public enum SimLifecycle {
    /// <summary>The node is being constructed. Children may be added.</summary>
    Building,

    /// <summary>The tree is complete. Arbors may now be bound.</summary>
    Finalizing,

    /// <summary>The simulation is running. Events may be scheduled.</summary>
    Running,

    /// <summary>The simulation has completed. State is read-only.</summary>
    Finished,
}

/// <summary>
/// The universal base for every named object in the simulation.
/// Every Gear, port collection, stat group, and parameter set lives
/// as a node in this tree, addressable by its full path.
/// </summary>
public class SimNode {
    private readonly List<SimNode> _children = [];

    // ── Identity ────────────────────────────────────────────────────────────

    /// <summary>The local name of this node within its parent.</summary>
    public string Name { get; }

    /// <summary>The parent node, or null if this is the root.</summary>
    public SimNode? Parent { get; private set; }

    /// <summary>The full dot-separated path from the root to this node.</summary>
    public string Path => Parent is null ? Name : $"{Parent.Path}.{Name}";

    /// <summary>The read-only collection of direct children.</summary>
    public ReadOnlyCollection<SimNode> Children => _children.AsReadOnly();

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// The current lifecycle state of this node.
    /// Getting the lifecycle always reflects the root's state —
    /// the whole tree moves together.
    /// </summary>
    public SimLifecycle Lifecycle {
        get => Parent?.Lifecycle ?? field;
        private set {
            // Only the root owns the lifecycle state
            if (Parent is not null)
                throw new InvalidOperationException(
                    $"Only the root node may set lifecycle. This node is '{Path}'."
                );
            field = value;
        }
    } = SimLifecycle.Building;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>Creates a root node with the given name.</summary>
    public SimNode(string name) {
        ValidateName(name);
        Name = name;
    }

    /// <summary>Creates a child node and attaches it to the given parent.</summary>
    public SimNode(string name, SimNode parent) {
        ValidateName(name);
        Name = name;
        AttachTo(parent);
    }

    // ── Tree Manipulation ────────────────────────────────────────────────────

    /// <summary>
    /// Adds a child node to this node.
    /// Only valid during <see cref="SimLifecycle.Building"/>.
    /// </summary>
    public void AddChild(SimNode child) {
        AssertLifecycle(SimLifecycle.Building, "add children");

        if (child.Parent is not null)
            throw new InvalidOperationException(
                $"Node '{child.Name}' already has a parent ('{child.Parent.Path}'). " +
                $"A node may only have one parent."
            );

        if (_children.Any(c => c.Name == child.Name))
            throw new InvalidOperationException(
                $"A child named '{child.Name}' already exists under '{Path}'."
            );

        child.Parent = this;
        _children.Add(child);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Finds a descendant node by dot-separated relative path.
    /// Returns null if not found.
    /// </summary>
    public SimNode? Find(string relativePath) {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        string[] parts = relativePath.Split('.', 2);
        SimNode? match = _children.FirstOrDefault(c => c.Name == parts[0]);

        if (match is null) return null;

        return parts.Length == 1 ? match : match.Find(parts[1]);
    }

    /// <summary>
    /// Finds a descendant node by dot-separated relative path.
    /// Throws if not found.
    /// </summary>
    public SimNode Require(string relativePath) =>
        Find(relativePath) ??
        throw new KeyNotFoundException(
            $"No node found at '{Path}.{relativePath}'."
        );

    /// <summary>
    /// Returns this node and all descendants in breadth-first order.
    /// </summary>
    public IEnumerable<SimNode> Descendants() {
        yield return this;
        foreach (SimNode? descendant in _children.SelectMany(child => child.Descendants())) yield return descendant;
    }

    // ── Lifecycle Transitions (root only) ────────────────────────────────────

    /// <summary>
    /// Advances the whole tree to <see cref="SimLifecycle.Finalizing"/>.
    /// Call this on the root once all nodes have been added.
    /// </summary>
    public void BeginFinalizing() {
        AssertIsRoot();
        AssertLifecycle(SimLifecycle.Building, "begin finalizing");
        Lifecycle = SimLifecycle.Finalizing;
    }

    /// <summary>
    /// Advances the whole tree to <see cref="SimLifecycle.Running"/>.
    /// Call this on the root once all arbors have been bound.
    /// </summary>
    public void BeginRunning() {
        AssertIsRoot();
        AssertLifecycle(SimLifecycle.Finalizing, "begin running");
        Lifecycle = SimLifecycle.Running;
    }

    /// <summary>
    /// Advances the whole tree to <see cref="SimLifecycle.Finished"/>.
    /// Call this on the root when the revolution is complete.
    /// </summary>
    public void BeginFinished() {
        AssertIsRoot();
        AssertLifecycle(SimLifecycle.Running, "finish");
        Lifecycle = SimLifecycle.Finished;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable tree dump, useful for debugging topology.
    /// </summary>
    public string DumpTree(int indent = 0) {
        var prefix = new string(' ', indent * 2);
        var sb = new StringBuilder();
        sb.AppendLine($"{prefix}{Name}  [{GetType().Name}]");
        foreach (SimNode child in _children) sb.Append(child.DumpTree(indent + 1));
        return sb.ToString();
    }

    public override string ToString() => Path;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AttachTo(SimNode parent) { parent.AddChild(this); }

    private bool IsRoot => Parent is null;

    private void AssertIsRoot() {
        if (!IsRoot)
            throw new InvalidOperationException(
                $"Lifecycle transitions must be called on the root node. " +
                $"This node is '{Path}'."
            );
    }

    /// <summary>
    /// Asserts the current lifecycle matches the expected state.
    /// Uses the root's lifecycle (the whole tree moves together).
    /// </summary>
    protected internal void AssertLifecycle(SimLifecycle expected, string action) {
        if (Lifecycle != expected)
            throw new InvalidOperationException(
                $"Cannot {action} on '{Path}': " +
                $"expected lifecycle '{expected}' but current is '{Lifecycle}'."
            );
    }

    private static void ValidateName(string name) {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("SimNode name must not be empty.", nameof(name));

        if (name.Contains('.'))
            throw new ArgumentException(
                $"SimNode name '{name}' must not contain '.'. " +
                $"Dots are path separators — use them in Find(), not in names.",
                nameof(name)
            );
    }

    /// <summary>
    /// Forces the lifecycle to a specific state without transition validation.
    /// Only the Train may call this — it is used to reset between Revolutions.
    /// Do not call this from anywhere else.
    /// </summary>
    internal void ForceLifecycle(SimLifecycle state) {
        if (Parent is not null)
            throw new InvalidOperationException(
                $"ForceLifecycle must be called on the root node. This node is '{Path}'."
            );
        Lifecycle = state;
    }
}