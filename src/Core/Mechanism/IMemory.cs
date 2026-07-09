namespace Mechanism;

/// <summary>
/// The memory subsystem visible to the ISA executor.
/// <para>
/// Addresses are byte-addressed. Width is specified per-access.
/// The implementation handles alignment, endianness, and memory-mapped I/O.
/// </para>
/// </summary>
public interface IMemory {
    /// <summary>Reads <paramref name="bytes"/> bytes from <paramref name="address"/>.</summary>
    ulong Read(ulong address, int bytes);

    /// <summary>Writes <paramref name="value"/> (<paramref name="bytes"/> wide) to <paramref name="address"/>.</summary>
    void Write(ulong address, ulong value, int bytes);

    /// <summary>
    /// Loads a byte array into memory starting at <paramref name="address"/>.
    /// Used to initialise memory with a program image before a Revolution.
    /// </summary>
    void Load(ulong address, ReadOnlySpan<byte> data);

    /// <summary>
    /// Invalidates the cache block containing <paramref name="address"/>, writing it
    /// back to backing first if it is dirty (conservative interpretation of cbo.inval).
    /// No-op on non-cache implementations.
    /// </summary>
    void InvalidateLine(ulong address) { }

    /// <summary>
    /// Writes back the cache block containing <paramref name="address"/> if it is dirty,
    /// leaving it valid in the cache (cbo.clean).
    /// No-op on non-cache implementations.
    /// </summary>
    void CleanLine(ulong address) { }

    /// <summary>
    /// Writes back the cache block containing <paramref name="address"/> if it is dirty,
    /// then invalidates it (cbo.flush).
    /// No-op on non-cache implementations.
    /// </summary>
    void FlushLine(ulong address) { }

    /// <summary>
    /// Notifies the memory subsystem of the PC of the instruction about to issue
    /// a memory request.  Used by SHiP-PC to index the SHCT by load PC rather than
    /// by memory address.  Must be called before the corresponding Read/Write.
    /// No-op on implementations that do not use PC-based signatures.
    /// </summary>
    void SetRequestPc(ulong pc) { }
}