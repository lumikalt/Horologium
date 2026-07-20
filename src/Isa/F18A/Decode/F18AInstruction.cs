#region

using Mechanism;

#endregion

namespace F18A.Decode;

public sealed class F18AInstruction(ulong pc, uint raw) : ITooth {
    // Slot opcodes extracted from the 18-bit word (5+5+5+3 bits)
    public byte Slot0 => (byte)((raw >> 13) & 0x1F);
    public byte Slot1 => (byte)((raw >> 8) & 0x1F);
    public byte Slot2 => (byte)((raw >> 3) & 0x1F);
    public byte Slot3 => (byte)(raw & 0x07);

    // Address field after each slot, masked to 9-bit word address
    public uint AddrAfterSlot0 => raw & 0x1FFF & 0x1FFu; // 13 bits
    public uint AddrAfterSlot1 => raw & 0x00FF & 0x1FFu; // 8 bits
    public uint AddrAfterSlot2 => raw & 0x0007 & 0x1FFu; // 3 bits
    public ulong Pc => pc;
    public uint RawEncoding => raw;
    public int SizeBytes => 4;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public object? Payload => null;
    public ToothClass Class { get; init; } = ToothClass.IntegerAlu;
}