using Mechanism;

namespace Move.Decode;

public sealed class MoveInstruction : ITooth {
    public MoveInstruction(ulong pc, byte dst, byte src, ushort imm) {
        Pc = pc;
        Dst = dst;
        Src = src;
        Imm = imm;
        RawEncoding = (uint)((dst << 24) | (src << 16) | imm);
        Class = dst switch {
            0x20 => ToothClass.Load,
            0x22 => ToothClass.Store,
            0x31 => ToothClass.ConditionalBranch,
            0xFF => ToothClass.Branch,
            _    => ToothClass.IntegerAlu,
        };
    }

    public byte Dst { get; }
    public byte Src { get; }
    public ushort Imm { get; }
    public ulong Pc { get; }
    public uint RawEncoding { get; }
    public int SizeBytes => 4;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public object? Payload => null;
    public ToothClass Class { get; }
}