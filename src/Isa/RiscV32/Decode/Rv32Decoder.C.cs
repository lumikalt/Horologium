using Mechanism;

namespace RiscV32.Decode;

public partial class Rv32Decoder {
    // ── C extension (16-bit compressed instructions) ─────────────────────────

    // Factory for a 2-byte instruction that expands to an existing RvOp.
    private static RvInstruction C(
        ulong pc,
        ushort raw,
        int dest,
        IReadOnlyList<int> sources,
        ToothClass cls,
        RvOp op
    ) =>
        new(pc, raw, dest, sources, cls, op, 2);

    protected static ITooth DecodeCompressed(ulong pc, ushort c) {
        var q = (uint)(c & 0x3);
        var funct3 = (uint)(c >> 13);
        return q switch {
            0x0 => DecodeCompressedQ0(pc, c, funct3),
            0x1 => DecodeCompressedQ1(pc, c, funct3),
            0x2 => DecodeCompressedQ2(pc, c, funct3),
            _ => throw new IllegalInstructionException(
                c, "Compressed opcode with quadrant 0x3 is a 32-bit instruction"
            ),
        };
    }

    // ── Quadrant 0 ────────────────────────────────────────────────────────────

    private static ITooth DecodeCompressedQ0(ulong pc, ushort c, uint funct3) {
        int rdp = ((c >> 2) & 0x7) + 8;  // rd'  → x8–x15
        int rs1P = ((c >> 7) & 0x7) + 8; // rs1' → x8–x15

        return funct3 switch {
            0x0 => DecodeAddi4Spn(pc, c, rdp),
            0x1 => DecodeCFld(pc, c, rdp, rs1P),
            0x2 => DecodeCLw(pc, c, rdp, rs1P),
            0x3 => DecodeCFlw(pc, c, rdp, rs1P),
            0x5 => DecodeCFsd(pc, c, rdp, rs1P),
            0x6 => DecodeCSw(pc, c, rdp, rs1P),
            0x7 => DecodeCFsw(pc, c, rdp, rs1P),
            _   => throw new IllegalInstructionException(c, $"Unknown C.Q0 funct3=0x{funct3:X}"),
        };
    }

    // Doubleword CL/CS-format offset (shared by C.FLD/C.FSD, and — on RV64 — C.LD/C.SD):
    // uimm[5:3]=c[12:10], uimm[7:6]=c[6:5].
    private static int CDoublewordMemImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) | (((c >> 5) & 0x3) << 6);

    private static RvInstruction DecodeCFld(ulong pc, ushort c, int rdp, int rs1P) =>
        C(pc, c, rdp + 32, [rs1P,], ToothClass.Load, new RvFld(rdp + 32, rs1P, CDoublewordMemImm(c)));

    private static RvInstruction DecodeCFsd(ulong pc, ushort c, int rs2P, int rs1P) =>
        C(pc, c, -1, [rs1P, rs2P + 32,], ToothClass.Store, new RvFsd(rs1P, rs2P + 32, CDoublewordMemImm(c)));

    private static ITooth DecodeAddi4Spn(ulong pc, ushort c, int rdp) {
        // CIW: nzuimm[5:4]=c[12:11], nzuimm[9:6]=c[10:7], nzuimm[2]=c[6], nzuimm[3]=c[5]
        int nzuimm = (((c >> 11) & 0x3) << 4)
                   | (((c >> 7) & 0xF) << 6)
                   | (((c >> 6) & 0x1) << 2)
                   | (((c >> 5) & 0x1) << 3);
        return nzuimm == 0
            ? throw new IllegalInstructionException(c, "C.ADDI4SPN with nzuimm=0 is reserved")
            : C(pc, c, rdp, [2,], ToothClass.IntegerAlu, new RvAddi(rdp, 2, nzuimm));
    }

    private static int ClMemImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) // c[12:10] → uimm[5:3]
      | (((c >> 6) & 0x1) << 2)  // c[6]     → uimm[2]
      | (((c >> 5) & 0x1) << 6); // c[5]     → uimm[6]

    private static RvInstruction DecodeCLw(ulong pc, ushort c, int rdp, int rs1P) =>
        C(pc, c, rdp, [rs1P,], ToothClass.Load, new RvLw(rdp, rs1P, ClMemImm(c)));

    private static RvInstruction DecodeCFlw(ulong pc, ushort c, int rdp, int rs1P) =>
        C(pc, c, rdp + 32, [rs1P,], ToothClass.Load, new RvFlw(rdp + 32, rs1P, ClMemImm(c)));

    private static RvInstruction DecodeCSw(ulong pc, ushort c, int rs2P, int rs1P) =>
        C(pc, c, -1, [rs1P, rs2P,], ToothClass.Store, new RvSw(rs1P, rs2P, ClMemImm(c)));

    private static RvInstruction DecodeCFsw(ulong pc, ushort c, int rs2P, int rs1P) =>
        C(pc, c, -1, [rs1P, rs2P + 32,], ToothClass.Store, new RvFsw(rs1P, rs2P + 32, ClMemImm(c)));

    // ── Quadrant 1 ────────────────────────────────────────────────────────────

    private static RvInstruction DecodeCompressedQ1(ulong pc, ushort c, uint funct3) {
        int rd = (c >> 7) & 0x1F;
        int rs1P = ((c >> 7) & 0x7) + 8; // for CB-type restricted registers
        int ci6Imm = SignExtendN((((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F), 6);

        return funct3 switch {
            0x0 => C(pc, c, rd, [rd,], ToothClass.IntegerAlu, new RvAddi(rd, rd, ci6Imm)), // C.NOP / C.ADDI
            0x1 => DecodeCJal(pc, c), // C.JAL (RV32 only) → JAL x1, offset
            0x2 => C(pc, c, rd, [], ToothClass.IntegerAlu, new RvAddi(rd, 0, ci6Imm)), // C.LI → ADDI rd, x0, imm
            0x3 => DecodeQ1Funct3_011(pc, c, rd, ci6Imm),
            0x4 => DecodeQ1Funct3_100(pc, c, rs1P, ci6Imm),
            0x5 => DecodeCj(pc, c), // C.J → JAL x0, offset
            0x6 => C(pc, c, -1, [rs1P, 0,], ToothClass.ConditionalBranch, new RvBeq(rs1P, 0, CBranchImm(c))), // C.BEQZ
            0x7 => C(pc, c, -1, [rs1P, 0,], ToothClass.ConditionalBranch, new RvBne(rs1P, 0, CBranchImm(c))), // C.BNEZ
            _   => throw new IllegalInstructionException(c, $"Unknown C.Q1 funct3=0x{funct3:X}"),
        };
    }

    private static int CJumpOffset(ushort c) {
        // CJ-format: offset[11]=c[12], [4]=c[11], [9:8]=c[10:9], [10]=c[8], [6]=c[7], [7]=c[6], [3:1]=c[5:3], [5]=c[2]
        int offset = (((c >> 12) & 0x1) << 11)
                   | (((c >> 11) & 0x1) << 4)
                   | (((c >> 9) & 0x3) << 8)
                   | (((c >> 8) & 0x1) << 10)
                   | (((c >> 7) & 0x1) << 6)
                   | (((c >> 6) & 0x1) << 7)
                   | (((c >> 3) & 0x7) << 1)
                   | (((c >> 2) & 0x1) << 5);
        return SignExtendN(offset, 12);
    }

    private static int CBranchImm(ushort c) {
        // CB-format: offset[8]=c[12], [4:3]=c[11:10], [7:6]=c[6:5], [2:1]=c[4:3], [5]=c[2]
        int offset = (((c >> 12) & 0x1) << 8)
                   | (((c >> 10) & 0x3) << 3)
                   | (((c >> 5) & 0x3) << 6)
                   | (((c >> 3) & 0x3) << 1)
                   | (((c >> 2) & 0x1) << 5);
        return SignExtendN(offset, 9);
    }

    private static RvInstruction DecodeCJal(ulong pc, ushort c) =>
        C(pc, c, 1, [], ToothClass.Branch, new RvJal(1, CJumpOffset(c)));

    private static RvInstruction DecodeCj(ulong pc, ushort c) =>
        C(pc, c, 0, [], ToothClass.Branch, new RvJal(0, CJumpOffset(c)));

    private static RvInstruction DecodeQ1Funct3_011(ulong pc, ushort c, int rd, int _) {
        // Zcmop: c.mop.N — 8 NOP hints encoded with nzimm=0 and rd ∈ {1,3,5,7,9,11,13,15}.
        // Pattern: bits[15:13]=011, bit[12]=0, bit[11]=0, bits[7:0]=0x81, bits[10:8]=N_index.
        // N = 2*N_index + 1 ∈ {1,3,5,...,15}.
        if ((c & 0xF8FF) == 0x6081) {
            int n = (((c >> 8) & 7) << 1) | 1; // N = 2*(bits[10:8]) + 1
            return C(pc, c, -1, [], ToothClass.IntegerAlu, new RvCMopN(n));
        }

        if (rd == 2) {
            // C.ADDI16SP: nzimm[9]=c[12], [4]=c[6], [6]=c[5], [8:7]=c[4:3], [5]=c[2]
            int nzimm = (((c >> 12) & 0x1) << 9)
                      | (((c >> 6) & 0x1) << 4)
                      | (((c >> 5) & 0x1) << 6)
                      | (((c >> 3) & 0x3) << 7)
                      | (((c >> 2) & 0x1) << 5);
            nzimm = SignExtendN(nzimm, 10);
            return nzimm == 0
                ? throw new IllegalInstructionException(c, "C.ADDI16SP with nzimm=0 is reserved")
                : C(pc, c, 2, [2,], ToothClass.IntegerAlu, new RvAddi(2, 2, nzimm));
        }

        // C.LUI: nzimm[17]=c[12], nzimm[16:12]=c[6:2] → placed at bits [17:12]
        int raw6 = (((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F);
        int nzimmLui = SignExtendN(raw6, 6) << 12;
        return nzimmLui == 0
            ? throw new IllegalInstructionException(c, "C.LUI with nzimm=0 is reserved")
            : C(pc, c, rd, [], ToothClass.IntegerAlu, new RvLui(rd, nzimmLui));
    }

    private static RvInstruction DecodeQ1Funct3_100(ulong pc, ushort c, int rs1P, int ci6Imm) {
        int sub = (c >> 10) & 0x3;
        int shamt = (((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F);

        switch (sub) {
            // C.SRLI → SRLI rs1', rs1', shamt
            case 0x0 when (shamt & 0x20) != 0:
                throw new IllegalInstructionException(c, "C.SRLI with shamt[5]=1 is reserved for RV32");
            case 0x0: return C(pc, c, rs1P, [rs1P,], ToothClass.IntegerAlu, new RvSrli(rs1P, rs1P, shamt));
            // C.SRAI → SRAI rs1', rs1', shamt
            case 0x1 when (shamt & 0x20) != 0:
                throw new IllegalInstructionException(c, "C.SRAI with shamt[5]=1 is reserved for RV32");
            case 0x1: return C(pc, c, rs1P, [rs1P,], ToothClass.IntegerAlu, new RvSrai(rs1P, rs1P, shamt));
            // C.ANDI → ANDI rs1', rs1', imm
            case 0x2: return C(pc, c, rs1P, [rs1P,], ToothClass.IntegerAlu, new RvAndi(rs1P, rs1P, ci6Imm));
        }

        // sub == 0x3: CA-type arithmetic
        if ((c & 0x1000) != 0) throw new IllegalInstructionException(c, "C.SUB/XOR/OR/AND with c[12]=1 is reserved");
        int rs2P = ((c >> 2) & 0x7) + 8;
        return ((c >> 5) & 0x3) switch {
            0x0 => C(pc, c, rs1P, [rs1P, rs2P,], ToothClass.IntegerAlu, new RvSub(rs1P, rs1P, rs2P)),
            0x1 => C(pc, c, rs1P, [rs1P, rs2P,], ToothClass.IntegerAlu, new RvXor(rs1P, rs1P, rs2P)),
            0x2 => C(pc, c, rs1P, [rs1P, rs2P,], ToothClass.IntegerAlu, new RvOr(rs1P, rs1P, rs2P)),
            0x3 => C(pc, c, rs1P, [rs1P, rs2P,], ToothClass.IntegerAlu, new RvAnd(rs1P, rs1P, rs2P)),
            _   => throw new IllegalInstructionException(c, $"Unknown CA funct2=0x{(c >> 5) & 0x3:X}"),
        };
    }

    // ── Quadrant 2 ────────────────────────────────────────────────────────────

    private static RvInstruction DecodeCompressedQ2(ulong pc, ushort c, uint funct3) {
        int rd = (c >> 7) & 0x1F;
        int rs2 = (c >> 2) & 0x1F;

        return funct3 switch {
            0x0 => DecodeCslli(pc, c, rd, rs2),
            0x1 => DecodeCFldsp(pc, c, rd),
            0x2 => DecodeCLwsp(pc, c, rd),
            0x3 => DecodeCFlwsp(pc, c, rd),
            0x4 => DecodeQ2Funct3_100(pc, c, rd, rs2),
            0x5 => DecodeCFsdsp(pc, c, rs2),
            0x6 => DecodeCSwsp(pc, c, rs2),
            0x7 => DecodeCFswsp(pc, c, rs2),
            _   => throw new IllegalInstructionException(c, $"Unknown C.Q2 funct3=0x{funct3:X}"),
        };
    }

    // CI-format C.FLDSP offset: uimm[5]=c[12], uimm[4:3]=c[6:5], uimm[8:6]=c[4:2].
    private static int CFldspImm(ushort c) =>
        (((c >> 12) & 0x1) << 5) | (((c >> 5) & 0x3) << 3) | (((c >> 2) & 0x7) << 6);

    private static RvInstruction DecodeCFldsp(ulong pc, ushort c, int rd) =>
        C(pc, c, rd + 32, [2,], ToothClass.Load, new RvFld(rd + 32, 2, CFldspImm(c)));

    // CSS-format C.FSDSP offset: uimm[5:3]=c[12:10], uimm[8:6]=c[9:7].
    private static int CFsdspImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) | (((c >> 7) & 0x7) << 6);

    private static RvInstruction DecodeCFsdsp(ulong pc, ushort c, int rs2) =>
        C(pc, c, -1, [2, rs2 + 32,], ToothClass.Store, new RvFsd(2, rs2 + 32, CFsdspImm(c)));

    private static RvInstruction DecodeCslli(ulong pc, ushort c, int rd, int rs2) {
        int shamt = (((c >> 12) & 0x1) << 5) | rs2;
        return (shamt & 0x20) != 0
            ? throw new IllegalInstructionException(c, "C.SLLI with shamt[5]=1 is reserved for RV32")
            : C(pc, c, rd, [rd,], ToothClass.IntegerAlu, new RvSlli(rd, rd, shamt));
    }

    private static int ClwspImm(ushort c) =>
        (((c >> 12) & 0x1) << 5) // c[12] → uimm[5]
      | (((c >> 4) & 0x7) << 2)  // c[6:4] → uimm[4:2]
      | (((c >> 2) & 0x3) << 6); // c[3:2] → uimm[7:6]

    private static RvInstruction DecodeCLwsp(ulong pc, ushort c, int rd) => rd == 0
        ? throw new IllegalInstructionException(c, "C.LWSP with rd=x0 is reserved")
        : C(pc, c, rd, [2,], ToothClass.Load, new RvLw(rd, 2, ClwspImm(c)));

    private static RvInstruction DecodeCFlwsp(ulong pc, ushort c, int rd) =>
        C(pc, c, rd + 32, [2,], ToothClass.Load, new RvFlw(rd + 32, 2, ClwspImm(c)));

    private static int CswspImm(ushort c) =>
        (((c >> 9) & 0xF) << 2)  // c[12:9] → uimm[5:2]
      | (((c >> 7) & 0x3) << 6); // c[8:7] → uimm[7:6]

    private static RvInstruction DecodeCSwsp(ulong pc, ushort c, int rs2) =>
        C(pc, c, -1, [2, rs2,], ToothClass.Store, new RvSw(2, rs2, CswspImm(c)));

    private static RvInstruction DecodeCFswsp(ulong pc, ushort c, int rs2) =>
        C(pc, c, -1, [2, rs2 + 32,], ToothClass.Store, new RvFsw(2, rs2 + 32, CswspImm(c)));

    private static RvInstruction DecodeQ2Funct3_100(ulong pc, ushort c, int rd, int rs2) {
        switch ((c & 0x1000) != 0) {
            case false when rs2 == 0: {
                // C.JR → JALR x0, 0(rs1)
                return rd == 0
                    ? throw new IllegalInstructionException(c, "C.JR with rs1=x0 is reserved")
                    : C(pc, c, 0, [rd,], ToothClass.Branch, new RvJalr(0, rd, 0));
            }
            // C.MV → ADD rd, x0, rs2
            case false: return C(pc, c, rd, [0, rs2,], ToothClass.IntegerAlu, new RvAdd(rd, 0, rs2));
        }

        if (rd == 0 && rs2 == 0)
            // C.EBREAK
            return C(pc, c, -1, [], ToothClass.Halt, new RvEbreak());
        return rs2 == 0
            ?
            // C.JALR → JALR x1, 0(rs1)
            C(pc, c, 1, [rd,], ToothClass.Branch, new RvJalr(1, rd, 0))
            :
            // C.ADD → ADD rd, rd, rs2
            C(pc, c, rd, [rd, rs2,], ToothClass.IntegerAlu, new RvAdd(rd, rd, rs2));
    }
}