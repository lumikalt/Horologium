using Mechanism;
using RiscV32.Decode;

namespace RiscV64.Decode;

/// <summary>
/// Instruction decoder for RV64I — extends Rv32Decoder with:
///   • OP-32 (opcode=0x3B): ADDW/SUBW/SLLW/SRLW/SRAW
///   • OP-IMM-32 (opcode=0x1B): ADDIW/SLLIW/SRLIW/SRAIW
///   • LOAD (opcode=0x03) funct3=3 (LD), funct3=6 (LWU)
///   • STORE (opcode=0x23) funct3=3 (SD)
///   • OP-IMM (opcode=0x13) shifts with 6-bit shamt instead of 5-bit
/// </summary>
public class Rv64Decoder : Rv32Decoder {
    protected override ITooth DecodeRaw(ulong pc, uint raw) {
        if ((raw & 0x3) != 0x3) return base.DecodeRaw(pc, raw); // delegate compressed to base

        uint opcode = raw & 0x7F;
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        uint funct3 = (raw >> 12) & 0x7;
        uint funct7 = (raw >> 25) & 0x7F;

        switch (opcode) {
            // ── OP-32: 32-bit operations sign-extended to 64 ─────────────────────
            case 0x3B: {
                IReadOnlyList<int> sources = [rs1, rs2,];
                RvOp op = (funct3, funct7) switch {
                    (0x0, 0x00) => new RvAddw(rd, rs1, rs2),
                    (0x0, 0x20) => new RvSubw(rd, rs1, rs2),
                    (0x1, 0x00) => new RvSllw(rd, rs1, rs2),
                    (0x5, 0x00) => new RvSrlw(rd, rs1, rs2),
                    (0x5, 0x20) => new RvSraw(rd, rs1, rs2),
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
                IReadOnlyList<int> sources = [rs1,];
                RvOp op = funct3 switch {
                    0x0                 => new RvAddiw(rd, rs1, imm),
                    0x1 when f7 == 0x00 => new RvSlliw(rd, rs1, (int)shamt),
                    0x5 when f7 == 0x00 => new RvSrliw(rd, rs1, (int)shamt),
                    0x5 when f7 == 0x20 => new RvSraiw(rd, rs1, (int)shamt),
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
                uint top6 = (raw >> 26) & 0x3F;   // distinguishes SLLI/SRLI/SRAI
                IReadOnlyList<int> sources = [rs1,];
                RvOp op = (funct3, top6) switch {
                    (0x1, 0x00) => new RvSlli(rd, rs1, (int)shamt6),
                    (0x5, 0x00) => new RvSrli(rd, rs1, (int)shamt6),
                    (0x5, 0x10) => new RvSrai(rd, rs1, (int)shamt6),
                    _ => throw new IllegalInstructionException(
                        raw,
                        $"Unknown RV64 OP-IMM shift funct3=0x{funct3:X} top6=0x{top6:X}"
                    ),
                };
                return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
            }
            default: return base.DecodeRaw(pc, raw);
        }
    }
}