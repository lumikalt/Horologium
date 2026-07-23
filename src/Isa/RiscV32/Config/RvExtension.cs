#region

using System.Text;

#endregion

namespace RiscV32.Config;

[Flags]
public enum RvExtension : uint {
    None = 0,
    M = 1u << 0,
    A = 1u << 1,
    F = 1u << 2,
    C = 1u << 3,
    V = 1u << 4,
    Zba = 1u << 5,
    Zbb = 1u << 6,
    Zbc = 1u << 7,
    Zbs = 1u << 8,
    Zicond = 1u << 9,
    Zabha = 1u << 10,
    Zacas = 1u << 11,
    Zihpm = 1u << 12,
    Zawrs = 1u << 13,
    Zicbom = 1u << 14,
    Zicboz = 1u << 15,
    Zcmop = 1u << 16,
    Zimop = 1u << 17,
    Zicntr = 1u << 18,

    Default
        = RvExtension.M | RvExtension.A | RvExtension.F | RvExtension.C | RvExtension.V | RvExtension.Zba
        | RvExtension.Zbb | RvExtension.Zbc | RvExtension.Zbs,

    All = RvExtension.Default | RvExtension.Zicond | RvExtension.Zabha | RvExtension.Zacas | RvExtension.Zihpm
        | RvExtension.Zawrs | RvExtension.Zicbom | RvExtension.Zicboz | RvExtension.Zcmop | RvExtension.Zimop
        | RvExtension.Zicntr,
}

public static class RvExtensionMethods {
    private static readonly (RvExtension Flag, string Name)[] ZExtensions = [
        (RvExtension.Zba, "zba"),
        (RvExtension.Zbb, "zbb"),
        (RvExtension.Zbc, "zbc"),
        (RvExtension.Zbs, "zbs"),
        (RvExtension.Zicond, "zicond"),
        (RvExtension.Zabha, "zabha"),
        (RvExtension.Zacas, "zacas"),
        (RvExtension.Zihpm, "zihpm"),
        (RvExtension.Zawrs, "zawrs"),
        (RvExtension.Zicbom, "zicbom"),
        (RvExtension.Zicboz, "zicboz"),
        (RvExtension.Zcmop, "zcmop"),
        (RvExtension.Zimop, "zimop"),
        (RvExtension.Zicntr, "zicntr"),
    ];

    /// <summary>
    ///     ISA string for GAS <c>-march=</c> or Spike <c>--isa=</c>.
    ///     Format: <c>rv{xlen}i[m][a][f][c][v][_z...]</c>
    /// </summary>
    public static string ToIsaString(this RvExtension ext, int xlen = 32) {
        var sb = new StringBuilder($"rv{xlen}i");
        if (ext.HasFlag(RvExtension.M)) sb.Append('m');
        if (ext.HasFlag(RvExtension.A)) sb.Append('a');
        if (ext.HasFlag(RvExtension.F)) sb.Append('f');
        if (ext.HasFlag(RvExtension.C)) sb.Append('c');
        if (ext.HasFlag(RvExtension.V)) sb.Append('v');
        foreach ((RvExtension flag, string name) in RvExtensionMethods.ZExtensions)
            if (ext.HasFlag(flag))
                sb.Append('_').Append(name);
        return sb.ToString();
    }

    /// <summary>GAS <c>-mabi=</c> value derived from the enabled extensions and XLEN.</summary>
    public static string ToGasAbi(this RvExtension ext, int xlen = 32) =>
        xlen == 64
            ? ext.HasFlag(RvExtension.F) ? "lp64f" : "lp64"
            : ext.HasFlag(RvExtension.F) ? "ilp32f" : "ilp32";
}