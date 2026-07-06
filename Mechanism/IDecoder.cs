namespace Mechanism;

/// <summary>
/// Decodes raw instruction bytes into an ITooth.
/// <para>
/// The decoder is stateless — the same bytes at the same PC always
/// produce the same instruction. All ISA-specific decode logic lives here.
/// </para>
/// </summary>
public interface IDecoder {
    /// <summary>
    /// Decodes the instruction at <paramref name="pc"/> from <paramref name="memory"/>.
    /// </summary>
    /// <returns>The decoded instruction.</returns>
    /// <exception cref="IllegalInstructionException">
    /// Thrown if the bytes do not form a legal instruction.
    /// </exception>
    ITooth Decode(ulong pc, IMemory memory);

    /// <summary>
    /// Decodes the instruction at <paramref name="pc"/> from <paramref name="raw"/>.
    /// </summary>
    /// <param name="pc">Program counter.</param>
    /// <param name="raw">
    /// Raw instruction bytes.
    /// </param>
    /// <returns></returns>
    ITooth Decode(ulong pc, uint raw);

    /// <summary>
    /// The number of bytes consumed by the instruction at <paramref name="pc"/>.
    /// For fixed-width ISAs this is always the same value.
    /// For variable-width ISAs (e.g. RVC) this must be called before Decode.
    /// </summary>
    int InstructionSize(ulong pc, IMemory memory);

    /// <summary>
    /// Returns a lightweight hint about the instruction at <paramref name="pc"/> using only
    /// the first word already fetched, without a full decode.
    /// Used by the fetch stage for branch classification and RAS management.
    /// </summary>
    FetchHint GetFetchHint(ulong pc, uint firstWord);

    /// <summary>
    /// Returns a human-readable disassembly string for the instruction.
    /// ISAs that do not override this return a hex fallback.
    /// </summary>
    virtual string Disassemble(ulong pc, uint raw) => $"0x{raw:X8}";
}

/// <summary>
/// Thrown when bytes at a given PC do not form a legal instruction.
/// The pipeline converts this into a trap via ITrapController.
/// </summary>
public sealed class IllegalInstructionException(uint encoding, string message)
    : Exception(message) {
    /// <summary>
    /// The raw encoding of the illegal instruction.
    /// </summary>
    public uint Encoding { get; } = encoding;
}