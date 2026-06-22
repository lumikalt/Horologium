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
}