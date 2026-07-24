#region

using Mechanism;

#endregion

namespace Move.Decode;

public sealed class MoveInstruction(ulong pc, byte dst, byte src, ushort imm)
    : ITooth {
    public byte Dst { get; } = dst;
    public byte Src { get; } = src;
    public ushort Imm { get; } = imm;
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = (uint)((dst << 24) | (src << 16) | imm);
    public int SizeBytes => 4;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public object? Payload => null;

    public ToothClass Class { get; } = dst switch {
        0x20 => ToothClass.Load,
        0x22 => ToothClass.Store,
        0x31 => ToothClass.ConditionalBranch,
        0xFF => ToothClass.Branch,
        _    => ToothClass.IntegerAlu,
    };
}