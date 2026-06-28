namespace Mechanism;

/// <summary>
/// A program image that can be loaded into a fresh memory for each simulation run.
/// Separates the workload (what to run) from the hardware configuration (how to run it).
/// </summary>
public interface IWorkload {
    /// <summary>Program counter value at the start of execution.</summary>
    ulong EntryPoint { get; }

    /// <summary>Minimum memory size in bytes required by this workload.</summary>
    int MemorySize { get; }

    /// <summary>Loads the program image into <paramref name="memory"/>.</summary>
    void Load(IMemory memory);

    /// <summary>
    /// Size of the executable code in bytes, used to auto-estimate snapshot intervals.
    /// Implementations that can determine this cheaply should override it.
    /// Default returns 0 (disables auto-estimation).
    /// </summary>
    int CodeSize => 0;

    /// <summary>
    /// The base physical address of this workload's memory region. Pass this to
    /// FlatMemory so that ELF-style high addresses (e.g. 0x80000000) map to
    /// array index 0 rather than requiring a 2 GB allocation.
    /// Default is 0 for zero-based images.
    /// </summary>
    ulong BaseAddress => 0;

    /// <summary>
    /// Wraps a freshly-loaded memory with any workload-specific peripheral emulation
    /// (e.g. HTIF auto-ACK for RISC-V benchmark ELFs). The default is a no-op pass-through.
    /// </summary>
    IMemory WrapMemory(IMemory memory) => memory;

    /// <summary>
    /// Address of the HTIF <c>tohost</c> exit register, if this workload terminates
    /// via HTIF. Pass to the mechanism so an exit-code store halts the run at the
    /// write itself (first-class termination) rather than relying on the spin-loop
    /// that follows. Null for workloads that do not use HTIF (the common case).
    /// </summary>
    ulong? HtifTohostAddress => null;
}