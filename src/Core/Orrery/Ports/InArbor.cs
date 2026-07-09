using Orrery.Tree;

namespace Orrery.Ports;

/// <summary>
/// The receiving end of a typed communication channel between Gears.
/// Holds a callback that fires when data arrives.
/// </summary>
public sealed class InArbor<T> {
    /// <summary>
    /// The callback invoked when data is delivered to this arbor.
    /// Must be set before the simulation begins running.
    /// </summary>
    public Action<T>? OnReceive { get; set; }

    public string Name { get; }

    /// <summary>Full qualified name: ownerPath.name, e.g., top.cpu.decode.in_instructions</summary>
    public string FullName => $"{field}.{Name}";

    // Used by tests and standalone arbors that aren't owned by a Gear
    public InArbor(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        FullName = "(unowned)";
    }

    // Used by Gear.AddInArbor — carries the owner's tree path
    internal InArbor(string name, SimNode owner) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(owner);
        Name = name;
        FullName = owner.Path;
    }

    internal void Deliver(T data) =>
        (OnReceive ?? throw new InvalidOperationException(
            $"InArbor '{FullName}' received data but has no OnReceive handler set."
        ))
       .Invoke(data);
}