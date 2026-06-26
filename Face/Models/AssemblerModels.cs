using CommunityToolkit.Mvvm.ComponentModel;
using RiscV.Decode;

namespace Face.Models;

public enum RegFormat {
    Hex,
    DecimalSigned,
    DecimalUnsigned,
    Binary,
    Float,
}

public partial class AssemblyRow(
    ulong offset,
    string hexEncoding,
    string mnemonic,
    bool isCompressed,
    IReadOnlyList<InstrField> fields
)
    : ObservableObject {
    public ulong Offset { get; } = offset;
    public string OffsetHex => $"{Offset:X}";
    public string HexEncoding { get; } = hexEncoding;
    public string Mnemonic { get; } = mnemonic;
    public bool IsCompressed { get; } = isCompressed;
    public IReadOnlyList<InstrField> Fields { get; } = fields;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

public partial class RegEntry(string name) : ObservableObject {
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Display { get; set; } = "0x00000000";

    [ObservableProperty]
    public partial bool Changed { get; set; }
}