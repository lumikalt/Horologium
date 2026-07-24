#region

using System.Numerics;
using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the ShangMi algorithm suite (Zksed/Zksh): SM4 block cipher and SM3 hash
///     function instructions.
///     <para>
///         SM3's P0/P1 rotate constants (GB/T 32905) are re-derived independently of
///         <c>Rv32Executor.Crypto.cs</c>, same as SHA-2 in <see cref="ZknTests" />. SM4's S-box
///         (GB/T 32907) has no cheap algebraic reconstruction — unlike AES's, it isn't defined as
///         an affine transform of a GF(2^8) inverse — so it is re-transcribed independently here
///         from the same spec appendix and spot-checked against published landmark values, giving
///         at least independence from a copy-paste error even though both ultimately cite the same
///         standard.
///     </para>
/// </summary>
public class ZksTests {
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

    // ── Encoders (bit layout confirmed against `riscv32-none-elf-as -march=..._zksed_zksh` +
    // objdump disassembly, and cross-checked against the spec's own encoding diagrams) ──────────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x13u);

    // funct7 = {bs[1:0], 5'bXXXXX}: sm4ed=11000, sm4ks=11010
    private static uint Sm4Ed(int rd, int rs1, int rs2, int bs) => ZksTests.RType((bs << 5) | 0x18, rs2, rs1, 0, rd);
    private static uint Sm4Ks(int rd, int rs1, int rs2, int bs) => ZksTests.RType((bs << 5) | 0x1A, rs2, rs1, 0, rd);

    // funct7=0x08, shamt selects sub-op: sm3p0=8, sm3p1=9
    private static uint Sm3P0(int rd, int rs1) => ZksTests.ITypeAlu((0x08 << 5) | 8, rs1, 1, rd);
    private static uint Sm3P1(int rd, int rs1) => ZksTests.ITypeAlu((0x08 << 5) | 9, rs1, 1, rd);

    // ── SM3 (GB/T 32905 §5.3.2 P0/P1 permutation functions, rotate constants 9/17 and 15/23) ──

    private static uint Rol32(uint x, int n) => (x << n) | (x >> (32 - n));
    private static uint Sm3P0Ref(uint x) => x ^ ZksTests.Rol32(x, 9) ^ ZksTests.Rol32(x, 17);
    private static uint Sm3P1Ref(uint x) => x ^ ZksTests.Rol32(x, 15) ^ ZksTests.Rol32(x, 23);

    public static TheoryData<uint> Uint32Values => new() {
        0u, 0xFFFFFFFFu, 1u, 0x80000000u, 0x12345678u, 0xDEADBEEFu, 0xAAAAAAAAu, 0x55555555u,
    };

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sm3P0_MatchesReference(uint x) {
        ExecuteResult r = Exec(ZksTests.Sm3P0(3, 1), ZksTests.MakeState((1, x)));
        Assert.Equal(ZksTests.Sm3P0Ref(x), (uint)r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sm3P1_MatchesReference(uint x) {
        ExecuteResult r = Exec(ZksTests.Sm3P1(3, 1), ZksTests.MakeState((1, x)));
        Assert.Equal(ZksTests.Sm3P1Ref(x), (uint)r.RegisterResult.Value);
    }

    // ── SM4 (GB/T 32907) ───────────────────────────────────────────────────────────────────────
    // S-box re-transcribed independently (separate keystroke pass from Rv32Executor.Crypto.cs)
    // from the spec's Appendix D, and spot-checked below against published landmark values.
    private static readonly byte[] Sm4SboxRefTable = [
        0xD6, 0x90, 0xE9, 0xFE, 0xCC, 0xE1, 0x3D, 0xB7, 0x16, 0xB6, 0x14, 0xC2, 0x28, 0xFB, 0x2C, 0x05,
        0x2B, 0x67, 0x9A, 0x76, 0x2A, 0xBE, 0x04, 0xC3, 0xAA, 0x44, 0x13, 0x26, 0x49, 0x86, 0x06, 0x99,
        0x9C, 0x42, 0x50, 0xF4, 0x91, 0xEF, 0x98, 0x7A, 0x33, 0x54, 0x0B, 0x43, 0xED, 0xCF, 0xAC, 0x62,
        0xE4, 0xB3, 0x1C, 0xA9, 0xC9, 0x08, 0xE8, 0x95, 0x80, 0xDF, 0x94, 0xFA, 0x75, 0x8F, 0x3F, 0xA6,
        0x47, 0x07, 0xA7, 0xFC, 0xF3, 0x73, 0x17, 0xBA, 0x83, 0x59, 0x3C, 0x19, 0xE6, 0x85, 0x4F, 0xA8,
        0x68, 0x6B, 0x81, 0xB2, 0x71, 0x64, 0xDA, 0x8B, 0xF8, 0xEB, 0x0F, 0x4B, 0x70, 0x56, 0x9D, 0x35,
        0x1E, 0x24, 0x0E, 0x5E, 0x63, 0x58, 0xD1, 0xA2, 0x25, 0x22, 0x7C, 0x3B, 0x01, 0x21, 0x78, 0x87,
        0xD4, 0x00, 0x46, 0x57, 0x9F, 0xD3, 0x27, 0x52, 0x4C, 0x36, 0x02, 0xE7, 0xA0, 0xC4, 0xC8, 0x9E,
        0xEA, 0xBF, 0x8A, 0xD2, 0x40, 0xC7, 0x38, 0xB5, 0xA3, 0xF7, 0xF2, 0xCE, 0xF9, 0x61, 0x15, 0xA1,
        0xE0, 0xAE, 0x5D, 0xA4, 0x9B, 0x34, 0x1A, 0x55, 0xAD, 0x93, 0x32, 0x30, 0xF5, 0x8C, 0xB1, 0xE3,
        0x1D, 0xF6, 0xE2, 0x2E, 0x82, 0x66, 0xCA, 0x60, 0xC0, 0x29, 0x23, 0xAB, 0x0D, 0x53, 0x4E, 0x6F,
        0xD5, 0xDB, 0x37, 0x45, 0xDE, 0xFD, 0x8E, 0x2F, 0x03, 0xFF, 0x6A, 0x72, 0x6D, 0x6C, 0x5B, 0x51,
        0x8D, 0x1B, 0xAF, 0x92, 0xBB, 0xDD, 0xBC, 0x7F, 0x11, 0xD9, 0x5C, 0x41, 0x1F, 0x10, 0x5A, 0xD8,
        0x0A, 0xC1, 0x31, 0x88, 0xA5, 0xCD, 0x7B, 0xBD, 0x2D, 0x74, 0xD0, 0x12, 0xB8, 0xE5, 0xB4, 0xB0,
        0x89, 0x69, 0x97, 0x4A, 0x0C, 0x96, 0x77, 0x7E, 0x65, 0xB9, 0xF1, 0x09, 0xC5, 0x6E, 0xC6, 0x84,
        0x18, 0xF0, 0x7D, 0xEC, 0x3A, 0xDC, 0x4D, 0x20, 0x79, 0xEE, 0x5F, 0x3E, 0xD7, 0xCB, 0x39, 0x48,
    ];

    // Published GB/T 32907 landmark values, independent of both transcriptions above.
    [Fact]
    public void Sm4SboxRef_MatchesPublishedLandmarks() {
        Assert.Equal(0xD6, ZksTests.Sm4SboxRefTable[0x00]);
        Assert.Equal(0x90, ZksTests.Sm4SboxRefTable[0x01]);
        Assert.Equal(0x48, ZksTests.Sm4SboxRefTable[0xFF]);
    }

    private static uint Sm4Ref(uint rs1, uint rs2, int bs, bool keySchedule) {
        int shamt = bs * 8;
        var sbIn = (byte)(rs2 >> shamt);
        uint x = ZksTests.Sm4SboxRefTable[sbIn];
        uint y = keySchedule
            ? x ^ ((x & 0x00000007u) << 29) ^ ((x & 0x000000FEu) << 7) ^ ((x & 0x00000001u) << 23) ^
              ((x & 0x000000F8u) << 13)
            : x ^ (x << 8) ^ (x << 2) ^ (x << 18) ^ ((x & 0x0000003Fu) << 26) ^ ((x & 0x000000C0u) << 10);
        uint z = BitOperations.RotateLeft(y, shamt);
        return z ^ rs1;
    }

    public static TheoryData<uint, uint> Sm4TestPairs => new() {
        { 0u, 0u },
        { 0xFFFFFFFFu, 0xFFFFFFFFu },
        { 0x01234567u, 0x89ABCDEFu },
        { 0xFEDCBA98u, 0x76543210u }, // GB/T 32907 standard test key/plaintext bytes, recombined as words
        { 0xDEADBEEFu, 0xCAFEBABEu },
        { 0x80808080u, 0x01010101u },
    };

    [Theory]
    [MemberData(nameof(Sm4TestPairs))]
    public void Sm4Ed_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = ZksTests.MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(ZksTests.Sm4Ed(3, 1, 2, bs), s);
            Assert.Equal(ZksTests.Sm4Ref(rs1, rs2, bs, false), (uint)r.RegisterResult.Value);
        }
    }

    [Theory]
    [MemberData(nameof(Sm4TestPairs))]
    public void Sm4Ks_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = ZksTests.MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(ZksTests.Sm4Ks(3, 1, 2, bs), s);
            Assert.Equal(ZksTests.Sm4Ref(rs1, rs2, bs, true), (uint)r.RegisterResult.Value);
        }
    }

    // Exhaustive input-byte coverage at bs=0, rs1=0.
    [Fact]
    public void Sm4Ed_Bs0_CoversFullSbox() {
        for (var b = 0; b < 256; b++) {
            Rv32ArchState s = ZksTests.MakeState((1, 0u), (2, (uint)b));
            ExecuteResult r = Exec(ZksTests.Sm4Ed(3, 1, 2, 0), s);
            Assert.Equal(ZksTests.Sm4Ref(0, (uint)b, 0, false), (uint)r.RegisterResult.Value);
        }
    }
}
