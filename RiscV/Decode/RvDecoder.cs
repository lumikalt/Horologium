using Mechanism;

namespace RiscV.Decode;

/// <summary>
/// Instruction decoder with a (PC, raw) keyed cache.
/// Safe for all programs — keying on both PC and raw encoding avoids false hits when
/// two different instructions happen to share the same PC (e.g. in unit tests).
/// </summary>
public sealed class RvDecoder : IDecoder {
    private readonly Dictionary<(ulong pc, uint raw), ITooth>    _cache     = new();
    private readonly Dictionary<(ulong pc, uint raw), FetchHint> _hintCache = new();
    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        if (_hintCache.TryGetValue((pc, firstWord), out FetchHint cached)) return cached;
        FetchHint hint = ComputeFetchHint(pc, firstWord);
        _hintCache[(pc, firstWord)] = hint;
        return hint;
    }

    private static FetchHint ComputeFetchHint(ulong pc, uint firstWord) {
        bool isCompressed = (firstWord & 0x3) != 0x3;
        if (isCompressed) {
            var c = (ushort)(firstWord & 0xFFFF);
            var q = (uint)(c & 0x3);
            var cfunct3 = (uint)(c >> 13);
            int crs1 = (c >> 7) & 0x1F;
            int crs2 = (c >> 2) & 0x1F;
            bool inst12 = (c & 0x1000) != 0;

            // c.jal (RV32): PC-relative call with statically known target.
            if (q == 0x1 && cfunct3 == 0x1)
                return new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsCall = true,
                    BranchTarget = (ulong)((long)pc + CJumpOffset(c)),
                };
            // c.j: unconditional PC-relative jump.
            if (q == 0x1 && cfunct3 == 0x5)
                return new FetchHint {
                    InstructionSize = 2, IsBranch = true,
                    BranchTarget = (ulong)((long)pc + CJumpOffset(c)),
                };
            // c.beqz / c.bnez: PC-relative conditional branches.
            if (q == 0x1 && (cfunct3 == 0x6 || cfunct3 == 0x7))
                return new FetchHint {
                    InstructionSize = 2, IsBranch = true,
                    BranchTarget = (ulong)((long)pc + CBranchImm(c)),
                };
            // c.jalr: register-indirect call — target not statically known.
            if (q == 0x2 && cfunct3 == 0x4 && inst12 && crs2 == 0 && crs1 != 0)
                return new FetchHint { InstructionSize = 2, IsBranch = true, IsCall = true, };
            // c.jr / c.ret: register-indirect return — target not statically known.
            if (q == 0x2 && cfunct3 == 0x4 && !inst12 && crs2 == 0 && crs1 != 0)
                return new FetchHint { InstructionSize = 2, IsBranch = true, IsReturn = crs1 is 1 or 5, };
            return new FetchHint { InstructionSize = 2, };
        }

        var opcode = (int)(firstWord & 0x7F);
        var rd = (int)((firstWord >> 7) & 0x1F);
        var rs1 = (int)((firstWord >> 15) & 0x1F);
        bool isJal = opcode == 0x6F;
        bool isJalr = opcode == 0x67;
        bool linkRd = rd is 1 or 5;
        bool linkRs1 = rs1 is 1 or 5;

        ulong? branchTarget = null;
        if (opcode == 0x63) {
            // B-type: imm[12|10:5] in bits[31:25], imm[4:1|11] in bits[11:7]
            var bImm = (int)(
                (((firstWord >> 31) & 1) << 12) |
                (((firstWord >> 7) & 1) << 11) |
                (((firstWord >> 25) & 0x3F) << 5) |
                (((firstWord >> 8) & 0xF) << 1));
            branchTarget = (ulong)((long)pc + SignExtendN(bImm, 13));
        }
        else if (isJal) {
            // J-type: imm[20|10:1|11|19:12] scattered across bits[31:12]
            var jImm = (int)(
                (((firstWord >> 31) & 1) << 20) |
                (((firstWord >> 12) & 0xFF) << 12) |
                (((firstWord >> 20) & 1) << 11) |
                (((firstWord >> 21) & 0x3FF) << 1));
            branchTarget = (ulong)((long)pc + SignExtendN(jImm, 21));
        }
        // JALR: register-indirect — target not statically known; branchTarget stays null.

        return new FetchHint {
            InstructionSize = 4,
            IsBranch = opcode is 0x63 || isJal || isJalr,
            IsCall = (isJal || isJalr) && linkRd,
            IsReturn = isJalr && linkRs1 && !linkRd,
            BranchTarget = branchTarget,
        };
    }

    public int InstructionSize(ulong pc, IMemory memory) {
        var half = (ushort)memory.Read(pc, 2);
        return (half & 0x3) != 0x3 ? 2 : 4;
    }

    public ITooth Decode(ulong pc, IMemory memory) {
        var half = (ushort)memory.Read(pc, 2);
        if ((half & 0x3) != 0x3) {
            if (_cache.TryGetValue((pc, half), out ITooth? c)) return c;
            return Cache(pc, half, DecodeCompressed(pc, half));
        }
        var raw = (uint)memory.Read(pc, 4);
        if (_cache.TryGetValue((pc, raw), out ITooth? cached)) return cached;
        return Cache(pc, raw, DecodeRaw(pc, raw));
    }

    public ITooth Decode(ulong pc, uint raw) {
        if (_cache.TryGetValue((pc, raw), out ITooth? cached)) return cached;
        return Cache(pc, raw, DecodeRaw(pc, raw));
    }

    private ITooth Cache(ulong pc, uint raw, ITooth tooth) {
        _cache[(pc, raw)] = tooth;
        return tooth;
    }

    private static ITooth DecodeRaw(ulong pc, uint raw) {
        if ((raw & 0x3) != 0x3) return DecodeCompressed(pc, (ushort)(raw & 0xFFFF));
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
            // F and V extension load/store (share opcodes 0x07/0x27, disambiguated by funct3)
            0x07 => DecodeFpOrVLoad(pc, raw, rd, rs1, funct3, raw),
            0x27 => DecodeFpOrVStore(pc, raw, rd, rs1, rs2, funct3, raw),
            0x53 => DecodeFpOp(pc, raw, rd, rs1, rs2, funct3, funct7),
            // V extension arithmetic/config
            0x57 => DecodeVOp(pc, raw, rd, rs1, rs2, funct3),
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
                0x001 => new RvInstruction(pc, raw, -1, [], ToothClass.Halt, new RvEbreak()),
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

    // LOAD-FP / vector load: opcode=0x07.
    // funct3=2 → FLW; funct3=0/5/6 → VLE8/16/32.
    private static RvInstruction DecodeFpOrVLoad(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        uint funct3,
        uint word
    ) {
        if (funct3 == 2) {
            int imm = SignExtend12((int)(word >> 20));
            return new RvInstruction(
                pc, raw, rd + 32, [rs1,], ToothClass.Load,
                new RvFlw(rd + 32, rs1, imm)
            );
        }

        // Vector load (funct3=0/5/6/7)
        return DecodeVLoad(pc, raw, rd, rs1, funct3, word);
    }

    // STORE-FP / vector store: opcode=0x27.
    // funct3=2 → FSW; funct3=0/5/6 → VSE8/16/32.
    private static RvInstruction DecodeFpOrVStore(
        ulong pc,
        uint raw,
        int rd, // bits[11:7] = vs3 for vector stores
        int rs1,
        int rs2, // bits[24:20] = sumop for unit-stride vector stores
        uint funct3,
        uint word
    ) {
        // Vector store (funct3=0/5/6/7)
        if (funct3 != 2) return DecodeVStore(pc, raw, rd, rs1, rs2, funct3, word);
        int imm = SignExtend12((int)(((word >> 25) << 5) | ((word >> 7) & 0x1F)));
        return new RvInstruction(
            pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store,
            new RvFsw(rs1, rs2 + 32, imm)
        );
    }

    // ── V extension ───────────────────────────────────────────────────────────

    // VLE8/16/32 and VLM: opcode=0x07, funct3 ≠ 2.
    private static RvInstruction DecodeVLoad(
        ulong pc,
        uint raw,
        int vd,
        int rs1,
        uint funct3,
        uint word
    ) {
        uint mop = (word >> 26) & 0x3;    // addressing mode: 00=unit-stride
        uint lumop = (word >> 20) & 0x1F; // unit-stride sub-mode
        bool masked = ((word >> 25) & 1) == 0;

        if (mop != 0)
            throw new IllegalInstructionException(
                pc, raw, $"V load: only unit-stride (mop=0) supported, got mop={mop}"
            );

        // VLM: funct3=0 + lumop=01011
        if (funct3 == 0 && lumop == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVlm(vd, rs1));

        int sew = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                pc, raw, $"V load: unsupported element width funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVleVV(vd, rs1, sew, masked));
    }

    // VSE8/16/32 and VSM: opcode=0x27, funct3 ≠ 2.
    // rd (bits[11:7]) = vs3, rs2 (bits[24:20]) = sumop.
    private static RvInstruction DecodeVStore(
        ulong pc,
        uint raw,
        int vs3, // bits[11:7]
        int rs1,
        int sumop, // bits[24:20]
        uint funct3,
        uint word
    ) {
        uint mop = (word >> 26) & 0x3;
        bool masked = ((word >> 25) & 1) == 0;

        if (mop != 0)
            throw new IllegalInstructionException(
                pc, raw, $"V store: only unit-stride (mop=0) supported, got mop={mop}"
            );

        // VSM: funct3=0 + sumop=01011
        if (funct3 == 0 && sumop == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVsm(vs3, rs1));

        int sew = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                pc, raw, $"V store: unsupported element width funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVseVV(vs3, rs1, sew, masked));
    }

    // OPIVV / OPIVX / OPIVI / OPCFG: opcode=0x57.
    private static RvInstruction DecodeVOp(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3
    ) {
        var vd = (int)((raw >> 7) & 0x1F);
        int vs1 = rs1;
        var vs2 = (int)((raw >> 20) & 0x1F);
        bool masked = ((raw >> 25) & 1) == 0;
        uint funct6 = (raw >> 26) & 0x3F;

        // OPCFG (funct3=7)
        if (funct3 == 7) return DecodeVCfg(pc, raw, vd, vs1, vs2);

        // OPIVV (funct3=0), OPIVX (funct3=4), OPIVI (funct3=3)
        if (funct3 is not (0 or 3 or 4))
            throw new IllegalInstructionException(
                pc, raw, $"V op: unsupported funct3=0x{funct3:X}"
            );

        VIntOp? intOp = funct6 switch {
            0  => VIntOp.Add,
            2  => VIntOp.Sub,
            9  => VIntOp.And,
            10 => VIntOp.Or,
            11 => VIntOp.Xor,
            37 => VIntOp.Sll,
            40 => VIntOp.Srl,
            41 => VIntOp.Sra,
            _  => (VIntOp?)null,
        };

        VMaskCmpOp? cmpOp = funct6 switch {
            24 => VMaskCmpOp.Eq,
            25 => VMaskCmpOp.Ne,
            26 => VMaskCmpOp.Ltu,
            27 => VMaskCmpOp.Lt,
            30 => VMaskCmpOp.Gtu,
            31 => VMaskCmpOp.Gt,
            _  => (VMaskCmpOp?)null,
        };

        if (intOp.HasValue) {
            // vsub has no VI variant
            if (intOp == VIntOp.Sub && funct3 == 3)
                throw new IllegalInstructionException(pc, raw, "vsub.vi is not a valid instruction");
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVIntAluVV(intOp.Value, vd, vs2, vs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVIntAluVI(intOp.Value, vd, vs2, SignExtend5(vs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [vs1,], ToothClass.Vector,
                    new RvVIntAluVX(intOp.Value, vd, vs2, vs1, masked)
                ),
            };
        }

        if (cmpOp.HasValue) {
            // vmsgtu/vmsgt: VX and VI only, not VV
            if (cmpOp is VMaskCmpOp.Gtu or VMaskCmpOp.Gt && funct3 == 0)
                throw new IllegalInstructionException(
                    pc, raw, $"vmsgtu/vmsgt.vv is not a valid encoding"
                );
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVMaskCmpVV(cmpOp.Value, vd, vs2, vs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVMaskCmpVI(cmpOp.Value, vd, vs2, SignExtend5(vs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [vs1,], ToothClass.Vector,
                    new RvVMaskCmpVX(cmpOp.Value, vd, vs2, vs1, masked)
                ),
            };
        }

        throw new IllegalInstructionException(
            pc, raw, $"V op: unknown funct6=0x{funct6:X2} funct3=0x{funct3:X}"
        );
    }

    // OPCFG: vsetvli / vsetivli / vsetvl.
    private static RvInstruction DecodeVCfg(ulong pc, uint raw, int rd, int rs1, int rs2) {
        uint bits31 = raw >> 31;
        uint bits3130 = (raw >> 30) & 0x3;

        if (bits31 == 0) {
            // vsetvli: bit[31]=0, vtypei = bits[30:20] (11 bits)
            var vtypei = (int)((raw >> 20) & 0x7FF);
            IReadOnlyList<int> sources = rs1 != 0 ? [rs1,] : [];
            return new RvInstruction(pc, raw, rd, sources, ToothClass.Vector, new RvVsetvli(rd, rs1, vtypei));
        }

        if (bits3130 == 3) {
            // vsetivli: bits[31:30]=11, vtypei = bits[29:20] (10 bits), zimm = bits[19:15]
            var vtypei = (int)((raw >> 20) & 0x3FF);
            var zimm = (int)((raw >> 15) & 0x1F);
            return new RvInstruction(pc, raw, rd, [], ToothClass.Vector, new RvVsetivli(rd, zimm, vtypei));
        }

        // vsetvl: bits[31:25]=1000000
        return new RvInstruction(pc, raw, rd, [rs1, rs2,], ToothClass.Vector, new RvVsetvl(rd, rs1, rs2));
    }

    private static int SignExtend5(int value) =>
        (value & 0x10) != 0 ? value | unchecked((int)0xFFFFFFE0) : value & 0x1F;

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

    private static ITooth DecodeCompressed(ulong pc, ushort c) {
        var q = (uint)(c & 0x3);
        var funct3 = (uint)(c >> 13);
        return q switch {
            0x0 => DecodeCompressedQ0(pc, c, funct3),
            0x1 => DecodeCompressedQ1(pc, c, funct3),
            0x2 => DecodeCompressedQ2(pc, c, funct3),
            _ => throw new IllegalInstructionException(
                pc, c, "Compressed opcode with quadrant 0x3 is a 32-bit instruction"
            ),
        };
    }

    // ── Quadrant 0 ────────────────────────────────────────────────────────────

    private static ITooth DecodeCompressedQ0(ulong pc, ushort c, uint funct3) {
        int rdp = ((c >> 2) & 0x7) + 8;  // rd'  → x8–x15
        int rs1p = ((c >> 7) & 0x7) + 8; // rs1' → x8–x15

        return funct3 switch {
            0x0 => DecodeAddi4spn(pc, c, rdp),
            0x2 => DecodeCLw(pc, c, rdp, rs1p),
            0x3 => DecodeCFlw(pc, c, rdp, rs1p),
            0x6 => DecodeCSw(pc, c, rdp, rs1p),
            0x7 => DecodeCFsw(pc, c, rdp, rs1p),
            _   => throw new IllegalInstructionException(pc, c, $"Unknown C.Q0 funct3=0x{funct3:X}"),
        };
    }

    private static ITooth DecodeAddi4spn(ulong pc, ushort c, int rdp) {
        // CIW: nzuimm[5:4]=c[12:11], nzuimm[9:6]=c[10:7], nzuimm[2]=c[6], nzuimm[3]=c[5]
        int nzuimm = (((c >> 11) & 0x3) << 4)
                   | (((c >> 7) & 0xF) << 6)
                   | (((c >> 6) & 0x1) << 2)
                   | (((c >> 5) & 0x1) << 3);
        if (nzuimm == 0) throw new IllegalInstructionException(pc, c, "C.ADDI4SPN with nzuimm=0 is reserved");
        return C(pc, c, rdp, [2,], ToothClass.IntegerAlu, new RvAddi(rdp, 2, nzuimm));
    }

    private static int ClMemImm(ushort c) =>
        (((c >> 10) & 0x7) << 3) // c[12:10] → uimm[5:3]
      | (((c >> 6) & 0x1) << 2)  // c[6]     → uimm[2]
      | (((c >> 5) & 0x1) << 6); // c[5]     → uimm[6]

    private static ITooth DecodeCLw(ulong pc, ushort c, int rdp, int rs1p) =>
        C(pc, c, rdp, [rs1p,], ToothClass.Load, new RvLw(rdp, rs1p, ClMemImm(c)));

    private static ITooth DecodeCFlw(ulong pc, ushort c, int rdp, int rs1p) =>
        C(pc, c, rdp + 32, [rs1p,], ToothClass.Load, new RvFlw(rdp + 32, rs1p, ClMemImm(c)));

    private static ITooth DecodeCSw(ulong pc, ushort c, int rs2p, int rs1p) =>
        C(pc, c, -1, [rs1p, rs2p,], ToothClass.Store, new RvSw(rs1p, rs2p, ClMemImm(c)));

    private static ITooth DecodeCFsw(ulong pc, ushort c, int rs2p, int rs1p) =>
        C(pc, c, -1, [rs1p, rs2p + 32,], ToothClass.Store, new RvFsw(rs1p, rs2p + 32, ClMemImm(c)));

    // ── Quadrant 1 ────────────────────────────────────────────────────────────

    private static ITooth DecodeCompressedQ1(ulong pc, ushort c, uint funct3) {
        int rd = (c >> 7) & 0x1F;
        int rs1p = ((c >> 7) & 0x7) + 8; // for CB-type restricted registers
        int ci6Imm = SignExtendN((((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F), 6);

        return funct3 switch {
            0x0 => C(pc, c, rd, [rd,], ToothClass.IntegerAlu, new RvAddi(rd, rd, ci6Imm)), // C.NOP / C.ADDI
            0x1 => DecodeCJal(pc, c), // C.JAL (RV32 only) → JAL x1, offset
            0x2 => C(pc, c, rd, [], ToothClass.IntegerAlu, new RvAddi(rd, 0, ci6Imm)), // C.LI → ADDI rd, x0, imm
            0x3 => DecodeQ1Funct3_011(pc, c, rd, ci6Imm),
            0x4 => DecodeQ1Funct3_100(pc, c, rs1p, ci6Imm),
            0x5 => DecodeCJ(pc, c), // C.J → JAL x0, offset
            0x6 => C(pc, c, -1, [rs1p, 0,], ToothClass.ConditionalBranch, new RvBeq(rs1p, 0, CBranchImm(c))), // C.BEQZ
            0x7 => C(pc, c, -1, [rs1p, 0,], ToothClass.ConditionalBranch, new RvBne(rs1p, 0, CBranchImm(c))), // C.BNEZ
            _   => throw new IllegalInstructionException(pc, c, $"Unknown C.Q1 funct3=0x{funct3:X}"),
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

    private static ITooth DecodeCJal(ulong pc, ushort c) =>
        C(pc, c, 1, [], ToothClass.Branch, new RvJal(1, CJumpOffset(c)));

    private static ITooth DecodeCJ(ulong pc, ushort c) =>
        C(pc, c, 0, [], ToothClass.Branch, new RvJal(0, CJumpOffset(c)));

    private static ITooth DecodeQ1Funct3_011(ulong pc, ushort c, int rd, int ci6Imm) {
        if (rd == 2) {
            // C.ADDI16SP: nzimm[9]=c[12], [4]=c[6], [6]=c[5], [8:7]=c[4:3], [5]=c[2]
            int nzimm = (((c >> 12) & 0x1) << 9)
                      | (((c >> 6) & 0x1) << 4)
                      | (((c >> 5) & 0x1) << 6)
                      | (((c >> 3) & 0x3) << 7)
                      | (((c >> 2) & 0x1) << 5);
            nzimm = SignExtendN(nzimm, 10);
            if (nzimm == 0) throw new IllegalInstructionException(pc, c, "C.ADDI16SP with nzimm=0 is reserved");
            return C(pc, c, 2, [2,], ToothClass.IntegerAlu, new RvAddi(2, 2, nzimm));
        }

        // C.LUI: nzimm[17]=c[12], nzimm[16:12]=c[6:2] → placed at bits [17:12]
        int raw6 = (((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F);
        int nzimmLui = SignExtendN(raw6, 6) << 12;
        if (nzimmLui == 0) throw new IllegalInstructionException(pc, c, "C.LUI with nzimm=0 is reserved");
        return C(pc, c, rd, [], ToothClass.IntegerAlu, new RvLui(rd, nzimmLui));
    }

    private static ITooth DecodeQ1Funct3_100(ulong pc, ushort c, int rs1p, int ci6Imm) {
        int sub = (c >> 10) & 0x3;
        int shamt = (((c >> 12) & 0x1) << 5) | ((c >> 2) & 0x1F);

        if (sub == 0x0) {
            // C.SRLI → SRLI rs1', rs1', shamt
            if ((shamt & 0x20) != 0)
                throw new IllegalInstructionException(pc, c, "C.SRLI with shamt[5]=1 is reserved for RV32");
            return C(pc, c, rs1p, [rs1p,], ToothClass.IntegerAlu, new RvSrli(rs1p, rs1p, shamt));
        }

        if (sub == 0x1) {
            // C.SRAI → SRAI rs1', rs1', shamt
            if ((shamt & 0x20) != 0)
                throw new IllegalInstructionException(pc, c, "C.SRAI with shamt[5]=1 is reserved for RV32");
            return C(pc, c, rs1p, [rs1p,], ToothClass.IntegerAlu, new RvSrai(rs1p, rs1p, shamt));
        }

        if (sub == 0x2)
            // C.ANDI → ANDI rs1', rs1', imm
            return C(pc, c, rs1p, [rs1p,], ToothClass.IntegerAlu, new RvAndi(rs1p, rs1p, ci6Imm));
        // sub == 0x3: CA-type arithmetic
        if ((c & 0x1000) != 0)
            throw new IllegalInstructionException(pc, c, "C.SUB/XOR/OR/AND with c[12]=1 is reserved");
        int rs2p = ((c >> 2) & 0x7) + 8;
        return ((c >> 5) & 0x3) switch {
            0x0 => C(pc, c, rs1p, [rs1p, rs2p,], ToothClass.IntegerAlu, new RvSub(rs1p, rs1p, rs2p)),
            0x1 => C(pc, c, rs1p, [rs1p, rs2p,], ToothClass.IntegerAlu, new RvXor(rs1p, rs1p, rs2p)),
            0x2 => C(pc, c, rs1p, [rs1p, rs2p,], ToothClass.IntegerAlu, new RvOr(rs1p, rs1p, rs2p)),
            0x3 => C(pc, c, rs1p, [rs1p, rs2p,], ToothClass.IntegerAlu, new RvAnd(rs1p, rs1p, rs2p)),
            _   => throw new IllegalInstructionException(pc, c, $"Unknown CA funct2=0x{(c >> 5) & 0x3:X}"),
        };
    }

    // ── Quadrant 2 ────────────────────────────────────────────────────────────

    private static ITooth DecodeCompressedQ2(ulong pc, ushort c, uint funct3) {
        int rd = (c >> 7) & 0x1F;
        int rs2 = (c >> 2) & 0x1F;

        return funct3 switch {
            0x0 => DecodeCslli(pc, c, rd, rs2),
            0x2 => DecodeCLwsp(pc, c, rd),
            0x3 => DecodeCFlwsp(pc, c, rd),
            0x4 => DecodeQ2Funct3_100(pc, c, rd, rs2),
            0x6 => DecodeCSwsp(pc, c, rs2),
            0x7 => DecodeCFswsp(pc, c, rs2),
            _   => throw new IllegalInstructionException(pc, c, $"Unknown C.Q2 funct3=0x{funct3:X}"),
        };
    }

    private static ITooth DecodeCslli(ulong pc, ushort c, int rd, int rs2) {
        int shamt = (((c >> 12) & 0x1) << 5) | rs2;
        if ((shamt & 0x20) != 0)
            throw new IllegalInstructionException(pc, c, "C.SLLI with shamt[5]=1 is reserved for RV32");
        return C(pc, c, rd, [rd,], ToothClass.IntegerAlu, new RvSlli(rd, rd, shamt));
    }

    private static int ClwspImm(ushort c) =>
        (((c >> 12) & 0x1) << 5) // c[12] → uimm[5]
      | (((c >> 4) & 0x7) << 2)  // c[6:4] → uimm[4:2]
      | (((c >> 2) & 0x3) << 6); // c[3:2] → uimm[7:6]

    private static ITooth DecodeCLwsp(ulong pc, ushort c, int rd) {
        if (rd == 0) throw new IllegalInstructionException(pc, c, "C.LWSP with rd=x0 is reserved");
        return C(pc, c, rd, [2,], ToothClass.Load, new RvLw(rd, 2, ClwspImm(c)));
    }

    private static ITooth DecodeCFlwsp(ulong pc, ushort c, int rd) =>
        C(pc, c, rd + 32, [2,], ToothClass.Load, new RvFlw(rd + 32, 2, ClwspImm(c)));

    private static int CswspImm(ushort c) =>
        (((c >> 9) & 0xF) << 2)  // c[12:9] → uimm[5:2]
      | (((c >> 7) & 0x3) << 6); // c[8:7] → uimm[7:6]

    private static ITooth DecodeCSwsp(ulong pc, ushort c, int rs2) =>
        C(pc, c, -1, [2, rs2,], ToothClass.Store, new RvSw(2, rs2, CswspImm(c)));

    private static ITooth DecodeCFswsp(ulong pc, ushort c, int rs2) =>
        C(pc, c, -1, [2, rs2 + 32,], ToothClass.Store, new RvFsw(2, rs2 + 32, CswspImm(c)));

    private static ITooth DecodeQ2Funct3_100(ulong pc, ushort c, int rd, int rs2) {
        bool inst12 = (c & 0x1000) != 0;
        if (!inst12 && rs2 == 0) {
            // C.JR → JALR x0, 0(rs1)
            if (rd == 0) throw new IllegalInstructionException(pc, c, "C.JR with rs1=x0 is reserved");
            return C(pc, c, 0, [rd,], ToothClass.Branch, new RvJalr(0, rd, 0));
        }

        if (!inst12)
            // C.MV → ADD rd, x0, rs2
            return C(pc, c, rd, [0, rs2,], ToothClass.IntegerAlu, new RvAdd(rd, 0, rs2));
        if (rd == 0 && rs2 == 0)
            // C.EBREAK
            return C(pc, c, -1, [], ToothClass.System, new RvEbreak());
        if (rs2 == 0)
            // C.JALR → JALR x1, 0(rs1)
            return C(pc, c, 1, [rd,], ToothClass.Branch, new RvJalr(1, rd, 0));
        // C.ADD → ADD rd, rd, rs2
        return C(pc, c, rd, [rd, rs2,], ToothClass.IntegerAlu, new RvAdd(rd, rd, rs2));
    }

    // ── Immediate helpers ─────────────────────────────────────────────────────

    private static int SignExtend12(int value) =>
        (value & 0x800) != 0 ? value | unchecked((int)0xFFFFF000) : value & 0xFFF;

    private static int SignExtendN(int value, int bits) {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }
}