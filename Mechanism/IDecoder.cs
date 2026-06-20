namespace Mechanism;

/// <summary>
/// Decodes raw instruction bytes into an IInstruction.
///
/// The decoder is stateless — the same bytes at the same PC always
/// produce the same instruction. All ISA-specific decode logic lives here.
/// </summary>
public interface IDecoder {
    /// <summary>
    /// Decodes the instruction at <paramref name="pc"/> from <paramref name="memory"/>.
    /// </summary>
    /// <returns>The decoded instruction.</returns>
    /// <exception cref="IllegalInstructionException">
    /// Thrown if the bytes do not form a legal instruction.
    /// </exception>
    IInstruction Decode(ulong pc, IMemory memory);

    /// <summary>
    /// The number of bytes consumed by the instruction at <paramref name="pc"/>.
    /// For fixed-width ISAs this is always the same value.
    /// For variable-width ISAs (e.g. RVC) this must be called before Decode.
    /// </summary>
    int InstructionSize(ulong pc, IMemory memory);
}

/// <summary>
/// Thrown when bytes at a given PC do not form a legal instruction.
/// The pipeline converts this into a trap via ITrapController.
/// </summary>
public sealed class IllegalInstructionException(ulong pc, uint encoding, string message)
    : Exception(message) {
    public ulong Pc { get; } = pc;
    public uint Encoding { get; } = encoding;
}