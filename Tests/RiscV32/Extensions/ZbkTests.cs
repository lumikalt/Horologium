#region

using Mechanism;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the crypto-adjacent bit-manipulation extensions Zbkb (pack/packh/brev8/zip/
///     unzip) and Zbkx (xperm4/xperm8), RV32 forms.
///     <para>
///         pack/packh/xperm4/xperm8 are checked against reference implementations written
///         independently of <c>Rv32Executor.cs</c> (different code shape: explicit byte/nibble
///         arrays instead of shift-and-mask). zip/unzip and brev8 are checked via their algebraic
///         inverse properties (unzip(zip(x))=x, brev8(brev8(x))=x — true for any correct
///         implementation of either direction, so a shared transcription bug in both directions
///         would still be caught by disagreeing with the hand-computed vectors below) plus
///         hand-computed vectors that pin down the actual bit ordering, not just its invertibility.
///     </para>
/// </summary>
public class ZbkTests {
    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private static Rv32ArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, uint v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv32ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── Encoders (bit layout confirmed against the RISC-V Cryptography Extensions Volume I
    // spec's per-instruction encoding diagrams, §3.13/3.17/3.18/3.45/3.47/3.48/3.49) ───────────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint Pack(int rd, int rs1, int rs2) => ZbkTests.RType(0x04, rs2, rs1, 4, rd);
    private static uint Packh(int rd, int rs1, int rs2) => ZbkTests.RType(0x04, rs2, rs1, 7, rd);
    private static uint Brev8(int rd, int rs1) => ZbkTests.ITypeAlu(0x687, rs1, 5, rd);
    private static uint Zip(int rd, int rs1) => ZbkTests.ITypeAlu(0x08F, rs1, 1, rd);
    private static uint Unzip(int rd, int rs1) => ZbkTests.ITypeAlu(0x08F, rs1, 5, rd);
    private static uint Xperm4(int rd, int rs1, int rs2) => ZbkTests.RType(0x14, rs2, rs1, 2, rd);
    private static uint Xperm8(int rd, int rs1, int rs2) => ZbkTests.RType(0x14, rs2, rs1, 4, rd);

    // rev8, re-encoded here so a regression test can confirm carving out brev8's shamt=0x07
    // from the funct7=0x34 slot didn't disturb rev8's own shamt=0x18 case.
    private static uint Rev8(int rd, int rs1) => ZbkTests.ITypeAlu((0x34 << 5) | 0x18, rs1, 5, rd);

    public static TheoryData<uint> Uint32Values => [
        0u, 0xFFFFFFFFu, 1u, 0x80000000u, 0x12345678u, 0xDEADBEEFu, 0xAAAAAAAAu, 0x55555555u,
    ];

    // ── pack / packh: independent reference (explicit half-extraction, not shift-and-mask
    // shared with the production Pack() helper) ───────────────────────────────────────────

    public static TheoryData<uint, uint> Pairs => new() {
        { 0u, 0u },
        { 0xFFFFFFFFu, 0xFFFFFFFFu },
        { 0x12345678u, 0x9ABCDEF0u },
        { 0xDEADBEEFu, 0xCAFEBABEu },
        { 0x0000FFFFu, 0xFFFF0000u },
        { 0xAAAAAAAAu, 0x55555555u },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Pack_MatchesIndependentReference(uint a, uint b) {
        var lo = (ushort)a;
        var hi = (ushort)b;
        var expected = (uint)((hi << 16) | lo);
        ExecuteResult r = Exec(ZbkTests.Pack(3, 1, 2), ZbkTests.MakeState((1, a), (2, b)));
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Packh_MatchesIndependentReference(uint a, uint b) {
        byte lo = (byte)a;
        byte hi = (byte)b;
        var expected = (uint)((hi << 8) | lo);
        ExecuteResult r = Exec(ZbkTests.Packh(3, 1, 2), ZbkTests.MakeState((1, a), (2, b)));
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    [Fact]
    public void Pack_RegisterOrder_Rs1IsLowHalfRs2IsHighHalf() {
        // Pins down which operand goes where — a swapped rs1/rs2 in the executor would still
        // pass Pack_MatchesIndependentReference for symmetric inputs, so use asymmetric ones.
        ExecuteResult r = Exec(ZbkTests.Pack(3, 1, 2), ZbkTests.MakeState((1, 0x0000AAAAu), (2, 0x0000BBBBu)));
        Assert.Equal(0xBBBBAAAAu, r.RegisterResult.Value);
    }

    // ── brev8: self-inverse (brev8(brev8(x))=x for any correct bit-reversal) plus hand vectors
    // that pin down the actual bit ordering ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Brev8_IsSelfInverse(uint x) {
        ExecuteResult once = Exec(ZbkTests.Brev8(3, 1), ZbkTests.MakeState((1, x)));
        ExecuteResult twice = Exec(ZbkTests.Brev8(4, 3), ZbkTests.MakeState((3, (uint)once.RegisterResult.Value)));
        Assert.Equal(x, twice.RegisterResult.Value);
    }

    [Theory]
    [InlineData(0x00000001u, 0x00000080u)] // reversing a single low bit in a byte moves it to the top
    [InlineData(0x00000080u, 0x00000001u)]
    [InlineData(0x000000FFu, 0x000000FFu)] // all-ones byte reverses to itself
    [InlineData(0x01020408u, 0x80402010u)] // one bit set per byte, all in different positions
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    [InlineData(0x00000000u, 0x00000000u)]
    public void Brev8_MatchesHandVector(uint input, uint expected) {
        ExecuteResult r = Exec(ZbkTests.Brev8(3, 1), ZbkTests.MakeState((1, input)));
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    [Fact]
    public void Brev8_DoesNotReorderBytes() {
        // Confirms brev8 operates within each byte independently (not a full 32-bit bit-reverse,
        // which would also move bits across byte boundaries).
        ExecuteResult r = Exec(ZbkTests.Brev8(3, 1), ZbkTests.MakeState((1, 0x000000F0u)));
        Assert.Equal(0x0000000Fu, r.RegisterResult.Value); // stays in byte 0, doesn't migrate to byte 3
    }

    [Fact]
    public void Rev8_StillWorksAfterBrev8SharesFunct7() {
        ExecuteResult r = Exec(ZbkTests.Rev8(3, 1), ZbkTests.MakeState((1, 0x12345678u)));
        Assert.Equal(0x78563412u, r.RegisterResult.Value);
    }

    // ── zip/unzip: mutual inverse plus hand vectors that pin down which half maps to even vs.
    // odd bit positions ────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Unzip_UndoesZip(uint x) {
        ExecuteResult zipped = Exec(ZbkTests.Zip(3, 1), ZbkTests.MakeState((1, x)));
        ExecuteResult unzipped = Exec(ZbkTests.Unzip(4, 3), ZbkTests.MakeState((3, (uint)zipped.RegisterResult.Value)));
        Assert.Equal(x, unzipped.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Zip_UndoesUnzip(uint x) {
        ExecuteResult unzipped = Exec(ZbkTests.Unzip(3, 1), ZbkTests.MakeState((1, x)));
        ExecuteResult zipped = Exec(ZbkTests.Zip(4, 3), ZbkTests.MakeState((3, (uint)unzipped.RegisterResult.Value)));
        Assert.Equal(x, zipped.RegisterResult.Value);
    }

    [Fact]
    public void Zip_LowHalfGoesToEvenBits() {
        ExecuteResult r = Exec(ZbkTests.Zip(3, 1), ZbkTests.MakeState((1, 0x0000FFFFu)));
        Assert.Equal(0x55555555u, r.RegisterResult.Value); // low half all-ones -> every even bit set
    }

    [Fact]
    public void Zip_HighHalfGoesToOddBits() {
        ExecuteResult r = Exec(ZbkTests.Zip(3, 1), ZbkTests.MakeState((1, 0xFFFF0000u)));
        Assert.Equal(0xAAAAAAAAu, r.RegisterResult.Value); // high half all-ones -> every odd bit set
    }

    [Fact]
    public void Unzip_EvenBitsGoToLowHalf() {
        ExecuteResult r = Exec(ZbkTests.Unzip(3, 1), ZbkTests.MakeState((1, 0x55555555u)));
        Assert.Equal(0x0000FFFFu, r.RegisterResult.Value);
    }

    [Fact]
    public void Unzip_OddBitsGoToHighHalf() {
        ExecuteResult r = Exec(ZbkTests.Unzip(3, 1), ZbkTests.MakeState((1, 0xAAAAAAAAu)));
        Assert.Equal(0xFFFF0000u, r.RegisterResult.Value);
    }

    // ── xperm4/xperm8: independent reference built from explicit element arrays (not the
    // production Xperm() helper's shift-and-mask extraction) ─────────────────────────────────

    private static uint Xperm8Ref(uint lut, uint idxVec) {
        byte[] table = [(byte)lut, (byte)(lut >> 8), (byte)(lut >> 16), (byte)(lut >> 24),];
        byte[] idx = [(byte)idxVec, (byte)(idxVec >> 8), (byte)(idxVec >> 16), (byte)(idxVec >> 24),];
        uint result = 0;
        for (var i = 0; i < 4; i++) {
            byte looked = idx[i] < table.Length ? table[idx[i]] : (byte)0;
            result |= (uint)looked << (i * 8);
        }

        return result;
    }

    private static uint Xperm4Ref(uint lut, uint idxVec) {
        var table = new byte[8];
        var idx = new byte[8];
        for (var i = 0; i < 8; i++) {
            table[i] = (byte)((lut >> (i * 4)) & 0xF);
            idx[i] = (byte)((idxVec >> (i * 4)) & 0xF);
        }

        uint result = 0;
        for (var i = 0; i < 8; i++) {
            byte looked = idx[i] < table.Length ? table[idx[i]] : (byte)0;
            result |= (uint)looked << (i * 4);
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Xperm8_MatchesIndependentReference(uint lut, uint idxVec) {
        ExecuteResult r = Exec(ZbkTests.Xperm8(3, 1, 2), ZbkTests.MakeState((1, lut), (2, idxVec)));
        Assert.Equal(ZbkTests.Xperm8Ref(lut, idxVec), r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Xperm4_MatchesIndependentReference(uint lut, uint idxVec) {
        ExecuteResult r = Exec(ZbkTests.Xperm4(3, 1, 2), ZbkTests.MakeState((1, lut), (2, idxVec)));
        Assert.Equal(ZbkTests.Xperm4Ref(lut, idxVec), r.RegisterResult.Value);
    }

    [Fact]
    public void Xperm8_IdentityPermutation() {
        // idxVec = 0x03020100 selects each byte of lut in its own original position.
        ExecuteResult r = Exec(
            ZbkTests.Xperm8(3, 1, 2), ZbkTests.MakeState((1, 0xDDCCBBAAu), (2, 0x03020100u))
        );
        Assert.Equal(0xDDCCBBAAu, r.RegisterResult.Value);
    }

    [Fact]
    public void Xperm8_OutOfRangeIndex_YieldsZero() {
        // RV32 has 4 byte-elements (valid indices 0-3). lut is all-0xFF so any valid lookup
        // reads back 0xFF; byte0's index (4) is out of range and must read back 0x00 instead,
        // while bytes 1-3 (index 0, valid) read back 0xFF.
        ExecuteResult r = Exec(
            ZbkTests.Xperm8(3, 1, 2), ZbkTests.MakeState((1, 0xFFFFFFFFu), (2, 0x00000004u))
        );
        Assert.Equal(0xFFFFFF00u, r.RegisterResult.Value);
    }

    [Fact]
    public void Xperm4_OutOfRangeIndex_YieldsZero() {
        // RV32 has 8 nibble-elements (valid indices 0-7). lut is all-0xF so any valid lookup
        // reads back 0xF; nibble0's index (8) is out of range and must read back 0x0 instead,
        // while nibbles 1-7 (index 0, valid) read back 0xF.
        ExecuteResult r = Exec(
            ZbkTests.Xperm4(3, 1, 2), ZbkTests.MakeState((1, 0xFFFFFFFFu), (2, 0x00000008u))
        );
        Assert.Equal(0xFFFFFFF0u, r.RegisterResult.Value);
    }
}
