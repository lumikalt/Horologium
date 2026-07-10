using Mechanism;

namespace RiscV32.Decode;

/// <summary>
/// Instruction decoder with a (PC, raw) keyed cache.
/// Safe for all programs — keying on both PC and raw encoding avoids false hits when
/// two different instructions happen to share the same PC (e.g. in unit tests).
/// </summary>
public class Rv32Decoder : IDecoder {
    private readonly Dictionary<(ulong pc, uint raw), ITooth> _cache = new();
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

            return q switch {
                // c.jal (RV32): PC-relative call with statically known target.
                0x1 when cfunct3 == 0x1 => new FetchHint {
                    InstructionSize = 2,
                    IsBranch = true,
                    IsCall = true,
                    BranchTarget = ((ulong)((long)pc + CJumpOffset(c)), true),
                },
                // c.j: unconditional PC-relative jump.
                0x1 when cfunct3 == 0x5 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, BranchTarget = ((ulong)((long)pc + CJumpOffset(c)), true),
                },
                // c.beqz / c.bnez: PC-relative conditional branches.
                0x1 when cfunct3 == 0x6 || cfunct3 == 0x7 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, BranchTarget = ((ulong)((long)pc + CBranchImm(c)), true),
                },
                // c.jalr: register-indirect call — target not statically known.
                0x2 when cfunct3 == 0x4 && inst12 && crs2 == 0 && crs1 != 0 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsCall = true,
                },
                // c.jr / c.ret: register-indirect return — target not statically known.
                0x2 when cfunct3 == 0x4 && !inst12 && crs2 == 0 && crs1 != 0 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsReturn = crs1 is 1 or 5,
                },
                _ => new FetchHint { InstructionSize = 2, },
            };
        }

        var opcode = (int)(firstWord & 0x7F);
        var rd = (int)((firstWord >> 7) & 0x1F);
        var rs1 = (int)((firstWord >> 15) & 0x1F);
        bool isJal = opcode == 0x6F;
        bool isJalr = opcode == 0x67;
        bool linkRd = rd is 1 or 5;
        bool linkRs1 = rs1 is 1 or 5;

        (ulong Value, bool HasValue) branchTarget = default;
        if (opcode == 0x63) {
            // B-type: imm[12|10:5] in bits[31:25], imm[4:1|11] in bits[11:7]
            var bImm = (int)(
                (((firstWord >> 31) & 1) << 12) |
                (((firstWord >> 7) & 1) << 11) |
                (((firstWord >> 25) & 0x3F) << 5) |
                (((firstWord >> 8) & 0xF) << 1));
            branchTarget = ((ulong)((long)pc + SignExtendN(bImm, 13)), true);
        }
        else if (isJal) {
            // J-type: imm[20|10:1|11|19:12] scattered across bits[31:12]
            var jImm = (int)(
                (((firstWord >> 31) & 1) << 20) |
                (((firstWord >> 12) & 0xFF) << 12) |
                (((firstWord >> 20) & 1) << 11) |
                (((firstWord >> 21) & 0x3FF) << 1));
            branchTarget = ((ulong)((long)pc + SignExtendN(jImm, 21)), true);
        }
        // JALR: register-indirect — target not statically known; branchTarget stays default.

        return new FetchHint {
            InstructionSize = 4,
            IsBranch = opcode is 0x63 || isJal || isJalr,
            IsCall = (isJal || isJalr) && linkRd,
            IsReturn = isJalr && linkRs1 && !linkRd,
            BranchTarget = branchTarget,
        };
    }

    public virtual int InstructionSize(ulong pc, IMemory memory) {
        var half = (ushort)memory.Read(pc, 2);
        return (half & 0x3) != 0x3 ? 2 : 4;
    }

    public virtual ITooth Decode(ulong pc, IMemory memory) {
        var half = (ushort)memory.Read(pc, 2);
        if ((half & 0x3) != 0x3)
            return _cache.TryGetValue((pc, half), out ITooth? c) ? c : Cache(pc, half, DecodeCompressed(pc, half));

        var raw = (uint)memory.Read(pc, 4);
        return _cache.TryGetValue((pc, raw), out ITooth? cached) ? cached : Cache(pc, raw, DecodeRaw(pc, raw));
    }

    public virtual ITooth Decode(ulong pc, uint raw) => _cache.TryGetValue((pc, raw), out ITooth? cached)
        ? cached
        : Cache(pc, raw, DecodeRaw(pc, raw));

    public string Disassemble(ulong pc, uint raw) {
        try {
            ITooth tooth = Decode(pc, raw);
            return RvDisassembler.Disassemble(((RvInstruction)tooth).Payload, pc);
        }
        catch { return $"0x{raw:X8}"; }
    }

    private ITooth Cache(ulong pc, uint raw, ITooth tooth) {
        _cache[(pc, raw)] = tooth;
        return tooth;
    }

    protected virtual ITooth DecodeRaw(ulong pc, uint raw) {
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
            0x0F => DecodeMiscMem(pc, raw, rs1, funct3),
            // F and V extension load/store (share opcodes 0x07/0x27, disambiguated by funct3)
            0x07 => DecodeFpOrVLoad(pc, raw, rd, rs1, funct3, raw),
            0x27 => DecodeFpOrVStore(pc, raw, rd, rs1, rs2, funct3, raw),
            0x53 => DecodeFpOp(pc, raw, rd, rs1, rs2, funct3, funct7),
            // V extension arithmetic/config
            0x57 => DecodeVOp(pc, raw, rs1, funct3),
            0x43 or 0x47 or 0x4B or 0x4F =>
                DecodeFmaR4(pc, raw, opcode, rd, rs1, rs2, (int)((raw >> 27) & 0x1F)),
            // UVE extension: custom-0 (stream setup), custom-1 (stream ops)
            0x0B => DecodeUveSetup(pc, raw),
            0x2B => DecodeUveOp(pc, raw),
            _ => throw new IllegalInstructionException(
                raw,
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
                    raw,
                    $"Unknown M-extension funct3=0x{funct3:X}"
                ),
            };
            return new RvInstruction(pc, raw, rd, sources, ToothClass.IntegerMulDiv, mop);
        }

        RvOp op = (funct3, funct7) switch {
            // Base integer (I)
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
            // Zbc carry-less multiply (funct7=0x05, funct3=1/2/3; no overlap with Zbb min/max funct3=4–7)
            (0x1, 0x05) => new RvClmul(rd, rs1, rs2),
            (0x2, 0x05) => new RvClmulr(rd, rs1, rs2),
            (0x3, 0x05) => new RvClmulh(rd, rs1, rs2),
            // Zba address generation (funct7=0x10): rd = rs2 + (rs1 << N)
            (0x2, 0x10) => new RvSh1Add(rd, rs1, rs2),
            (0x4, 0x10) => new RvSh2Add(rd, rs1, rs2),
            (0x6, 0x10) => new RvSh3Add(rd, rs1, rs2),
            // Zbs single-bit (R-type)
            (0x1, 0x24) => new RvBclr(rd, rs1, rs2),
            (0x5, 0x24) => new RvBext(rd, rs1, rs2),
            (0x1, 0x34) => new RvBinv(rd, rs1, rs2),
            (0x1, 0x14) => new RvBset(rd, rs1, rs2),
            // Zicond (funct7=0x07)
            (0x5, 0x07) => new RvCzeroEqz(rd, rs1, rs2),
            (0x7, 0x07) => new RvCzeroNez(rd, rs1, rs2),
            // Zbb logical-with-negate (funct7=0x20, distinct funct3 from SUB)
            (0x7, 0x20) => new RvAndn(rd, rs1, rs2),
            (0x6, 0x20) => new RvOrn(rd, rs1, rs2),
            (0x4, 0x20) => new RvXnor(rd, rs1, rs2),
            // Zbb min/max (funct7=0x05)
            (0x4, 0x05) => new RvMin(rd, rs1, rs2),
            (0x5, 0x05) => new RvMinu(rd, rs1, rs2),
            (0x6, 0x05) => new RvMax(rd, rs1, rs2),
            (0x7, 0x05) => new RvMaxu(rd, rs1, rs2),
            // Zbb rotate (funct7=0x30)
            (0x1, 0x30) => new RvRol(rd, rs1, rs2),
            (0x5, 0x30) => new RvRor(rd, rs1, rs2),
            // Zbb zero-extend halfword (funct7=0x04, rs2=0)
            (0x4, 0x04) when rs2 == 0 => new RvZextH(rd, rs1),
            _ => throw new IllegalInstructionException(
                raw,
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
            // SLLI space: funct7 selects base SLLI, Zbb unary ops, or Zbs immediate ops
            0x1 => funct7 switch {
                0x00 => new RvSlli(rd, rs1, (int)shamt),
                0x14 => new RvBseti(rd, rs1, (int)shamt),
                0x24 => new RvBclri(rd, rs1, (int)shamt),
                0x30 => shamt switch {
                    0 => new RvClz(rd, rs1),
                    1 => new RvCtz(rd, rs1),
                    2 => new RvCpop(rd, rs1),
                    4 => new RvSextB(rd, rs1),
                    5 => new RvSextH(rd, rs1),
                    _ => throw new IllegalInstructionException(
                        raw, $"Unknown Zbb unary op shamt=0x{shamt:X}"
                    ),
                },
                0x34 => new RvBinvi(rd, rs1, (int)shamt),
                _ => throw new IllegalInstructionException(
                    raw, $"Unknown OP-IMM funct3=1 funct7=0x{funct7:X}"
                ),
            },
            // SRLI/SRAI space: Zbb and Zbs immediate ops
            0x5 => funct7 switch {
                0x00 => new RvSrli(rd, rs1, (int)shamt),
                0x20 => new RvSrai(rd, rs1, (int)shamt),
                0x14 => new RvOrcB(rd, rs1),
                0x24 => new RvBexti(rd, rs1, (int)shamt),
                0x30 => new RvRori(rd, rs1, (int)shamt),
                0x34 => new RvRev8(rd, rs1),
                _ => throw new IllegalInstructionException(
                    raw, $"Unknown OP-IMM funct3=5 funct7=0x{funct7:X}"
                ),
            },
            _ => throw new IllegalInstructionException(
                raw,
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
                raw,
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
                raw,
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
                raw,
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

        switch (funct3) {
            // ECALL / EBREAK / SRET / MRET / WFI / Zawrs / SFENCE.VMA
            case 0x0:
                // SFENCE.VMA: funct7 = 0001001 (0x09) — TLB flush, no-op in NOMMU simulation
                if (word >> 25 == 0x09) return new RvInstruction(pc, raw, -1, [], ToothClass.Fence, new RvSfenceVma());
                return (word >> 20) switch {
                    0x000 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvEcall()),
                    0x001 => new RvInstruction(pc, raw, -1, [], ToothClass.Halt, new RvEbreak()),
                    0x102 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvSret()),
                    0x105 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvWfi()),
                    0x302 => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvMret()),
                    // Zawrs (single-core: NOP)
                    0x00D => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvWrsNto()),
                    0x01D => new RvInstruction(pc, raw, -1, [], ToothClass.System, new RvWrsSto()),
                    _ => throw new IllegalInstructionException(
                        raw,
                        $"Unknown SYSTEM instruction at PC=0x{pc:X8}"
                    ),
                };
            // Zimop: funct3=4 is unused by standard CSR encoding; pattern-detect by CSR address bits.
            case 0x4: {
                if ((csr & 0xB3C) == 0x81C)
                    return new RvInstruction(pc, raw, rd, [], ToothClass.IntegerAlu, new RvMopR(rd));
                if ((csr & 0xB3F) == 0x823)
                    return new RvInstruction(pc, raw, rd, [], ToothClass.IntegerAlu, new RvMopRr(rd));
                throw new IllegalInstructionException(raw, $"Unknown SYSTEM funct3=4 csr=0x{csr:X3}");
            }
        }

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
                raw,
                $"Unknown SYSTEM funct3=0x{funct3:X}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.System, op);
    }

    // ── MISC-MEM (opcode=0x0F): FENCE, Zicbom, Zicboz ───────────────────────────

    private static RvInstruction DecodeMiscMem(ulong pc, uint raw, int rs1, uint funct3) {
        switch (funct3) {
            case 0x0: {
                uint fm = (raw >> 28) & 0xF;
                uint pred = (raw >> 24) & 0xF;
                uint succ = (raw >> 20) & 0xF;
                return new RvInstruction(pc, raw, -1, [], ToothClass.Fence, new RvFence(pred, succ, fm));
            }
            case 0x1: return new RvInstruction(pc, raw, -1, [], ToothClass.Fence, new RvFenceI());
            case 0x2: {
                uint op = (raw >> 20) & 0x1F; // bits[24:20] select cbo operation
                IReadOnlyList<int> src = [rs1,];
                return op switch {
                    0x00 => new RvInstruction(pc, raw, -1, src, ToothClass.Fence, new RvCboInval(rs1)),
                    0x01 => new RvInstruction(pc, raw, -1, src, ToothClass.Fence, new RvCboClean(rs1)),
                    0x02 => new RvInstruction(pc, raw, -1, src, ToothClass.Fence, new RvCboFlush(rs1)),
                    0x04 => new RvInstruction(pc, raw, -1, src, ToothClass.Store, new RvCboZero(rs1)),
                    _    => throw new IllegalInstructionException(raw, $"Unknown CBO op bits[24:20]=0x{op:X2}"),
                };
            }
            default: throw new IllegalInstructionException(raw, $"Unknown MISC-MEM funct3=0x{funct3:X}");
        }
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
        // Zabha: byte (.b) and halfword (.h) AMOs — funct3=0 and funct3=1 respectively.
        if (funct3 is 0x0 or 0x1) return DecodeZabha(pc, raw, rd, rs1, rs2, funct3, funct5);

        if (funct3 != 0x2)
            throw new IllegalInstructionException(
                raw,
                $"AMO with unsupported funct3=0x{funct3:X}"
            );

        // Zacas amocas.w: rd is also a source (it's the comparand).
        if (funct5 == 0x05)
            return new RvInstruction(
                pc, raw, rd, [rs1, rs2, rd,], ToothClass.Atomic,
                new RvAmocasW(rd, rs1, rs2)
            );

        // LR.W reads only the address register; all other word AMOs use rs1 and rs2.
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
                raw,
                $"Unknown AMO funct5=0x{funct5:X2}"
            ),
        };
        return new RvInstruction(pc, raw, rd, sources, ToothClass.Atomic, op);
    }

    private static RvInstruction DecodeZabha(
        ulong pc,
        uint raw,
        int rd,
        int rs1,
        int rs2,
        uint funct3,
        uint funct5
    ) {
        bool isByte = funct3 == 0x0;
        RvOp op = funct5 switch {
            0x01 => isByte ? new RvAmoswapB(rd, rs1, rs2) : new RvAmoswapH(rd, rs1, rs2),
            0x00 => isByte ? new RvAmoaddB(rd, rs1, rs2) : new RvAmoaddH(rd, rs1, rs2),
            0x04 => isByte ? new RvAmoxorB(rd, rs1, rs2) : new RvAmoxorH(rd, rs1, rs2),
            0x0C => isByte ? new RvAmoandB(rd, rs1, rs2) : new RvAmoandH(rd, rs1, rs2),
            0x08 => isByte ? new RvAmoorB(rd, rs1, rs2) : new RvAmoorH(rd, rs1, rs2),
            0x10 => isByte ? new RvAmominB(rd, rs1, rs2) : new RvAmominH(rd, rs1, rs2),
            0x14 => isByte ? new RvAmomaxB(rd, rs1, rs2) : new RvAmomaxH(rd, rs1, rs2),
            0x18 => isByte ? new RvAmominuB(rd, rs1, rs2) : new RvAmominuH(rd, rs1, rs2),
            0x1C => isByte ? new RvAmomaxuB(rd, rs1, rs2) : new RvAmomaxuH(rd, rs1, rs2),
            _ => throw new IllegalInstructionException(
                raw,
                $"Zabha: unsupported funct5=0x{funct5:X2}"
            ),
        };
        return new RvInstruction(pc, raw, rd, [rs1, rs2,], ToothClass.Atomic, op);
    }

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
            2 => new RvInstruction(pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store, new RvFsw(rs1, rs2 + 32, imm)),
            3 => new RvInstruction(pc, raw, -1, [rs1, rs2 + 32,], ToothClass.Store, new RvFsd(rs1, rs2 + 32, imm)),
            _ => DecodeVStore(pc, raw, rd, rs1, rs2, funct3, word),
        };
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
        uint mop = (word >> 26) & 0x3; // 00=unit-stride, 01=unordered-indexed, 10=strided, 11=ordered-indexed
        bool masked = ((word >> 25) & 1) == 0;

        switch (mop) {
            case 2: {
                // Strided: bits[24:20] = rs2 (stride register)
                var rs2 = (int)((word >> 20) & 0x1F);
                int sew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V strided load: unsupported funct3=0x{funct3:X}"
                    ),
                };
                var nfS = (int)((word >> 29) & 0x7);
                if (nfS > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1, rs2,], ToothClass.Vector,
                        new RvVlssegVv(nfS + 1, vd, rs1, rs2, sew, masked)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2,], ToothClass.Vector,
                    new RvVlseVv(vd, rs1, rs2, sew, masked)
                );
            }
            case 1 or 3: {
                // Indexed (unordered mop=1, ordered mop=3): bits[24:20] = vs2 (index vector)
                var vs2 = (int)((word >> 20) & 0x1F);
                int indexSew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V indexed load: unsupported index width funct3=0x{funct3:X}"
                    ),
                };
                var nfI = (int)((word >> 29) & 0x7);
                if (nfI > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVlxsegVv(nfI + 1, vd, rs1, vs2, indexSew, masked, mop == 3)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVlxeiVv(vd, rs1, vs2, indexSew, masked, mop == 3)
                );
            }
        }

        if (mop != 0)
            throw new IllegalInstructionException(
                raw, $"V load: mop={mop} not supported"
            );

        uint lumop = (word >> 20) & 0x1F; // unit-stride sub-mode

        // VLM: funct3=0 + lumop=01011
        if (funct3 == 0 && lumop == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVlm(vd, rs1));

        // VL1R/2R/4R/8R: lumop=8, nf in bits[31:29] (0→1, 1→2, 3→4, 7→8)
        if (lumop == 0x08)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVlrV((int)((word >> 29) & 0x7) + 1, vd, rs1)
            );

        int sewU = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                raw, $"V load: unsupported element width funct3=0x{funct3:X}"
            ),
        };

        // VLE{8,16,32}FF: lumop=0x10, fault-only-first (modeled as regular vle)
        if (lumop == 0x10)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVleFf(vd, rs1, sewU, masked)
            );

        var nf = (int)((word >> 29) & 0x7);
        if (nf > 0)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVlsegVv(nf + 1, vd, rs1, sewU, masked)
            );
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVleVv(vd, rs1, sewU, masked));
    }

    // VSE8/16/32, VSM, and VSSE8/16/32: opcode=0x27, funct3 ≠ 2.
    // rd (bits[11:7]) = vs3, rs2 (bits[24:20]) = sumop or stride register.
    private static RvInstruction DecodeVStore(
        ulong pc,
        uint raw,
        int vs3, // bits[11:7]
        int rs1,
        int rs2Field, // bits[24:20]: sumop for unit-stride, rs2 for strided
        uint funct3,
        uint word
    ) {
        uint mop = (word >> 26) & 0x3;
        bool masked = ((word >> 25) & 1) == 0;

        switch (mop) {
            case 2: {
                // Strided: rs2Field is the stride register
                int sew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V strided store: unsupported funct3=0x{funct3:X}"
                    ),
                };
                var nfSs = (int)((word >> 29) & 0x7);
                if (nfSs > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1, rs2Field,], ToothClass.Vector,
                        new RvVsssegVv(nfSs + 1, vs3, rs1, rs2Field, sew, masked)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2Field,], ToothClass.Vector,
                    new RvVsseVv(vs3, rs1, rs2Field, sew, masked)
                );
            }
            case 1:
            case 3: {
                // Indexed: bits[24:20] = vs2 (index vector); rs2Field already holds that value
                int indexSew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V indexed store: unsupported index width funct3=0x{funct3:X}"
                    ),
                };
                var nfSx = (int)((word >> 29) & 0x7);
                if (nfSx > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVsxsegVv(nfSx + 1, vs3, rs1, rs2Field, indexSew, masked, mop == 3)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVsxeiVv(vs3, rs1, rs2Field, indexSew, masked, mop == 3)
                );
            }
        }

        if (mop != 0)
            throw new IllegalInstructionException(
                raw, $"V store: mop={mop} not supported"
            );

        // VSM: funct3=0 + sumop=01011
        if (funct3 == 0 && rs2Field == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVsm(vs3, rs1));

        // VS1R/2R/4R/8R: sumop=8, nf in bits[31:29] (0→1, 1→2, 3→4, 7→8)
        if (rs2Field == 0x08)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVsrV((int)((word >> 29) & 0x7) + 1, vs3, rs1)
            );

        int sewU = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                raw, $"V store: unsupported element width funct3=0x{funct3:X}"
            ),
        };
        var nf = (int)((word >> 29) & 0x7);
        if (nf > 0)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVssegVv(nf + 1, vs3, rs1, sewU, masked)
            );
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVseVv(vs3, rs1, sewU, masked));
    }

    // OPIVV / OPIVX / OPIVI / OPCFG: opcode=0x57.
    private static RvInstruction DecodeVOp(
        ulong pc,
        uint raw,
        int rs1,
        uint funct3
    ) {
        var vd = (int)((raw >> 7) & 0x1F);
        var vs2 = (int)((raw >> 20) & 0x1F);
        bool masked = ((raw >> 25) & 1) == 0;
        uint funct6 = (raw >> 26) & 0x3F;

        switch (funct3) {
            // OPCFG (funct3=7)
            case 7: return DecodeVCfg(pc, raw, vd, rs1, vs2);

            // OPMVV (funct3=2): reductions, vmv.x.s, vcpop.m, vfirst.m, mask-unary, vcompress, …
            case 2: {
                switch (funct6) {
                    // VWXUNARY0: disambiguation by vs1 field
                    case 0x10 when rs1 == 0:
                        return new RvInstruction(pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVMvXs(vd, vs2));
                    case 0x10 when rs1 == 16:
                        return new RvInstruction(
                            pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVcpop(vd, vs2, masked)
                        );
                    case 0x10 when rs1 == 17:
                        return new RvInstruction(
                            pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVfirst(vd, vs2, masked)
                        );
                    case 0x10: throw new IllegalInstructionException(raw, $"V VWXUNARY0: unknown vs1=0x{rs1:X}");
                    case 0x14: {
                        // VMUNARY0: disambiguation by vs1 field
                        VMaskUnaryOp? unaryOp = rs1 switch {
                            1  => VMaskUnaryOp.Msbf,
                            2  => VMaskUnaryOp.Msof,
                            3  => VMaskUnaryOp.Msif,
                            16 => VMaskUnaryOp.Iota,
                            17 => VMaskUnaryOp.Id,
                            _  => null,
                        };
                        if (!unaryOp.HasValue)
                            throw new IllegalInstructionException(raw, $"V VMUNARY0: unknown vs1=0x{rs1:X}");
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVMaskUnary(unaryOp.Value, vd, vs2, masked)
                        );
                    }
                    case 0x17:
                        return new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVCompress(vd, vs2, rs1));
                }

                // vaaddu/vaadd/vasubu/vasub (fixed-point averaging): funct6=0x08-0x0B
                VAvgOp? avgOpVv = funct6 switch {
                    0x08 => VAvgOp.Addu, 0x09 => VAvgOp.Add,
                    0x0A => VAvgOp.Subu, 0x0B => VAvgOp.Sub,
                    _    => null,
                };
                if (avgOpVv.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVAvgVv(avgOpVv.Value, vd, vs2, rs1, masked)
                    );

                // vzext/vsext (VXUNARY0): funct6=0x12, vs1 field selects factor and sign
                if (funct6 == 0x12) {
                    bool extSigned = (rs1 & 1) != 0;
                    int factor = rs1 switch {
                        2 or 3 => 8, 4 or 5 => 4, 6 or 7 => 2,
                        _      => throw new IllegalInstructionException(raw, $"V VXUNARY0: unknown vs1=0x{rs1:X}"),
                    };
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVExt(extSigned, factor, vd, vs2, masked)
                    );
                }

                VRedOp? redOp = funct6 switch {
                    0 => VRedOp.Sum, 1  => VRedOp.And,
                    2 => VRedOp.Or, 3   => VRedOp.Xor,
                    4 => VRedOp.Minu, 5 => VRedOp.Min,
                    6 => VRedOp.Maxu, 7 => VRedOp.Max,
                    _ => null,
                };
                if (redOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRedVs(redOp.Value, vd, vs2, rs1, masked)
                    );
                VMulOp? mulOp = funct6 switch {
                    0x25 => VMulOp.Mul,
                    0x27 => VMulOp.MulH,
                    0x24 => VMulOp.MulHu,
                    0x26 => VMulOp.MulHsu,
                    0x21 => VMulOp.Div,
                    0x20 => VMulOp.Divu,
                    0x23 => VMulOp.Rem,
                    0x22 => VMulOp.Remu,
                    _    => null,
                };
                if (mulOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMulVv(mulOp.Value, vd, vs2, rs1, masked)
                    );
                // Widening add/sub (funct6 0x30-0x37) and widening mul (0x38, 0x3A, 0x3B)
                VWideOp? wideOpMvv = funct6 switch {
                    0x30 => VWideOp.AddU, 0x31 => VWideOp.Add,
                    0x32 => VWideOp.SubU, 0x33 => VWideOp.Sub,
                    0x34 => VWideOp.AddU, 0x35 => VWideOp.Add, // .wv: vs2 is 2*SEW
                    0x36 => VWideOp.SubU, 0x37 => VWideOp.Sub, // .wv: vs2 is 2*SEW
                    0x38 => VWideOp.MulU, 0x3A => VWideOp.MulSu, 0x3B => VWideOp.Mul,
                    _    => null,
                };
                if (wideOpMvv.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVWideVv(wideOpMvv.Value, vd, vs2, rs1, masked, funct6 is >= 0x34 and <= 0x37)
                    );
                VIntMacOp? macOp = funct6 switch {
                    0x29 => VIntMacOp.Madd,
                    0x2B => VIntMacOp.Nmsub,
                    0x2D => VIntMacOp.Macc,
                    0x2F => VIntMacOp.Nmsac,
                    _    => null,
                };
                if (macOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVIntMacVv(macOp.Value, vd, vs2, rs1, masked)
                    );
                VMaskLogOp? logOp = funct6 switch {
                    0x18 => VMaskLogOp.Andn,
                    0x19 => VMaskLogOp.And,
                    0x1A => VMaskLogOp.Or,
                    0x1B => VMaskLogOp.Xor,
                    0x1C => VMaskLogOp.Orn,
                    0x1D => VMaskLogOp.Nand,
                    0x1E => VMaskLogOp.Nor,
                    0x1F => VMaskLogOp.Xnor,
                    _    => null,
                };
                if (logOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMaskLogMm(logOp.Value, vd, vs2, rs1)
                    );
                // Widening MAC: vwmaccu=0x3C, vwmacc=0x3D, vwmaccsu=0x3F (OPMVV; vwmaccus has no VV form)
                VwMacOp? wMacOp = funct6 switch {
                    0x3C => VwMacOp.Maccu,
                    0x3D => VwMacOp.Macc,
                    0x3F => VwMacOp.Maccsu,
                    _    => null,
                };
                if (wMacOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVwMacVv(wMacOp.Value, vd, vs2, rs1, masked)
                    );
                throw new IllegalInstructionException(raw, $"V op: unsupported OPMVV funct6=0x{funct6:X2}");
            }

            // OPMVX (funct3=6): integer multiply/divide VX
            case 6: {
                // vaaddu/vaadd/vasubu/vasub VX: funct6=0x08-0x0B
                VAvgOp? avgOpVx = funct6 switch {
                    0x08 => VAvgOp.Addu, 0x09 => VAvgOp.Add,
                    0x0A => VAvgOp.Subu, 0x0B => VAvgOp.Sub,
                    _    => null,
                };
                if (avgOpVx.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVAvgVx(avgOpVx.Value, vd, vs2, rs1, masked)
                    );

                VMulOp? mulOp = funct6 switch {
                    0x25 => VMulOp.Mul,
                    0x27 => VMulOp.MulH,
                    0x24 => VMulOp.MulHu,
                    0x26 => VMulOp.MulHsu,
                    0x21 => VMulOp.Div,
                    0x20 => VMulOp.Divu,
                    0x23 => VMulOp.Rem,
                    0x22 => VMulOp.Remu,
                    _    => null,
                };
                if (mulOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVMulVx(mulOp.Value, vd, vs2, rs1, masked)
                    );
                // Widening add/sub/mul VX variants (same funct6 as OPMVV)
                VWideOp? wideOpMvx = funct6 switch {
                    0x30 => VWideOp.AddU, 0x31 => VWideOp.Add,
                    0x32 => VWideOp.SubU, 0x33 => VWideOp.Sub,
                    0x34 => VWideOp.AddU, 0x35 => VWideOp.Add, // .wx: vs2 is 2*SEW
                    0x36 => VWideOp.SubU, 0x37 => VWideOp.Sub,
                    0x38 => VWideOp.MulU, 0x3A => VWideOp.MulSu, 0x3B => VWideOp.Mul,
                    _    => null,
                };
                if (wideOpMvx.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVWideVx(wideOpMvx.Value, vd, vs2, rs1, masked, funct6 is >= 0x34 and <= 0x37)
                    );
                switch (funct6) {
                    // vslide1up.vx (0x0E) / vslide1down.vx (0x0F)
                    case 0x0E:
                    case 0x0F:
                        return new RvInstruction(
                            pc, raw, -1, [rs1,], ToothClass.Vector,
                            new RvVSlideVx(funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down, true, vd, vs2, rs1, masked)
                        );
                    // vmv.s.x: move scalar integer rs1 into element 0 of vd (vs2 field must be 0)
                    case 0x10: return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMvSx(vd, rs1));
                }

                VIntMacOp? macOpX = funct6 switch {
                    0x29 => VIntMacOp.Madd,
                    0x2B => VIntMacOp.Nmsub,
                    0x2D => VIntMacOp.Macc,
                    0x2F => VIntMacOp.Nmsac,
                    _    => null,
                };
                if (macOpX.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVIntMacVx(macOpX.Value, vd, vs2, rs1, masked)
                    );
                // Widening MAC VX: vwmaccu=0x3C, vwmacc=0x3D, vwmaccus=0x3E, vwmaccsu=0x3F
                VwMacOp? wMacOpX = funct6 switch {
                    0x3C => VwMacOp.Maccu,
                    0x3D => VwMacOp.Macc,
                    0x3E => VwMacOp.Maccus,
                    0x3F => VwMacOp.Maccsu,
                    _    => null,
                };
                if (wMacOpX.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVwMacVx(wMacOpX.Value, vd, vs2, rs1, masked)
                    );
                throw new IllegalInstructionException(raw, $"V op: unsupported OPMVX funct6=0x{funct6:X2}");
            }
        }

        // OPFVV (funct3=1) and OPFVF (funct3=5): FP vector ops
        if (funct3 is 1 or 5) {
            bool isVf = funct3 == 5;
            int fpRs1 = rs1 + 32; // unified FRF index for scalar FP source

            switch (funct6) {
                // vfmv.f.s (OPFVV, funct6=0x10): float scalar ← vs2[0]
                case 0x10 when !isVf:
                    return new RvInstruction(
                        pc, raw, vd + 32, [], ToothClass.Vector,
                        new RvVFpMvFs(vd + 32, vs2)
                    );
                // vfmv.s.f (OPFVF, funct6=0x10, vs2=0): vd[0] ← float scalar
                case 0x10 when isVf:
                    return new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpMvSf(vd, fpRs1)
                    );
                // vfmv.v.f (OPFVF, funct6=0x17, vm=1): broadcast scalar float
                // vfmerge.vfm (OPFVF, funct6=0x17, vm=0): FP conditional merge using v0 mask
                case 0x17 when isVf:
                    return masked
                        ? new RvInstruction(pc, raw, -1, [fpRs1,], ToothClass.Vector, new RvVFpMergeVf(vd, vs2, fpRs1))
                        : new RvInstruction(pc, raw, -1, [fpRs1,], ToothClass.Vector, new RvVFpMvVf(vd, fpRs1, false));
            }

            // vfslide1up.vf (0x0E) / vfslide1down.vf (0x0F): FP slide1 with scalar float
            if (isVf && (funct6 == 0x0E || funct6 == 0x0F))
                return new RvInstruction(
                    pc, raw, -1, [fpRs1,], ToothClass.Vector,
                    new RvVFpSlide1Vf(funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down, vd, vs2, fpRs1, masked)
                );

            switch (funct6) {
                // vfcvt.* / vfwcvt.* / vfncvt.* (funct6=0x12): vs1 field selects op
                case 0x12: {
                    VFpCvtOp? cvtOp = rs1 switch {
                        0 => VFpCvtOp.XuFromF,
                        1 => VFpCvtOp.XFromF,
                        2 => VFpCvtOp.FFromXu,
                        3 => VFpCvtOp.FFromX,
                        6 => VFpCvtOp.RtzXuFromF,
                        7 => VFpCvtOp.RtzXFromF,
                        _ => null,
                    };
                    if (cvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpCvt(cvtOp.Value, vd, vs2, masked)
                        );
                    VFpWCvtOp? wCvtOp = rs1 switch {
                        8  => VFpWCvtOp.XuFromF,
                        9  => VFpWCvtOp.XFromF,
                        10 => VFpWCvtOp.FFromXu,
                        11 => VFpWCvtOp.FFromX,
                        12 => VFpWCvtOp.FFromF,
                        14 => VFpWCvtOp.RtzXuFromF,
                        15 => VFpWCvtOp.RtzXFromF,
                        _  => null,
                    };
                    if (wCvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpWCvt(wCvtOp.Value, vd, vs2, masked)
                        );
                    VFpNCvtOp? nCvtOp = rs1 switch {
                        16 => VFpNCvtOp.XuFromF,
                        17 => VFpNCvtOp.XFromF,
                        18 => VFpNCvtOp.FFromXu,
                        19 => VFpNCvtOp.FFromX,
                        20 => VFpNCvtOp.FFromF,
                        21 => VFpNCvtOp.RodFFromF,
                        22 => VFpNCvtOp.RtzXuFromF,
                        23 => VFpNCvtOp.RtzXFromF,
                        _  => null,
                    };
                    if (nCvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpNCvt(nCvtOp.Value, vd, vs2, masked)
                        );
                    throw new IllegalInstructionException(raw, $"V vfcvt: unknown vs1=0x{rs1:X}");
                }
                // vfsqrt.v (funct6=0x13, vs1=0) / vfclass.v (funct6=0x13, vs1=16)
                case 0x13:
                    return rs1 switch {
                        0  => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVFpSqrt(vd, vs2, masked)),
                        16 => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVFpClass(vd, vs2, masked)),
                        _  => throw new IllegalInstructionException(raw, $"V op: funct6=0x13 unknown vs1=0x{rs1:X}"),
                    };
            }

            // FP reductions: odd funct6 0x01/0x03/0x05/0x07 (OPFVV only, no VF form)
            VFpRedOp? fpRedOp = funct6 switch {
                0x01 => VFpRedOp.Usum,
                0x03 => VFpRedOp.Osum,
                0x05 => VFpRedOp.Min,
                0x07 => VFpRedOp.Max,
                _    => null,
            };
            if (fpRedOp.HasValue) {
                if (isVf)
                    throw new IllegalInstructionException(raw, $"V FP reduction has no VF form (funct6=0x{funct6:X2})");
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVFpRedVs(fpRedOp.Value, vd, vs2, rs1, masked)
                );
            }

            VFpBinOp? binOp = funct6 switch {
                0x00 => VFpBinOp.Add, 0x02  => VFpBinOp.Sub,
                0x04 => VFpBinOp.Min, 0x06  => VFpBinOp.Max,
                0x08 => VFpBinOp.Sgnj, 0x09 => VFpBinOp.Sgnjn, 0x0A => VFpBinOp.Sgnjx,
                0x20 => VFpBinOp.Div, 0x24  => VFpBinOp.Mul,
                _    => null,
            };
            if (binOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpBinVf(binOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpBinVv(binOp.Value, vd, vs2, rs1, masked)
                    );

            VFpCmpOp? fpCmpOp = funct6 switch {
                0x18 => VFpCmpOp.Eq, 0x19 => VFpCmpOp.Le,
                0x1B => VFpCmpOp.Lt, 0x1C => VFpCmpOp.Ne,
                0x1D => VFpCmpOp.Gt, 0x1F => VFpCmpOp.Ge,
                _    => null,
            };
            if (fpCmpOp.HasValue) {
                if ((fpCmpOp == VFpCmpOp.Gt || fpCmpOp == VFpCmpOp.Ge) && !isVf)
                    throw new IllegalInstructionException(raw, "vmfgt/vmfge.vv is not a valid encoding");
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVmFpCmpVf(fpCmpOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVmFpCmpVv(fpCmpOp.Value, vd, vs2, rs1, masked)
                    );
            }

            VFpFmaOp? fmaOp = funct6 switch {
                0x28 => VFpFmaOp.Madd, 0x29 => VFpFmaOp.Nmadd,
                0x2A => VFpFmaOp.Msub, 0x2B => VFpFmaOp.Nmsub,
                0x2C => VFpFmaOp.Macc, 0x2D => VFpFmaOp.Nmacc,
                0x2E => VFpFmaOp.Msac, 0x2F => VFpFmaOp.Nmsac,
                _    => null,
            };
            if (fmaOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpFmaVf(fmaOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpFmaVv(fmaOp.Value, vd, vs2, rs1, masked)
                    );

            // vfwredusum.vs (0x31) / vfwredosum.vs (0x33): widening FP sum reduction (OPFVV only)
            if ((funct6 == 0x31 || funct6 == 0x33) && !isVf)
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVFpWideRedVs(funct6 == 0x33, vd, vs2, rs1, masked)
                );

            // Widening FP arithmetic: 0x30=vfwadd.vv, 0x32=vfwsub.vv,
            //   0x34=vfwadd.wv, 0x36=vfwsub.wv, 0x38=vfwmul.vv
            VFpWideArithOp? wArithOp = funct6 switch {
                0x30 => VFpWideArithOp.Add, 0x32 => VFpWideArithOp.Sub,
                0x34 => VFpWideArithOp.Add, 0x36 => VFpWideArithOp.Sub,
                0x38 => VFpWideArithOp.Mul, _    => null,
            };
            if (wArithOp.HasValue) {
                bool vs2Wide = funct6 is 0x34 or 0x36;
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpWArithVf(wArithOp.Value, vd, vs2, fpRs1, vs2Wide, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpWArithVv(wArithOp.Value, vd, vs2, rs1, vs2Wide, masked)
                    );
            }

            // Widening FP MAC: 0x3C=vfwmacc, 0x3D=vfwnmacc, 0x3E=vfwmsac, 0x3F=vfwnmsac
            VFpWMacOp? wMacOp = funct6 switch {
                0x3C => VFpWMacOp.Macc, 0x3D => VFpWMacOp.Nmacc,
                0x3E => VFpWMacOp.Msac, 0x3F => VFpWMacOp.Nmsac,
                _    => null,
            };
            if (wMacOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpWMacVf(wMacOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpWMacVv(wMacOp.Value, vd, vs2, rs1, masked)
                    );

            throw new IllegalInstructionException(
                raw, $"V op: unsupported OPFVV/OPFVF funct6=0x{funct6:X2}"
            );
        }

        // OPIVV (funct3=0), OPIVX (funct3=4), OPIVI (funct3=3)
        if (funct3 is not (0 or 3 or 4))
            throw new IllegalInstructionException(
                raw, $"V op: unsupported funct3=0x{funct3:X}"
            );

        switch (funct6) {
            // vrgather (funct6=0x0C): VV/VX/VI
            case 0x0C:
                return funct3 switch {
                    0 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRgatherVv(vd, vs2, rs1, masked)
                    ),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRgatherVi(vd, vs2, rs1, masked)
                    ), // rs1 field = imm (unsigned uimm5)
                    _ => new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVRgatherVx(vd, vs2, rs1, masked)
                    ),
                };
            // vrgatherei16.vv: funct6=0x0E, OPIVV (funct3=0); u16 index vector regardless of SEW
            case 0x0E when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVRgatherEi16Vv(vd, vs2, rs1, masked)
                );
            // vslideup (funct6=0x0E) / vslidedown (funct6=0x0F): VX and VI
            case 0x0E or 0x0F: {
                VSlideDir dir = funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down;
                return funct3 == 3
                    ? new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVSlideVi(dir, vd, vs2, rs1, masked)
                    ) // rs1 field = uimm5 offset
                    : new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVSlideVx(dir, false, vd, vs2, rs1, masked)
                    );
            }
        }

        // Narrowing shift: funct6=0x2C (vnsrl) or 0x2D (vnsra); uses same funct3 as OPIVV/OPIVX/OPIVI.
        VNarrOp? narrOp = funct6 switch {
            0x2C => VNarrOp.Srl,
            0x2D => VNarrOp.Sra,
            _    => null,
        };
        if (narrOp.HasValue)
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVNarrVv(narrOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVNarrVi(narrOp.Value, vd, vs2, rs1, masked)
                ), // rs1 field = imm
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVNarrVx(narrOp.Value, vd, vs2, rs1, masked)
                ),
            };

        switch (funct6) {
            // vmerge.vvm/vxm/vim: funct6=0x17 with vm=0 (masked=true); same funct6 as vmv.v.* but masked
            case 0x17 when masked:
                return funct3 switch {
                    0 => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVMergeVv(vd, vs2, rs1)),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector, new RvVMergeVi(vd, vs2, SignExtend5(rs1))
                    ),
                    _ => new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMergeVx(vd, vs2, rs1)),
                };
            // vmv{N}r.v: OPIVI (funct3=3) funct6=0x27; imm5 field (rs1) = N-1
            case 0x27 when funct3 == 3:
                return new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVMvNr(rs1 + 1, vd, vs2));
            // vwredsumu.vs (0x30) / vwredsum.vs (0x31): widening integer sum reduction (OPIVV only)
            case 0x30 or 0x31 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVWideRedVs(funct6 == 0x31, vd, vs2, rs1, masked)
                );
        }

        // Saturating: 0x20=vsaddu, 0x21=vsadd, 0x22=vssubu, 0x23=vssub, 0x27=vsmul, 0x2A=vssrl, 0x2B=vssra
        VSatIntOp? satOp = funct6 switch {
            0x20 => VSatIntOp.Saddu,
            0x21 => VSatIntOp.Sadd,
            0x22 => VSatIntOp.Ssubu,
            0x23 => VSatIntOp.Ssub,
            0x27 => VSatIntOp.Smul,
            0x2A => VSatIntOp.Ssrl,
            0x2B => VSatIntOp.Ssra,
            _    => null,
        };
        if (satOp.HasValue) {
            if (funct3 == 3 && satOp.Value is VSatIntOp.Ssub or VSatIntOp.Ssubu)
                throw new IllegalInstructionException(raw, "V: vssub/vssubu has no VI form");
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVSatIntVv(satOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVSatIntVi(satOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVSatIntVx(satOp.Value, vd, vs2, rs1, masked)
                ),
            };
        }

        // Narrowing saturating clip: 0x2E=vnclipu, 0x2F=vnclip
        VnClipOp? clipOp = funct6 switch {
            0x2E => VnClipOp.Clipu,
            0x2F => VnClipOp.Clip,
            _    => null,
        };
        if (clipOp.HasValue)
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVnClipVv(clipOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVnClipVi(clipOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVnClipVx(clipOp.Value, vd, vs2, rs1, masked)
                ),
            };

        VIntOp? intOp = funct6 switch {
            0  => VIntOp.Add,
            2  => VIntOp.Sub,
            3  => VIntOp.Rsub,
            4  => VIntOp.Minu,
            5  => VIntOp.Min,
            6  => VIntOp.Maxu,
            7  => VIntOp.Max,
            9  => VIntOp.And,
            10 => VIntOp.Or,
            11 => VIntOp.Xor,
            23 => VIntOp.Mov, // vmv.v.v / vmv.v.x / vmv.v.i (vm=1, unmasked)
            37 => VIntOp.Sll,
            40 => VIntOp.Srl,
            41 => VIntOp.Sra,
            _  => null,
        };

        VMaskCmpOp? cmpOp = funct6 switch {
            24 => VMaskCmpOp.Eq,
            25 => VMaskCmpOp.Ne,
            26 => VMaskCmpOp.Ltu,
            27 => VMaskCmpOp.Lt,
            30 => VMaskCmpOp.Gtu,
            31 => VMaskCmpOp.Gt,
            _  => null,
        };

        return intOp switch {
            null => cmpOp switch {
                null => throw new IllegalInstructionException(
                    raw, $"V op: unknown funct6=0x{funct6:X2} funct3=0x{funct3:X}"
                ),
                // vmsgtu/vmsgt: VX and VI only, not VV
                VMaskCmpOp.Gtu or VMaskCmpOp.Gt when funct3 == 0 => throw new IllegalInstructionException(
                    raw, "vmsgtu/vmsgt.vv is not a valid encoding"
                ),
                _ => funct3 switch {
                    0 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector, new RvVMaskCmpVv(cmpOp.Value, vd, vs2, rs1, masked)
                    ),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMaskCmpVi(cmpOp.Value, vd, vs2, SignExtend5(rs1), masked)
                    ),
                    _ => new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMaskCmpVx(cmpOp.Value, vd, vs2, rs1, masked)
                    ),
                },
            },
            // vsub has no VI variant
            VIntOp.Sub when funct3 == 3 => throw new IllegalInstructionException(
                raw, "vsub.vi is not a valid instruction"
            ),
            // vrsub has no VV form
            VIntOp.Rsub when funct3 == 0 => throw new IllegalInstructionException(
                raw, "vrsub.vv is not a valid encoding"
            ),
            // vminu/vmin/vmaxu/vmax have no VI variant
            VIntOp.Minu or VIntOp.Min or VIntOp.Maxu or VIntOp.Max when funct3 == 3 =>
                throw new IllegalInstructionException(raw, "vminu/vmin/vmaxu/vmax.vi is not a valid instruction"),
            _ => funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector, new RvVIntAluVv(intOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector, new RvVIntAluVi(intOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector, new RvVIntAluVx(intOp.Value, vd, vs2, rs1, masked)
                ),
            },
        };
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
            // FCVT.S.D (double→single): fmt=S(0), rs2=1(D)
            0x20 => rs2 == 1
                ? FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtSd(rd + 32, rs1 + 32, (int)funct3))
                : throw new IllegalInstructionException(raw, $"Unknown FCVT.S.? rs2={rs2}"),
            // FCVT.D.S (single→double): fmt=D(1), rs2=0(S)
            0x21 => rs2 == 0
                ? FpR1(pc, raw, rd + 32, rs1 + 32, new RvFcvtDs(rd + 32, rs1 + 32, (int)funct3))
                : throw new IllegalInstructionException(raw, $"Unknown FCVT.D.? rs2={rs2}"),
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
            _         => throw new IllegalInstructionException(raw, $"FMA: unsupported fmt={fmt} opcode=0x{opcode:X}"),
        };
        return new RvInstruction(
            pc, raw, rd + 32,
            [rs1 + 32, rs2 + 32, rs3 + 32,], ToothClass.FloatingPoint, op
        );
    }

    // ── FP instruction factories ───────────────────────────────────────────────

    private static RvInstruction FpRr(
        ulong pc,
        uint raw,
        int dest,
        int s0,
        int s1,
        RvOp op,
        ToothClass cls = ToothClass.FloatingPoint
    ) =>
        new(pc, raw, dest, [s0, s1,], cls, op);

    private static RvInstruction FpR1(
        ulong pc,
        uint raw,
        int dest,
        int s0,
        RvOp op,
        ToothClass cls = ToothClass.FloatingPoint
    ) =>
        new(pc, raw, dest, [s0,], cls, op);

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
            0x2 => DecodeCLw(pc, c, rdp, rs1P),
            0x3 => DecodeCFlw(pc, c, rdp, rs1P),
            0x6 => DecodeCSw(pc, c, rdp, rs1P),
            0x7 => DecodeCFsw(pc, c, rdp, rs1P),
            _   => throw new IllegalInstructionException(c, $"Unknown C.Q0 funct3=0x{funct3:X}"),
        };
    }

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
            0x2 => DecodeCLwsp(pc, c, rd),
            0x3 => DecodeCFlwsp(pc, c, rd),
            0x4 => DecodeQ2Funct3_100(pc, c, rd, rs2),
            0x6 => DecodeCSwsp(pc, c, rs2),
            0x7 => DecodeCFswsp(pc, c, rs2),
            _   => throw new IllegalInstructionException(c, $"Unknown C.Q2 funct3=0x{funct3:X}"),
        };
    }

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

    // ── UVE extension ─────────────────────────────────────────────────────────
    // custom-0 (0x0B): stream setup (ss.*)
    // custom-1 (0x2B): stream operations (so.*)

    private static RvInstruction DecodeUveSetup(ulong pc, uint raw) {
        // R4-type: rs3[31:27] | funct2[26:25] | rs2[24:20] | rs1[19:15] | funct3[14:12] | rd[11:7] | 0x0B
        var ud = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        var rs3 = (int)((raw >> 27) & 0x1F);
        uint funct2 = (raw >> 25) & 0x3;
        uint funct3 = (raw >> 12) & 0x7;

        switch (funct2) {
            case 0: {
                // ss.sta.{ld|st}.*: funct3[2]=1→load,0→store; ew=1<<(funct3&3)
                // rs3 bit[3]=1 → vector mode; bits[2:0]=7 → innermost dim (-1); bits[2:0]=0..6 → explicit dim
                // rs3 bit[4]=1 → masked variant (predicate reg; decoded but mask is ignored until SO_P implemented)
                // rs2 bit[4]=1 + isLoad → IndSource stream (ss.sta.ld.*_inds)
                int ew = UveElementBytes(funct3);
                bool isLoad = funct3 >> 2 != 0;
                if (isLoad && (rs2 & 0x10) != 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSsStaLdWInds(ud, rs1, ew)
                    );
                bool isVec = (rs3 & 0x8) != 0;
                int vecCfgDim = isVec ? (rs3 & 0x7) == 0x7 ? -1 : rs3 & 0x7 : -1;
                return isLoad
                    ? new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSsStaLdW(ud, rs1, ew, isVec, vecCfgDim)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSsStaStW(ud, rs1, ew, isVec, vecCfgDim)
                    );
            }
            // ss.app ud, rs1_offset, rs2_count, rs3_stride
            case 1 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2, rs3,], ToothClass.Uve, new RvUveSsApp(ud, rs1, rs2, rs3)
                );
            // ss.app.ind ud, rs1_indsrc — attach indirect modifier; funct2=1, funct3=6
            // rs3[4:1] = Spike dim index (outermost=0); rs2[1:0]=target, rs2[4:2]=behavior
            // rs1 = UVE register number of the IndSource stream (not an integer register read)
            case 1 when funct3 == 6: {
                int spikeDimIndex = rs3 >> 1;
                StreamModifierTarget indTarget = (rs2 & 0x3) switch {
                    0 => StreamModifierTarget.Size,
                    1 => StreamModifierTarget.Stride,
                    _ => StreamModifierTarget.Offset,
                };
                StreamModifierBehavior indBehavior = ((rs2 >> 2) & 0x7) switch {
                    0 => StreamModifierBehavior.Inc,
                    1 => StreamModifierBehavior.Dec,
                    2 => StreamModifierBehavior.Add,
                    3 => StreamModifierBehavior.Sub,
                    _ => StreamModifierBehavior.Set,
                };
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Uve,
                    new RvUveSsAppInd(ud, spikeDimIndex, indTarget, indBehavior, rs1)
                );
            }
            case 3: {
                // ss.app.mod: funct2=3, funct3=dimIndex (0-7), rs1=E register, rs2=target+behavior literal, rs3=disp reg
                // Spike target encoding: 0=Size, 1=Stride, 2=Offset → map to Horologium enum: Size=0, Stride=2, Offset=1
                var dimIndex = (int)funct3;
                int spikeTarget = rs2 & 0x3;
                StreamModifierTarget target = spikeTarget switch {
                    0 => StreamModifierTarget.Size,
                    1 => StreamModifierTarget.Stride,
                    _ => StreamModifierTarget.Offset,
                };
                var behavior = (StreamModifierBehavior)((rs2 >> 2) & 0x1);
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs3,], ToothClass.Uve,
                    new RvUveSsAppMod(ud, dimIndex, target, behavior, rs3, rs1)
                );
            }
            // ss.end ud, rs1_offset, rs2_count, rs3_stride
            case 2 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2, rs3,], ToothClass.Uve, new RvUveSsEnd(ud, rs1, rs2, rs3)
                );
            default:
                throw new IllegalInstructionException(
                    raw, $"Unknown UVE setup funct2=0x{funct2:X} funct3=0x{funct3:X}"
                );
        }
    }

    // funct3 encodes element width for ss.sta.*: ew = 1 << (funct3 & 3) → 1/2/4/8 bytes
    private static int UveElementBytes(uint funct3) => 1 << (int)(funct3 & 3);

    private static RvInstruction DecodeUveOp(ulong pc, uint raw) {
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        uint funct3 = (raw >> 12) & 0x7;
        uint funct7 = (raw >> 25) & 0x7F;

        // UVE branch: bits[31:29]=111 (funct7[6:4]=111, i.e. raw>>29==7)
        if (raw >> 29 == 7) {
            int imm = UveBranchImm(raw);
            int notDone = rs2 & 1; // LSB of rs2 field

            if (funct3 == 0)
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Uve,
                    notDone != 0 ? new RvUveSoBNc(rs1, imm) : new RvUveSoBc(rs1, imm)
                );

            return new RvInstruction(
                pc, raw, -1, [], ToothClass.Uve,
                notDone != 0
                    ? new RvUveSoBNdc(rs1, (int)funct3, imm)
                    : new RvUveSoBdc(rs1, (int)funct3, imm)
            );
        }

        // so.v.dp.(width): funct7=0x56; funct3 selects element width (0=b, 1=h, 2=w, 3=d)
        if (funct7 == 0x56) {
            int elemBytes = (int)funct3 switch {
                0 => 1, 1 => 2, 2 => 4, 3 => 8,
                _ => throw new IllegalInstructionException(raw, $"Unknown so.v.dp width funct3=0x{funct3:X}"),
            };
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSoVDp(rd, rs1, elemBytes));
        }

        // so.v.mv family: funct7=0x54; rs2[4:3] selects op (2=mvvs, 3=mvsv, 0=mv, 1=mvt)
        if (funct7 == 0x54) {
            int mvKind = (rs2 >> 3) & 3;
            if (mvKind == 2) return new RvInstruction(pc, raw, rd, [], ToothClass.Uve, new RvUveSoVMvvs(rs1, rd));
            if (mvKind == 3) {
                int elemBytes = (int)funct3 switch {
                    0 => 1, 1 => 2, 2 => 4, 3 => 8,
                    _ => throw new IllegalInstructionException(raw, $"Unknown so.v.mvsv width funct3=0x{funct3:X}"),
                };
                return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSoVMvsv(rd, rs1, elemBytes));
            }

            // mv/mvt: rs2[2:0] = uve_v_pred = bits[22:20]
            int predIdx = rs2 & 7;
            return new RvInstruction(pc, raw, -1, [], ToothClass.Uve, new RvUveSoVMv(mvKind == 1, rd, rs1, predIdx));
        }

        // so.a.*: group = funct7>>3, upper = funct3&4, type = funct3&3 (0=US, 1=FP, 2=SG)
        var group = (int)(funct7 >> 3);
        bool upper = (funct3 & 4) != 0;
        var type = (int)(funct3 & 3);

        RvOp uvOp = group switch {
            0 => UveArith(upper ? UveFpOp.Sub : UveFpOp.Add, upper ? UveIntOp.Sub : UveIntOp.Add, type, rd, rs1, rs2),
            1 => UveArith(upper ? UveFpOp.Div : UveFpOp.Mul, upper ? UveIntOp.Div : UveIntOp.Mul, type, rd, rs1, rs2),
            // Group 2 lower: adde — accumulate stream element into ud; rs2=1 selects the += variant.
            // Group 2 upper: sadde/fsadde — write stream element (or accumulate) into integer/FP scalar reg.
            2 when !upper && rs2 == 1 => UveArith(UveFpOp.AddeAcc, UveIntOp.AddeAcc, type, rd, rs1, -1),
            2 when !upper             => UveArith(UveFpOp.Adde, UveIntOp.Adde, type, rd, rs1, -1),
            2 when upper && rs2 == 1  => new RvUveSoASadde(type == 1, true, type == 1 ? rd + 32 : rd, rs1),
            2 when upper              => new RvUveSoASadde(type == 1, false, type == 1 ? rd + 32 : rd, rs1),
            3 when upper              => UveArith(UveFpOp.Mac, UveIntOp.Mac, type, rd, rs1, rs2),
            // ABS has no US variant in Spike (MATCH_SO_A_ABS_SG uses funct3=0); force Signed=true.
            3 when !upper && type == 1 => new RvUveSoAFp(UveFpOp.Abs, rd, rs1, -1),
            3 when !upper => new RvUveSoAInt(UveIntOp.Abs, true, rd, rs1, -1),
            4 => UveArith(upper ? UveFpOp.Max : UveFpOp.Min, upper ? UveIntOp.Max : UveIntOp.Min, type, rd, rs1, rs2),
            // Group 5: mine/maxe — running min/max reduction into ud.
            5 => UveArith(
                upper ? UveFpOp.Maxe : UveFpOp.Mine, upper ? UveIntOp.Maxe : UveIntOp.Mine, type, rd, rs1, -1
            ),
            6 when upper && rs2 == 1 && type == 1 => new RvUveSoAFp(UveFpOp.Sqrt, rd, rs1, -1),
            6 => UveArith(upper ? UveFpOp.Dec : UveFpOp.Inc, upper ? UveIntOp.Dec : UveIntOp.Inc, type, rd, rs1, -1),
            // Group 11 (funct7=0x58): SO_C — stream lifecycle and vector-length control.
            // funct3 distinguishes ops; only rd (and rs1 for SETVL) are register fields.
            11 => (int)funct3 switch {
                0 => (RvOp)new RvUveSoCSetvl(rd, rs1),
                1 => new RvUveSoCSuspd(rd),
                2 => new RvUveSoCResum(rd),
                3 => new RvUveSoCBreak(rd),
                7 => new RvUveSoCGetvl(rd),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE SO_C funct3=0x{funct3:X}"),
            },
            12 => (int)funct3 switch {
                0 => (RvOp)new RvUveSoALogic(UveLogicOp.Nand, rd, rs1, rs2),
                1 => new RvUveSoALogic(UveLogicOp.And, rd, rs1, rs2),
                2 => new RvUveSoALogic(UveLogicOp.Nor, rd, rs1, rs2),
                3 => new RvUveSoALogic(UveLogicOp.Or, rd, rs1, rs2),
                4 => new RvUveSoALogic(UveLogicOp.Not, rd, rs1, -1),
                5 => new RvUveSoALogic(UveLogicOp.Xor, rd, rs1, rs2),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE logic funct3=0x{funct3:X}"),
            },
            13 => (int)funct3 switch {
                0 => (RvOp)new RvUveSoAShiftV(UveShiftOp.Sll, rd, rs1, rs2),
                1 => new RvUveSoAShiftS(UveShiftOp.Sll, rd, rs1, rs2),
                2 => new RvUveSoAShiftV(UveShiftOp.Srl, rd, rs1, rs2),
                3 => new RvUveSoAShiftS(UveShiftOp.Srl, rd, rs1, rs2),
                4 => new RvUveSoAShiftV(UveShiftOp.Sra, rd, rs1, rs2),
                5 => new RvUveSoAShiftS(UveShiftOp.Sra, rd, rs1, rs2),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE shift funct3=0x{funct3:X}"),
            },
            // Groups 8/9: SO_P predicate register operations.
            // bits[31:28]=1000 → group=8 (simple ops + GE); bits[31:28]=1001 → group=9 (EQ + LT)
            8 or 9 => DecodeSoP(raw, group, funct3),
            _      => throw new IllegalInstructionException(raw, $"Unknown UVE op group={group} funct3=0x{funct3:X}"),
        };

        // ShiftS uses integer shift-amount; Sadde/fsadde and SO_C getvl/setvl write scalar regs.
        int dest = uvOp switch {
            RvUveSoASadde s => s.Rd,
            RvUveSoCGetvl s => s.Rd,
            RvUveSoCSetvl s => s.Rd,
            _               => -1,
        };
        int[] intSrcs = uvOp switch {
            RvUveSoAShiftS ss              => [ss.Rs2,],
            RvUveSoASadde { Acc: true, } s => [s.Rd,],
            RvUveSoCSetvl s                => [s.Rs1,],
            _                              => [],
        };
        return new RvInstruction(pc, raw, dest, intSrcs, ToothClass.Uve, uvOp);
    }

    // SO_P predicate register operations.
    // group=8: simple manipulation ops (funct3[2]=0) and GE comparisons (funct3[2]=1)
    // group=9: EQ comparisons (funct3[2]=0) and LT comparisons (funct3[2]=1)
    private static RvOp DecodeSoP(uint raw, int group, uint funct3) {
        var govPred = (int)((raw >> 25) & 0x7); // bits[27:25] — governing predicate reg index
        bool zeroing = (raw & (1u << 24)) != 0; // bit[24] — only valid for simple ops
        var predRd = (int)((raw >> 7) & 0xF);   // bits[10:7] — dest pred reg (uve_pred_rd)
        var vs1 = (int)((raw >> 15) & 0x1F);    // bits[19:15] — source ud or pred reg
        var predRs1 = (int)((raw >> 15) & 0xF); // bits[18:15] — source pred reg (4-bit)

        if (group == 8 && (funct3 & 4) == 0) {
            // Simple ops: funct3[1:0] + bit[11]
            var bit11 = (int)((raw >> 11) & 1);
            var subOp = (int)(funct3 & 3);
            return (subOp, bit11) switch {
                (0, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Zero, predRd, govPred, zeroing, -1, -1),
                (0, 1) => new RvUveSoPSimple(UveSoPSimpleOp.One, predRd, govPred, zeroing, -1, -1),
                (1, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Vr, predRd, govPred, zeroing, -1, vs1),
                (1, 1) => new RvUveSoPSimple(UveSoPSimpleOp.Not, predRd, govPred, zeroing, predRs1, -1),
                (2, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Mv, predRd, govPred, zeroing, predRs1, -1),
                (2, 1) => new RvUveSoPSimple(UveSoPSimpleOp.Mvt, predRd, govPred, zeroing, predRs1, -1),
                _ => throw new IllegalInstructionException(raw, $"Unknown SO_P simple subOp={subOp} bit11={bit11}"),
            };
        }

        // Comparison ops: GE (group=8, funct3[2]=1), EQ (group=9, funct3[2]=0), LT (group=9, funct3[2]=1)
        // vs2 = bits[24:20] (uve_pred_rs2); bit24 is the MSB of vs2, NOT the zeroing flag.
        var vs2 = (int)((raw >> 20) & 0x1F);
        UveSoPCmpType cmpType = (funct3 & 3) switch {
            0 => UveSoPCmpType.Us,
            1 => UveSoPCmpType.Fp,
            2 => UveSoPCmpType.Sg,
            _ => throw new IllegalInstructionException(raw, $"Unknown SO_P cmp type funct3[1:0]={funct3 & 3}"),
        };
        UveSoPCmpOp cmpOp = group == 8 ? UveSoPCmpOp.Ge : (funct3 & 4) == 0 ? UveSoPCmpOp.Eq : UveSoPCmpOp.Lt;
        return new RvUveSoPCmp(cmpOp, cmpType, predRd, govPred, vs1, vs2);
    }

    private static RvOp UveArith(UveFpOp fpOp, UveIntOp intOp, int type, int ud, int usrc1, int usrc2) =>
        type switch {
            1 => new RvUveSoAFp(fpOp, ud, usrc1, usrc2),
            2 => new RvUveSoAInt(intOp, true, ud, usrc1, usrc2),
            _ => new RvUveSoAInt(intOp, false, ud, usrc1, usrc2),
        };

    // UVE non-standard B-type immediate: bit28=imm[12](sign), bits[27:22]=imm[10:5], bit7=imm[11], bits[11:8]=imm[4:1]
    private static int UveBranchImm(uint raw) => SignExtendN(
        (int)(((raw >> 8) & 0xF) << 1) |
        (int)(((raw >> 22) & 0x3F) << 5) |
        (int)(((raw >> 7) & 1) << 11) |
        (int)(((raw >> 28) & 1) << 12),
        13
    );

    // ── Immediate helpers ─────────────────────────────────────────────────────

    protected static int SignExtend12(int value) =>
        (value & 0x800) != 0 ? value | unchecked((int)0xFFFFF000) : value & 0xFFF;

    private static int SignExtendN(int value, int bits) {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }
}