using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
///     Tests for RV64M — MULW/DIVW/DIVUW/REMW/REMUW (32-bit operands, sign-extended result),
///     and the 64-bit-native overrides of MULH/MULHSU/MULHU/DIV/DIVU/REM/REMU that replace the
///     RV32 versions inherited from Rv32Executor (which truncate to 32 bits).
/// </summary>
public class Rv64MTests {
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

    // R-type encoding helper: opcode=0x3B (OP-32), funct7=0x01 (M-extension).
    private static uint Op32M(uint funct3, int rd, int rs1, int rs2) =>
        (0x01u << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x3B;

    // R-type encoding helper: opcode=0x33 (OP), funct7=0x01 (M-extension) — base RV32M encoding.
    private static uint OpM(uint funct3, int rd, int rs1, int rs2) =>
        (0x01u << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x33;

    // ── MULW ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mulw_TruncatesTo32ThenSignExtends() {
        // x2 = 0x100000001 (upper bits should be dropped), x3 = 2 → lower32 * 2 = 2, sign-ext = 2
        Rv64ArchState s = MakeState((2, 0x1_0000_0001UL), (3, 2UL));
        ExecuteResult r = Exec(Op32M(0x0, 1, 2, 3), s);
        Assert.Equal(2UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Mulw_NegativeResultSignExtends() {
        // x2 = 0x80000000 (lower32 = INT32_MIN), x3 = 1 → product lower32 = 0x80000000 → sign-ext
        Rv64ArchState s = MakeState((2, 0x8000_0000UL), (3, 1UL));
        ExecuteResult r = Exec(Op32M(0x0, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_80000000UL, r.RegisterResult.Value);
    }

    // ── DIVW ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Divw_SignedDivision() {
        Rv64ArchState s = MakeState((2, unchecked((ulong)-7)), (3, 2UL));
        ExecuteResult r = Exec(Op32M(0x4, 1, 2, 3), s);
        Assert.Equal(unchecked((ulong)-3), r.RegisterResult.Value);
    }

    [Fact]
    public void Divw_ByZero_ReturnsAllOnes() {
        Rv64ArchState s = MakeState((2, 5UL), (3, 0UL));
        ExecuteResult r = Exec(Op32M(0x4, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Divw_Overflow_IntMinDivNegOne() {
        // lower32 = INT32_MIN, divisor lower32 = -1 → result = INT32_MIN, sign-extended
        Rv64ArchState s = MakeState((2, 0x8000_0000UL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(Op32M(0x4, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_80000000UL, r.RegisterResult.Value);
    }

    // ── DIVUW ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Divuw_UnsignedDivision_TruncatesTo32() {
        // x2 = 0x1_00000009 → lower32 = 9, x3 = 2 → 9/2 = 4
        Rv64ArchState s = MakeState((2, 0x1_0000_0009UL), (3, 2UL));
        ExecuteResult r = Exec(Op32M(0x5, 1, 2, 3), s);
        Assert.Equal(4UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Divuw_ByZero_ReturnsAllOnes() {
        Rv64ArchState s = MakeState((2, 5UL), (3, 0UL));
        ExecuteResult r = Exec(Op32M(0x5, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    // ── REMW ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remw_SignedRemainder() {
        Rv64ArchState s = MakeState((2, unchecked((ulong)-7)), (3, 2UL));
        ExecuteResult r = Exec(Op32M(0x6, 1, 2, 3), s);
        Assert.Equal(unchecked((ulong)-1), r.RegisterResult.Value);
    }

    [Fact]
    public void Remw_ByZero_ReturnsDividendSignExtended() {
        Rv64ArchState s = MakeState((2, 0x8000_0000UL), (3, 0UL));
        ExecuteResult r = Exec(Op32M(0x6, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_80000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Remw_Overflow_IntMinDivNegOne_ReturnsZero() {
        Rv64ArchState s = MakeState((2, 0x8000_0000UL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(Op32M(0x6, 1, 2, 3), s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    // ── REMUW ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remuw_UnsignedRemainder_TruncatesTo32() {
        Rv64ArchState s = MakeState((2, 0x1_0000_0009UL), (3, 2UL));
        ExecuteResult r = Exec(Op32M(0x7, 1, 2, 3), s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Remuw_ByZero_ReturnsDividendSignExtended() {
        Rv64ArchState s = MakeState((2, 0x8000_0000UL), (3, 0UL));
        ExecuteResult r = Exec(Op32M(0x7, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_80000000UL, r.RegisterResult.Value);
    }

    // ── MULH / MULHSU / MULHU — 64-bit-native (not truncated to 32 bits) ──────────

    [Fact]
    public void Mulh_64BitSignedHighHalf() {
        // x2 = -1 (all ones), x3 = -1 → product = 1 → high64 = 0
        Rv64ArchState s = MakeState((2, 0xFFFFFFFF_FFFFFFFFUL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(OpM(0x1, 1, 2, 3), s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Mulh_LargePositiveOperands() {
        // x2 = x3 = 0x7FFFFFFF_FFFFFFFF (INT64_MAX) → product high64 = 0x3FFFFFFFFFFFFFFF
        Rv64ArchState s = MakeState((2, 0x7FFFFFFF_FFFFFFFFUL), (3, 0x7FFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(OpM(0x1, 1, 2, 3), s);
        Assert.Equal(0x3FFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Mulhu_64BitUnsignedHighHalf() {
        // x2 = x3 = 0xFFFFFFFF_FFFFFFFF (UINT64_MAX) → product high64 = 0xFFFFFFFF_FFFFFFFE
        Rv64ArchState s = MakeState((2, 0xFFFFFFFF_FFFFFFFFUL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(OpM(0x3, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Mulhsu_SignedTimesUnsigned() {
        // x2 = -1 (signed), x3 = 2 (unsigned) → -1 * 2 = -2 → high64 = 0xFFFFFFFF_FFFFFFFF
        Rv64ArchState s = MakeState((2, 0xFFFFFFFF_FFFFFFFFUL), (3, 2UL));
        ExecuteResult r = Exec(OpM(0x2, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    // ── DIV / DIVU / REM / REMU — 64-bit-native ────────────────────────────────────

    [Fact]
    public void Div_64BitSignedDivision() {
        // x2 = 0x1_00000000 (upper bits matter, would be wrong if truncated to 32 bits)
        Rv64ArchState s = MakeState((2, 0x1_0000_0000UL), (3, 2UL));
        ExecuteResult r = Exec(OpM(0x4, 1, 2, 3), s);
        Assert.Equal(0x8000_0000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Div_ByZero_ReturnsAllOnes() {
        Rv64ArchState s = MakeState((2, 5UL), (3, 0UL));
        ExecuteResult r = Exec(OpM(0x4, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Div_Overflow_LongMinDivNegOne() {
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(OpM(0x4, 1, 2, 3), s);
        Assert.Equal(0x8000_0000_0000_0000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Divu_64BitUnsignedDivision() {
        // x2 = 0x1_00000000 (would be 0 if truncated to 32 bits) / 2
        Rv64ArchState s = MakeState((2, 0x1_0000_0000UL), (3, 2UL));
        ExecuteResult r = Exec(OpM(0x5, 1, 2, 3), s);
        Assert.Equal(0x8000_0000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Divu_ByZero_ReturnsAllOnes() {
        Rv64ArchState s = MakeState((2, 5UL), (3, 0UL));
        ExecuteResult r = Exec(OpM(0x5, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFF_FFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Rem_64BitSignedRemainder() {
        Rv64ArchState s = MakeState((2, unchecked((ulong)-7)), (3, 2UL));
        ExecuteResult r = Exec(OpM(0x6, 1, 2, 3), s);
        Assert.Equal(unchecked((ulong)-1), r.RegisterResult.Value);
    }

    [Fact]
    public void Rem_ByZero_ReturnsDividend() {
        Rv64ArchState s = MakeState((2, 0x1_0000_0000UL), (3, 0UL));
        ExecuteResult r = Exec(OpM(0x6, 1, 2, 3), s);
        Assert.Equal(0x1_0000_0000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Rem_Overflow_LongMinDivNegOne_ReturnsZero() {
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL), (3, 0xFFFFFFFF_FFFFFFFFUL));
        ExecuteResult r = Exec(OpM(0x6, 1, 2, 3), s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Remu_64BitUnsignedRemainder() {
        Rv64ArchState s = MakeState((2, 0x1_0000_0009UL), (3, 2UL));
        ExecuteResult r = Exec(OpM(0x7, 1, 2, 3), s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Remu_ByZero_ReturnsDividend() {
        Rv64ArchState s = MakeState((2, 0x1_0000_0000UL), (3, 0UL));
        ExecuteResult r = Exec(OpM(0x7, 1, 2, 3), s);
        Assert.Equal(0x1_0000_0000UL, r.RegisterResult.Value);
    }
}