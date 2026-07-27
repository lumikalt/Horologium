namespace Mechanism;

/// <summary>
///     The top-level ISA plugin.
///     <para>
///         A Mechanism is a factory and registry for all ISA-specific components.
///         The Train receives one Mechanism and uses it to construct the pipeline.
///         Swapping the Mechanism swaps the entire ISA without touching the Train.
///     </para>
/// </summary>
public interface IMechanism {
    /// <summary>A human-readable name for this ISA (e.g. "RV32I", "RV64GC").</summary>
    string Name { get; }

    /// <summary>The instruction decoder for this ISA.</summary>
    IDecoder Decoder { get; }

    /// <summary>The instruction executor for this ISA.</summary>
    IExecutor Executor { get; }

    /// <summary>
    ///     The µop cracker for this ISA, or null if this ISA does not
    ///     support cracking (i.e. all instructions are already atomic).
    /// </summary>
    IImpulseCracker? UopCracker { get; }

    /// <summary>
    ///     The macro-op fuser for this ISA, or null if this ISA/configuration does not
    ///     fuse instruction pairs. Consumed only by trains that model a per-cycle
    ///     issue-width cap where fusion has an observable effect (see
    ///     <see cref="IMacroFuser" />).
    /// </summary>
    IMacroFuser? MacroFuser => null;

    /// <summary>The trap controller for this ISA.</summary>
    ITrapController TrapController { get; }

    /// <summary>
    ///     The syscall handler wired into this mechanism's executor, or null if this ISA/instance
    ///     does not use syscall emulation (e.g. bare-metal HTIF workloads, or ISAs with no
    ///     syscall convention at all).
    /// </summary>
    ISyscallHandler? SyscallHandler => null;

    /// <summary>Creates a fresh architectural state for a new hart.</summary>
    IArchState CreateArchState();

    /// <summary>
    ///     Returns a fetch translator bound to the given state and memory,
    ///     or null if this ISA does not support virtual addressing.
    /// </summary>
    IFetchTranslator? CreateFetchTranslator(IArchState state, IMemory memory) => null;
}