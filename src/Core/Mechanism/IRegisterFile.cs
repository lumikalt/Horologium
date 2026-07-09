namespace Mechanism;

/// <summary>
/// A flat, indexed register file.
/// <para>
/// Register indices are ISA-defined. For RISC-V, index 0 is always zero.
/// The pipeline uses indices opaquely — it does not interpret their meaning.
/// </para>
/// </summary>
public interface IRegisterFile {
    /// <summary>The number of architectural registers.</summary>
    int Count { get; }

    /// <summary>The width of each register in bits (e.g. 32 or 64).</summary>
    int Width { get; }

    /// <summary>Reads the value of register at <paramref name="index"/>.</summary>
    ulong Read(int index);

    /// <summary>
    /// Writes <paramref name="value"/> to register at <paramref name="index"/>.
    /// Implementations may silently ignore writes to hardwired-zero registers.
    /// </summary>
    void Write(int index, ulong value);

    /// <summary>Resets all registers to their power-on default (typically zero).</summary>
    void Reset() { }
}