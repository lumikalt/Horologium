using System.Numerics;
using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Registers;
using RiscV32.State;
using RiscV64.Memory;
using RiscV64.State;

namespace RiscV64.Execute;

/// <summary>
/// Executes RV64I instructions — extends Rv32Executor with:
///   • W-suffix instructions (ADDW, SUBW, …, ADDIW, SLLIW, …): operate on lower 32 bits,
///     sign-extend the 32-bit result to 64 bits.
///   • New loads/stores: LWU (zero-extend), LD (64-bit), SD (64-bit).
///   • Override of Reg() to return the full 64-bit value (no truncation).
///   • Override of Load() for 64-bit sign/zero extension.
///   • Semantic fixes for RV64: 6-bit shift amounts, 64-bit signed comparisons, LW sign-extension.
///   • RV64M: MULW/DIVW/DIVUW/REMW/REMUW (32-bit operands, sign-extended result), plus 64-bit-native
///     overrides of MULH/MULHSU/MULHU/DIV/DIVU/REM/REMU — the inherited RV32 versions operate on the
///     lower 32 bits only (via (int)/(uint) casts), which is wrong once regs.Read() returns a genuine
///     64-bit value under RV64.
///   • RV64F/D: FCVT.L/LU.S/D (float/double→int64), FCVT.S/D.L/LU (int64→float/double), FMV.X.D/FMV.D.X
///     (full 64-bit double↔int bit copy — RV32D has no FMV.X.D since XLEN &lt; FLEN there), plus a fix for
///     the inherited FCVT.W/WU.S/D: the RV32 IntRegF helper zero-extends the 32-bit result, but RV64
///     requires it to be sign-extended into the 64-bit destination register.
/// </summary>
public class Rv64Executor : Rv32Executor {
    // Sign-extend the lower 32 bits of v to 64 bits.
    private static ulong SexW(ulong v) => (ulong)(int)(uint)v;

    // Zero-extend the lower 32 bits of v to 64 bits.
    private static ulong ZextW(ulong v) => v & 0xFFFFFFFFUL;

    protected override ExecuteResult Reg(ulong value) => ExecuteResult.WithResult(value);

    protected override ExecuteResult Load(
        IMemory memory,
        IArchState state,
        ulong pc,
        ulong @base,
        int imm,
        int bytes,
        bool signExtend,
        int bits
    ) {
        ulong vaddr = @base + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, false, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        ulong value = memory.Read(addr, bytes);
        if (!signExtend) return Reg(value);
        int shift = 64 - bits;
        return Reg((ulong)((long)(value << shift) >> shift));
    }

    public override ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op)
            throw new InvalidOperationException(
                $"Rv64Executor received an instruction with an unexpected payload type: " +
                $"{instruction.Payload?.GetType().Name ?? "null"}"
            );

        IRegisterFile regs = state.IntegerRegisters;
        ulong pc = instruction.Pc;

        // Handle RV64-specific ops and base instruction overrides.
        // null means "fall through to base".
        ExecuteResult? r = op switch {
            // ── W-suffix (OP-32) ──────────────────────────────────────────────────
            RvAddw(_, var rs1, var rs2) =>
                Reg(SexW(regs.Read(rs1) + regs.Read(rs2))),
            RvSubw(_, var rs1, var rs2) =>
                Reg(SexW(regs.Read(rs1) - regs.Read(rs2))),
            RvSllw(_, var rs1, var rs2) =>
                Reg(SexW((uint)regs.Read(rs1) << (int)(regs.Read(rs2) & 0x1F))),
            RvSrlw(_, var rs1, var rs2) =>
                Reg(SexW((uint)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F))),
            RvSraw(_, var rs1, var rs2) =>
                Reg(SexW((ulong)((int)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F)))),

            // ── W-suffix (OP-IMM-32) ──────────────────────────────────────────────
            RvAddiw(_, var rs1, var imm) =>
                Reg(SexW(regs.Read(rs1) + (ulong)imm)),
            RvSlliw(_, var rs1, var sh) =>
                Reg(SexW((uint)regs.Read(rs1) << sh)),
            RvSrliw(_, var rs1, var sh) =>
                Reg(SexW((uint)regs.Read(rs1) >> sh)),
            RvSraiw(_, var rs1, var sh) =>
                Reg(SexW((ulong)((int)regs.Read(rs1) >> sh))),

            // ── New RV64I loads/stores ─────────────────────────────────────────────
            RvLwu(_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 4, false, 32),
            RvLd (_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 8, false, 64),
            RvSd (var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 8),

            // ── RV64M W-suffix (OP-32, funct7=0x01) ─────────────────────────────────
            RvMulw (_, var rs1, var rs2) => Reg(SexW(regs.Read(rs1) * regs.Read(rs2))),
            RvDivw (_, var rs1, var rs2) => DivSignedW(regs, rs1, rs2),
            RvDivuw(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(0xFFFFFFFF_FFFFFFFFUL)
                : Reg(SexW((uint)regs.Read(rs1) / (uint)regs.Read(rs2))),
            RvRemw (_, var rs1, var rs2) => RemSignedW(regs, rs1, rs2),
            RvRemuw(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(SexW(regs.Read(rs1)))
                : Reg(SexW((uint)regs.Read(rs1) % (uint)regs.Read(rs2))),

            // ── RV64M: 64-bit-native overrides of the base (RV32-truncating) M-extension ops ──
            RvMulh(_, var rs1, var rs2) =>
                Reg((ulong)(((Int128)(long)regs.Read(rs1) * (long)regs.Read(rs2)) >> 64)),
            RvMulhsu(_, var rs1, var rs2) =>
                Reg((ulong)(((Int128)(long)regs.Read(rs1) * (Int128)regs.Read(rs2)) >> 64)),
            RvMulhu(_, var rs1, var rs2) =>
                Reg((ulong)(((UInt128)regs.Read(rs1) * regs.Read(rs2)) >> 64)),
            RvDiv(_, var rs1, var rs2) => DivSigned64(regs, rs1, rs2),
            RvDivu(_, var rs1, var rs2) => regs.Read(rs2) == 0
                ? Reg(0xFFFFFFFF_FFFFFFFFUL)
                : Reg(regs.Read(rs1) / regs.Read(rs2)),
            RvRem(_, var rs1, var rs2) => RemSigned64(regs, rs1, rs2),
            RvRemu(_, var rs1, var rs2) => regs.Read(rs2) == 0
                ? Reg(regs.Read(rs1))
                : Reg(regs.Read(rs1) % regs.Read(rs2)),

            // ── RV64F/D: 64-bit integer conversions and moves ───────────────────────
            RvFcvtLs (_, var rs1, var rm) => FcvtLsResult(FBits(regs, rs1), rm, state),
            RvFcvtLuS(_, var rs1, var rm) => FcvtLuSResult(FBits(regs, rs1), rm, state),
            RvFcvtSl (_, var rs1, _)      => FpInt64ToFloat((long)regs.Read(rs1)),
            RvFcvtSLu(_, var rs1, _)      => FpUInt64ToFloat(regs.Read(rs1)),
            RvFcvtLd (_, var rs1, var rm) => FcvtLdResult(DBits(regs, rs1), rm, state),
            RvFcvtLuD(_, var rs1, var rm) => FcvtLuDResult(DBits(regs, rs1), rm, state),
            RvFcvtDl (_, var rs1, _)      => DpInt64ToDouble((long)regs.Read(rs1)),
            RvFcvtDLu(_, var rs1, _)      => DpUInt64ToDouble(regs.Read(rs1)),
            RvFmvXd(_, var rs1) => Reg(regs.Read(rs1)),              // double bits → int reg (full 64 bits)
            RvFmvDx(_, var rs1) => ExecuteResult.WithResult(regs.Read(rs1)), // int reg bits → double reg

            // FCVT.W/WU.S/D: base RV32 result zero-extends via IntRegF; RV64 must sign-extend.
            RvFcvtWs or RvFcvtWuS or RvFcvtWd or RvFcvtWuD =>
                SignExtendLow32(base.Execute(instruction, state, memory)),

            // ── RV64 semantic overrides for base instructions ──────────────────────
            // ORI: immediate must be sign-extended to 64 bits, not zero-extended via (uint).
            RvOri(_, var rs1, var imm) =>
                Reg(regs.Read(rs1) | (uint)imm),

            // Shifts: 6-bit shamt mask in RV64 (RV32 uses 5-bit).
            RvSll(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) << (int)(regs.Read(rs2) & 0x3F)),
            RvSrl(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x3F)),
            RvSra(_, var rs1, var rs2) =>
                Reg((ulong)((long)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x3F))),
            RvSlli(_, var rs1, var sh) =>
                Reg(regs.Read(rs1) << sh),
            RvSrli(_, var rs1, var sh) =>
                Reg(regs.Read(rs1) >> sh),
            RvSrai(_, var rs1, var sh) =>
                Reg((ulong)((long)regs.Read(rs1) >> sh)),

            // SLT/SLTI/SLTIU: 64-bit signed/unsigned comparisons.
            RvSlt(_, var rs1, var rs2) =>
                Reg((long)regs.Read(rs1) < (long)regs.Read(rs2) ? 1UL : 0UL),
            RvSlti(_, var rs1, var imm) =>
                Reg((long)regs.Read(rs1) < imm ? 1UL : 0UL),
            RvSltiu(_, var rs1, var imm) =>
                Reg(regs.Read(rs1) < (ulong)imm ? 1UL : 0UL),

            // BLT/BGE: 64-bit signed comparisons.
            RvBlt(var rs1, var rs2, var imm) =>
                Branch((long)regs.Read(rs1) < (long)regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBge(var rs1, var rs2, var imm) =>
                Branch((long)regs.Read(rs1) >= (long)regs.Read(rs2), pc, imm, instruction.SizeBytes),

            // LW: sign-extend 32→64 in RV64 (base executor zero-extends).
            RvLw(_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 4, true, 32),

            // satp: routed to Rv64ArchState.Rv64Csrs (see Rv64CsrFile) instead of the inherited
            // RV32 CsrFile, which stores every CSR as a 32-bit uint and would truncate away Sv39's
            // MODE field (bits 63:60). All other CSR addresses fall through to the base executor.
            RvCsrrw(_, var rs1, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, regs.Read(rs1), true, (_, src) => src),
            RvCsrrs(_, var rs1, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, regs.Read(rs1), rs1 != 0, (old, src) => old | src),
            RvCsrrc(_, var rs1, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, regs.Read(rs1), rs1 != 0, (old, src) => old & ~src),
            RvCsrrwi(_, var zimm, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, zimm, true, (_, src) => src),
            RvCsrrsi(_, var zimm, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, zimm, zimm != 0, (old, src) => old | src),
            RvCsrrci(_, var zimm, var csr) when csr == CsrFile.Satp =>
                ExecuteSatpCsr(state, pc, zimm, zimm != 0, (old, src) => old & ~src),

            // ── RV64A doubleword atomics ─────────────────────────────────────────
            RvLrD(_, var rs1) => AmoLrD(memory, state, pc, regs, rs1),

            RvScD(_, var rs1, var rs2) => AmoScD(memory, state, pc, regs, rs1, rs2),

            RvAmoswapD(_, var rs1, var rs2) => AmoD(memory, state, pc, regs, rs1, rs2, (_, v) => v),
            RvAmoaddD (_, var rs1, var rs2) => AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => a + v),
            RvAmoxorD (_, var rs1, var rs2) => AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => a ^ v),
            RvAmoandD (_, var rs1, var rs2) => AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => a & v),
            RvAmoorD (_, var rs1, var rs2)  => AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => a | v),
            RvAmominD (_, var rs1, var rs2) =>
                AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => (ulong)Math.Min((long)a, (long)v)),
            RvAmomaxD (_, var rs1, var rs2) =>
                AmoD(memory, state, pc, regs, rs1, rs2, (a, v) => (ulong)Math.Max((long)a, (long)v)),
            RvAmominuD(_, var rs1, var rs2) =>
                AmoD(memory, state, pc, regs, rs1, rs2, Math.Min),
            RvAmomaxuD(_, var rs1, var rs2) =>
                AmoD(memory, state, pc, regs, rs1, rs2, Math.Max),

            // ── Zbb/Zbs immediate ops: 64-bit width (base RV32 versions truncate to 32) ──
            // RvSextB/RvSextH are not overridden: the base implementation sign-extends
            // through Reg(), which Rv64Executor already returns untruncated.
            RvBclri(_, var rs1, var sh) => Reg(regs.Read(rs1) & ~(1UL << sh)),
            RvBexti(_, var rs1, var sh) => Reg((regs.Read(rs1) >> sh) & 1),
            RvBinvi(_, var rs1, var sh) => Reg(regs.Read(rs1) ^ (1UL << sh)),
            RvBseti(_, var rs1, var sh) => Reg(regs.Read(rs1) | (1UL << sh)),
            RvClz (_, var rs1)          => Reg((ulong)BitOperations.LeadingZeroCount(regs.Read(rs1))),
            RvCtz (_, var rs1)          => Reg((ulong)BitOperations.TrailingZeroCount(regs.Read(rs1))),
            RvCpop (_, var rs1)         => Reg((ulong)BitOperations.PopCount(regs.Read(rs1))),
            RvRori (_, var rs1, var sh) => Reg(BitOperations.RotateRight(regs.Read(rs1), sh)),
            RvOrcB (_, var rs1)         => OrcB64(regs, rs1),
            RvRev8 (_, var rs1)         => Rev8_64(regs, rs1),

            // ── Zbb/Zbs register-form ops: 64-bit width and 6-bit shift-amount mask
            // (base RV32 versions truncate to 32 bits and mask the shift amount with & 31).
            RvBclr(_, var rs1, var rs2) => Reg(regs.Read(rs1) & ~(1UL << (int)(regs.Read(rs2) & 63))),
            RvBext(_, var rs1, var rs2) => Reg((regs.Read(rs1) >> (int)(regs.Read(rs2) & 63)) & 1),
            RvBinv(_, var rs1, var rs2) => Reg(regs.Read(rs1) ^ (1UL << (int)(regs.Read(rs2) & 63))),
            RvBset(_, var rs1, var rs2) => Reg(regs.Read(rs1) | (1UL << (int)(regs.Read(rs2) & 63))),
            RvRol (_, var rs1, var rs2) =>
                Reg(BitOperations.RotateLeft(regs.Read(rs1), (int)(regs.Read(rs2) & 63))),
            RvRor (_, var rs1, var rs2) =>
                Reg(BitOperations.RotateRight(regs.Read(rs1), (int)(regs.Read(rs2) & 63))),

            // ── Zba: address-generation ops over the zero-extended low 32 bits of rs1 ──
            RvAdduw    (_, var rs1, var rs2) => Reg(regs.Read(rs2) + ZextW(regs.Read(rs1))),
            RvSh1AddUw (_, var rs1, var rs2) => Reg(regs.Read(rs2) + (ZextW(regs.Read(rs1)) << 1)),
            RvSh2AddUw (_, var rs1, var rs2) => Reg(regs.Read(rs2) + (ZextW(regs.Read(rs1)) << 2)),
            RvSh3AddUw (_, var rs1, var rs2) => Reg(regs.Read(rs2) + (ZextW(regs.Read(rs1)) << 3)),
            RvSlliUw   (_, var rs1, var sh)  => Reg(ZextW(regs.Read(rs1)) << sh),

            _ => null,
        };

        return r ?? base.Execute(instruction, state, memory);
    }

    // DIVW: signed 32-bit truncated division, sign-extended; div-by-zero → -1; INT_MIN/-1 → INT_MIN.
    private ExecuteResult DivSignedW(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(0xFFFFFFFF_FFFFFFFFUL);
        if (a == int.MinValue && b == -1) return Reg(SexW(unchecked((uint)int.MinValue)));
        return Reg(SexW(unchecked((uint)(a / b))));
    }

    // REMW: signed 32-bit remainder, sign-extended; div-by-zero → rs1 (sign-extended); INT_MIN/-1 → 0.
    private ExecuteResult RemSignedW(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(SexW(regs.Read(rs1)));
        if (a == int.MinValue && b == -1) return Reg(0);
        return Reg(SexW(unchecked((uint)(a % b))));
    }

    // DIV: signed 64-bit division; div-by-zero → -1; LONG_MIN/-1 → LONG_MIN.
    private ExecuteResult DivSigned64(IRegisterFile regs, int rs1, int rs2) {
        var a = (long)regs.Read(rs1);
        var b = (long)regs.Read(rs2);
        if (b == 0) return Reg(0xFFFFFFFF_FFFFFFFFUL);
        if (a == long.MinValue && b == -1) return Reg(unchecked((ulong)long.MinValue));
        return Reg(unchecked((ulong)(a / b)));
    }

    // REM: signed 64-bit remainder; div-by-zero → rs1; LONG_MIN/-1 → 0.
    private ExecuteResult RemSigned64(IRegisterFile regs, int rs1, int rs2) {
        var a = (long)regs.Read(rs1);
        var b = (long)regs.Read(rs2);
        if (b == 0) return Reg(regs.Read(rs1));
        if (a == long.MinValue && b == -1) return Reg(0);
        return Reg(unchecked((ulong)(a % b)));
    }

    // Atomic doubleword read-modify-write. Returns original value; combines with rs2 and stores.
    private ExecuteResult AmoD(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2,
        Func<ulong, ulong, ulong> combine
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        ulong old = memory.Read(addr, 8);
        memory.Write(addr, combine(old, regs.Read(rs2)), 8);
        return Reg(old);
    }

    private ExecuteResult AmoLrD(IMemory memory, IArchState state, ulong pc, IRegisterFile regs, int rs1) {
        ulong vaddr = regs.Read(rs1);
        (ulong paddr, int fault) = Translate(memory, state, vaddr, false, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        if (ReservationTable is not null)
            ReservationTable.Set(HartId, paddr, 8);
        else
            _reservation = paddr;
        return Reg(memory.Read(paddr, 8));
    }

    private ExecuteResult AmoScD(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong paddr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        bool success = ReservationTable?.TryConsume(HartId, paddr, 8) ?? ConsumePrivateReservation(paddr);
        if (!success) return Reg(1); // reservation is absent or invalidated → fail
        memory.Write(paddr, regs.Read(rs2), 8);
        return Reg(0); // 0 = success
    }

    // orc.b: per-byte OR-combine over the full 64-bit register — nonzero byte → 0xFF.
    private ExecuteResult OrcB64(IRegisterFile regs, int rs1) {
        ulong v = regs.Read(rs1);
        ulong r = 0;
        for (var i = 0; i < 8; i++) {
            ulong b = (v >> (i * 8)) & 0xFF;
            if (b != 0) r |= 0xFFUL << (i * 8);
        }

        return Reg(r);
    }

    // rev8: reverse byte order of a 64-bit doubleword.
    private ExecuteResult Rev8_64(IRegisterFile regs, int rs1) {
        ulong v = regs.Read(rs1);
        ulong r = 0;
        for (var i = 0; i < 8; i++) r |= ((v >> (i * 8)) & 0xFF) << ((7 - i) * 8);
        return Reg(r);
    }

    // Sign-extend the lower 32 bits of a base-class ExecuteResult's register value.
    // Used to fix FCVT.W/WU.S/D under RV64: the inherited RV32 result zero-extends via
    // IntRegF, but a 32-bit conversion result must be sign-extended into a 64-bit register.
    private static ExecuteResult SignExtendLow32(ExecuteResult r) =>
        r.RegisterResult.HasValue ? r with { RegisterResult = (SexW(r.RegisterResult.Value), true), } : r;

    // FCVT.L.S: float→signed int64, with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtLsResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF64(0x7FFFFFFF_FFFFFFFFUL, 0x10u); // NaN → LONG_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            case >= 9223372036854775808f: return IntRegF64(0x7FFFFFFF_FFFFFFFFUL, 0x10u); // > LONG_MAX → NV
            case < -9223372036854775808f: return IntRegF64(0x8000000000000000UL, 0x10u); // < LONG_MIN → NV
        }

        var result = (long)rounded;
        uint flags = f != result ? 0x01u : 0u;
        return IntRegF64(unchecked((ulong)result), flags);
    }

    // FCVT.LU.S: float→unsigned int64, with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtLuSResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF64(0xFFFFFFFF_FFFFFFFFUL, 0x10u); // NaN → ULONG_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            case >= 18446744073709551616f: return IntRegF64(0xFFFFFFFF_FFFFFFFFUL, 0x10u); // > ULONG_MAX → NV
            case < 0f:                     return IntRegF64(0UL, 0x10u);                   // < 0 → NV
        }

        var result = (ulong)rounded;
        uint flags = f != result ? 0x01u : 0u;
        return IntRegF64(result, flags);
    }

    // FCVT.L.D: double→signed int64, with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtLdResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF64(0x7FFFFFFF_FFFFFFFFUL, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 9223372036854775808.0: return IntRegF64(0x7FFFFFFF_FFFFFFFFUL, 0x10u);
            case < -9223372036854775808.0: return IntRegF64(0x8000000000000000UL, 0x10u);
        }

        var result = (long)rounded;
        uint flags = d != result ? 0x01u : 0u;
        return IntRegF64(unchecked((ulong)result), flags);
    }

    // FCVT.LU.D: double→unsigned int64, with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtLuDResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF64(0xFFFFFFFF_FFFFFFFFUL, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 18446744073709551616.0: return IntRegF64(0xFFFFFFFF_FFFFFFFFUL, 0x10u);
            case < 0.0:                     return IntRegF64(0UL, 0x10u);
        }

        var result = (ulong)rounded;
        uint flags = d != result ? 0x01u : 0u;
        return IntRegF64(result, flags);
    }

    // FCVT.S.L: signed int64→float (may set NX if not exactly representable in 24-bit mantissa).
    private static ExecuteResult FpInt64ToFloat(long intVal) {
        var r = (float)intVal;
        bool nx = (long)r != intVal;
        return FloatRegF(r, nx ? 0x01u : 0u);
    }

    // FCVT.S.LU: unsigned int64→float.
    private static ExecuteResult FpUInt64ToFloat(ulong intVal) {
        var r = (float)intVal;
        bool nx = (ulong)r != intVal;
        return FloatRegF(r, nx ? 0x01u : 0u);
    }

    // FCVT.D.L: signed int64→double (may set NX — int64 has more precision than a double's 53-bit mantissa).
    private static ExecuteResult DpInt64ToDouble(long intVal) {
        double r = intVal;
        bool nx = (long)r != intVal;
        return FloatRegD(r, nx ? 0x01u : 0u);
    }

    // FCVT.D.LU: unsigned int64→double.
    private static ExecuteResult DpUInt64ToDouble(ulong intVal) {
        double r = intVal;
        bool nx = (ulong)r != intVal;
        return FloatRegD(r, nx ? 0x01u : 0u);
    }

    // ── satp CSR (Rv64Csrs, not the inherited RV32 CsrFile — see Rv64CsrFile) ─────────────────
    private static ExecuteResult ExecuteSatpCsr(
        IArchState state, ulong pc, ulong src, bool writeSrc, Func<ulong, ulong, ulong> combine
    ) {
        if (state.PrivilegeLevel < RvPrivilege.Supervisor)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        var csrs = ((Rv64ArchState)state).Rv64Csrs;
        ulong old = csrs.Satp;
        if (writeSrc) csrs.Satp = combine(old, src);
        return ExecuteResult.WithResult(old);
    }

    // Sv39 page-table walk for load/store/AMO address translation — overrides the inherited
    // Sv32 walk, which reads satp from the (32-bit, MODE-truncating) RV32 CsrFile.
    protected override (ulong paddr, int faultCause) Translate(
        IMemory memory, IArchState state, ulong vaddr, bool isWrite, bool isExec
    ) {
        // Read(..., Machine) bypasses the privilege check — DirectRead isn't reachable here since
        // it's an internal member of RiscV32.Registers.CsrFile and RiscV64 has no cross-assembly
        // internals access; Machine always satisfies CheckPrivilege regardless of actual privilege.
        bool sum = (state.SystemRegisters.Read(CsrFile.Sstatus, RvPrivilege.Machine) & CsrFile.SstatusSum) != 0;
        return Sv39Walker.Translate(
            memory, ((Rv64ArchState)state).Rv64Csrs.Satp, vaddr, isWrite, isExec, state.PrivilegeLevel, sum
        );
    }
}