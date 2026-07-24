#region

using System.Numerics;
using Mechanism;
using RiscV32;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;
using Tests.RiscV32.Extensions;

#endregion

namespace Tests.RiscV64.Isa;

/// <summary>
///     Tests for the RV64-only scalar crypto forms: Zknd/Zkne's <c>aes64*</c> (full-register-pair
///     AES) and Zknh's direct (non-split-register) SHA2-512 transforms — plus regression coverage
///     for the two <see cref="Rv64Decoder" /> gaps this work closed (RV32-only forms now trap;
///     the RV32/RV64-common sha256*/sm3p0/p1 forms now decode and correctly sign-extend).
///     <para>
///         The AES round instructions (aes64ds/dsm/es/esm) are validated by round-trip identity —
///         decrypting a round's own output must reproduce the original state — rather than by
///         re-deriving the RISC-V-specific byte-shuffle formula a second time. This is a genuine,
///         non-circular check: ShiftRows and SubBytes commute (SubBytes substitutes bytes in
///         place; ShiftRows only permutes positions), so
///         InvSubBytes(InvShiftRows(SubBytes(ShiftRows(x)))) = InvSubBytes(SubBytes(x)) = x
///         algebraically, regardless of the exact byte-position convention — a wrong byte index in
///         either the forward or inverse shiftrows formula breaks this identity. Word-level ops
///         (aes64im/ks1i/ks2, direct sha512sig/sum) are checked against independently-derived
///         reference formulas, same as <see cref="ZknTests" />.
///     </para>
/// </summary>
public class Rv64CryptoTests {
    // ── aes64ks1i / aes64ks2 (independent reference using the sbox above) ─────────────────────

    private static readonly uint[] RconRef = [
        0x00000001, 0x00000002, 0x00000004, 0x00000008, 0x00000010, 0x00000020, 0x00000040, 0x00000080,
        0x0000001B, 0x00000036, 0x00000000,
    ];

    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    // ── AES round-trip identity: aes64ds/aes64dsm undo aes64es/aes64esm ──────────────────────

    public static TheoryData<ulong, ulong> StatePairs => new() {
        { 0UL, 0UL },
        { ulong.MaxValue, ulong.MaxValue },
        { 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL },
        { 0xDEADBEEFCAFEBABEUL, 0x0011223344556677UL },
        { 1UL, 1UL << 63 },
        { 0xAAAAAAAAAAAAAAAAUL, 0x5555555555555555UL },
    };

    public static TheoryData<ulong> Uint64Values => [
        0UL, ulong.MaxValue, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL, 1UL, 1UL << 63,
    ];

    private static Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── Encoders (bit layout confirmed against `riscv64-none-elf-as -march=..._zknd_zkne_zknh` +
    // objdump disassembly) ──────────────────────────────────────────────────────────────────────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint Aes64Ds(int rd, int rs1, int rs2) => RType(0x1D, rs2, rs1, 0, rd);
    private static uint Aes64Dsm(int rd, int rs1, int rs2) => RType(0x1F, rs2, rs1, 0, rd);
    private static uint Aes64Es(int rd, int rs1, int rs2) => RType(0x19, rs2, rs1, 0, rd);
    private static uint Aes64Esm(int rd, int rs1, int rs2) => RType(0x1B, rs2, rs1, 0, rd);
    private static uint Aes64Ks2(int rd, int rs1, int rs2) => RType(0x3F, rs2, rs1, 0, rd);
    private static uint Aes64Im(int rd, int rs1) => ITypeAlu(0x300, rs1, 1, rd);
    private static uint Aes64Ks1I(int rd, int rs1, int rnum) => ITypeAlu(0x300 | 0x10 | rnum, rs1, 1, rd);

    private static uint Sha256Sig0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 2, rs1, 1, rd);
    private static uint Sm3P0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 8, rs1, 1, rd);
    private static uint Sm4Ed(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x18, rs2, rs1, 0, rd);
    private static uint Sha512Sig0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 6, rs1, 1, rd);
    private static uint Sha512Sig1(int rd, int rs1) => ITypeAlu((0x08 << 5) | 7, rs1, 1, rd);
    private static uint Sha512Sum0(int rd, int rs1) => ITypeAlu((0x08 << 5) | 4, rs1, 1, rd);
    private static uint Sha512Sum1(int rd, int rs1) => ITypeAlu((0x08 << 5) | 5, rs1, 1, rd);

    // A representative RV32-only encoding from each family the decoder must now reject on RV64.
    private static uint Aes32Dsi(int rd, int rs1, int rs2, int bs) => RType((bs << 5) | 0x15, rs2, rs1, 0, rd);
    private static uint Sha512Sig0H(int rd, int rs1, int rs2) => RType(0x2E, rs2, rs1, 0, rd);

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Aes64Ds_UndoesAes64Es(ulong t0, ulong t1) {
        Rv64ArchState s = MakeState((5, t0), (6, t1));
        ulong t2 = Exec(Aes64Es(7, 5, 6), s).RegisterResult.Value;
        ulong t3 = Exec(Aes64Es(8, 6, 5), s).RegisterResult.Value;

        Rv64ArchState s2 = MakeState((7, t2), (8, t3));
        ulong d0 = Exec(Aes64Ds(9, 7, 8), s2).RegisterResult.Value;
        ulong d1 = Exec(Aes64Ds(10, 8, 7), s2).RegisterResult.Value;

        Assert.Equal(t0, d0);
        Assert.Equal(t1, d1);
    }

    // NOTE: aes64dsm does NOT invert aes64esm as a naive round-trip, unlike aes64ds/aes64es
    // above. SubBytes and ShiftRows are pure per-byte-position operations that always commute,
    // so InvSubBytes(InvShiftRows(SubBytes(ShiftRows(x)))) = x algebraically regardless of any
    // byte-layout convention — that's what makes the Ds/Es round-trip a valid, convention-free
    // check. MixColumns does NOT commute with ShiftRows (it mixes bytes within a column;
    // ShiftRows moves bytes between columns), so composing esm then dsm does not undo a round —
    // real AES decryption instead relies on the "equivalent inverse cipher" construction (see
    // aes64im), not a per-instruction inverse. Validated below by comparing dsm/esm's MixColumns
    // step against an independently-derived MixColumns function, applied to aes64ds/es's
    // already-round-trip-verified SubBytes+ShiftRows output — testing the actual thing that
    // distinguishes dsm/esm from ds/es without relying on the invalid round-trip identity.

    private static uint AesMixColumnFwdRef(uint x) {
        byte s0 = (byte)x, s1 = (byte)(x >> 8), s2 = (byte)(x >> 16), s3 = (byte)(x >> 24);
        var b0 = (byte)(GfMulSmall(s0, 0x2) ^ GfMulSmall(s1, 0x3) ^ s2 ^ s3);
        var b1 = (byte)(s0 ^ GfMulSmall(s1, 0x2) ^ GfMulSmall(s2, 0x3) ^ s3);
        var b2 = (byte)(s0 ^ s1 ^ GfMulSmall(s2, 0x2) ^ GfMulSmall(s3, 0x3));
        var b3 = (byte)(GfMulSmall(s0, 0x3) ^ s1 ^ s2 ^ GfMulSmall(s3, 0x2));
        return ((uint)b3 << 24) | ((uint)b2 << 16) | ((uint)b1 << 8) | b0;
    }

    // Self-check: MixColumns and InvMixColumns must be true 32-bit-word inverses of each other —
    // an unconditional AES design property, independent of any ShiftRows/SubBytes byte layout.
    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void AesMixColumnRef_FwdAndInvAreInverses(ulong seed) {
        var x = (uint)seed;
        Assert.Equal(x, AesMixColumnInvRef(AesMixColumnFwdRef(x)));
    }

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Aes64Esm_AppliesMixColumnsToAes64EsOutput(ulong t0, ulong t1) {
        Rv64ArchState s = MakeState((5, t0), (6, t1));
        ulong esOut = Exec(Aes64Es(7, 5, 6), s).RegisterResult.Value;
        ulong esmOut = Exec(Aes64Esm(8, 5, 6), s).RegisterResult.Value;
        ulong expected = ((ulong)AesMixColumnFwdRef((uint)(esOut >> 32)) << 32) |
                         AesMixColumnFwdRef((uint)esOut);
        Assert.Equal(expected, esmOut);
    }

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Aes64Dsm_AppliesInvMixColumnsToAes64DsOutput(ulong t0, ulong t1) {
        Rv64ArchState s = MakeState((5, t0), (6, t1));
        ulong dsOut = Exec(Aes64Ds(7, 5, 6), s).RegisterResult.Value;
        ulong dsmOut = Exec(Aes64Dsm(8, 5, 6), s).RegisterResult.Value;
        ulong expected = ((ulong)AesMixColumnInvRef((uint)(dsOut >> 32)) << 32) |
                         AesMixColumnInvRef((uint)dsOut);
        Assert.Equal(expected, dsmOut);
    }

    // ── Independent oracle for the ShiftRows byte-shuffle itself ──────────────────────────────
    // The round-trip test above only proves InvShiftRows undoes ShiftRows — a shared transcription
    // error in both aes64es's and aes64ds's byte-shuffle formulas would still pass it, since any
    // permutation composed with its own inverse is the identity. This builds ShiftRows+SubBytes
    // (and InvShiftRows+InvSubBytes) from scratch against the textbook state layout
    // (index = 4*column + row) and checks aes64es/aes64ds against it directly. The two-64-bit-half
    // register packing convention (rs1 = state bytes 0-7, rs2 = state bytes 8-15, byte i -> bit
    // 8*(i%8)) was determined empirically by probing each of the 16 byte positions independently
    // (a single-byte-set input against an all-zero background, tracking where the S-boxed byte
    // reappears in the output) — it isn't asserted anywhere in the spec text used for this port.

    private static byte[] UnpackState(ulong lo, ulong hi) {
        var state = new byte[16];
        for (var i = 0; i < 8; i++) state[i] = (byte)(lo >> (i * 8));
        for (var i = 0; i < 8; i++) state[8 + i] = (byte)(hi >> (i * 8));
        return state;
    }

    private static (ulong lo, ulong hi) PackState(byte[] state) {
        ulong lo = 0, hi = 0;
        for (var i = 0; i < 8; i++) lo |= (ulong)state[i] << (i * 8);
        for (var i = 0; i < 8; i++) hi |= (ulong)state[8 + i] << (i * 8);
        return (lo, hi);
    }

    private static byte AesSboxInvRef(byte y) {
        for (var x = 0; x < 256; x++)
            if (AesSboxFwdRef((byte)x) == y)
                return (byte)x;
        throw new InvalidOperationException("no S-box preimage found");
    }

    private static byte[] AesShiftRowsSubBytesRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            int srcIdx = 4 * ((c + r) % 4) + r;
            result[i] = AesSboxFwdRef(state[srcIdx]);
        }

        return result;
    }

    private static byte[] AesInvShiftRowsInvSubBytesRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            int srcIdx = 4 * ((c - r + 4) % 4) + r;
            result[i] = AesSboxInvRef(state[srcIdx]);
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Aes64Es_MatchesIndependentShiftRowsSubBytesReference(ulong t0, ulong t1) {
        byte[] expected = AesShiftRowsSubBytesRef(UnpackState(t0, t1));
        (ulong expLo, ulong expHi) = PackState(expected);

        Rv64ArchState s = MakeState((5, t0), (6, t1));
        ulong outLo = Exec(Aes64Es(7, 5, 6), s).RegisterResult.Value;
        ulong outHi = Exec(Aes64Es(8, 6, 5), s).RegisterResult.Value;

        Assert.Equal(expLo, outLo);
        Assert.Equal(expHi, outHi);
    }

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void Aes64Ds_MatchesIndependentInvShiftRowsInvSubBytesReference(ulong t0, ulong t1) {
        byte[] expected = AesInvShiftRowsInvSubBytesRef(UnpackState(t0, t1));
        (ulong expLo, ulong expHi) = PackState(expected);

        Rv64ArchState s = MakeState((5, t0), (6, t1));
        ulong outLo = Exec(Aes64Ds(7, 5, 6), s).RegisterResult.Value;
        ulong outHi = Exec(Aes64Ds(8, 6, 5), s).RegisterResult.Value;

        Assert.Equal(expLo, outLo);
        Assert.Equal(expHi, outHi);
    }

    // A non-trivial round must actually change the state (guards against a no-op bug that would
    // trivially pass the round-trip test above).
    [Fact]
    public void Aes64Es_ChangesNonZeroState() {
        Rv64ArchState s = MakeState((5, 0x0123456789ABCDEFUL), (6, 0xFEDCBA9876543210UL));
        ulong t2 = Exec(Aes64Es(7, 5, 6), s).RegisterResult.Value;
        Assert.NotEqual(0x0123456789ABCDEFUL, t2);
    }

    // ── aes64im: InvMixColumns on each 32-bit half (independent GF-inverse+affine sbox — same
    // construction as ZknTests, not re-derived from the RISC-V spec's byte-shuffle formula) ────

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
        if (a == 0) return 0;
        for (var candidate = 1; candidate < 256; candidate++)
            if (GfMulFull(a, (byte)candidate) == 1)
                return (byte)candidate;
        throw new InvalidOperationException("no GF(2^8) inverse found");
    }

    private static byte Rotl8(byte x, int n) => (byte)((x << n) | (x >> (8 - n)));

    private static byte AesSboxFwdRef(byte x) {
        byte inv = GfInverse(x);
        return (byte)(inv ^ Rotl8(inv, 1) ^ Rotl8(inv, 2) ^
                      Rotl8(inv, 3) ^ Rotl8(inv, 4) ^ 0x63);
    }

    private static byte GfMulSmall(byte x, int y) {
        byte r = 0;
        if ((y & 0x1) != 0) r ^= x;
        if ((y & 0x2) != 0) r ^= Xtime(x);
        if ((y & 0x4) != 0) r ^= Xtime(Xtime(x));
        if ((y & 0x8) != 0) r ^= Xtime(Xtime(Xtime(x)));
        return r;
        byte Xtime(byte v) => (byte)((v << 1) ^ ((v & 0x80) != 0 ? 0x1B : 0));
    }

    private static uint AesMixColumnInvRef(uint x) {
        byte s0 = (byte)x, s1 = (byte)(x >> 8), s2 = (byte)(x >> 16), s3 = (byte)(x >> 24);
        var b0 = (byte)(GfMulSmall(s0, 0xE) ^ GfMulSmall(s1, 0xB) ^
                        GfMulSmall(s2, 0xD) ^ GfMulSmall(s3, 0x9));
        var b1 = (byte)(GfMulSmall(s0, 0x9) ^ GfMulSmall(s1, 0xE) ^
                        GfMulSmall(s2, 0xB) ^ GfMulSmall(s3, 0xD));
        var b2 = (byte)(GfMulSmall(s0, 0xD) ^ GfMulSmall(s1, 0x9) ^
                        GfMulSmall(s2, 0xE) ^ GfMulSmall(s3, 0xB));
        var b3 = (byte)(GfMulSmall(s0, 0xB) ^ GfMulSmall(s1, 0xD) ^
                        GfMulSmall(s2, 0x9) ^ GfMulSmall(s3, 0xE));
        return ((uint)b3 << 24) | ((uint)b2 << 16) | ((uint)b1 << 8) | b0;
    }

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Aes64Im_MatchesIndependentReference(ulong v) {
        var lo = (uint)v;
        var hi = (uint)(v >> 32);
        ulong expected = ((ulong)AesMixColumnInvRef(hi) << 32) | AesMixColumnInvRef(lo);
        ExecuteResult r = Exec(Aes64Im(3, 1), MakeState((1, v)));
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(5UL)]
    [InlineData(10UL)]
    public void Aes64Ks1I_MatchesIndependentReference(ulong rnum) {
        const ulong rs1 = 0x1122334455667788UL;
        const uint hi = (uint)(rs1 >> 32);
        uint rc = Rv64CryptoTests.RconRef[rnum];
        uint tmp2 = rnum == 10 ? hi : BitOperations.RotateRight(hi, 8);
        uint sub = ((uint)AesSboxFwdRef((byte)(tmp2 >> 24)) << 24) |
                   ((uint)AesSboxFwdRef((byte)(tmp2 >> 16)) << 16) |
                   ((uint)AesSboxFwdRef((byte)(tmp2 >> 8)) << 8) |
                   AesSboxFwdRef((byte)tmp2);
        uint word = sub ^ rc;
        ulong expected = ((ulong)word << 32) | word;

        ExecuteResult r = Exec(Aes64Ks1I(3, 1, (int)rnum), MakeState((1, rs1)));
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    [Fact]
    public void Aes64Ks1I_RnumAboveTen_Traps() {
        ExecuteResult r = Exec(
            Aes64Ks1I(3, 1, 11), MakeState((1, 0x1122334455667788UL))
        );
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void Aes64Ks2_MatchesIndependentReference() {
        const ulong rs1 = 0x1122334400000000UL;
        const ulong rs2 = 0xAABBCCDD99887766UL;
        const uint hi1 = (uint)(rs1 >> 32);
        const uint lo2 = unchecked((uint)rs2);
        const uint hi2 = (uint)(rs2 >> 32);
        const uint w0 = hi1 ^ lo2;
        const uint w1 = hi1 ^ lo2 ^ hi2;
        const ulong expected = ((ulong)w1 << 32) | w0;

        ExecuteResult r = Exec(
            Aes64Ks2(3, 1, 2), MakeState((1, rs1), (2, rs2))
        );
        Assert.Equal(expected, r.RegisterResult.Value);
    }

    // ── Direct SHA2-512 (FIPS-180-4 §4.1.3 — same rotate constants already validated via the
    // split-pair reconstruction tests in ZknTests, now checked against the direct instruction) ──

    private static ulong Ror64(ulong x, int n) => (x >> n) | (x << (64 - n));
    private static ulong Sha512Sig0Ref(ulong x) => Ror64(x, 1) ^ Ror64(x, 8) ^ (x >> 7);
    private static ulong Sha512Sig1Ref(ulong x) => Ror64(x, 19) ^ Ror64(x, 61) ^ (x >> 6);

    private static ulong Sha512Sum0Ref(ulong x) =>
        Ror64(x, 28) ^ Ror64(x, 34) ^ Ror64(x, 39);

    private static ulong Sha512Sum1Ref(ulong x) =>
        Ror64(x, 14) ^ Ror64(x, 18) ^ Ror64(x, 41);

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sig0_MatchesReference(ulong x) {
        ExecuteResult r = Exec(Sha512Sig0(3, 1), MakeState((1, x)));
        Assert.Equal(Sha512Sig0Ref(x), r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sig1_MatchesReference(ulong x) {
        ExecuteResult r = Exec(Sha512Sig1(3, 1), MakeState((1, x)));
        Assert.Equal(Sha512Sig1Ref(x), r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sum0_MatchesReference(ulong x) {
        ExecuteResult r = Exec(Sha512Sum0(3, 1), MakeState((1, x)));
        Assert.Equal(Sha512Sum0Ref(x), r.RegisterResult.Value);
    }

    [Theory]
    [MemberData(nameof(Uint64Values))]
    public void Sha512Sum1_MatchesReference(ulong x) {
        ExecuteResult r = Exec(Sha512Sum1(3, 1), MakeState((1, x)));
        Assert.Equal(Sha512Sum1Ref(x), r.RegisterResult.Value);
    }

    // ── Decoder gap regressions ────────────────────────────────────────────────────────────────

    [Fact]
    public void Aes32Dsi_OnRv64_IsIllegalInstruction() {
        // RV32-only encoding (aes64* claims disjoint funct7 values); must trap, not silently work.
        var ex = Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, Aes32Dsi(3, 1, 2, 0))
        );
        Assert.Contains("RV32-only", ex.Message);
    }

    [Fact]
    public void Sha512Sig0H_OnRv64_IsIllegalInstruction() {
        var ex = Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, Sha512Sig0H(3, 1, 2))
        );
        Assert.Contains("RV32-only", ex.Message);
    }

    [Fact]
    public void Sha256Sig0_OnRv64_DecodesAndExecutes() {
        // Previously threw IllegalInstructionException (shadowed by the 6-bit-shamt intercept).
        ExecuteResult r = Exec(Sha256Sig0(3, 1), MakeState((1, 0x12345678UL)));
        Assert.False(r.HasTrap);
    }

    [Fact]
    public void Sm3P0_OnRv64_DecodesAndExecutes() {
        ExecuteResult r = Exec(Sm3P0(3, 1), MakeState((1, 0x12345678UL)));
        Assert.False(r.HasTrap);
    }

    [Fact]
    public void Sm4Ed_OnRv64_DecodesAndExecutesUnmodified() {
        // sm4ed is shared between RV32 and RV64 (only funct3=0, not the funct3=1/5 intercept),
        // so it should already fall through Rv64Decoder's default case untouched — confirming
        // this pathway wasn't accidentally disturbed while gating the RV32-only aes32* family.
        ExecuteResult r = Exec(
            Sm4Ed(3, 1, 2, 0), MakeState((1, 0UL), (2, 0UL))
        );
        Assert.False(r.HasTrap);
    }

    // ── Sign-extension regression: sha256*/sm3*/sm4* must sign-extend to XLEN on RV64 (the
    // fix in Rv32Executor.cs/.Crypto.cs casts through (int) before Reg()) ────────────────────

    [Fact]
    public void Sha256Sig0_OnRv64_SignExtendsWhenResultBit31Set() {
        // rs1=0x40: ror(0x40,7)=0x80000000, ror(0x40,18)=0x00100000, (0x40>>3)=0x8 →
        // sig0 = 0x80000000 ^ 0x00100000 ^ 0x00000008 = 0x80100008, which has bit31 set — a
        // deterministic case that only passes if RV64 actually sign-extends (not zero-extends)
        // the 32-bit result, guarding the (ulong)(int)(...) cast fix in Rv32Executor.cs.
        const uint expected32 = 0x80100008u;
        ExecuteResult r = Exec(Sha256Sig0(3, 1), MakeState((1, 0x40UL)));
        Assert.Equal(unchecked((ulong)(int)expected32), r.RegisterResult.Value);
        Assert.Equal(0xFFFFFFFF00000000UL, r.RegisterResult.Value & 0xFFFFFFFF00000000UL);
    }
}