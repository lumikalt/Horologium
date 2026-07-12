using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for RV64F/D — the int64 conversions (FCVT.L/LU.S/D, FCVT.S/D.L/LU) that don't exist
/// under RV32, FMV.X.D/FMV.D.X (full 64-bit double↔int bit copy, impossible under RV32 where
/// XLEN &lt; FLEN), and the RV64 sign-extension fix for the inherited FCVT.W/WU.S/D.
/// </summary>
public class Rv64FTests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(65536);

    private Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // OP-FP encoding helper: opcode=0x53. rs1/rs2/rd are the *raw* 5-bit instruction fields
    // (0-31) — for fp operands this is the bare "f<n>" number; the decoder adds +32 internally
    // to map into the unified 64-register file.
    private static uint OpFp(uint funct7, int rs2, int rs1, uint funct3, int rd) =>
        (funct7 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x53;

    private static ulong FBoxed(float f) => 0xFFFFFFFF00000000UL | (uint)BitConverter.SingleToInt32Bits(f);
    private static float AFloat(ulong bits) => BitConverter.Int32BitsToSingle((int)(uint)bits);
    private static ulong Dbl(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);
    private static double ADouble(ulong bits) => BitConverter.Int64BitsToDouble((long)bits);

    // Unified register-file index for f2 (raw fp number 2 + 32).
    private const int F2 = 34;

    // ── FCVT.L.S / FCVT.LU.S: float → int64 ────────────────────────────────────────

    [Fact]
    public void FcvtLs_TruncatesPositiveFloat() {
        // fcvt.l.s x1, f2, rm=1(RTZ): funct7=0x60, rs2=2
        Rv64ArchState s = MakeState((F2, FBoxed(3.7f)));
        ExecuteResult r = Exec(OpFp(0x60, 2, 2, 1, 1), s);
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void FcvtLs_NegativeFloat_SignExtends() {
        Rv64ArchState s = MakeState((F2, FBoxed(-3.7f)));
        ExecuteResult r = Exec(OpFp(0x60, 2, 2, 1, 1), s); // RTZ
        Assert.Equal(unchecked((ulong)(long)(-3)), r.RegisterResult.Value);
    }

    [Fact]
    public void FcvtLs_LargeValue_UsesFull64Bits() {
        // A value that overflows int32 but fits comfortably in int64.
        Rv64ArchState s = MakeState((F2, FBoxed(1e15f)));
        ExecuteResult r = Exec(OpFp(0x60, 2, 2, 1, 1), s); // RTZ
        Assert.Equal((ulong)(long)1e15f, r.RegisterResult.Value);
    }

    [Fact]
    public void FcvtLuS_ConvertsPositiveFloat() {
        // funct7=0x60, rs2=3
        Rv64ArchState s = MakeState((F2, FBoxed(5.0f)));
        ExecuteResult r = Exec(OpFp(0x60, 3, 2, 0, 1), s);
        Assert.Equal(5UL, r.RegisterResult.Value);
    }

    // ── FCVT.S.L / FCVT.S.LU: int64 → float ────────────────────────────────────────

    [Fact]
    public void FcvtSl_ConvertsNegativeInt64() {
        // fcvt.s.l f1, x2: funct7=0x68, rs2=2, rs1=2(x2)
        Rv64ArchState s = MakeState((2, unchecked((ulong)(long)(-42))));
        ExecuteResult r = Exec(OpFp(0x68, 2, 2, 0, 1), s);
        Assert.Equal(-42f, AFloat(r.RegisterResult.Value));
    }

    [Fact]
    public void FcvtSLu_ConvertsLargeUnsignedInt64() {
        // funct7=0x68, rs2=3 — value whose top bit would look negative if treated as signed
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL)); // 2^63
        ExecuteResult r = Exec(OpFp(0x68, 3, 2, 0, 1), s);
        Assert.Equal((float)0x8000_0000_0000_0000UL, AFloat(r.RegisterResult.Value));
    }

    // ── FCVT.L.D / FCVT.LU.D: double → int64 ───────────────────────────────────────

    [Fact]
    public void FcvtLd_TruncatesPositiveDouble() {
        // funct7=0x61, rs2=2, rm=1(RTZ)
        Rv64ArchState s = MakeState((F2, Dbl(3.7)));
        ExecuteResult r = Exec(OpFp(0x61, 2, 2, 1, 1), s);
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void FcvtLuD_ConvertsPositiveDouble() {
        // funct7=0x61, rs2=3
        Rv64ArchState s = MakeState((F2, Dbl(7.0)));
        ExecuteResult r = Exec(OpFp(0x61, 3, 2, 0, 1), s);
        Assert.Equal(7UL, r.RegisterResult.Value);
    }

    // ── FCVT.D.L / FCVT.D.LU: int64 → double ───────────────────────────────────────

    [Fact]
    public void FcvtDl_ConvertsNegativeInt64() {
        // funct7=0x69, rs2=2
        Rv64ArchState s = MakeState((2, unchecked((ulong)(long)(-123))));
        ExecuteResult r = Exec(OpFp(0x69, 2, 2, 0, 1), s);
        Assert.Equal(-123.0, ADouble(r.RegisterResult.Value));
    }

    [Fact]
    public void FcvtDLu_ConvertsLargeUnsignedInt64() {
        // funct7=0x69, rs2=3
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL));
        ExecuteResult r = Exec(OpFp(0x69, 3, 2, 0, 1), s);
        Assert.Equal((double)0x8000_0000_0000_0000UL, ADouble(r.RegisterResult.Value));
    }

    // ── FMV.X.D / FMV.D.X: full 64-bit bit copy ────────────────────────────────────

    [Fact]
    public void FmvXd_CopiesFullDoubleBitsToIntReg() {
        // fmv.x.d x1, f2: funct7=0x71, rs2=0, funct3=0
        ulong bits = Dbl(-2.5);
        Rv64ArchState s = MakeState((F2, bits));
        ExecuteResult r = Exec(OpFp(0x71, 0, 2, 0, 1), s);
        Assert.Equal(bits, r.RegisterResult.Value);
    }

    [Fact]
    public void FmvDx_CopiesFullIntBitsToDoubleReg() {
        // fmv.d.x f1, x2: funct7=0x79, rs2=0
        ulong bits = Dbl(1.25);
        Rv64ArchState s = MakeState((2, bits));
        ExecuteResult r = Exec(OpFp(0x79, 0, 2, 0, 1), s);
        Assert.Equal(bits, r.RegisterResult.Value);
    }

    // ── FCVT.W/WU.S/D: RV64 must sign-extend, not zero-extend ─────────────────────

    [Fact]
    public void FcvtWs_NegativeResult_SignExtendsTo64Bits() {
        // fcvt.w.s x1, f2, rm=1(RTZ) — f2 = -3.7 → truncates to -3
        Rv64ArchState s = MakeState((F2, FBoxed(-3.7f)));
        ExecuteResult r = Exec(OpFp(0x60, 0, 2, 1, 1), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFDUL, r.RegisterResult.Value); // -3 sign-extended, not zero-extended
    }

    [Fact]
    public void FcvtWuS_LargeResult_SignExtendsPerSpec() {
        // fcvt.wu.s x1, f2 — f2 = 3000000000.0 (> INT32_MAX, fits in uint32).
        // Per spec, RV64 FCVT.WU.S sign-extends its 32-bit unsigned result too (like ADDW):
        // the "U" only affects the conversion's range/rounding, not the final register width.
        Rv64ArchState s = MakeState((F2, FBoxed(3000000000.0f)));
        ExecuteResult r = Exec(OpFp(0x60, 1, 2, 1, 1), s); // RTZ
        Assert.Equal(0xFFFFFFFF_B2D05E00UL, r.RegisterResult.Value);
    }

    [Fact]
    public void FcvtWd_NegativeResult_SignExtendsTo64Bits() {
        // fcvt.w.d x1, f2, rm=1(RTZ) — f2 = -5.9 → truncates to -5
        Rv64ArchState s = MakeState((F2, Dbl(-5.9)));
        ExecuteResult r = Exec(OpFp(0x61, 0, 2, 1, 1), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFBUL, r.RegisterResult.Value); // -5 sign-extended
    }

    [Fact]
    public void FcvtWuD_LargeResult_SignExtendsPerSpec() {
        // fcvt.wu.d x1, f2 — f2 = 3000000000.0 (> INT32_MAX, fits in uint32); sign-extended, see above.
        Rv64ArchState s = MakeState((F2, Dbl(3000000000.0)));
        ExecuteResult r = Exec(OpFp(0x61, 1, 2, 1, 1), s); // RTZ
        Assert.Equal(0xFFFFFFFF_B2D05E00UL, r.RegisterResult.Value);
    }
}
