#region

using CommunityToolkit.Mvvm.ComponentModel;
using RiscV32.Config;
using RiscV32.Decode;

#endregion

namespace Face.Models;

public enum RegFormat {
    Hex,
    DecimalSigned,
    DecimalUnsigned,
    Binary,
    Float,
}

public enum PipelineMode {
    SingleCycle, FiveStage, OoO,
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

    [ObservableProperty] public partial string Stage { get; set; } = "";
}

public partial class ExtensionToggle(string name, RvExtension flag, bool enabled = true) : ObservableObject {
    public string Name { get; } = name;
    public RvExtension Flag { get; } = flag;
    [ObservableProperty] public partial bool IsEnabled { get; set; } = enabled;
}

public partial class RegEntry(string name) : ObservableObject {
    public string Name { get; } = name;

    [ObservableProperty] public partial string Display { get; set; } = "0x00000000";

    [ObservableProperty] public partial bool Changed { get; set; }
}