using Mechanism;

namespace RiscV.Decode;

/// <summary>
/// Stateless RV32I instruction decoder.
/// Decodes a 32-bit word at a given PC into an RvInstruction.
/// </summary>
public sealed class RvDecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 4; // RV32I is fixed-width

    public ITooth Decode(ulong pc, IMemory memory) => Decode(pc, (uint)memory.Read(pc, 4));


    public ITooth Decode(ulong pc, uint raw) {
        uint opcode = raw & 0x7F;
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        uint funct3 = (raw >> 12) & 0x7;
        uint funct7 = (raw >> 25) & 0x7F;

        return opcode switch {
            0x33 => DecodeRType(pc, raw, rd, rs1, rs2, funct3, funct7),
            0x13 => DecodeITypeAlu(pc, raw, rd, rs1, funct3, raw),
            0x03 => DecodeLoad(pc, raw, rd, rs1, funct3, raw),
            0x23 => DecodeStore(pc, raw, rs1, rs2, funct3, raw),
            0x63 => DecodeBranch(pc, raw, rs1, rs2, funct3, raw),
            0x6F => DecodeJal(pc, raw, rd, raw),
            0x67 => DecodeJalr(pc, raw, rd, rs1, raw),
            0x37 => DecodeUType(pc, raw, rd, raw, true),
            0x17 => DecodeUType(pc, raw, rd, raw, false),
            0x73 => DecodeSystem(pc, raw, rd, rs1, funct3, raw),
            0x2F => DecodeAmo(pc, raw, rd, rs1, rs2, funct3, (raw >> 27) & 0x1F),
            0x0F => new RvInstruction(pc, raw, -1, [], ToothClass.Fence, new RvFence()),
            // F extension
            0x07 => DecodeFpLoad(pc, raw, rd, rs1, funct3, raw),
            0x27 => DecodeFpStore(pc, raw, rs1, rs2, funct3, raw),
            0x53 => DecodeFpOp(pc, raw, rd, rs1, rs2, funct3, funct7),
            0x43 or 0x47 or 0x4B or 0x4F =>
                DecodeFmaR4(pc, raw, opcode, rd, rs1, rs2, (int)((raw >> 27) & 0x1F)),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown opcode 0x{opcode:X2} at PC=0x{pc:X8}"
            ),
        };
    }

    // ── R-type ────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeRType(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3,
        uint funct7
    ) {
        var sources = (IReadOnlyList<int>)[rs1, rs2,];
        if (funct7 == 0x01) {
            // M extension: multiply / divide
            RvOp mop = funct3 switch {
                0x0 => new RvMul(rd, rs1, rs2),
                0x1 => new RvMulh(rd, rs1, rs2),
                0x2 => new RvMulhsu(rd, rs1, rs2),
                0x3 => new RvMulhu(rd, rs1, rs2),
                0x4 => new RvDiv(rd, rs1, rs2),
                0x5 => new RvDivu(rd, rs1, rs2),
                0x6 => new RvRem(rd, rs1, rs2),
                0x7 => new RvRemu(rd, rs1, rs2),
                _ => throw new IllegalInstructionException(
                    pc, raw,
                    $"Unknown M-extension funct3=0x{funct3:X}"
                ),
            };
            return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerMulDiv, mop);
        }

        RvOp op = (funct3, funct7) switch {
            (0x0, 0x00) => new RvAdd(rd, rs1, rs2),
            (0x0, 0x20) => new RvSub(rd, rs1, rs2),
            (0x4, 0x00) => new RvXor(rd, rs1, rs2),
            (0x6, 0x00) => new RvOr(rd, rs1, rs2),
            (0x7, 0x00) => new RvAnd(rd, rs1, rs2),
            (0x1, 0x00) => new RvSll(rd, rs1, rs2),
            (0x5, 0x00) => new RvSrl(rd, rs1, rs2),
            (0x5, 0x20) => new RvSra(rd, rs1, rs2),
            (0x2, 0x00) => new RvSlt(rd, rs1, rs2),
            (0x3, 0x00) => new RvSltu(rd, rs1, rs2),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown R-type funct3=0x{funct3:X} funct7=0x{funct7:X}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
    }

    // ── I-type ALU ────────────────────────────────────────────────────────────

    private static RvInstruction DecodeITypeAlu(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        int imm = SignExtend12((int)(word >> 20));
        uint shamt = (word >> 20) & 0x1F;
        uint funct7 = (word >> 25) & 0x7F;
        var sources = (IReadOnlyList<int>)[rs1,];

        RvOp op = funct3 switch {
            0x0 => new RvAddi(rd, rs1, imm),
            0x4 => new RvXori(rd, rs1, imm),
            0x6 => new RvOri(rd, rs1, imm),
            0x7 => new RvAndi(rd, rs1, imm),
            0x2 => new RvSlti(rd, rs1, imm),
            0x3 => new RvSltiu(rd, rs1, imm),
            0x1 => new RvSlli(rd, rs1, (int)shamt),
            0x5 => funct7 == 0x20
                ? new RvSrai(rd, rs1, (int)shamt)
                : new RvSrli(rd, rs1, (int)shamt),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown OP-IMM funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerAlu, op);
    }

    // ── Loads ─────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeLoad(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        int imm = SignExtend12((int)(word >> 20));
        var sources = (IReadOnlyList<int>)[rs1,];

        RvOp op = funct3 switch {
            0x0 => new RvLb(rd, rs1, imm),
            0x1 => new RvLh(rd, rs1, imm),
            0x2 => new RvLw(rd, rs1, imm),
            0x4 => new RvLbu(rd, rs1, imm),
            0x5 => new RvLhu(rd, rs1, imm),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown LOAD funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.Load, op);
    }

    // ── Stores ────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeStore(
        ulong pc,
        uint raw,
        int rs1,
        int rs2,
        uint funct3,
        uint word
    ) {
        int imm = SignExtend12((int)(((word >> 25) << 5) | ((word >> 7) & 0x1F)));
        var sources = (IReadOnlyList<int>)[rs1, rs2,];

        RvOp op = funct3 switch {
            0x0 => new RvSb(rs1, rs2, imm),
            0x1 => new RvSh(rs1, rs2, imm),
            0x2 => new RvSw(rs1, rs2, imm),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown STORE funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, -1, sources, ToothClass.Store, op);
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    private static RvInstruction DecodeBranch(
        ulong pc,
        uint raw,
        int rs1,
        int rs2,
        uint funct3,
        uint word
    ) {
        // B-type immediate: inst[31]|inst[7]|inst[30:25]|inst[11:8] << 1
        var imm = (int)(
            (((word >> 31) & 1) << 12) |
            (((word >> 7) & 1) << 11) |
            (((word >> 25) & 0x3F) << 5) |
            (((word >> 8) & 0xF) << 1));
        imm = SignExtendN(imm, 13);
        var sources = (IReadOnlyList<int>)[rs1, rs2,];

        RvOp op = funct3 switch {
            0x0 => new RvBeq(rs1, rs2, imm),
            0x1 => new RvBne(rs1, rs2, imm),
            0x4 => new RvBlt(rs1, rs2, imm),
            0x5 => new RvBge(rs1, rs2, imm),
            0x6 => new RvBltu(rs1, rs2, imm),
            0x7 => new RvBgeu(rs1, rs2, imm),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown BRANCH funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, -1, sources, ToothClass.ConditionalBranch, op);
    }

    // ── JAL ───────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeJal(ulong pc, uint raw, int rd, uint word) {
        // J-type immediate: inst[31]|inst[19:12]|inst[20]|inst[30:21] << 1
        var imm = (int)(
            (((word >> 31) & 1) << 20) |
            (((word >> 12) & 0xFF) << 12) |
            (((word >> 20) & 1) << 11) |
            (((word >> 21) & 0x3FF) << 1));
        imm = SignExtendN(imm, 21);
        return new RvInstruction(
            pc, raw, rd, [], ToothClass.Branch,
            new RvJal(rd, imm)
        );
    }

    // ── JALR ──────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeJalr(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint word
    ) {
        int imm = SignExtend12((int)(word >> 20));
        return new RvInstruction(
            pc, raw, rd, [rs1,], ToothClass.Branch,
            new RvJalr(rd, rs1, imm)
        );
    }

    // ── U-type ────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeUType(
        ulong pc,
        uint raw,
        int rd,
        uint word,
        bool isLui
    ) {
        var imm = (int)(word & 0xFFFFF000); // upper 20 bits, lower 12 zeroed
        RvOp op = isLui ? new RvLui(rd, imm) : new RvAuipc(rd, imm);
        return new RvInstruction(pc, raw, rd, [], ToothClass.IntegerAlu, op);
    }

    // ── System ────────────────────────────────────────────────────────────────

    private static RvInstruction DecodeSystem(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        uint csr = word >> 20;
        uint zimm = (word >> 15) & 0x1F;

        if (funct3 == 0x0)
            // ECALL / EBREAK / MRET
            return (word >> 20) switch {
                0x000 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvEcall()),
                0x001 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvEbreak()),
                0x302 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvMret()),
                _ => throw new IllegalInstructionException(
                    pc, raw,
                    $"Unknown SYSTEM instruction at PC=0x{pc:X8}"
                ),
            };

        // CSR instructions
        IReadOnlyList<int> sources = funct3 is 0x1 or 0x2 or 0x3
            ? [rs1,]
            : [];

        RvOp op = funct3 switch {
            0x1 => new RvCsrrw(rd, rs1, csr),
            0x2 => new RvCsrrs(rd, rs1, csr),
            0x3 => new RvCsrrc(rd, rs1, csr),
            0x5 => new RvCsrrwi(rd, zimm, csr),
            0x6 => new RvCsrrsi(rd, zimm, csr),
            0x7 => new RvCsrrci(rd, zimm, csr),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown SYSTEM funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.System, op);
    }

    // ── A extension (AMO) ─────────────────────────────────────────────────────

    private static RvInstruction DecodeAmo(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3,
        uint funct5
    ) {
        if (funct3 != 0x2)
            throw new IllegalInstructionException(
                pc, raw,
                $"AMO with non-word funct3=0x{funct3:X} (only .W supported)"
            );

        // LR.W reads only the address register; all others use rs1 (addr) and rs2 (operand).
        IReadOnlyList<int> sources = funct5 == 0x02 ? [rs1,] : [rs1, rs2,];

        RvOp op = funct5 switch {
            0x02 => new RvLrW(rd, rs1),
            0x03 => new RvScW(rd, rs1, rs2),
            0x01 => new RvAmoswapW(rd, rs1, rs2),
            0x00 => new RvAmoaddW(rd, rs1, rs2),
            0x04 => new RvAmoxorW(rd, rs1, rs2),
            0x0C => new RvAmoandW(rd, rs1, rs2),
            0x08 => new RvAmoorW(rd, rs1, rs2),
            0x10 => new RvAmominW(rd, rs1, rs2),
            0x14 => new RvAmomaxW(rd, rs1, rs2),
            0x18 => new RvAmominuW(rd, rs1, rs2),
            0x1C => new RvAmomaxuW(rd, rs1, rs2),
            _ => throw new IllegalInstructionException(
                pc, raw,
                $"Unknown AMO funct5=0x{funct5:X2}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.Atomic, op);
    }

    // ── F extension ───────────────────────────────────────────────────────────

    // FLW: opcode=0x07, funct3=2. Dest is a FP register (rd+32).
    private static RvInstruction DecodeFpLoad(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        if (funct3 != 2)
            throw new IllegalInstructionException(
                pc, raw, $"Unknown LOAD-FP funct3=0x{funct3:X} (only FLW/f32 supported)"
            );
        int imm = SignExtend12((int)(word >> 20));
        return new RvInstruction(
            pc, raw, rd + 32, [rs1,], ToothClass.Load,
            new RvFlw(rd + 32, rs1, imm)
        );
    }

    // FSW: opcode=0x27, funct3=2. rs2 is a FP register (rs2+32).
    private static RvInstruction DecodeFpStore(
        ulong pc,
        uint raw,
        int rs1,
        int rs2,
        uint funct3,
        uint word
    ) {
        if (funct3 != 2)
            throw new IllegalInstructionException(
                pc, raw, $"Unknown STORE-FP funct3=0x{funct3:X} (only FSW/f32 supported)"
            );
        int imm = SignExtend12((int)(((word >> 25) << 5) | ((word >> 7) & 0x1F)));
        return new RvInstruction(
            pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store,
            new RvFsw(rs1, rs2 + 32, imm)
        );
    }

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
            0x00 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFaddS(rd + 32, rs1 + 32, rs2 + 32)),
            0x04 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsubS(rd + 32, rs1 + 32, rs2 + 32)),
            0x08 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmulS(rd + 32, rs1 + 32, rs2 + 32)),
            0x0C => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFdivS(rd + 32, rs1 + 32, rs2 + 32)),
            0x2C => FpR1(pc, raw, rd + 32, rs1 + 32, new RvFsqrtS(rd + 32, rs1 + 32)),
            0x10 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjS(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjnS(rd + 32, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFsgnjxS(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FSGNJ funct3=0x{funct3:X}"
                ),
            },
            0x14 => funct3 switch {
                0 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFminS(rd + 32, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd + 32, rs1 + 32, rs2 + 32, new RvFmaxS(rd + 32, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FMIN/FMAX funct3=0x{funct3:X}"
                ),
            },
            // Comparisons: FP sources, integer result
            0x50 => funct3 switch {
                0 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFleS(rd, rs1 + 32, rs2 + 32)),
                1 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFltS(rd, rs1 + 32, rs2 + 32)),
                2 => FpRr(pc, raw, rd, rs1 + 32, rs2 + 32, new RvFeqS(rd, rs1 + 32, rs2 + 32)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FP compare funct3=0x{funct3:X}"
                ),
            },
            // Conversions float→int
            0x60 => rs2 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWs(rd, rs1 + 32)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFcvtWuS(rd, rs1 + 32)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FCVT.W rs2={rs2}"
                ),
            },
            // Conversions int→float
            0x68 => rs2 switch {
                0 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSw(rd + 32, rs1)),
                1 => FpR1(pc, raw, rd + 32, rs1, new RvFcvtSWu(rd + 32, rs1)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FCVT.S rs2={rs2}"
                ),
            },
            // FMV.X.W / FCLASS.S
            0x70 => funct3 switch {
                0 => FpR1(pc, raw, rd, rs1 + 32, new RvFmvXw(rd, rs1 + 32)),
                1 => FpR1(pc, raw, rd, rs1 + 32, new RvFclassS(rd, rs1 + 32)),
                _ => throw new IllegalInstructionException(
                    pc, raw, $"Unknown FMV.X.W/FCLASS funct3=0x{funct3:X}"
                ),
            },
            // FMV.W.X: int→float bit copy
            0x78 => FpR1(pc, raw, rd + 32, rs1, new RvFmvWx(rd + 32, rs1)),
            _ => throw new IllegalInstructionException(
                pc, raw, $"Unknown OP-FP funct7=0x{funct7:X2}"
            ),
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
        if (fmt != 0)
            throw new IllegalInstructionException(
                pc, raw, $"FMA: only .S format (fmt=0) supported, got fmt={fmt}"
            );
        RvOp op = opcode switch {
            0x43 => new RvFmaddS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            0x47 => new RvFmsubS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            0x4B => new RvFnmsubS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            0x4F => new RvFnmaddS(rd + 32, rs1 + 32, rs2 + 32, rs3 + 32),
            _ => throw new IllegalInstructionException(
                pc, raw, $"Unknown FMA opcode 0x{opcode:X2}"
            ),
        };
        return new RvInstruction(
            pc, raw, rd + 32,
            [rs1 + 32, rs2 + 32, rs3 + 32,], ToothClass.IntegerAlu, op
        );
    }

    // ── FP instruction factories ───────────────────────────────────────────────

    private static RvInstruction FpRr(ulong pc, uint raw, int dest, int s0, int s1, RvOp op) =>
        new(pc, raw, dest, [s0, s1,], ToothClass.IntegerAlu, op);

    private static RvInstruction FpR1(ulong pc, uint raw, int dest, int s0, RvOp op) =>
        new(pc, raw, dest, [s0,], ToothClass.IntegerAlu, op);

    // ── Immediate helpers ─────────────────────────────────────────────────────

    private static int SignExtend12(int value) =>
        (value & 0x800) != 0 ? value | unchecked((int)0xFFFFF000) : value & 0xFFF;

    private static int SignExtendN(int value, int bits) {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }
}