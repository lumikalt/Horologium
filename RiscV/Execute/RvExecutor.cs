using Mechanism;
using RiscV.Decode;

namespace RiscV.Execute;

/// <summary>
/// Executes a single decoded RV32I instruction.
/// Reads from IArchState, returns an ExecuteResult — never writes back directly.
/// </summary>
public sealed class RvExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op)
            throw new InvalidOperationException(
                $"RvExecutor received an instruction with an unexpected payload type: " +
                $"{instruction.Payload?.GetType().Name ?? "null"}"
            );

        IRegisterFile regs = state.IntegerRegisters;
        ulong pc = instruction.Pc;

        return op switch {
            // ── R-type ────────────────────────────────────────────────────────
            RvAdd (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) + regs.Read(rs2)),
            RvSub (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) - regs.Read(rs2)),
            RvXor (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) ^ regs.Read(rs2)),
            RvOr (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) | regs.Read(rs2)),
            RvAnd (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) & regs.Read(rs2)),
            RvSll (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) << (int)(regs.Read(rs2) & 0x1F)),
            RvSrl (_, var rs1, var rs2) =>
                Reg((uint)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F)),
            RvSra (_, var rs1, var rs2) =>
                Reg((ulong)((int)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F))),
            RvSlt (_, var rs1, var rs2) =>
                Reg((int)regs.Read(rs1) < (int)regs.Read(rs2) ? 1UL : 0UL),
            RvSltu (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) < regs.Read(rs2) ? 1UL : 0UL),

            // ── I-type ALU ────────────────────────────────────────────────────
            RvAddi (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) + (ulong)imm),
            RvXori (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) ^ (ulong)imm),
            RvOri (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) | (uint)imm),
            RvAndi (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) & (ulong)imm),
            RvSlli (_, var rs1, var shamt) =>
                Reg(regs.Read(rs1) << shamt),
            RvSrli (_, var rs1, var shamt) =>
                Reg((uint)regs.Read(rs1) >> shamt),
            RvSrai (_, var rs1, var shamt) =>
                Reg((ulong)((int)regs.Read(rs1) >> shamt)),
            RvSlti (_, var rs1, var imm) =>
                Reg((int)regs.Read(rs1) < imm ? 1UL : 0UL),
            RvSltiu(_, var rs1, var imm) =>
                Reg((uint)regs.Read(rs1) < (uint)imm ? 1UL : 0UL),

            // ── Loads ─────────────────────────────────────────────────────────
            RvLb (_, var rs1, var imm) => Load(
                memory,
                regs.Read(rs1), imm, 1, true, 8
            ),
            RvLh (_, var rs1, var imm) => Load(
                memory,
                regs.Read(rs1), imm, 2, true, 16
            ),
            RvLw (_, var rs1, var imm) => Load(
                memory,
                regs.Read(rs1), imm, 4, false, 32
            ),
            RvLbu (_, var rs1, var imm) => Load(
                memory,
                regs.Read(rs1), imm, 1, false, 8
            ),
            RvLhu (_, var rs1, var imm) => Load(
                memory,
                regs.Read(rs1), imm, 2, false, 16
            ),

            // ── Stores ────────────────────────────────────────────────────────
            RvSb (var rs1, var rs2, var imm) =>
                Store(memory, regs.Read(rs1), imm, regs.Read(rs2), 1),
            RvSh (var rs1, var rs2, var imm) =>
                Store(memory, regs.Read(rs1), imm, regs.Read(rs2), 2),
            RvSw (var rs1, var rs2, var imm) =>
                Store(memory, regs.Read(rs1), imm, regs.Read(rs2), 4),

            // ── Branches ──────────────────────────────────────────────────────
            RvBeq (var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) == regs.Read(rs2), pc, imm),
            RvBne (var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) != regs.Read(rs2), pc, imm),
            RvBlt (var rs1, var rs2, var imm) =>
                Branch((int)regs.Read(rs1) < (int)regs.Read(rs2), pc, imm),
            RvBge (var rs1, var rs2, var imm) =>
                Branch((int)regs.Read(rs1) >= (int)regs.Read(rs2), pc, imm),
            RvBltu(var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) < regs.Read(rs2), pc, imm),
            RvBgeu(var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) >= regs.Read(rs2), pc, imm),

            // ── Jumps ─────────────────────────────────────────────────────────
            RvJal (_, var imm) =>
                new ExecuteResult {
                    RegisterResult = pc + 4,
                    BranchTaken = true,
                    BranchTarget = (ulong)((long)pc + imm),
                },
            RvJalr(_, var rs1, var imm) =>
                new ExecuteResult {
                    RegisterResult = pc + 4,
                    BranchTaken = true,
                    BranchTarget = (regs.Read(rs1) + (ulong)imm) & ~1UL,
                },

            // ── Upper immediates ──────────────────────────────────────────────
            RvLui (_, var imm)  => Reg((ulong)imm),
            RvAuipc(_, var imm) => Reg(pc + (ulong)imm),

            // ── System ────────────────────────────────────────────────────────
            RvEcall => ExecuteResult.WithTrap(
                new TrapInfo(
                    TrapCause.EnvironmentCallFromM, 0, pc
                )
            ),

            RvEbreak => ExecuteResult.WithTrap(
                new TrapInfo(
                    TrapCause.Breakpoint, 0, pc
                )
            ),

            RvMret => ExecuteResult.Clean, // handled by TrapController at commit

            RvFence => ExecuteResult.Clean, // NOP in single-core simulation

            // ── M extension ───────────────────────────────────────────────────
            // MUL: lower 32 bits of product (signed or unsigned — same result)
            RvMul(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) * regs.Read(rs2)),

            // MULH: upper 32 bits, signed × signed
            RvMulh(_, var rs1, var rs2) =>
                Reg((uint)(((int)regs.Read(rs1) * (Int128)(int)regs.Read(rs2)) >> 32)),

            // MULHSU: upper 32 bits, signed × unsigned
            RvMulhsu(_, var rs1, var rs2) =>
                Reg((uint)(((int)regs.Read(rs1) * (Int128)(uint)regs.Read(rs2)) >> 32)),

            // MULHU: upper 32 bits, unsigned × unsigned
            RvMulhu(_, var rs1, var rs2) =>
                Reg((uint)(((uint)regs.Read(rs1) * (UInt128)(uint)regs.Read(rs2)) >> 32)),

            // DIV: signed truncated division; div-by-zero → -1; INT_MIN/-1 → INT_MIN
            RvDiv(_, var rs1, var rs2) => DivSigned(regs, rs1, rs2),

            // DIVU: unsigned division; div-by-zero → 2^32-1
            RvDivu(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(0xFFFF_FFFF)
                : Reg((uint)regs.Read(rs1) / (uint)regs.Read(rs2)),

            // REM: signed remainder; div-by-zero → rs1; INT_MIN/-1 → 0
            RvRem(_, var rs1, var rs2) => RemSigned(regs, rs1, rs2),

            // REMU: unsigned remainder; div-by-zero → rs1
            RvRemu(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(regs.Read(rs1))
                : Reg((uint)regs.Read(rs1) % (uint)regs.Read(rs2)),

            // ── A extension (single-core: SC always succeeds, no reservation needed) ──
            RvLrW(_, var rs1) =>
                Load(memory, regs.Read(rs1), 0, 4, false, 32),

            RvScW(_, var rs1, var rs2) => AmoSc(memory, regs, rs1, rs2),

            RvAmoswapW(_, var rs1, var rs2) => Amo(memory, regs, rs1, rs2, (_, v) => v),
            RvAmoaddW (_, var rs1, var rs2) => Amo(memory, regs, rs1, rs2, (a, v) => a + v),
            RvAmoxorW (_, var rs1, var rs2) => Amo(memory, regs, rs1, rs2, (a, v) => a ^ v),
            RvAmoandW (_, var rs1, var rs2) => Amo(memory, regs, rs1, rs2, (a, v) => a & v),
            RvAmoorW (_, var rs1, var rs2)  => Amo(memory, regs, rs1, rs2, (a, v) => a | v),
            RvAmominW (_, var rs1, var rs2) =>
                Amo(memory, regs, rs1, rs2, (a, v) => (uint)Math.Min((int)a, (int)v)),
            RvAmomaxW (_, var rs1, var rs2) =>
                Amo(memory, regs, rs1, rs2, (a, v) => (uint)Math.Max((int)a, (int)v)),
            RvAmominuW(_, var rs1, var rs2) =>
                Amo(memory, regs, rs1, rs2, (a, v) => Math.Min(a, v)),
            RvAmomaxuW(_, var rs1, var rs2) =>
                Amo(memory, regs, rs1, rs2, (a, v) => Math.Max(a, v)),

            // ── F extension ───────────────────────────────────────────────────
            // FLW: address computed from int rs1; result is raw bits stored in fp rd.
            RvFlw(_, var rs1, var imm) => Load(memory, regs.Read(rs1), imm, 4, false, 32),

            // FSW: rs1 = int base address, rs2 = fp data register (unified index).
            RvFsw(var rs1, var rs2, var imm) =>
                Store(memory, regs.Read(rs1), imm, regs.Read(rs2), 4),

            RvFaddS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) + FBits(regs, rs2)),
            RvFsubS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) - FBits(regs, rs2)),
            RvFmulS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) * FBits(regs, rs2)),
            RvFdivS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) / FBits(regs, rs2)),
            RvFsqrtS(_, var rs1)          => FloatReg(MathF.Sqrt(FBits(regs, rs1))),

            RvFsgnjS (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) | ((uint)regs.Read(rs2) & 0x80000000)
                ),
            RvFsgnjnS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) | (~(uint)regs.Read(rs2) & 0x80000000)
                ),
            RvFsgnjxS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) |
                    (((uint)regs.Read(rs1) ^ (uint)regs.Read(rs2)) & 0x80000000)
                ),

            RvFminS(_, var rs1, var rs2) => FloatReg(FMin(FBits(regs, rs1), FBits(regs, rs2))),
            RvFmaxS(_, var rs1, var rs2) => FloatReg(FMax(FBits(regs, rs1), FBits(regs, rs2))),

            RvFeqS(_, var rs1, var rs2) => Reg(FBits(regs, rs1) == FBits(regs, rs2) ? 1UL : 0UL),
            RvFltS(_, var rs1, var rs2) => Reg(FBits(regs, rs1) < FBits(regs, rs2) ? 1UL : 0UL),
            RvFleS(_, var rs1, var rs2) => Reg(FBits(regs, rs1) <= FBits(regs, rs2) ? 1UL : 0UL),

            RvFclassS(_, var rs1) => Reg(FClass((uint)regs.Read(rs1))),

            RvFcvtWS (_, var rs1) => Reg((uint)(int)FBits(regs, rs1)),
            RvFcvtWuS(_, var rs1) => Reg((uint)FBits(regs, rs1)),
            RvFcvtSW (_, var rs1) => FloatReg((float)(int)regs.Read(rs1)),
            RvFcvtSWu(_, var rs1) => FloatReg((float)(uint)regs.Read(rs1)),

            RvFmvXW(_, var rs1) => Reg(regs.Read(rs1)), // fp bits → int (bit-exact)
            RvFmvWX(_, var rs1) => ExecuteResult.WithResult(regs.Read(rs1) & 0xFFFFFFFF),

            RvFmaddS (_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3))),
            RvFmsubS (_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3))),
            RvFnmsubS(_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(-FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3))),
            RvFnmaddS(_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(-FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3))),

            RvCsrrw (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr,
                (old, src) => src
            ),
            RvCsrrs (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr,
                (old, src) => old | src
            ),
            RvCsrrc (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr,
                (old, src) => old & ~src
            ),
            RvCsrrwi (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr,
                (old, src) => src
            ),
            RvCsrrsi (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr,
                (old, src) => old | src
            ),
            RvCsrrci (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr,
                (old, src) => old & ~src
            ),

            _ => throw new InvalidOperationException(
                $"Unhandled RvOp: {op.GetType().Name}"
            ),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExecuteResult Reg(ulong value) =>
        ExecuteResult.WithResult(value & 0xFFFFFFFF); // truncate to 32 bits

    private static ExecuteResult DivSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(0xFFFF_FFFF);                                         // div-by-zero → -1
        if (a == int.MinValue && b == -1) return Reg(unchecked((uint)int.MinValue)); // overflow
        return Reg(unchecked((uint)(a / b)));
    }

    private static ExecuteResult RemSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(regs.Read(rs1));          // div-by-zero → rs1
        if (a == int.MinValue && b == -1) return Reg(0); // overflow → 0
        return Reg(unchecked((uint)(a % b)));
    }

    // Atomic read-modify-write. Returns original value; combines with rs2 and stores.
    private static ExecuteResult Amo(
        IMemory memory,
        IRegisterFile regs,
        int rs1,
        int rs2,
        Func<uint, uint, uint> combine
    ) {
        ulong addr = regs.Read(rs1);
        var old = (uint)memory.Read(addr, 4);
        memory.Write(addr, combine(old, (uint)regs.Read(rs2)), 4);
        return Reg(old);
    }

    // SC.W always succeeds in single-core (no competing stores possible).
    private static ExecuteResult AmoSc(IMemory memory, IRegisterFile regs, int rs1, int rs2) {
        memory.Write(regs.Read(rs1), regs.Read(rs2), 4);
        return Reg(0); // 0 = success
    }

    private static ExecuteResult Load(
        IMemory memory,
        ulong @base,
        int imm,
        int bytes,
        bool signExtend,
        int bits
    ) {
        ulong addr = @base + (ulong)imm;
        ulong value = memory.Read(addr, bytes);
        if (!signExtend || bits >= 32) return ExecuteResult.WithResult(value & 0xFFFFFFFF);
        int shift = 32 - bits;
        value = (uint)((int)(value << shift) >> shift);

        return ExecuteResult.WithResult(value & 0xFFFFFFFF);
    }

    private static ExecuteResult Store(
        IMemory memory,
        ulong @base,
        int imm,
        ulong value,
        int bytes
    ) {
        ulong addr = @base + (ulong)imm;
        memory.Write(addr, value, bytes);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult Branch(bool taken, ulong pc, int imm) =>
        ExecuteResult.WithBranch(taken, taken ? (ulong)((long)pc + imm) : pc + 4);

    private static ExecuteResult ExecuteCsr(
        IArchState state,
        int rs1,
        uint csr,
        Func<ulong, ulong, ulong> combine
    ) {
        ICsrFile csrFile = state.Csrs!;
        ulong old = csrFile.Read(csr, state.PrivilegeLevel);
        ulong src = state.IntegerRegisters.Read(rs1);
        csrFile.Write(csr, combine(old, src), state.PrivilegeLevel);
        return ExecuteResult.WithResult(old & 0xFFFFFFFF);
    }

    // ── FP helpers ────────────────────────────────────────────────────────────

    // Read a float register as a C# float (bit-exact reinterpret).
    private static float FBits(IRegisterFile regs, int rs) =>
        BitConverter.Int32BitsToSingle((int)regs.Read(rs));

    // Produce a float result stored as raw 32-bit bits.
    private static ExecuteResult FloatReg(float value) =>
        ExecuteResult.WithResult((uint)BitConverter.SingleToInt32Bits(value));

    // RISC-V FCLASS encoding (10-bit result).
    private static ulong FClass(uint bits) {
        bool sign = bits >> 31 != 0;
        uint exp = (bits >> 23) & 0xFF;
        uint frac = bits & 0x7FFFFF;
        if (exp == 0xFF) {
            if (frac == 0) return sign ? 1UL << 0 : 1UL << 7; // ±inf
            return frac >> 22 != 0 ? 1UL << 9 : 1UL << 8;     // qNaN / sNaN
        }

        if (exp == 0)
            return frac == 0
                ? sign ? 1UL << 3 : 1UL << 4 // ±zero
                : sign
                    ? 1UL << 2
                    : 1UL << 5;            // ±subnormal
        return sign ? 1UL << 1 : 1UL << 6; // ±normal
    }

    // RISC-V FMIN: if one arg is NaN, return the other; -0.0 < +0.0.
    private static float FMin(float a, float b) {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        if (a == 0f && b == 0f)
            return BitConverter.SingleToInt32Bits(a) < 0 ||
                   BitConverter.SingleToInt32Bits(b) < 0
                ? -0f
                : 0f;
        return a < b ? a : b;
    }

    // RISC-V FMAX: if one arg is NaN, return the other; +0.0 > -0.0.
    private static float FMax(float a, float b) {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        if (a == 0f && b == 0f)
            return BitConverter.SingleToInt32Bits(a) >= 0 ||
                   BitConverter.SingleToInt32Bits(b) >= 0
                ? 0f
                : -0f;
        return a > b ? a : b;
    }

    private static ExecuteResult ExecuteCsrImm(
        IArchState state,
        uint zimm,
        uint csr,
        Func<ulong, ulong, ulong> combine
    ) {
        ICsrFile csrFile = state.Csrs!;
        ulong old = csrFile.Read(csr, state.PrivilegeLevel);
        csrFile.Write(csr, combine(old, zimm), state.PrivilegeLevel);
        return ExecuteResult.WithResult(old & 0xFFFFFFFF);
    }
}