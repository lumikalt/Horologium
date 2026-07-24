#region

using System.Numerics;
using Mechanism;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the NIST algorithm suite (Zknd/Zkne/Zknh): AES round instructions and SHA2
///     transformation instructions, RV32 forms.
///     <para>
///         Every reference value below is derived independently of
///         <c>Rv32Executor.Crypto.cs</c> — from FIPS-197/FIPS-180-4 first principles (GF(2^8)
///         inverse + affine transform for the AES S-box, the textbook MixColumns matrix, and the
///         standard SHA-2 rotate constants) — never by re-typing the RISC-V spec's per-instruction
///         formula a second time, which would let a shared transcription bug pass silently. See
///         encoding comments for the objdump ground truth each opcode was checked against.
///     </para>
/// </summary>
public class ZknTests {
    // The inverse S-box is the functional inverse of the forward S-box (both are bijections).
    private static readonly byte[] AesSboxInvRefTable = BuildAesSboxInvRef();
    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    public static TheoryData<uint, uint> AesTestPairs => new() {
        { 0u, 0u },
        { 0xFFFFFFFFu, 0xFFFFFFFFu },
        { 0x12345678u, 0x9ABCDEF0u },
        { 0xDEADBEEFu, 0xCAFEBABEu },
        { 0x00000000u, 0xFFFFFFFFu },
        { 0x80808080u, 0x01010101u },
        { 0xAAAAAAAAu, 0x55555555u },
    };

    public static TheoryData<uint> Uint32Values => [
        0u, 0xFFFFFFFFu, 1u, 0x80000000u, 0x12345678u, 0xDEADBEEFu, 0xAAAAAAAAu, 0x55555555u,
    ];

    public static TheoryData<ulong> Uint64Values => [
        0UL, ulong.MaxValue, 1UL, 1UL << 63, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL,
        0xAAAAAAAAAAAAAAAAUL, 0x5555555555555555UL, 0xDEADBEEFCAFEBABEUL,
    ];

    private static Rv32ArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, uint v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv32ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── Encoders (bit layout confirmed against `riscv32-none-elf-as -march=..._zknd_zkne_zknh`
    // + objdump disassembly, and cross-checked against the spec's own encoding diagrams) ───────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x13u);

    // funct7 = {bs[1:0], 5'bXXXXX}: aes32dsi=10101, aes32dsmi=10111, aes32esi=10001, aes32esmi=10011
    private static uint Aes32Dsi(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x15, rs2, rs1, 0, rd);
    private static uint Aes32Dsmi(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x17, rs2, rs1, 0, rd);
    private static uint Aes32Esi(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x11, rs2, rs1, 0, rd);
    private static uint Aes32Esmi(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x13, rs2, rs1, 0, rd);

    // funct7=0x08, shamt selects sub-op: sum0=0, sum1=1, sig0=2, sig1=3
    private static uint Sha256Sum0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 0, rs1, 1, rd);
    private static uint Sha256Sum1(int rd, int rs1) => ITypeAlu((0x08 << 5) | 1, rs1, 1, rd);
    private static uint Sha256Sig0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 2, rs1, 1, rd);
    private static uint Sha256Sig1(int rd, int rs1) => ITypeAlu((0x08 << 5) | 3, rs1, 1, rd);

    private static uint Sha512Sig0H(int rd, int rs1, int rs2) => RType(0x2E, rs2, rs1, 0, rd);
    private static uint Sha512Sig0L(int rd, int rs1, int rs2) => RType(0x2A, rs2, rs1, 0, rd);
    private static uint Sha512Sig1H(int rd, int rs1, int rs2) => RType(0x2F, rs2, rs1, 0, rd);
    private static uint Sha512Sig1L(int rd, int rs1, int rs2) => RType(0x2B, rs2, rs1, 0, rd);
    private static uint Sha512Sum0R(int rd, int rs1, int rs2) => RType(0x28, rs2, rs1, 0, rd);
    private static uint Sha512Sum1R(int rd, int rs1, int rs2) => RType(0x29, rs2, rs1, 0, rd);

    // ── Independent AES reference (FIPS-197 §5.1.1, §5.3.2) ───────────────────────────────────

    // Full GF(2^8) multiply mod the AES field polynomial x^8+x^4+x^3+x+1 (0x11B) — used only to
    // brute-force the multiplicative inverse below.
    private static byte GfMulFull(byte a, byte b) {
        byte result = 0;
        for (var i = 0; i < 8; i++) {
            if ((b & 1) != 0) result ^= a;
            bool hi = (a & 0x80) != 0;
            a <<= 1;
            if (hi) a ^= 0x1B;
            b >>= 1;
        }

        return result;
    }

    private static byte GfInverse(byte a) {
        if (a == 0) return 0; // AES defines inverse(0) = 0
        for (var candidate = 1; candidate < 256; candidate++)
            if (GfMulFull(a, (byte)candidate) == 1)
                return (byte)candidate;
        throw new InvalidOperationException("no GF(2^8) inverse found");
    }

    private static byte Rotl8(byte x, int n) => (byte)((x << n) | (x >> (8 - n)));

    // FIPS-197 §5.1.1: S-box = affine transform of the GF(2^8) multiplicative inverse.
    private static byte AesSboxFwdRef(byte x) {
        byte inv = GfInverse(x);
        return (byte)(inv ^ Rotl8(inv, 1) ^ Rotl8(inv, 2) ^ Rotl8(inv, 3) ^
                      Rotl8(inv, 4) ^ 0x63);
    }

    private static byte[] BuildAesSboxInvRef() {
        var table = new byte[256];
        for (var x = 0; x < 256; x++) table[AesSboxFwdRef((byte)x)] = (byte)x;
        return table;
    }

    private static byte AesSboxInvRef(byte x) => ZknTests.AesSboxInvRefTable[x];

    // Textbook AES MixColumns matrix (a single-nonzero-byte column, as aes32esmi/dsmi consume).
    private static byte GfMulSmall(byte x, int y) {
        byte r = 0;
        if ((y & 0x1) != 0) r ^= x;
        if ((y & 0x2) != 0) r ^= Xtime(x);
        if ((y & 0x4) != 0) r ^= Xtime(Xtime(x));
        if ((y & 0x8) != 0) r ^= Xtime(Xtime(Xtime(x)));
        return r;
        byte Xtime(byte v) => (byte)((v << 1) ^ ((v & 0x80) != 0 ? 0x1B : 0));
    }

    private static uint AesMixColumnByteFwdRef(byte so) {
        byte b0 = GfMulSmall(so, 2);
        byte b3 = GfMulSmall(so, 3);
        return ((uint)b3 << 24) | ((uint)so << 16) | ((uint)so << 8) | b0;
    }

    private static uint AesMixColumnByteInvRef(byte so) {
        byte b0 = GfMulSmall(so, 0xE);
        byte b1 = GfMulSmall(so, 0x9);
        byte b2 = GfMulSmall(so, 0xD);
        byte b3 = GfMulSmall(so, 0xB);
        return ((uint)b3 << 24) | ((uint)b2 << 16) | ((uint)b1 << 8) | b0;
    }

    private static uint Aes32Ref(uint rs1, uint rs2, int bs, bool inverse, bool mixColumns) {
        int shamt = bs * 8;
        var si = (byte)(rs2 >> shamt);
        byte so = inverse ? AesSboxInvRef(si) : AesSboxFwdRef(si);
        uint pre = mixColumns
            ? inverse ? AesMixColumnByteInvRef(so) : AesMixColumnByteFwdRef(so)
            : so;
        return rs1 ^ BitOperations.RotateLeft(pre, shamt);
    }

    // Self-check: confirms the independently-derived S-box lands on FIPS-197's own well-known
    // landmark values before it's trusted as an oracle for anything else.
    [Fact]
    public void AesSboxFwdRef_MatchesFips197Landmarks() {
        Assert.Equal(0x63, AesSboxFwdRef(0x00));
        Assert.Equal(0x7C, AesSboxFwdRef(0x01));
        Assert.Equal(0x77, AesSboxFwdRef(0x02));
        Assert.Equal(0x76, AesSboxFwdRef(0x0F));
    }

    [Fact]
    public void AesSboxInvRef_IsFunctionalInverseOfFwd() {
        for (var x = 0; x < 256; x++) Assert.Equal((byte)x, AesSboxInvRef(AesSboxFwdRef((byte)x)));
    }

    [Theory]
    [MemberData(nameof(AesTestPairs))]
    public void Aes32Dsi_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(Aes32Dsi(3, 1, 2, bs), s);
            Assert.Equal(Aes32Ref(rs1, rs2, bs, true, false), (uint)r.RegisterResult.Value);
        }
    }

    [Theory]
    [MemberData(nameof(AesTestPairs))]
    public void Aes32Dsmi_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(Aes32Dsmi(3, 1, 2, bs), s);
            Assert.Equal(Aes32Ref(rs1, rs2, bs, true, true), (uint)r.RegisterResult.Value);
        }
    }

    [Theory]
    [MemberData(nameof(AesTestPairs))]
    public void Aes32Esi_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(Aes32Esi(3, 1, 2, bs), s);
            Assert.Equal(Aes32Ref(rs1, rs2, bs, false, false), (uint)r.RegisterResult.Value);
        }
    }

    [Theory]
    [MemberData(nameof(AesTestPairs))]
    public void Aes32Esmi_MatchesIndependentReference(uint rs1, uint rs2) {
        for (var bs = 0; bs < 4; bs++) {
            Rv32ArchState s = MakeState((1, rs1), (2, rs2));
            ExecuteResult r = Exec(Aes32Esmi(3, 1, 2, bs), s);
            Assert.Equal(Aes32Ref(rs1, rs2, bs, false, true), (uint)r.RegisterResult.Value);
        }
    }

    // Exhaustive S-box coverage at bs=0, rs1=0 (result = sbox(rs2 & 0xFF) directly).
    [Fact]
    public void Aes32Esi_Bs0_CoversFullSbox() {
        for (var b = 0; b < 256; b++) {
            Rv32ArchState s = MakeState((1, 0u), (2, (uint)b));
            ExecuteResult r = Exec(Aes32Esi(3, 1, 2, 0), s);
            Assert.Equal(AesSboxFwdRef((byte)b), r.RegisterResult.Value);
        }
    }

    [Fact]
    public void Aes32Dsi_Bs0_CoversFullSbox() {
        for (var b = 0; b < 256; b++) {
            Rv32ArchState s = MakeState((1, 0u), (2, (uint)b));
            ExecuteResult r = Exec(Aes32Dsi(3, 1, 2, 0), s);
            Assert.Equal(AesSboxInvRef((byte)b), r.RegisterResult.Value);
        }
    }

    // ── SHA2-256 (FIPS-180-4 §4.1.2 rotate constants — 7/18/3, 17/19/10, 2/13/22, 6/11/25) ────

    private static uint Ror32(uint x, int n) => (x >> n) | (x << (32 - n));
    private static uint Sha256Sig0Ref(uint x) => Ror32(x, 7) ^ Ror32(x, 18) ^ (x >> 3);
    private static uint Sha256Sig1Ref(uint x) => Ror32(x, 17) ^ Ror32(x, 19) ^ (x >> 10);
    private static uint Sha256Sum0Ref(uint x) => Ror32(x, 2) ^ Ror32(x, 13) ^ Ror32(x, 22);
    private static uint Sha256Sum1Ref(uint x) => Ror32(x, 6) ^ Ror32(x, 11) ^ Ror32(x, 25);

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sha256Sig0_MatchesReference(uint x) {
        ExecuteResult r = Exec(Sha256Sig0(3, 1), MakeState((1, x)));
        Assert.Equal(Sha256Sig0Ref(x), (uint)r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sha256Sig1_MatchesReference(uint x) {
        ExecuteResult r = Exec(Sha256Sig1(3, 1), MakeState((1, x)));
        Assert.Equal(Sha256Sig1Ref(x), (uint)r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sha256Sum0_MatchesReference(uint x) {
        ExecuteResult r = Exec(Sha256Sum0(3, 1), MakeState((1, x)));
        Assert.Equal(Sha256Sum0Ref(x), (uint)r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint32Values))]
    public void Sha256Sum1_MatchesReference(uint x) {
        ExecuteResult r = Exec(Sha256Sum1(3, 1), MakeState((1, x)));
        Assert.Equal(Sha256Sum1Ref(x), (uint)r.RegisterResult.Value);
    }

    // ── SHA2-512 split-register pairs (FIPS-180-4 §4.1.3, RV32-only) ─────────────────────────
    // Each pair is tested by running BOTH instructions per the spec's documented calling
    // sequence and reconstructing the full 64-bit result, then comparing against the 64-bit
    // Sigma/sigma functions computed directly from FIPS-180-4 — never against the RISC-V spec's
    // own split-word formula, which would just be checking the code against itself.

    private static ulong Ror64(ulong x, int n) => (x >> n) | (x << (64 - n));
    private static ulong Sha512Sig0Ref(ulong x) => Ror64(x, 1) ^ Ror64(x, 8) ^ (x >> 7);
    private static ulong Sha512Sig1Ref(ulong x) => Ror64(x, 19) ^ Ror64(x, 61) ^ (x >> 6);

    private static ulong Sha512Sum0Ref(ulong x) =>
        Ror64(x, 28) ^ Ror64(x, 34) ^ Ror64(x, 39);

    private static ulong Sha512Sum1Ref(ulong x) =>
        Ror64(x, 14) ^ Ror64(x, 18) ^ Ror64(x, 41);

    // §3.31/3.32 note to software developers: sha512sig0l t0,a0,a1 ; sha512sig0h t1,a1,a0
    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sig0_SplitPair_ReconstructsFullSigma0(ulong v) {
        var lo = (uint)v;
        var hi = (uint)(v >> 32);
        var t0 = (uint)Exec(Sha512Sig0L(5, 10, 11), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        var t1 = (uint)Exec(Sha512Sig0H(6, 11, 10), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        Assert.Equal(Sha512Sig0Ref(v), ((ulong)t1 << 32) | t0);
    }

    // §3.33/3.34 note to software developers: sha512sig1l t0,a0,a1 ; sha512sig1h t1,a1,a0
    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sig1_SplitPair_ReconstructsFullSigma1(ulong v) {
        var lo = (uint)v;
        var hi = (uint)(v >> 32);
        var t0 = (uint)Exec(Sha512Sig1L(5, 10, 11), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        var t1 = (uint)Exec(Sha512Sig1H(6, 11, 10), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        Assert.Equal(Sha512Sig1Ref(v), ((ulong)t1 << 32) | t0);
    }

    // §3.35 note to software developers: sha512sum0r t0,a0,a1 ; sha512sum0r t1,a1,a0 (same
    // instruction both times, reversed source register ordering).
    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sum0_SplitPair_ReconstructsFullSum0(ulong v) {
        var lo = (uint)v;
        var hi = (uint)(v >> 32);
        var t0 = (uint)Exec(Sha512Sum0R(5, 10, 11), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        var t1 = (uint)Exec(Sha512Sum0R(6, 11, 10), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        Assert.Equal(Sha512Sum0Ref(v), ((ulong)t1 << 32) | t0);
    }

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sum1_SplitPair_ReconstructsFullSum1(ulong v) {
        var lo = (uint)v;
        var hi = (uint)(v >> 32);
        var t0 = (uint)Exec(Sha512Sum1R(5, 10, 11), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        var t1 = (uint)Exec(Sha512Sum1R(6, 11, 10), MakeState((10, lo), (11, hi)))
                      .RegisterResult.Value;
        Assert.Equal(Sha512Sum1Ref(v), ((ulong)t1 << 32) | t0);
    }
}