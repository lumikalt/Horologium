using System.Text;

namespace RiscV32.Decode;

public record InstrField(string Name, int HiBit, int LoBit, uint Value, string? Decoded = null) {
    public string BitsText {
        get {
            int width = HiBit - LoBit + 1;
            var sb = new StringBuilder(width);
            for (int i = width - 1; i >= 0; i--) sb.Append((Value >> i) & 1);
            return sb.ToString();
        }
    }

    public bool HasDecoded => Decoded != null;
}

public static class RvFieldInfo {
    private static readonly string[] IntAbi = [
        "zero", "ra", "sp", "gp", "tp", "t0", "t1", "t2",
        "s0", "s1", "a0", "a1", "a2", "a3", "a4", "a5",
        "a6", "a7", "s2", "s3", "s4", "s5", "s6", "s7",
        "s8", "s9", "s10", "s11", "t3", "t4", "t5", "t6",
    ];

    public static IReadOnlyList<InstrField> GetFields(uint raw) {
        if ((raw & 3) != 3) return GetCompressedFields((ushort)(raw & 0xFFFF));

        var opcode = (int)(raw & 0x7F);
        return opcode switch {
            0x37 or 0x17                 => UType(raw),
            0x6F                         => JType(raw),
            0x63                         => BType(raw),
            0x23 or 0x27                 => SType(raw),
            0x43 or 0x47 or 0x4B or 0x4F => R4Type(raw),
            0x33 or 0x2F or 0x53         => RType(raw),
            _                            => IType(raw),
        };
    }

    private static uint Bits(uint raw, int hi, int lo) => (raw >> lo) & ((1u << (hi - lo + 1)) - 1);

    private static InstrField F(string name, int hi, int lo, uint raw, string? decoded = null) =>
        new(name, hi, lo, Bits(raw, hi, lo), decoded);

    private static InstrField Reg(string name, int hi, int lo, uint raw) {
        uint idx = Bits(raw, hi, lo);
        return new InstrField(name, hi, lo, idx, RvFieldInfo.IntAbi[idx & 31]);
    }

    private static IReadOnlyList<InstrField> RType(uint raw) {
        uint op = raw & 0x7F;
        return [
            F("funct7", 31, 25, raw),
            Reg("rs2", 24, 20, raw),
            Reg("rs1", 19, 15, raw),
            F("funct3", 14, 12, raw),
            Reg("rd", 11, 7, raw),
            F("opcode", 6, 0, raw, OpcodeLabel(op)),
        ];
    }

    private static IReadOnlyList<InstrField> IType(uint raw) {
        uint op = raw & 0x7F;
        int immSigned = (int)raw >> 20;
        return [
            new InstrField("imm[11:0]", 31, 20, Bits(raw, 31, 20), immSigned.ToString()),
            Reg("rs1", 19, 15, raw),
            F("funct3", 14, 12, raw),
            Reg("rd", 11, 7, raw),
            F("opcode", 6, 0, raw, OpcodeLabel(op)),
        ];
    }

    private static IReadOnlyList<InstrField> SType(uint raw) {
        uint op = raw & 0x7F;
        uint hi = Bits(raw, 31, 25);
        uint lo = Bits(raw, 11, 7);
        var imm = (int)((uint)((int)((hi << 5) | lo) << 20) >> 20);
        return [
            new InstrField("imm[11:5]", 31, 25, hi, $"imm={imm}"),
            Reg("rs2", 24, 20, raw),
            Reg("rs1", 19, 15, raw),
            F("funct3", 14, 12, raw),
            new InstrField("imm[4:0]", 11, 7, lo),
            F("opcode", 6, 0, raw, OpcodeLabel(op)),
        ];
    }

    private static IReadOnlyList<InstrField> BType(uint raw) {
        var imm = (int)(
            (Bits(raw, 31, 31) << 12) |
            (Bits(raw, 7, 7) << 11) |
            (Bits(raw, 30, 25) << 5) |
            (Bits(raw, 11, 8) << 1));
        imm = (int)((uint)imm << 19) >> 19;
        return [
            new InstrField("imm[12|10:5]", 31, 25, Bits(raw, 31, 25), $"imm={imm}"),
            Reg("rs2", 24, 20, raw),
            Reg("rs1", 19, 15, raw),
            F("funct3", 14, 12, raw),
            new InstrField("imm[4:1|11]", 11, 7, Bits(raw, 11, 7)),
            F("opcode", 6, 0, raw, "BRANCH"),
        ];
    }

    private static IReadOnlyList<InstrField> UType(uint raw) {
        uint op = raw & 0x7F;
        return [
            new InstrField("imm[31:12]", 31, 12, Bits(raw, 31, 12), $"0x{raw >> 12:X}"),
            Reg("rd", 11, 7, raw),
            F("opcode", 6, 0, raw, op == 0x37 ? "LUI" : "AUIPC"),
        ];
    }

    private static IReadOnlyList<InstrField> JType(uint raw) {
        var imm = (int)(
            (Bits(raw, 31, 31) << 20) |
            (Bits(raw, 19, 12) << 12) |
            (Bits(raw, 20, 20) << 11) |
            (Bits(raw, 30, 21) << 1));
        imm = (int)((uint)imm << 11) >> 11;
        return [
            new InstrField("imm[20|10:1|11|19:12]", 31, 12, Bits(raw, 31, 12), $"imm={imm}"),
            Reg("rd", 11, 7, raw),
            F("opcode", 6, 0, raw, "JAL"),
        ];
    }

    private static IReadOnlyList<InstrField> R4Type(uint raw) => [
        Reg("rs3", 31, 27, raw),
        F("fmt", 26, 25, raw),
        Reg("rs2", 24, 20, raw),
        Reg("rs1", 19, 15, raw),
        F("rm", 14, 12, raw),
        Reg("rd", 11, 7, raw),
        F("opcode", 6, 0, raw),
    ];

    private static IReadOnlyList<InstrField> GetCompressedFields(ushort c) {
        var quad = (uint)(c & 0x3);
        var funct3 = (uint)(c >> 13);
        return [
            new InstrField("funct3", 15, 13, funct3),
            new InstrField("enc[12:2]", 12, 2, (uint)((c >> 2) & 0x7FF)),
            new InstrField("quad", 1, 0, quad, $"Q{quad}"),
        ];
    }

    private static string OpcodeLabel(uint op) => op switch {
        0x03 => "LOAD", 0x07     => "LOAD-FP", 0x0F => "FENCE",
        0x13 => "OP-IMM", 0x17   => "AUIPC", 0x23   => "STORE",
        0x27 => "STORE-FP", 0x2F => "AMO", 0x33     => "OP",
        0x37 => "LUI", 0x43      => "MADD", 0x47    => "MSUB",
        0x4B => "NMSUB", 0x4F    => "NMADD", 0x53   => "OP-FP",
        0x57 => "OP-V", 0x63     => "BRANCH", 0x67  => "JALR",
        0x6F => "JAL", 0x73      => "SYSTEM",
        _    => $"0x{op:X2}",
    };
}