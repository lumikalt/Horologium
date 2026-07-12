using Mechanism;
using RiscV32.Decode;

namespace RiscV64.Decode;

/// <summary>
/// Instruction decoder for RV64I — extends Rv32Decoder with:
///   • OP-32 (opcode=0x3B): ADDW/SUBW/SLLW/SRLW/SRAW, plus RV64M MULW/DIVW/DIVUW/REMW/REMUW
///   • OP-IMM-32 (opcode=0x1B): ADDIW/SLLIW/SRLIW/SRAIW
///   • LOAD (opcode=0x03) funct3=3 (LD), funct3=6 (LWU)
///   • STORE (opcode=0x23) funct3=3 (SD)
///   • OP-IMM (opcode=0x13) shifts with 6-bit shamt instead of 5-bit
///   • OP-FP (opcode=0x53): RV64F/D 64-bit integer conversions/moves (FCVT.L/LU.S/D,
///     FCVT.S/D.L/LU, FMV.X.D, FMV.D.X) not present in RV32F/D; everything else on 0x53
///     falls through to the base RV32F/D decode table.
/// </summary>
public class Rv64Decoder : Rv32Decoder {
    // The (pc, IMemory) fetch entry point decodes compressed instructions directly
    // (bypassing DecodeRaw) for performance, so it must be overridden separately to
    // pick up the RV64C quadrant reassignments below.
    public override ITooth Decode(ulong pc, IMemory memory) {
        var half = (ushort)memory.Read(pc, 2);
        if ((half & 0x3) != 0x3) {
            return _cache.TryGetValue((pc, half), out ITooth? cached)
                ? cached
                : Cache(pc, half, TryDecodeRv64Compressed(pc, half) ?? DecodeCompressed(pc, half));
        }

        return base.Decode(pc, memory);
    }

    protected override ITooth DecodeRaw(ulong pc, uint raw) {
        if ((raw & 0x3) != 0x3) {
            ITooth? rv64C = TryDecodeRv64Compressed(pc, (ushort)raw);
            return rv64C ?? base.DecodeRaw(pc, raw);
        }

        uint opcode = raw & 0x7F;
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        uint funct3 = (raw >> 12) & 0x7;
        uint funct7 = (raw >> 25) & 0x7F;

        switch (opcode) {
            // ── OP-32: 32-bit operations sign-extended to 64 ─────────────────────
            case 0x3B when funct7 == 0x01: {
                // RV64M: MULW/DIVW/DIVUW/REMW/REMUW — operate on the lower 32 bits, sign-extend.
                IReadOnlyList<int> sources = [rs1, rs2,];
                RvOp mop = funct3 switch {
                    0x0 => new RvMulw(rd, rs1, rs2),
                    0x4 => new RvDivw(rd, rs1, rs2),
                    0x5 => new RvDivuw(rd, rs1, rs2),
                    0x6 => new RvRemw(rd, rs1, rs2),
                    0x7 => new RvRemuw(rd, rs1, rs2),
                    _ => throw new IllegalInstructionException(raw, $"Unknown RV64M OP-32 funct3=0x{funct3:X}"),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerMulDiv, mop);
            }
            case 0x3B: {
                IReadOnlyList<int> sources = [rs1, rs2,];
                RvOp op = (funct3, funct7) switch {
                    (0x0, 0x00) => new RvAddw(rd, rs1, rs2),
                    (0x0, 0x20) => new RvSubw(rd, rs1, rs2),
                    (0x1, 0x00) => new RvSllw(rd, rs1, rs2),
                    (0x5, 0x00) => new RvSrlw(rd, rs1, rs2),
                    (0x5, 0x20) => new RvSraw(rd, rs1, rs2),
                    // Zba: zero-extend the low 32 bits of rs1 before the address-generation shift-add.
                    (0x0, 0x04) => new RvAdduw(rd, rs1, rs2),
                    (0x2, 0x10) => new RvSh1AddUw(rd, rs1, rs2),
                    (0x4, 0x10) => new RvSh2AddUw(rd, rs1, rs2),
                    (0x6, 0x10) => new RvSh3AddUw(rd, rs1, rs2),
                    _ => throw new IllegalInstructionException(
                        raw,
                        $"Unknown OP-32 funct3=0x{funct3:X} funct7=0x{funct7:X}"
                    ),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
            }
            // ── OP-IMM-32: immediate 32-bit operations sign-extended to 64 ───────
            case 0x1B: {
                int imm = SignExtend12((int)(raw >> 20));
                uint shamt = (raw >> 20) & 0x1F;
                uint f7 = (raw >> 25) & 0x7F;
                uint top6 = (raw >> 26) & 0x3F;
                IReadOnlyList<int> sources = [rs1,];
                RvOp op = funct3 switch {
                    0x0                    => new RvAddiw(rd, rs1, imm),
                    0x1 when f7 == 0x00    => new RvSlliw(rd, rs1, (int)shamt),
                    // Zba SLLI.UW: funct6=0b000010, 6-bit shamt (bits 25:20).
                    0x1 when top6 == 0x02  => new RvSlliUw(rd, rs1, (int)((raw >> 20) & 0x3F)),
                    0x5 when f7 == 0x00    => new RvSrliw(rd, rs1, (int)shamt),
                    0x5 when f7 == 0x20    => new RvSraiw(rd, rs1, (int)shamt),
                    _ => throw new IllegalInstructionException(
                        raw,
                        $"Unknown OP-IMM-32 funct3=0x{funct3:X} funct7=0x{f7:X}"
                    ),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
            }
            // ── LOAD: add LD (funct3=3) and LWU (funct3=6) ───────────────────────
            case 0x03 when funct3 is 0x3 or 0x6: {
                int imm = SignExtend12((int)(raw >> 20));
                IReadOnlyList<int> sources = [rs1,];
                RvOp op = funct3 == 0x3
                    ? new RvLd(rd, rs1, imm)
                    : new RvLwu(rd, rs1, imm);
                return new RvInstruction(pc, raw, rd, sources, ToothClass.Load, op);
            }
            // ── STORE: add SD (funct3=3) ──────────────────────────────────────────
            case 0x23 when funct3 == 0x3: {
                int imm = SignExtend12((int)(((raw >> 25) << 5) | ((raw >> 7) & 0x1F)));
                return new RvInstruction(pc, raw, -1, [rs1, rs2,], ToothClass.Store, new RvSd(rs1, rs2, imm));
            }
            // ── OP-IMM shifts: 6-bit shamt in RV64 ───────────────────────────────
            // In RV64, bit[25] is shamt[5], so funct7 is no longer 0x00/0x20 for base shifts.
            // Intercept funct3=1 and funct3=5 before the base decoder misreads the shamt.
            case 0x13 when funct3 is 0x1 or 0x5: {
                uint shamt6 = (raw >> 20) & 0x3F; // 6-bit shamt
                uint top6 = (raw >> 26) & 0x3F;   // distinguishes SLLI/SRLI/SRAI/Zbb/Zbs
                IReadOnlyList<int> sources = [rs1,];
                RvOp op = (funct3, top6) switch {
                    (0x1, 0x00) => new RvSlli(rd, rs1, (int)shamt6),
                    (0x5, 0x00) => new RvSrli(rd, rs1, (int)shamt6),
                    (0x5, 0x10) => new RvSrai(rd, rs1, (int)shamt6),
                    // Zbs immediate ops: RV32's funct7 has bit25=0, so top6 = funct7 >> 1.
                    (0x1, 0x0A) => new RvBseti(rd, rs1, (int)shamt6),
                    (0x1, 0x12) => new RvBclri(rd, rs1, (int)shamt6),
                    (0x1, 0x1A) => new RvBinvi(rd, rs1, (int)shamt6),
                    (0x5, 0x12) => new RvBexti(rd, rs1, (int)shamt6),
                    // Zbb unary ops (CLZ/CTZ/CPOP/SEXT.B/SEXT.H), selected by the low shamt bits.
                    (0x1, 0x18) => shamt6 switch {
                        0 => new RvClz(rd, rs1),
                        1 => new RvCtz(rd, rs1),
                        2 => new RvCpop(rd, rs1),
                        4 => new RvSextB(rd, rs1),
                        5 => new RvSextH(rd, rs1),
                        _ => throw new IllegalInstructionException(
                            raw, $"Unknown RV64 Zbb unary op shamt=0x{shamt6:X}"
                        ),
                    },
                    (0x5, 0x0A) => new RvOrcB(rd, rs1),
                    (0x5, 0x18) => new RvRori(rd, rs1, (int)shamt6),
                    (0x5, 0x1A) => new RvRev8(rd, rs1),
                    _ => throw new IllegalInstructionException(
                        raw,
                        $"Unknown RV64 OP-IMM shift funct3=0x{funct3:X} top6=0x{top6:X}"
                    ),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
            }
            // ── OP-FP: RV64F/D 64-bit integer conversions/moves ──────────────────
            case 0x53: {
                RvInstruction? rv64Fp = TryDecodeRv64FpOp(pc, raw, rd, rs1, rs2, funct3, funct7);
                return rv64Fp ?? base.DecodeRaw(pc, raw);
            }
            // ── RV64A: AMO*.D / LR.D / SC.D — funct3=3 selects doubleword width;
            // funct3=2 (word AMOs) falls through to the base RV32A table unchanged.
            case 0x2F when funct3 == 0x3: {
                uint funct5 = (raw >> 27) & 0x1F;
                IReadOnlyList<int> sources = funct5 == 0x02 ? [rs1,] : [rs1, rs2,];
                RvOp op = funct5 switch {
                    0x02 => new RvLrD(rd, rs1),
                    0x03 => new RvScD(rd, rs1, rs2),
                    0x01 => new RvAmoswapD(rd, rs1, rs2),
                    0x00 => new RvAmoaddD(rd, rs1, rs2),
                    0x04 => new RvAmoxorD(rd, rs1, rs2),
                    0x0C => new RvAmoandD(rd, rs1, rs2),
                    0x08 => new RvAmoorD(rd, rs1, rs2),
                    0x10 => new RvAmominD(rd, rs1, rs2),
                    0x14 => new RvAmomaxD(rd, rs1, rs2),
                    0x18 => new RvAmominuD(rd, rs1, rs2),
                    0x1C => new RvAmomaxuD(rd, rs1, rs2),
                    _ => throw new IllegalInstructionException(raw, $"Unknown RV64A AMO.D funct5=0x{funct5:X2}"),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.Atomic, op);
            }
            default: return base.DecodeRaw(pc, raw);
        }
    }

    // RV64F/D-only OP-FP encodings not present in RV32F/D (funct7=0x60/0x61/0x68/0x69 with
    // rs2=2/3 select the L/LU int64 conversions; funct7=0x71/0x79 with rs2=0 select FMV.X.D/FMV.D.X).
    // Returns null for everything else so the caller falls through to the base RV32F/D table.
    private static RvInstruction? TryDecodeRv64FpOp(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3,
        uint funct7
    ) => (funct7, rs2) switch {
        // FCVT.L.S / FCVT.LU.S: float→int64 (funct3 = rounding mode)
        (0x60, 2) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLs(rd, rs1 + 32, (int)funct3)),
        (0x60, 3) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLuS(rd, rs1 + 32, (int)funct3)),
        // FCVT.S.L / FCVT.S.LU: int64→float
        (0x68, 2) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSl(rd + 32, rs1, (int)funct3)),
        (0x68, 3) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSLu(rd + 32, rs1, (int)funct3)),
        // FCVT.L.D / FCVT.LU.D: double→int64
        (0x61, 2) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLd(rd, rs1 + 32, (int)funct3)),
        (0x61, 3) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLuD(rd, rs1 + 32, (int)funct3)),
        // FCVT.D.L / FCVT.D.LU: int64→double
        (0x69, 2) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtDl(rd + 32, rs1, (int)funct3)),
        (0x69, 3) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtDLu(rd + 32, rs1, (int)funct3)),
        // FMV.X.D: double bit pattern → int reg (funct3=0; funct3=1 at the same funct7/rs2 is FCLASS.D)
        (0x71, 0) when funct3 == 0 => FpR1(pc, raw, rd, rs1 + 32, new RvFmvXd(rd, rs1 + 32)),
        // FMV.D.X: int reg bit pattern → double reg
        (0x79, 0) => FpR1(pc, raw, rd + 32, rs1, new RvFmvDx(rd + 32, rs1)),
        // FCVT.L.H / FCVT.LU.H: half→int64 (funct3 = rounding mode)
        (0x62, 2) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLh(rd, rs1 + 32, (int)funct3)),
        (0x62, 3) => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtLuH(rd, rs1 + 32, (int)funct3)),
        // FCVT.H.L / FCVT.H.LU: int64→half
        (0x6A, 2) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtHl(rd + 32, rs1, (int)funct3)),
        (0x6A, 3) => FpR1(pc, raw, rd + 32, rs1, new RvFcvtHLu(rd + 32, rs1, (int)funct3)),
        _ => null,
    };

    // ── RV64C quadrant reassignments ──────────────────────────────────────────
    // RV64 doesn't need a compressed single-precision FP load/store slot (no RV32-only
    // narrow-float compressed encodings), and its wider address space drops C.JAL in favor
    // of a compressed 32-bit-op immediate add. The freed encodings become doubleword
    // load/store forms instead:
    //   Q0 funct3=3/7: C.FLW/C.FSW  → C.LD/C.SD
    //   Q1 funct3=1:   C.JAL        → C.ADDIW
    //   Q2 funct3=3/7: C.FLWSP/C.FSWSP → C.LDSP/C.SDSP
    // Returns null for every other encoding so the caller falls through to the base RV32C table.
    private static ITooth? TryDecodeRv64Compressed(ulong pc, ushort c) {
        uint q = (uint)(c & 0x3);
        var funct3 = (uint)(c >> 13);

        switch (q) {
            case 0x0 when funct3 == 0x3: { // C.LD
                int rdp = ((c >> 2) & 0x7) + 8;
                int rs1P = ((c >> 7) & 0x7) + 8;
                return new RvInstruction(pc, c, rdp, [rs1P,], ToothClass.Load, new RvLd(rdp, rs1P, CldMemImm(c)), 2);
            }
            case 0x0 when funct3 == 0x7: { // C.SD
                int rs2P = ((c >> 2) & 0x7) + 8;
                int rs1P = ((c >> 7) & 0x7) + 8;
                return new RvInstruction(
                    pc, c, -1, [rs1P, rs2P,], ToothClass.Store, new RvSd(rs1P, rs2P, CldMemImm(c)), 2
                );
            }
            case 0x1 when funct3 == 0x1: { // C.ADDIW
                int rd = (c >> 7) & 0x1F;
                if (rd == 0) throw new IllegalInstructionException(c, "C.ADDIW with rd=x0 is reserved");
                int imm = SignExtendN((((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F), 6);
                return new RvInstruction(pc, c, rd, [rd,], ToothClass.IntegerAlu, new RvAddiw(rd, rd, imm), 2);
            }
            case 0x2 when funct3 == 0x3: { // C.LDSP
                int rd = (c >> 7) & 0x1F;
                if (rd == 0) throw new IllegalInstructionException(c, "C.LDSP with rd=x0 is reserved");
                return new RvInstruction(pc, c, rd, [2,], ToothClass.Load, new RvLd(rd, 2, CldspImm(c)), 2);
            }
            case 0x2 when funct3 == 0x7: { // C.SDSP
                int rs2 = (c >> 2) & 0x1F;
                return new RvInstruction(pc, c, -1, [2, rs2,], ToothClass.Store, new RvSd(2, rs2, CsdspImm(c)), 2);
            }
            default: return null;
        }
    }

    // CL/CS-format doubleword offset: uimm[5:3]=c[12:10], uimm[7:6]=c[6:5].
    private static int CldMemImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) | (((c >> 5) & 0x3) << 6);

    // CI-format C.LDSP offset: uimm[5]=c[12], uimm[4:3]=c[6:5], uimm[8:6]=c[4:2].
    private static int CldspImm(ushort c) =>
        (((c >> 12) & 0x1) << 5) | (((c >> 5) & 0x3) << 3) | (((c >> 2) & 0x7) << 6);

    // CSS-format C.SDSP offset: uimm[5:3]=c[12:10], uimm[8:6]=c[9:7].
    private static int CsdspImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) | (((c >> 7) & 0x7) << 6);
}