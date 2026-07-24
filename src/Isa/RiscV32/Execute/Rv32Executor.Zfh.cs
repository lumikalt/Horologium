#region

using Mechanism;
using RiscV32.Registers;

#endregion

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // ── Half precision (Zfh) helpers ────────────────────────────────────────────

    // RISC-V canonical NaN for float16 (positive, quiet NaN with the top mantissa bit set).
    private const ushort RvCanonicalNaNh = 0x7E00;

    // Half16 minimum normal magnitude (2^-14).
    private static readonly Half MinNormalH = (Half)6.103515625e-05;

    // Wrap an ExecuteResult (e.g., from Load) to NaN-box the 16-bit half value.
    private static ExecuteResult NanBoxH(ExecuteResult r) =>
        r.RegisterResult.HasValue
            ? ExecuteResult.WithResult(0xFFFFFFFFFFFF0000UL | r.RegisterResult.Value)
            : r;

    // Read a half-register as a C# Half (bit-exact reinterpret).
    // NaN-boxing (§11.3, extended to fmt=H): the upper 48 bits must be all 1s; otherwise canonical NaN.
    protected static Half HBits(IRegisterFile regs, int rs) {
        ulong raw = regs.Read(rs);
        return raw >> 16 == 0xFFFFFFFFFFFFUL
            ? BitConverter.UInt16BitsToHalf((ushort)raw)
            : BitConverter.UInt16BitsToHalf(Rv32Executor.RvCanonicalNaNh);
    }

    // Half-result + OR flags into fflags via SideEffect. Writes NaN-boxed (upper 48 bits = 1).
    private static ExecuteResult FloatRegH(Half value, uint flags) {
        ushort bits = Half.IsNaN(value) ? Rv32Executor.RvCanonicalNaNh : BitConverter.HalfToUInt16Bits(value);
        ulong nanBoxed = 0xFFFFFFFFFFFF0000UL | bits;
        if (flags == 0) return ExecuteResult.WithResult(nanBoxed);
        return new ExecuteResult
            { RegisterResult = (nanBoxed, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Signaling NaN check for half: exponent bits[14:10]=0x1F, fraction!=0, quiet bit (bit 9)=0.
    private static bool IsHNan(ushort bits) =>
        (bits & 0x7C00) == 0x7C00 && (bits & 0x3FF) != 0 && (bits & 0x200) == 0;

    // Detect FP exception flags for a half binary op by comparing the half result against
    // float-precision arithmetic (float's 24-bit mantissa is exact for 11-bit-mantissa half ops).
    // op: 0=add, 1=sub, 2=mul, 3=div
    private static uint HpArithFlags(Half a, Half b, Half r, int op) {
        ushort rawA = BitConverter.HalfToUInt16Bits(a);
        ushort rawB = BitConverter.HalfToUInt16Bits(b);
        bool aNaN = Half.IsNaN(a), bNaN = Half.IsNaN(b);
        uint flags = 0;

        if (IsHNan(rawA) || IsHNan(rawB) || (Half.IsNaN(r) && !aNaN && !bNaN)) flags |= 0x10;
        if (Half.IsNaN(r)) return flags;

        float fa = (float)a, fb = (float)b;
        float exact = op switch { 0 => fa + fb, 1 => fa - fb, 2 => fa * fb, _ => fa / fb, };

        if (op == 3 && b == (Half)0f && !aNaN && !Half.IsInfinity(a)) flags |= 0x08;
        if (Half.IsInfinity(r) && !float.IsInfinity(exact)) flags |= 0x04;
        if (Half.IsInfinity(r)) return flags;

        bool nx = (float)r != exact;
        if (nx) flags |= 0x01;
        if (nx && r != (Half)0f && Half.Abs(r) < Rv32Executor.MinNormalH) flags |= 0x02;
        return flags;
    }

    // FMA version: r = a*b + c, exact via float FMA.
    private static uint HpFmaFlags(Half a, Half b, Half c, Half r) {
        ushort rawA = BitConverter.HalfToUInt16Bits(a);
        ushort rawB = BitConverter.HalfToUInt16Bits(b);
        ushort rawC = BitConverter.HalfToUInt16Bits(c);
        bool aNaN = Half.IsNaN(a), bNaN = Half.IsNaN(b), cNaN = Half.IsNaN(c);
        uint flags = 0;
        if (IsHNan(rawA) || IsHNan(rawB) || IsHNan(rawC) ||
            (Half.IsNaN(r) && !aNaN && !bNaN && !cNaN))
            flags |= 0x10;
        if (Half.IsNaN(r)) return flags;

        float exact = MathF.FusedMultiplyAdd((float)a, (float)b, (float)c);
        if (Half.IsInfinity(r) && !float.IsInfinity(exact)) flags |= 0x04;
        if (Half.IsInfinity(r)) return flags;

        bool nx = (float)r != exact;
        if (nx) flags |= 0x01;
        if (nx && r != (Half)0f && Half.Abs(r) < Rv32Executor.MinNormalH) flags |= 0x02;
        return flags;
    }

    // FSQRT.H flags: NV if a < 0, otherwise same NX/UF/OF logic.
    private static uint HpSqrtFlags(Half a, Half r) {
        ushort rawA = BitConverter.HalfToUInt16Bits(a);
        if (IsHNan(rawA)) return 0x10;
        if (a < (Half)0f) return 0x10; // sqrt of negative
        if (Half.IsNaN(r)) return 0;
        float exact = MathF.Sqrt((float)a);
        if (Half.IsInfinity(r) && !float.IsInfinity(exact)) return 0x04;
        if (Half.IsInfinity(r)) return 0;
        bool nx = (float)r != exact;
        uint flags = nx ? 0x01u : 0u;
        if (nx && r != (Half)0f && Half.Abs(r) < Rv32Executor.MinNormalH) flags |= 0x02;
        return flags;
    }

    private static ExecuteResult HpBin(Half a, Half b, int op) {
        Half r = op switch { 0 => a + b, 1 => a - b, 2 => a * b, _ => a / b, };
        return FloatRegH(r, HpArithFlags(a, b, r, op));
    }

    private static ExecuteResult HpSqrt(Half a) {
        Half r = Half.Sqrt(a);
        return FloatRegH(r, HpSqrtFlags(a, r));
    }

    private static ExecuteResult HpFma(Half a, Half b, Half c) {
        Half r = Half.FusedMultiplyAdd(a, b, c);
        return FloatRegH(r, HpFmaFlags(a, b, c, r));
    }

    // FCVT.H.W / FCVT.H.WU: integer → half (may set NX if inexact, or OF+NX if it overflows
    // half's much narrower range). Widens through double first (exact for int32/uint32), then
    // rounds once to half, so there is no double-rounding.
    protected static ExecuteResult FpIntToHalf(long intVal) {
        var r = (Half)(double)intVal;
        if (Half.IsInfinity(r)) return FloatRegH(r, 0x05u); // OF + NX
        bool nx = (double)r != intVal;
        return FloatRegH(r, nx ? 0x01u : 0u);
    }

    // FCVT.H.LU (RV64): unsigned int64 → half, same rounding-through-double approach as FpIntToHalf.
    protected static ExecuteResult FpUIntToHalf(ulong intVal) {
        var r = (Half)(double)intVal;
        if (Half.IsInfinity(r)) return FloatRegH(r, 0x05u); // OF + NX
        bool nx = (double)r != intVal;
        return FloatRegH(r, nx ? 0x01u : 0u);
    }

    // FMIN.H/FMAX.H: set NV if either input is a signaling NaN.
    // Operands go through HBits() for the NaN-boxing check (§11.3) — an improperly
    // boxed source register must read as the canonical NaN, not its raw truncated bits.
    private static ExecuteResult HpMinMax(IRegisterFile regs, int rs1, int rs2, bool isMin) {
        Half a = HBits(regs, rs1), b = HBits(regs, rs2);
        ushort raw1 = BitConverter.HalfToUInt16Bits(a), raw2 = BitConverter.HalfToUInt16Bits(b);
        uint flags = IsHNan(raw1) || IsHNan(raw2) ? 0x10u : 0u;
        Half result = isMin ? HMin(a, b) : HMax(a, b);
        return FloatRegH(result, flags);
    }

    // FEQ.H/FLT.H/FLE.H: NV flag for sNaN (FEQ) or any NaN (FLT/FLE).
    private static ExecuteResult HpCmp(IRegisterFile regs, int rs1, int rs2, int op) {
        Half a = HBits(regs, rs1), b = HBits(regs, rs2);
        ushort raw1 = BitConverter.HalfToUInt16Bits(a), raw2 = BitConverter.HalfToUInt16Bits(b);
        bool nvFlt = Half.IsNaN(a) || Half.IsNaN(b);
        bool nvFeq = IsHNan(raw1) || IsHNan(raw2);
        uint flags = op switch { 0 => nvFeq ? 0x10u : 0u, _ => nvFlt ? 0x10u : 0u, };
        ulong cmp = op switch { 0  => a == b ? 1UL : 0UL, 1 => a < b ? 1UL : 0UL, _ => a <= b ? 1UL : 0UL, };
        return flags != 0
            ? new ExecuteResult { RegisterResult = (cmp, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), }
            : ExecuteResult.WithResult(cmp);
    }

    // RISC-V FCLASS encoding (10-bit result), half-precision bit layout.
    private static ulong HClass(ushort bits) {
        bool sign = bits >> 15 != 0;
        var exp = (uint)((bits >> 10) & 0x1F);
        var frac = (uint)(bits & 0x3FF);
        return exp switch {
            0x1F when frac == 0 => sign ? 1UL << 0 : 1UL << 7,
            0x1F                => frac >> 9 != 0 ? 1UL << 9 : 1UL << 8,
            0 => frac == 0 ? sign ? 1UL << 3 : 1UL << 4 // ±zero
                : sign     ? 1UL << 2 : 1UL << 5,
            _ => sign ? 1UL << 1 : 1UL << 6,
        };
    }

    // RISC-V HMIN: if one arg is NaN, return the other; -0.0 < +0.0.
    private static Half HMin(Half a, Half b) {
        if (Half.IsNaN(a)) return b;
        if (Half.IsNaN(b)) return a;
        if (a == (Half)0f && b == (Half)0f) return Half.IsNegative(a) || Half.IsNegative(b) ? (Half)(-0f) : (Half)0f;
        return a < b ? a : b;
    }

    // RISC-V HMAX: if one arg is NaN, return the other; +0.0 > -0.0.
    private static Half HMax(Half a, Half b) {
        if (Half.IsNaN(a)) return b;
        if (Half.IsNaN(b)) return a;
        if (a == (Half)0f && b == (Half)0f) return !Half.IsNegative(a) || !Half.IsNegative(b) ? (Half)0f : (Half)(-0f);
        return a > b ? a : b;
    }

    // FCVT.H.S: narrow single → half (may set NX, OF).
    private static ExecuteResult FpFcvtHs(float f, int rm, IArchState state) {
        ResolveRm(rm, state);
        var r = (Half)f;
        uint flags = 0;
        uint rawF = BitConverter.SingleToUInt32Bits(f);
        if (IsSNan(rawF))
            flags |= 0x10;
        else
            switch (float.IsNaN(f)) {
                case false when Half.IsInfinity(r) && !float.IsInfinity(f):
                    flags |= 0x04; // OF
                    break;
                case false when !float.IsInfinity(f) && (float)r != f:
                    flags |= 0x01; // NX
                    break;
            }

        return FloatRegH(r, flags);
    }

    // FCVT.S.H: widen half → single (exact; no flags except for sNaN input).
    private static ExecuteResult FpFcvtSh(Half h) {
        ushort rawH = BitConverter.HalfToUInt16Bits(h);
        uint flags = IsHNan(rawH) ? 0x10u : 0u;
        float result = IsHNan(rawH) ? BitConverter.Int32BitsToSingle((int)Rv32Executor.RvCanonicalNaN) : (float)h;
        return FloatRegF(result, flags);
    }

    // FCVT.H.D: narrow double → half (may set NX, OF).
    private static ExecuteResult FpFcvtHd(double d, int rm, IArchState state) {
        ResolveRm(rm, state);
        var r = (Half)d;
        uint flags = 0;
        var rawD = (ulong)BitConverter.DoubleToInt64Bits(d);
        if (IsDsNan(rawD))
            flags |= 0x10;
        else
            switch (double.IsNaN(d)) {
                case false when Half.IsInfinity(r) && !double.IsInfinity(d):
                    flags |= 0x04; // OF
                    break;
                case false when !double.IsInfinity(d) && (double)r != d:
                    flags |= 0x01; // NX
                    break;
            }

        return FloatRegH(r, flags);
    }

    // FCVT.D.H: widen half → double (exact; no flags except for sNaN input).
    private static ExecuteResult FpFcvtDh(Half h) {
        ushort rawH = BitConverter.HalfToUInt16Bits(h);
        uint flags = IsHNan(rawH) ? 0x10u : 0u;
        double result = IsHNan(rawH)
            ? BitConverter.Int64BitsToDouble((long)Rv32Executor.RvCanonicalNaNd)
            : (double)h;
        return FloatRegD(result, flags);
    }

    private static ExecuteResult ExecuteCsrImm(
        IArchState state,
        uint zimm,
        uint csr,
        ulong pc,
        Func<ulong, ulong, ulong> combine,
        bool writeIfSrcZero = true
    ) {
        // Zkr §4.1: a read-only access to seed (CSRRSI/CSRRCI with zimm==0) is illegal —
        // checked before Read() since polling seed is stateful (wipe-on-read) and must not fire
        // on a trapped access.
        if (csr == CsrFile.Seed && !(writeIfSrcZero || zimm != 0))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        ISystemRegisters csrFile = state.SystemRegisters;
        try {
            ulong old = csrFile.Read(csr, state.PrivilegeLevel);
            // Per spec §2.8: CSRRSI/CSRRCI with zimm==0 must not write the CSR.
            if (writeIfSrcZero || zimm != 0) csrFile.Write(csr, combine(old, zimm), state.PrivilegeLevel);
            return ExecuteResult.WithResult(old);
        }
        catch (SystemRegisterAccessException) {
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        }
    }
}