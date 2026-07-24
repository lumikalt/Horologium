#region

using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

#endregion

namespace Tests.RiscV64.Isa;

/// <summary>
///     RV64 coverage for Zbkb/Zbkx: width-dependent pack/brev8/xperm4/xperm8 (XLEN=64 doubles
///     the half-width/byte-count/element-count versus the RV32 forms in
///     <see cref="Extensions.ZbkTests" />), the RV64-only packw, and regression coverage for the
///     decoder gap this work closed — <c>pack rd, rs1, x0</c> previously fell through to RV32's
///     rs2=0 special case (<c>RvZextH</c>, a 16-bit halfword zero-extend) instead of decoding as
///     a 32-bit-half pack, silently producing the wrong result for exactly that operand
///     combination.
/// </summary>
public class Rv64ZbkTests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private static Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint OpW(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x3Bu);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint Pack(int rd, int rs1, int rs2) => Rv64ZbkTests.RType(0x04, rs2, rs1, 4, rd);
    private static uint Packw(int rd, int rs1, int rs2) => Rv64ZbkTests.OpW(0x04, rs2, rs1, 4, rd);
    private static uint Brev8(int rd, int rs1) => Rv64ZbkTests.ITypeAlu(0x687, rs1, 5, rd);
    private static uint Zip(int rd, int rs1) => Rv64ZbkTests.ITypeAlu(0x08F, rs1, 1, rd);
    private static uint Unzip(int rd, int rs1) => Rv64ZbkTests.ITypeAlu(0x08F, rs1, 5, rd);
    private static uint Xperm4(int rd, int rs1, int rs2) => Rv64ZbkTests.RType(0x14, rs2, rs1, 2, rd);
    private static uint Xperm8(int rd, int rs1, int rs2) => Rv64ZbkTests.RType(0x14, rs2, rs1, 4, rd);

    // rev8.rv64: distinct 12-bit immediate from RV32's rev8 (0x698) — regression-checks that
    // carving brev8's shamt6=0x07 out of top6=0x1A didn't disturb rev8's own shamt6=0x38.
    private static uint Rev8(int rd, int rs1) => Rv64ZbkTests.ITypeAlu(0x6B8, rs1, 5, rd);

    // ── The landmine: pack rd, rs1, x0 must be a 32-bit word zero-extend on RV64, not RV32's
    // 16-bit RvZextH special case. ──────────────────────────────────────────────────────────

    [Fact]
    public void Pack_WithX0_IsWordZeroExtendNotHalfwordZextH() {
        ExecuteResult r = Exec(Rv64ZbkTests.Pack(3, 1, 0), Rv64ZbkTests.MakeState((1, 0x00000000_FFFFFFFFUL)));
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value); // NOT 0xFFFF (RvZextH's 16-bit answer)
    }

    [Fact]
    public void Pack_UsesXlenOver2Halves() {
        ExecuteResult r = Exec(
            Rv64ZbkTests.Pack(3, 1, 2),
            Rv64ZbkTests.MakeState((1, 0x00000000_AAAAAAAAUL), (2, 0x00000000_BBBBBBBBUL))
        );
        Assert.Equal(0xBBBBBBBB_AAAAAAAAUL, r.RegisterResult.Value);
    }

    // ── packw: RV64-only, sign-extends the 32-bit packed result to XLEN (unlike zext.h/pack,
    // whose high input bits are always zero here bit31 of the packed word can be set). ────────

    [Fact]
    public void Packw_SignExtendsWhenBit31OfPackedResultIsSet() {
        // rs2's low 16 bits (0x8000) become bits 31:16 of the packed word -> bit31 set.
        ExecuteResult r = Exec(
            Rv64ZbkTests.Packw(3, 1, 2), Rv64ZbkTests.MakeState((1, 0x1234UL), (2, 0x8000UL))
        );
        Assert.Equal(0xFFFFFFFF_80001234UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Packw_ZeroExtendsWhenBit31OfPackedResultIsClear() {
        ExecuteResult r = Exec(
            Rv64ZbkTests.Packw(3, 1, 2), Rv64ZbkTests.MakeState((1, 0x1234UL), (2, 0x7FFFUL))
        );
        Assert.Equal(0x00000000_7FFF1234UL, r.RegisterResult.Value);
    }

    // ── brev8: 8 bytes wide on RV64 (vs. 4 on RV32) — regression-checks the high 4 bytes are
    // actually reversed too, not left untouched by an inherited 4-byte loop bound. ────────────

    [Fact]
    public void Brev8_ReversesAllEightBytesOnRv64() {
        ExecuteResult r = Exec(
            Rv64ZbkTests.Brev8(3, 1), Rv64ZbkTests.MakeState((1, 0x0102040800000001UL))
        );
        // byte-by-byte bit reversal (LSB first): 01->80, 00->00, 00->00, 00->00,
        // 08->10, 04->20, 02->40, 01->80
        Assert.Equal(0x8040201000000080UL, r.RegisterResult.Value);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x0123456789ABCDEFUL)]
    public void Brev8_IsSelfInverseOnRv64(ulong x) {
        ExecuteResult once = Exec(Rv64ZbkTests.Brev8(3, 1), Rv64ZbkTests.MakeState((1, x)));
        ExecuteResult twice = Exec(Rv64ZbkTests.Brev8(4, 3), Rv64ZbkTests.MakeState((3, once.RegisterResult.Value)));
        Assert.Equal(x, twice.RegisterResult.Value);
    }

    [Fact]
    public void Rev8Rv64_StillWorksAfterBrev8SharesTop6() {
        ExecuteResult r = Exec(Rv64ZbkTests.Rev8(3, 1), Rv64ZbkTests.MakeState((1, 0x0123456789ABCDEFUL)));
        Assert.Equal(0xEFCDAB8967452301UL, r.RegisterResult.Value);
    }

    // ── xperm4/xperm8: RV64 has 16/8 elements (vs. RV32's 8/4) — the extra elements must not
    // be silently dropped by an inherited element-count bound. ────────────────────────────────

    [Fact]
    public void Xperm8_IndexesAllEightBytesOnRv64() {
        // idxVec selects each lut byte in reverse order (byte i picks lut's byte 7-i).
        ExecuteResult r = Exec(
            Rv64ZbkTests.Xperm8(3, 1, 2),
            Rv64ZbkTests.MakeState((1, 0x0011223344556677UL), (2, 0x0001020304050607UL))
        );
        Assert.Equal(0x7766554433221100UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Xperm8_OutOfRangeIndex_YieldsZeroOnRv64() {
        // RV64 has 8 byte-elements (valid indices 0-7); index 8 is out of range.
        ExecuteResult r = Exec(
            Rv64ZbkTests.Xperm8(3, 1, 2), Rv64ZbkTests.MakeState((1, ulong.MaxValue), (2, 0x08UL))
        );
        Assert.Equal(0xFFFFFFFFFFFFFF00UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Xperm4_IndexesAllSixteenNibblesOnRv64() {
        // idxVec's nibble i = i for all i=0..15 (0xFEDCBA9876543210, LSB nibble=0 through MSB
        // nibble=F) — the identity permutation, which must return lut unchanged regardless of
        // its pattern. RV32 has only 8 nibble-elements, so this exact identity vector (which
        // relies on indices up to 15) only exercises the full range on RV64.
        ExecuteResult r = Exec(
            Rv64ZbkTests.Xperm4(3, 1, 2),
            Rv64ZbkTests.MakeState((1, 0x0123456789ABCDEFUL), (2, 0xFEDCBA9876543210UL))
        );
        Assert.Equal(0x0123456789ABCDEFUL, r.RegisterResult.Value);
    }

    // No RV64 xperm4 out-of-range test exists: a nibble index field is only 4 bits (max value
    // 15), and RV64 has exactly 16 nibble-elements (valid indices 0-15) — every representable
    // index is always in range, unlike RV32's 8 elements (see ZbkTests.Xperm4_OutOfRangeIndex).

    // ── packh inherited unmodified: byte-based, same on both widths. ──────────────────────────

    [Fact]
    public void Packh_InheritedUnmodifiedOnRv64() {
        var raw = Rv64ZbkTests.RType(0x04, 2, 1, 7, 3);
        ExecuteResult r = Exec(raw, Rv64ZbkTests.MakeState((1, 0xAAUL), (2, 0xBBUL)));
        Assert.Equal(0xBBAAUL, r.RegisterResult.Value);
    }

    // ── zip/unzip: RV32-only per spec — must trap on RV64, not silently reinterpret the RV32
    // encoding as something else. ───────────────────────────────────────────────────────────

    [Fact]
    public void Zip_OnRv64_IsIllegalInstruction() {
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, Rv64ZbkTests.Zip(3, 1)));
    }

    [Fact]
    public void Unzip_OnRv64_IsIllegalInstruction() {
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, Rv64ZbkTests.Unzip(3, 1)));
    }
}
