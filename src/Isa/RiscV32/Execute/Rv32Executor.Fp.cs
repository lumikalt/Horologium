#region

using System.Numerics;
using Mechanism;
using RiscV32.Registers;
using RiscV32.State;

#endregion

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // RISC-V canonical NaN for float32 (positive, quiet NaN with one mantissa bit set).
    private const uint RvCanonicalNaN = 0x7FC00000u;

    // Double result + OR flags into fflags via SideEffect.
    private const ulong RvCanonicalNaNd = 0x7FF8000000000000UL;

    // ── Float32 minimum normal magnitude (2^-126) ─────────────────────────────
    private const float MinNormalF = 1.1754944e-38f;

    // ── Float64 minimum normal magnitude (2^-1022) ────────────────────────────
    private const double MinNormalD = 2.2250738585072014E-308;
    // ── FP helpers ────────────────────────────────────────────────────────────

    // Wrap an ExecuteResult (e.g., from Load) to NaN-box the 32-bit float value.
    private static ExecuteResult NanBoxF(ExecuteResult r) =>
        r.RegisterResult.HasValue
            ? ExecuteResult.WithResult(0xFFFFFFFF00000000UL | r.RegisterResult.Value)
            : r;

    // Read a float register as a C# float (bit-exact reinterpret).
    // NaN-boxing (§11.3): upper 32 bits must be all 1s; otherwise canonical NaN.
    protected static float FBits(IRegisterFile regs, int rs) {
        ulong raw = regs.Read(rs);
        return raw >> 32 == 0xFFFFFFFFu
            ? BitConverter.Int32BitsToSingle((int)(uint)raw)
            : BitConverter.Int32BitsToSingle((int)Rv32Executor.RvCanonicalNaN);
    }

    // Read a double register as a C# double (bit-exact reinterpret).
    protected static double DBits(IRegisterFile regs, int rs) =>
        BitConverter.Int64BitsToDouble((long)regs.Read(rs));

    // Float result + OR flags into fflags via SideEffect.
    // Writes NaN-boxed value (upper 32 bits = 0xFFFFFFFF) per spec §11.3.
    protected static ExecuteResult FloatRegF(float value, uint flags) {
        uint bits = float.IsNaN(value) ? Rv32Executor.RvCanonicalNaN : (uint)BitConverter.SingleToInt32Bits(value);
        ulong nanBoxed = 0xFFFFFFFF00000000UL | bits;
        if (flags == 0) return ExecuteResult.WithResult(nanBoxed);
        return new ExecuteResult
            { RegisterResult = (nanBoxed, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    protected static ExecuteResult FloatRegD(double value, uint flags) {
        ulong bits = double.IsNaN(value) ? Rv32Executor.RvCanonicalNaNd : (ulong)BitConverter.DoubleToInt64Bits(value);
        if (flags == 0) return ExecuteResult.WithResult(bits);
        return new ExecuteResult
            { RegisterResult = (bits, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Integer result + OR flags into fflags via SideEffect.
    // FCVT.W*.{S,D,H} results are always sign-extended to XLEN on RV64, even for
    // the unsigned WU variants (they're specified like other W-suffixed ops). Routed
    // through the virtual Reg() so RV32 truncates the sign-extension back to 32 bits
    // (a no-op there) while RV64's override keeps the full 64-bit sign-extended value.
    private ExecuteResult IntRegF(uint value, uint flags) {
        ExecuteResult result = Reg((ulong)(int)value);
        if (flags == 0) return result;
        return result with { SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Same as IntRegF but for a full 64-bit result (RV64F/D int64 conversions — FCVT.L/LU.S/D —
    // fill the whole destination register directly, rather than sign-extending a 32-bit value).
    protected static ExecuteResult IntRegF64(ulong value, uint flags) {
        if (flags == 0) return ExecuteResult.WithResult(value);
        return new ExecuteResult
            { RegisterResult = (value, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Detect FP exception flags for binary arithmetic op by comparing the float
    // result against double-precision arithmetic (which is exact for 24-bit mantissa ops).
    // op: 0=add, 1=sub, 2=mul, 3=div
    private static uint FpArithFlags(float a, float b, float r, int op) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        uint rawB = BitConverter.SingleToUInt32Bits(b);
        bool aNaN = float.IsNaN(a), bNaN = float.IsNaN(b);
        uint flags = 0;

        // NV: sNaN input, or NaN result from non-NaN inputs (e.g., Inf-Inf, 0*Inf)
        if (IsSNan(rawA) || IsSNan(rawB) || (float.IsNaN(r) && !aNaN && !bNaN)) flags |= 0x10;
        if (float.IsNaN(r)) return flags;

        double da = a, db = b;
        double exact = op switch { 0 => da + db, 1 => da - db, 2 => da * db, _ => da / db, };

        // DZ: finite non-zero / 0
        if (op == 3 && b == 0f && !aNaN && !float.IsInfinity(a)) flags |= 0x08;
        // OF: float overflowed to Inf but math didn't
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) flags |= 0x04;
        if (float.IsInfinity(r)) return flags;

        bool nx = r != exact;
        if (nx) flags |= 0x01;
        // UF: subnormal result that is also inexact
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // FMA version: r = a*b + c, exact via double FMA.
    private static uint FpFmaFlags(float a, float b, float c, float r) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        uint rawB = BitConverter.SingleToUInt32Bits(b);
        uint rawC = BitConverter.SingleToUInt32Bits(c);
        bool aNaN = float.IsNaN(a), bNaN = float.IsNaN(b), cNaN = float.IsNaN(c);
        uint flags = 0;
        if (IsSNan(rawA) || IsSNan(rawB) || IsSNan(rawC) ||
            (float.IsNaN(r) && !aNaN && !bNaN && !cNaN))
            flags |= 0x10;
        if (float.IsNaN(r)) return flags;

        double exact = Math.FusedMultiplyAdd(a, b, c);
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) flags |= 0x04;
        if (float.IsInfinity(r)) return flags;

        bool nx = r != exact;
        if (nx) flags |= 0x01;
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // FSQRT flags: NV if a < 0, otherwise same NX/UF/OF logic.
    private static uint FpSqrtFlags(float a, float r) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        if (IsSNan(rawA)) return 0x10;
        if (a < 0f) return 0x10; // sqrt of negative
        if (float.IsNaN(r)) return 0;
        double exact = Math.Sqrt(a);
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) return 0x04;
        if (float.IsInfinity(r)) return 0;
        bool nx = r != exact;
        uint flags = nx ? 0x01u : 0u;
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // Signaling NaN check: exponent=0xFF, fraction!=0, quiet bit (bit 22) = 0.
    private static bool IsSNan(uint bits) =>
        (bits & 0x7F800000u) == 0x7F800000u && (bits & 0x7FFFFFu) != 0 && (bits & 0x400000u) == 0;

    // ── MXCSR-tracked FP arithmetic ───────────────────────────────────────────

    private static ExecuteResult FpBin(float a, float b, int op) {
        float r = op switch { 0 => a + b, 1 => a - b, 2 => a * b, _ => a / b, };
        return FloatRegF(r, FpArithFlags(a, b, r, op));
    }

    private static ExecuteResult FpSqrt(float a) {
        float r = MathF.Sqrt(a);
        return FloatRegF(r, FpSqrtFlags(a, r));
    }

    private static ExecuteResult FpFma(float a, float b, float c) {
        float r = MathF.FusedMultiplyAdd(a, b, c);
        return FloatRegF(r, FpFmaFlags(a, b, c, r));
    }

    // FCVT.S.W / FCVT.S.WU: integer → float (may set NX if inexact).
    // Note: rounding mode affects which float is chosen; C# uses RNE by default.
    // We use the hardware default (RNE) since .NET doesn't expose per-op rounding.
    private static ExecuteResult FpIntToFloat(long intVal) {
        var r = (float)intVal;
        // NX if the integer can't be exactly represented in float32 (24-bit mantissa)
        bool nx = (long)r != intVal;
        return FloatRegF(r, nx ? 0x01u : 0u);
    }

    // FCVT.W.S with rounding mode and NV/NX flags.
    private ExecuteResult FcvtWsResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF(0x7FFFFFFFu, 0x10u); // NaN → INT_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            // Check if rounded value is out of int32 range
            case >= 2147483648f: return IntRegF(0x7FFFFFFFu, 0x10u); // > INT_MAX → NV
            case < -2147483648f: return IntRegF(0x80000000u, 0x10u); // < INT_MIN → NV
        }

        var result = (uint)(int)rounded;
        uint flags = f != (int)result ? 0x01u : 0u; // NX if original was not exact integer
        return IntRegF(result, flags);
    }

    // FCVT.WU.S with rounding mode and NV/NX flags.
    private ExecuteResult FcvtWuSResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF(0xFFFFFFFFu, 0x10u); // NaN → UINT_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            // Check if rounded value is out of uint32 range
            case >= 4294967296f: return IntRegF(0xFFFFFFFFu, 0x10u); // > UINT_MAX → NV
            case < 0f:           return IntRegF(0u, 0x10u);          // < 0 → NV
        }

        var result = (uint)rounded;
        uint flags = f != result ? 0x01u : 0u; // NX if original was not exact integer
        return IntRegF(result, flags);
    }

    // FMIN/FMAX: set NV if either input is a signaling NaN.
    // Operands go through FBits() for the NaN-boxing check (§11.3) — an improperly
    // boxed source register must read as the canonical NaN, not its raw truncated bits.
    private static ExecuteResult FpMinMax(IRegisterFile regs, int rs1, int rs2, bool isMin) {
        float a = FBits(regs, rs1), b = FBits(regs, rs2);
        uint raw1 = BitConverter.SingleToUInt32Bits(a), raw2 = BitConverter.SingleToUInt32Bits(b);
        uint flags = IsSNan(raw1) || IsSNan(raw2) ? 0x10u : 0u;
        float result = isMin ? FMin(a, b) : FMax(a, b);
        return FloatRegF(result, flags);
    }

    // FEQ/FLT/FLE: NV flag for sNaN (FEQ) or any NaN (FLT/FLE).
    private static ExecuteResult FpCmp(IRegisterFile regs, int rs1, int rs2, int op) {
        float a = FBits(regs, rs1), b = FBits(regs, rs2);
        uint raw1 = BitConverter.SingleToUInt32Bits(a), raw2 = BitConverter.SingleToUInt32Bits(b);
        // FLT/FLE: NV if either operand is NaN; FEQ: NV only for sNaN
        bool nvFlt = float.IsNaN(a) || float.IsNaN(b);
        bool nvFeq = IsSNan(raw1) || IsSNan(raw2);
        uint flags = op switch { 0 => nvFeq ? 0x10u : 0u, _ => nvFlt ? 0x10u : 0u, };
        ulong cmp = op switch { 0  => a == b ? 1UL : 0UL, 1 => a < b ? 1UL : 0UL, _ => a <= b ? 1UL : 0UL, };
        return flags != 0
            ? new ExecuteResult { RegisterResult = (cmp, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), }
            : ExecuteResult.WithResult(cmp);
    }

    // RISC-V FCLASS encoding (10-bit result).
    private static ulong FClass(uint bits) {
        bool sign = bits >> 31 != 0;
        uint exp = (bits >> 23) & 0xFF;
        uint frac = bits & 0x7FFFFF;
        return exp switch {
            0xFF when frac == 0 => sign ? 1UL << 0 : 1UL << 7,
            0xFF                => frac >> 22 != 0 ? 1UL << 9 : 1UL << 8,
            0 => frac == 0 ? sign ? 1UL << 3 : 1UL << 4 // ±zero
                : sign     ? 1UL << 2 : 1UL << 5,
            _ => sign ? 1UL << 1 : 1UL << 6,
        };
    }

    // Apply rounding mode to a float before converting to integer.
    // rm: 0=RNE, 1=RTZ, 2=RDN, 3=RUP, 4=RMM, 7=DYN (resolved before call).
    protected static float ApplyRm(float f, int rm) => rm switch {
        0 => MathF.Round(f, MidpointRounding.ToEven),
        2 => MathF.Floor(f),
        3 => MathF.Ceiling(f),
        4 => MathF.Round(f, MidpointRounding.AwayFromZero),
        _ => f >= 0f ? MathF.Floor(f) : MathF.Ceiling(f), // RTZ (truncate toward zero)
    };

    // Resolve DYN rounding mode (rm=7) from fcsr.frm.
    protected static int ResolveRm(int rm, IArchState state) =>
        rm == 7 ? (int)((Rv32ArchState)state).CsrFile.DirectRead(CsrFile.Frm) : rm;

    // RISC-V FCVT.W.S: float → signed int with rounding mode and saturating clamp.
    private static uint FcvtWs(float f) =>
        f switch {
            float.NaN or >= 2147483648f => 0x7FFFFFFF // INT_MAX
           ,
            < -2147483648f => 0x80000000u // INT_MIN
           ,
            _ => (uint)(int)f,
        };

    // RISC-V FCVT.WU.S: float → unsigned int with saturating clamp.
    private static uint FcvtWuS(float f) =>
        f switch {
            float.NaN or >= 4294967296f => 0xFFFFFFFF,
            < 0f                        => 0,
            _                           => (uint)f,
        };

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

    // ── Double precision helpers ───────────────────────────────────────────────

    // FLD: reads 8 bytes from memory; returns all 64 bits (no truncation).
    private ExecuteResult LoadD(IMemory memory, IArchState state, ulong pc, ulong @base, int imm) {
        ulong vaddr = @base + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, false, false);
        return fault != 0
            ? ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc))
            : ExecuteResult.WithResult(memory.Read(addr, 8));
    }

    // sNaN detection for double (quiet bit = bit 51 is 0, mantissa != 0).
    private static bool IsDsNan(ulong raw) =>
        (raw & 0x7FF0000000000000UL) == 0x7FF0000000000000UL &&
        (raw & 0x000FFFFFFFFFFFFFUL) != 0 &&
        (raw & 0x0008000000000000UL) == 0;

    // Decompose a finite double into (mantissa, exp2) such that the exact real
    // value is mantissa * 2^exp2, with mantissa a signed BigInteger. This lets
    // NX/UF for double-precision ops be computed by *exact* integer arithmetic
    // instead of a wider floating type (C# has none wider than double).
    private static (BigInteger Mantissa, int Exp2) DecomposeExact(double d) {
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool neg = bits < 0;
        var exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa;
        int e2;
        if (exp == 0) {
            if (frac == 0) return (BigInteger.Zero, 0);
            mantissa = frac;
            e2 = -1074;
        }
        else {
            mantissa = frac | (1L << 52);
            e2 = exp - 1075;
        }

        return (neg ? -mantissa : mantissa, e2);
    }

    private static (BigInteger Mantissa, int Exp2) ExactAdd(
        (BigInteger Mantissa, int Exp2) x,
        (BigInteger Mantissa, int Exp2) y
    ) =>
        x.Exp2 < y.Exp2
            ? (x.Mantissa + (y.Mantissa << (y.Exp2 - x.Exp2)), x.Exp2)
            : y.Exp2 < x.Exp2
                ? (y.Mantissa + (x.Mantissa << (x.Exp2 - y.Exp2)), y.Exp2)
                : (x.Mantissa + y.Mantissa, x.Exp2);

    private static (BigInteger Mantissa, int Exp2) ExactMul(
        (BigInteger Mantissa, int Exp2) x,
        (BigInteger Mantissa, int Exp2) y
    ) =>
        (x.Mantissa * y.Mantissa, x.Exp2 + y.Exp2);

    private static bool ExactEqual((BigInteger Mantissa, int Exp2) x, (BigInteger Mantissa, int Exp2) y) {
        if (x.Exp2 < y.Exp2)
            y = (y.Mantissa << (y.Exp2 - x.Exp2), x.Exp2);
        else if (y.Exp2 < x.Exp2) x = (x.Mantissa << (x.Exp2 - y.Exp2), y.Exp2);
        return x.Mantissa == y.Mantissa;
    }

    // Detect flags for D-precision binary op via exact BigInteger arithmetic
    // (no double-rounding ambiguity, unlike a wider-float approximation).
    private static uint DpArithFlags(double a, double b, double r, int op) {
        var rawA = (ulong)BitConverter.DoubleToInt64Bits(a);
        var rawB = (ulong)BitConverter.DoubleToInt64Bits(b);
        bool aNaN = double.IsNaN(a), bNaN = double.IsNaN(b);
        uint flags = 0;
        if (IsDsNan(rawA) || IsDsNan(rawB) || (double.IsNaN(r) && !aNaN && !bNaN)) flags |= 0x10;
        if (double.IsNaN(r)) return flags;
        if (op == 3 && b == 0.0 && !aNaN && !double.IsInfinity(a)) flags |= 0x08;
        if (double.IsInfinity(r) && !double.IsInfinity(a) && !double.IsInfinity(b)) flags |= 0x04;
        if (double.IsInfinity(r)) return flags;

        (BigInteger Mantissa, int Exp2) da = DecomposeExact(a);
        (BigInteger Mantissa, int Exp2) db = DecomposeExact(b);
        (BigInteger Mantissa, int Exp2) dr = DecomposeExact(r);
        bool nx = op switch {
            0 => !ExactEqual(ExactAdd(da, db), dr),
            1 => !ExactEqual(ExactAdd(da, (-db.Mantissa, db.Exp2)), dr),
            2 => !ExactEqual(ExactMul(da, db), dr),
            _ => !ExactEqual(da, ExactMul(dr, db)), // a == r*b exactly ⟺ r == a/b exactly
        };
        if (nx) flags |= 0x01;
        if (nx && r != 0.0 && Math.Abs(r) < Rv32Executor.MinNormalD) flags |= 0x02;
        return flags;
    }

    private static uint DpFmaFlags(double a, double b, double c, double r) {
        var rawA = (ulong)BitConverter.DoubleToInt64Bits(a);
        var rawB = (ulong)BitConverter.DoubleToInt64Bits(b);
        var rawC = (ulong)BitConverter.DoubleToInt64Bits(c);
        bool aNaN = double.IsNaN(a), bNaN = double.IsNaN(b), cNaN = double.IsNaN(c);
        uint flags = 0;
        if (IsDsNan(rawA) || IsDsNan(rawB) || IsDsNan(rawC) ||
            (double.IsNaN(r) && !aNaN && !bNaN && !cNaN))
            flags |= 0x10;
        if (double.IsNaN(r)) return flags;
        if (double.IsInfinity(r) &&
            !double.IsInfinity(a) && !double.IsInfinity(b) && !double.IsInfinity(c))
            flags |= 0x04;
        if (double.IsInfinity(r)) return flags;

        bool nx = !ExactEqual(
            ExactAdd(ExactMul(DecomposeExact(a), DecomposeExact(b)), DecomposeExact(c)),
            DecomposeExact(r)
        );
        if (nx) flags |= 0x01;
        if (nx && r != 0.0 && Math.Abs(r) < Rv32Executor.MinNormalD) flags |= 0x02;
        return flags;
    }

    private static uint DpSqrtFlags(double a, double r) {
        if (!double.IsNaN(a) && a < 0.0) return 0x10; // NV: sqrt of negative
        if (double.IsInfinity(r) || double.IsNaN(r)) return 0;

        bool nx = !ExactEqual(ExactMul(DecomposeExact(r), DecomposeExact(r)), DecomposeExact(a));
        uint flags = nx ? 0x01u : 0u;
        if (nx && r != 0.0 && Math.Abs(r) < Rv32Executor.MinNormalD) flags |= 0x02;
        return flags;
    }

    private static ExecuteResult DpBin(double a, double b, int op) {
        double r = op switch { 0 => a + b, 1 => a - b, 2 => a * b, _ => a / b, };
        return FloatRegD(r, DpArithFlags(a, b, r, op));
    }

    private static ExecuteResult DpSqrt(double a) {
        double r = Math.Sqrt(a);
        return FloatRegD(r, DpSqrtFlags(a, r));
    }

    private static ExecuteResult DpFma(double a, double b, double c) {
        double r = Math.FusedMultiplyAdd(a, b, c);
        return FloatRegD(r, DpFmaFlags(a, b, c, r));
    }

    private static ExecuteResult DpMinMax(IRegisterFile regs, int rs1, int rs2, bool isMin) {
        ulong raw1 = regs.Read(rs1), raw2 = regs.Read(rs2);
        double a = BitConverter.Int64BitsToDouble((long)raw1);
        double b = BitConverter.Int64BitsToDouble((long)raw2);
        uint flags = IsDsNan(raw1) || IsDsNan(raw2) ? 0x10u : 0u;
        double result = isMin ? DMin(a, b) : DMax(a, b);
        return FloatRegD(result, flags);
    }

    private static ExecuteResult DpCmp(IRegisterFile regs, int rs1, int rs2, int op) {
        ulong raw1 = regs.Read(rs1), raw2 = regs.Read(rs2);
        double a = BitConverter.Int64BitsToDouble((long)raw1);
        double b = BitConverter.Int64BitsToDouble((long)raw2);
        bool nvFlt = double.IsNaN(a) || double.IsNaN(b);
        bool nvFeq = IsDsNan(raw1) || IsDsNan(raw2);
        uint flags = op switch { 0 => nvFeq ? 0x10u : 0u, _ => nvFlt ? 0x10u : 0u, };
        ulong cmp = op switch { 0  => a == b ? 1UL : 0UL, 1 => a < b ? 1UL : 0UL, _ => a <= b ? 1UL : 0UL, };
        return flags != 0
            ? new ExecuteResult { RegisterResult = (cmp, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), }
            : ExecuteResult.WithResult(cmp);
    }

    // FCLASS.D encoding (10-bit result, same bit semantics as FCLASS.S).
    private static ulong DClass(ulong bits) {
        bool sign = bits >> 63 != 0;
        var exp = (uint)((bits >> 52) & 0x7FF);
        ulong frac = bits & 0x000FFFFFFFFFFFFFUL;
        return exp switch {
            0x7FF when frac == 0 => sign ? 1UL << 0 : 1UL << 7,
            0x7FF                => frac >> 51 != 0 ? 1UL << 9 : 1UL << 8,
            0 => frac == 0 ? sign ? 1UL << 3 : 1UL << 4
                : sign     ? 1UL << 2 : 1UL << 5,
            _ => sign ? 1UL << 1 : 1UL << 6,
        };
    }

    protected static double ApplyRmD(double d, int rm) => rm switch {
        0 => Math.Round(d, MidpointRounding.ToEven),
        2 => Math.Floor(d),
        3 => Math.Ceiling(d),
        4 => Math.Round(d, MidpointRounding.AwayFromZero),
        _ => d >= 0.0 ? Math.Floor(d) : Math.Ceiling(d), // RTZ
    };

    private ExecuteResult FcvtWdResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF(0x7FFFFFFFu, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 2147483648.0: return IntRegF(0x7FFFFFFFu, 0x10u);
            case < -2147483648.0: return IntRegF(0x80000000u, 0x10u);
        }

        var result = (uint)(int)rounded;
        uint flags = d != (int)result ? 0x01u : 0u;
        return IntRegF(result, flags);
    }

    private ExecuteResult FcvtWudResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF(0xFFFFFFFFu, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 4294967296.0: return IntRegF(0xFFFFFFFFu, 0x10u);
            case < 0.0:           return IntRegF(0u, 0x10u);
        }

        var result = (uint)rounded;
        uint flags = d != result ? 0x01u : 0u;
        return IntRegF(result, flags);
    }

    // FCVT.D.W / FCVT.D.WU: integer → double (always exact for 32-bit integers).
    private static ExecuteResult DpIntToDouble(long intVal) =>
        FloatRegD(intVal, 0);

    // FCVT.S.D: narrow double → single (may set NX, OF).
    private static ExecuteResult FpFcvtSd(double d, int rm, IArchState state) {
        ResolveRm(rm, state);
        // C# always rounds to nearest-even; other rounding modes are best-effort.
        var r = (float)d;
        uint flags = 0;
        var rawD = (ulong)BitConverter.DoubleToInt64Bits(d);
        if (IsDsNan(rawD))
            flags |= 0x10;
        else
            switch (double.IsNaN(d)) {
                case false when float.IsInfinity(r) && !double.IsInfinity(d):
                    flags |= 0x04; // OF
                    break;
                case false when !double.IsInfinity(d) && r != d:
                    flags |= 0x01; // NX
                    break;
            }

        return FloatRegF(r, flags);
    }

    // FCVT.D.S: widen single → double (exact; no flags except for sNaN input).
    private static ExecuteResult FpFcvtDs(float f) {
        uint rawF = BitConverter.SingleToUInt32Bits(f);
        uint flags = IsSNan(rawF) ? 0x10u : 0u;
        // Canonical NaN on sNaN input; otherwise exact widening.
        double result = IsSNan(rawF) ? BitConverter.Int64BitsToDouble((long)Rv32Executor.RvCanonicalNaNd) : f;
        return FloatRegD(result, flags);
    }

    // RISC-V DMIN: if one arg is NaN return the other; -0.0 < +0.0.
    private static double DMin(double a, double b) {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        if (a == 0.0 && b == 0.0)
            return BitConverter.DoubleToInt64Bits(a) < 0 || BitConverter.DoubleToInt64Bits(b) < 0 ? -0.0 : 0.0;
        return a < b ? a : b;
    }

    // RISC-V DMAX: if one arg is NaN return the other; +0.0 > -0.0.
    private static double DMax(double a, double b) {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        if (a == 0.0 && b == 0.0)
            return BitConverter.DoubleToInt64Bits(a) >= 0 || BitConverter.DoubleToInt64Bits(b) >= 0 ? 0.0 : -0.0;
        return a > b ? a : b;
    }
}