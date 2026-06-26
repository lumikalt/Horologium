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

public partial class AssemblyRow : ObservableObject {
    public ulong Offset { get; }
    public string OffsetHex => $"0x{Offset:X4}";
    public string HexEncoding { get; }
    public string Mnemonic { get; }
    public bool IsCompressed { get; }
    public IReadOnlyList<InstrField> Fields { get; }

    [ObservableProperty] private bool _isCurrent;

    public AssemblyRow(
        ulong offset,
        string hexEncoding,
        string mnemonic,
        bool isCompressed,
        IReadOnlyList<InstrField> fields
    ) {
        Offset = offset;
        HexEncoding = hexEncoding;
        Mnemonic = mnemonic;
        IsCompressed = isCompressed;
        Fields = fields;
    }
}

public partial class RegEntry : ObservableObject {
    public string Name { get; }

    [ObservableProperty] private string _display = "0x00000000";
    [ObservableProperty] private bool _changed;

    public RegEntry(string name) => Name = name;
}