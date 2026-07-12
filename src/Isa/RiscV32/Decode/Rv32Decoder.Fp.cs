using Mechanism;

namespace RiscV32.Decode;

public partial class Rv32Decoder {
    // ── F extension ───────────────────────────────────────────────────────────

    // LOAD-FP / vector load: opcode=0x07.
    // funct3=2 → FLW; funct3=3 → FLD; funct3=0/5/6 → VLE8/16/32.
    private static RvInstruction DecodeFpOrVLoad(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        int imm = SignExtend12((int)(word >> 20));
        return funct3 switch {
            1 => new RvInstruction(pc, raw, rd + 32, [rs1,], ToothClass.Load, new RvFlh(rd + 32, rs1, imm)),
            2 => new RvInstruction(pc, raw, rd + 32, [rs1,], ToothClass.Load, new RvFlw(rd + 32, rs1, imm)),
            3 => new RvInstruction(pc, raw, rd + 32, [rs1,], ToothClass.Load, new RvFld(rd + 32, rs1, imm)),
            _ => DecodeVLoad(pc, raw, rd, rs1, funct3, word),
        };
    }

    // STORE-FP / vector store: opcode=0x27.
    // funct3=2 → FSW; funct3=3 → FSD; funct3=0/5/6 → VSE8/16/32.
    private static RvInstruction DecodeFpOrVStore(
        ulong pc,
        uint raw,
        int rd, // bits[11:7] = vs3 for vector stores
        int rs1,
        int rs2, // bits[24:20] = sumop for unit-stride vector stores
        uint funct3,
        uint word
    ) {
        int imm = SignExtend12((int)(((word >> 25) << 5) | ((word >> 7) & 0x1F)));

        return funct3 switch {
            1 => new RvInstruction(pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store, new RvFsh(rs1, rs2 + 32, imm)),
            2 => new RvInstruction(pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store, new RvFsw(rs1, rs2 + 32, imm)),
            3 => new RvInstruction(pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store, new RvFsd(rs1, rs2 + 32, imm)),
            _ => DecodeVStore(pc, raw, rd, rs1, rs2, funct3, word),
        };
    }

    // ── V extension ───────────────────────────────────────────────────────────

    // OP-FP (opcode=0x53): all two-source FP operations.
    private static RvInstruction DecodeFpOp(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3,
        uint funct7
    ) {
        return funct7 switch {
            // ── Single precision ─────────────────────────────────────────────
            0x00 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFaddS(rd + 32, rs1 + 32, rs2 + 32)),
            0x04 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsubS(rd + 32, rs1 + 32, rs2 + 32)),
            0x08 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmulS(rd + 32, rs1 + 32, rs2 + 32)),
            0x0C => FpRr(
                pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFdivS(rd + 32, rs1 + 32, rs2 + 32), ToothClass.FloatDivSqrt
            ),
            0x2C => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFsqrtS(rd + 32, rs1 + 32), ToothClass.FloatDivSqrt),
            0x10 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjS(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjnS(rd + 32, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjxS(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FSGNJ funct3=0x{funct3:X}"),
            },
            0x14 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFminS(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmaxS(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FMIN/FMAX funct3=0x{funct3:X}"),
            },
            // Comparisons: FP sources, integer result
            0x50 => funct3 switch {
                0 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFleS(rd, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFltS(rd, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFeqS(rd, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FP compare funct3=0x{funct3:X}"),
            },
            // Conversions float→int (funct3 = rounding mode)
            0x60 => rs2 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWs(rd, rs1 + 32, (int)funct3)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWuS(rd, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.W rs2={rs2}"),
            },
            // Conversions int→float (funct3 = rounding mode)
            0x68 => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSw(rd + 32, rs1, (int)funct3)),
                1 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSWu(rd + 32, rs1, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.S rs2={rs2}"),
            },
            // FMV.X.W / FCLASS.S
            0x70 => funct3 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFmvXw(rd, rs1 + 32)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFclassS(rd, rs1 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FMV.X.W/FCLASS funct3=0x{funct3:X}"),
            },
            // FMV.W.X: int→float bit copy
            0x78 => FpR1(pc, raw, rd + 32, rs1, new RvFmvWx(rd + 32, rs1)),

            // ── Double precision ─────────────────────────────────────────────
            0x01 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFaddD(rd + 32, rs1 + 32, rs2 + 32)),
            0x05 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsubD(rd + 32, rs1 + 32, rs2 + 32)),
            0x09 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmulD(rd + 32, rs1 + 32, rs2 + 32)),
            0x0D => FpRr(
                pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFdivD(rd + 32, rs1 + 32, rs2 + 32), ToothClass.FloatDivSqrt
            ),
            0x2D => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFsqrtD(rd + 32, rs1 + 32), ToothClass.FloatDivSqrt),
            0x11 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjD(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjnD(rd + 32, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjxD(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FSGNJ.D funct3=0x{funct3:X}"),
            },
            0x15 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFminD(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmaxD(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FMIN/FMAX.D funct3=0x{funct3:X}"),
            },
            // FCVT.S.D (double→single, rs2=1) / FCVT.S.H (half→single, rs2=2): fmt=S(0)
            0x20 => rs2 switch {
                1 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtSd(rd + 32, rs1 + 32, (int)funct3)),
                2 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtSh(rd + 32, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.S.? rs2={rs2}"),
            },
            // FCVT.D.S (single→double, rs2=0) / FCVT.D.H (half→double, rs2=2): fmt=D(1)
            0x21 => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtDs(rd + 32, rs1 + 32, (int)funct3)),
                2 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtDh(rd + 32, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.D.? rs2={rs2}"),
            },
            // Comparisons D: FP sources, integer result
            0x51 => funct3 switch {
                0 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFleD(rd, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFltD(rd, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFeqD(rd, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FP.D compare funct3=0x{funct3:X}"),
            },
            // Conversions double→int
            0x61 => rs2 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWd(rd, rs1 + 32, (int)funct3)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWuD(rd, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.W.D rs2={rs2}"),
            },
            // Conversions int→double
            0x69 => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtDw(rd + 32, rs1, (int)funct3)),
                1 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtDWu(rd + 32, rs1, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.D.W rs2={rs2}"),
            },
            // FCLASS.D (no FMV.X.D in RV32D since XLEN < FLEN)
            0x71 when funct3 == 1 => FpR1(pc, raw, rd, rs1 + 32, new RvFclassD(rd, rs1 + 32)),

            // ── Half precision (Zfh) ─────────────────────────────────────────
            0x02 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFaddH(rd + 32, rs1 + 32, rs2 + 32)),
            0x06 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsubH(rd + 32, rs1 + 32, rs2 + 32)),
            0x0A => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmulH(rd + 32, rs1 + 32, rs2 + 32)),
            0x0E => FpRr(
                pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFdivH(rd + 32, rs1 + 32, rs2 + 32), ToothClass.FloatDivSqrt
            ),
            0x2E => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFsqrtH(rd + 32, rs1 + 32), ToothClass.FloatDivSqrt),
            0x12 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjH(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjnH(rd + 32, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjxH(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FSGNJ.H funct3=0x{funct3:X}"),
            },
            0x16 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFminH(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmaxH(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FMIN/FMAX.H funct3=0x{funct3:X}"),
            },
            // FCVT.H.S (fmt=H, rs2=0) / FCVT.H.D (fmt=H, rs2=1)
            0x22 => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtHs(rd + 32, rs1 + 32, (int)funct3)),
                1 => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtHd(rd + 32, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.H.? rs2={rs2}"),
            },
            // Comparisons H: FP sources, integer result
            0x52 => funct3 switch {
                0 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFleH(rd, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFltH(rd, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFeqH(rd, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FP.H compare funct3=0x{funct3:X}"),
            },
            // Conversions half→int (funct3 = rounding mode)
            0x62 => rs2 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWh(rd, rs1 + 32, (int)funct3)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWuH(rd, rs1 + 32, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.W.H rs2={rs2}"),
            },
            // Conversions int→half
            0x6A => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtHw(rd + 32, rs1, (int)funct3)),
                1 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtHWu(rd + 32, rs1, (int)funct3)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FCVT.H.W rs2={rs2}"),
            },
            // FMV.X.H / FCLASS.H
            0x72 => funct3 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFmvXh(rd, rs1 + 32)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFclassH(rd, rs1 + 32)),
                _ => throw new IllegalInstructionException(raw, $"Unknown FMV.X.H/FCLASS.H funct3=0x{funct3:X}"),
            },
            // FMV.H.X: int→half bit copy
            0x7A => FpR1(pc, raw, rd + 32, rs1, new RvFmvHx(rd + 32, rs1)),

            _ => throw new IllegalInstructionException(raw, $"Unknown OP-FP funct7=0x{funct7:X2}"),
        };
    }

    // R4-type: FMADD/FMSUB/FNMADD/FNMSUB (opcodes 0x43/0x47/0x4B/0x4F).
    private static RvInstruction DecodeFmaR4(
        ulong pc,
        uint raw,
        uint opcode,
        int rd,
        int rs1,
        int rs2,
        int rs3
    ) {
        uint fmt = (raw >> 25) & 0x3;
        RvOp op = (opcode, fmt) switch {
            (0x43, 0) => new RvFmaddS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x47, 0) => new RvFmsubS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4B, 0) => new RvFnmsubS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4F, 0) => new RvFnmaddS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x43, 1) => new RvFmaddD(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x47, 1) => new RvFmsubD(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4B, 1) => new RvFnmsubD(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4F, 1) => new RvFnmaddD(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x43, 2) => new RvFmaddH(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x47, 2) => new RvFmsubH(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4B, 2) => new RvFnmsubH(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            (0x4F, 2) => new RvFnmaddH(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            _         => throw new IllegalInstructionException(raw, $"FMA: unsupported fmt={fmt} opcode=0x{opcode:X}"),
        };
        return new RvInstruction(
            pc, raw, rd + 32,
            [rs1 + 32, rs2 + 32, rs3 + 32,], ToothClass.FloatingPoint, op
        );
    }

    // ── FP instruction factories ───────────────────────────────────────────────

    protected static RvInstruction FpRr(
        ulong pc,
        uint raw,
        int dest,
        int s0,
        int s1,
        RvOp op,
        ToothClass cls = ToothClass.FloatingPoint
    ) =>
        new(pc, raw, dest, [s0, s1,], cls, op);

    protected static RvInstruction FpR1(
        ulong pc,
        uint raw,
        int dest,
        int s0,
        RvOp op,
        ToothClass cls = ToothClass.FloatingPoint
    ) =>
        new(pc, raw, dest, [s0,], cls, op);
}