namespace Mechanism;

/// <summary>
/// The memory subsystem visible to the ISA executor.
///
/// Addresses are byte-addressed. Width is specified per-access.
/// The implementation handles alignment, endianness, and memory-mapped I/O.
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
}