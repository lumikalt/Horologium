using Mechanism;

namespace RiscV32.Decode;

/// <summary>
/// Instruction decoder with a (PC, raw) keyed cache.
/// Safe for all programs — keying on both PC and raw encoding avoids false hits when
/// two different instructions happen to share the same PC (e.g., in unit tests).
/// </summary>
public partial class Rv32Decoder : IDecoder {
    protected readonly Dictionary<(ulong pc, uint raw), ITooth> _cache = new();
    private readonly Dictionary<(ulong pc, uint raw), FetchHint> _hintCache = new();

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        if (_hintCache.TryGetValue((pc, firstWord), out FetchHint cached)) return cached;
        FetchHint hint = ComputeFetchHint(pc, firstWord);
        _hintCache[(pc, firstWord)] = hint;
        return hint;
    }

    protected virtual FetchHint ComputeFetchHint(ulong pc, uint firstWord) {
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
                    IsUnconditional = true,
                    BranchTarget = ((ulong)((long)pc + CJumpOffset(c)), true),
                },
                // c.j: unconditional PC-relative jump.
                0x1 when cfunct3 == 0x5 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsUnconditional = true,
                    BranchTarget = ((ulong)((long)pc + CJumpOffset(c)), true),
                },
                // c.beqz / c.bnez: PC-relative conditional branches.
                0x1 when cfunct3 == 0x6 || cfunct3 == 0x7 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, BranchTarget = ((ulong)((long)pc + CBranchImm(c)), true),
                },
                // c.jalr: register-indirect call — target not statically known.
                0x2 when cfunct3 == 0x4 && inst12 && crs2 == 0 && crs1 != 0 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsCall = true, IsUnconditional = true,
                },
                // c.jr / c.ret: register-indirect return — target not statically known.
                0x2 when cfunct3 == 0x4 && !inst12 && crs2 == 0 && crs1 != 0 => new FetchHint {
                    InstructionSize = 2, IsBranch = true, IsUnconditional = true, IsReturn = crs1 is 1 or 5,
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
            IsUnconditional = isJal || isJalr,
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

    protected ITooth Cache(ulong pc, uint raw, ITooth tooth) {
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

        // Zabha+Zacas amocas.b / amocas.h: rd is also a source (the comparand), same shape
        // as amocas.w.
        if (funct5 == 0x05) {
            RvOp casOp = isByte ? new RvAmocasB(rd, rs1, rs2) : new RvAmocasH(rd, rs1, rs2);
            return new RvInstruction(pc, raw, rd, [rs1, rs2, rd,], ToothClass.Atomic, casOp);
        }

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


    // ── Immediate helpers ─────────────────────────────────────────────────────

    protected static int SignExtend12(int value) =>
        (value & 0x800) != 0 ? value | unchecked((int)0xFFFFF000) : value & 0xFFF;

    protected static int SignExtendN(int value, int bits) {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }
}
