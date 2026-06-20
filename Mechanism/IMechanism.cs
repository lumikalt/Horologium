namespace Mechanism;

/// <summary>
/// The top-level ISA plugin.
///
/// A Mechanism is a factory and registry for all ISA-specific components.
/// The Train receives one Mechanism and uses it to construct the pipeline.
/// Swapping the Mechanism swaps the entire ISA without touching the Train.
/// </summary>
public interface IMechanism {
    /// <summary>A human-readable name for this ISA (e.g. "RV32I", "RV64GC").</summary>
    string Name { get; }

    /// <summary>Creates a fresh architectural state for a new hart.</summary>
    IArchState CreateArchState();

    /// <summary>The instruction decoder for this ISA.</summary>
    IDecoder Decoder { get; }

    /// <summary>The instruction executor for this ISA.</summary>
    IExecutor Executor { get; }

    /// <summary>
    /// The µop cracker for this ISA, or null if this ISA does not
    /// support cracking (i.e. all instructions are already atomic).
    /// </summary>
    IUopCracker? UopCracker { get; }

    /// <summary>The trap controller for this ISA.</summary>
    ITrapController TrapController { get; }
}