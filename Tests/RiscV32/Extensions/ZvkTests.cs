#region

using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the RISC-V Vector Cryptography Extensions Volume II element-group
///     architecture, exercised through the first two instructions built on it: Zvkned's
///     <c>vaesem.vv</c>/<c>vaesef.vv</c> (AES middle/final-round encryption).
///     <para>
///         Deliberately run at LMUL=4 (not LMUL=1): at LMUL=1 an EGW=128 element group fits in a
///         single physical register and the existing (pre-element-group) single-register code
///         path would silently "work" without exercising any of the new multi-register-group
///         iteration/addressing logic at all. LMUL=4 spans vd/vs2 across 4 physical registers
///         (one element group each) and is the whole point of this test file.
///     </para>
///     <para>
///         AES round math is checked against an independent from-scratch 128-bit-block reference
///         (FIPS-197 GF-inverse+affine S-box, textbook MixColumns matrix, standard
///         <c>state[4*col+row]</c> layout) — not against the production
///         <c>AesSboxFwd</c>/<c>AesMixColumnFwd</c> primitives the executor reuses from the
///         scalar crypto work, since testing a lifted primitive against itself proves nothing.
///     </para>
/// </summary>
public class ZvkTests {
    // vtypei for e32,m4,ta,ma: vma=bit7, vta=bit6, vsew[5:3]=010(e32), vlmul[2:0]=010(m4)
    private const int VtypeiE32M4Tama = (1 << 7) | (1 << 6) | (2 << 3) | 2;

    // vtypei for e32,mf2,ta,ma: vlmul[2:0]=101 (fractional field 5 = LMUL 1/2)
    // LMUL(1/2)*VLEN(128)=64 < EGW(128) -> must trap regardless of vl.
    private const int VtypeiE32Mf2Tama = (1 << 7) | (1 << 6) | (2 << 3) | 5;

    // vtypei for e16,m4,ta,ma / e64,m4,ta,ma: wrong SEW for AES (must be 32).
    private const int VtypeiE16M4Tama = (1 << 7) | (1 << 6) | (1 << 3) | 2;
    private const int VtypeiE64M4Tama = (1 << 7) | (1 << 6) | (3 << 3) | 2;

    // vtypei for e32,m1,ta,ma: LMUL(1)*VLEN(128)=EGW(128) exactly — the ">=" boundary case.
    private const int VtypeiE32M1Tama = (1 << 7) | (1 << 6) | (2 << 3) | 0;

    // Explicit output<-input byte permutation (the standard textbook AES ShiftRows table: row r
    // cyclically shifted left by r), hand-listed rather than recomputed via the same
    // modular-arithmetic formula the production AesShiftRowsFwdBlock uses — a formula bug shared
    // between "reference" and "production" would otherwise pass silently.
    private static readonly int[] ShiftRowsPermutation = [0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11,];

    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    // ── Encoders ────────────────────────────────────────────────────────────────────────────

    private static uint Vsetivli(int rd, int zimm, int vtypei) =>
        0xC0000000u | (uint)((vtypei << 20) | (zimm << 15) | (7 << 12) | (rd << 7) | 0x57);

    // Vector-crypto opcode (0x77) — NOT the standard OP-V opcode (0x57), despite the field
    // layout otherwise matching OPMVV. See DecodeVCryptoOp.
    private static uint VopMvv(int funct6, int vd, int vs2, int vs1) =>
        (uint)(((funct6 & 0x3F) << 26) | (1 << 25) | ((vs2 & 0x1F) << 20)
             | ((vs1 & 0x1F) << 15) | (2 << 12) | ((vd & 0x1F) << 7) | 0x77);

    private static uint VaesEmVv(int vd, int vs2) => VopMvv(0x28, vd, vs2, 2);
    private static uint VaesEfVv(int vd, int vs2) => VopMvv(0x28, vd, vs2, 3);
    private static uint VaesDmVv(int vd, int vs2) => VopMvv(0x28, vd, vs2, 0);
    private static uint VaesDfVv(int vd, int vs2) => VopMvv(0x28, vd, vs2, 1);

    private static uint VaesEmVs(int vd, int vs2) => VopMvv(0x29, vd, vs2, 2);
    private static uint VaesEfVs(int vd, int vs2) => VopMvv(0x29, vd, vs2, 3);
    private static uint VaesZVs(int vd, int vs2) => VopMvv(0x29, vd, vs2, 7);

    private static uint VaesKf1Vi(int vd, int vs2, int round) => VopMvv(0x22, vd, vs2, round);
    private static uint VaesKf2Vi(int vd, int vs2, int round) => VopMvv(0x2A, vd, vs2, round);

    private static uint Sm4RVv(int vd, int vs2) => VopMvv(0x28, vd, vs2, 16);
    private static uint Sm4RVs(int vd, int vs2) => VopMvv(0x29, vd, vs2, 16);
    private static uint Sm4KVi(int vd, int vs2, int round) => VopMvv(0x21, vd, vs2, round);

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    private static Rv32ArchState MakeState() => new();

    private ExecuteResult Exec(uint raw, Rv32ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    private void ApplySideEffect(ExecuteResult r, Rv32ArchState state) => r.SideEffect?.Invoke(state);

    private void Vsetivli(Rv32ArchState state, int vl, int vtypei) =>
        ApplySideEffect(Exec(Vsetivli(0, vl, vtypei), state), state);

    private static void WriteBlock(Rv32ArchState state, int vreg, byte[] block16) =>
        state.VectorRegisters.Write(vreg, block16);

    private static byte[] ReadBlock(Rv32ArchState state, int vreg) => state.VectorRegisters.Read(vreg);

    // ── Independent AES-128-bit-block reference (fresh GF-inverse+affine S-box, textbook
    // MixColumns matrix, standard state[4*col+row] layout) ────────────────────────────────────

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
        byte Xtime(byte v) => (byte)((v << 1) ^ ((v & 0x80) != 0 ? 0x1B : 0));
        byte r = 0;
        if ((y & 0x1) != 0) r ^= x;
        if ((y & 0x2) != 0) r ^= Xtime(x);
        if ((y & 0x4) != 0) r ^= Xtime(Xtime(x));
        if ((y & 0x8) != 0) r ^= Xtime(Xtime(Xtime(x)));
        return r;
    }

    private static byte[] AesSubBytesRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = AesSboxFwdRef(state[i]);
        return result;
    }

    private static byte[] AesShiftRowsRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = state[ZvkTests.ShiftRowsPermutation[i]];
        return result;
    }

    private static byte[] AesMixColumnsRef(byte[] state) {
        var result = new byte[16];
        for (var c = 0; c < 4; c++) {
            byte s0 = state[4 * c], s1 = state[4 * c + 1], s2 = state[4 * c + 2], s3 = state[4 * c + 3];
            result[4 * c] = (byte)(GfMulSmall(s0, 0x2) ^ GfMulSmall(s1, 0x3) ^ s2 ^ s3);
            result[4 * c + 1] = (byte)(s0 ^ GfMulSmall(s1, 0x2) ^ GfMulSmall(s2, 0x3) ^ s3);
            result[4 * c + 2] = (byte)(s0 ^ s1 ^ GfMulSmall(s2, 0x2) ^ GfMulSmall(s3, 0x3));
            result[4 * c + 3] = (byte)(GfMulSmall(s0, 0x3) ^ s1 ^ s2 ^ GfMulSmall(s3, 0x2));
        }

        return result;
    }

    private static byte[] Xor16(byte[] a, byte[] b) {
        var r = new byte[16];
        for (var i = 0; i < 16; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    private static byte[] AesEmRef(byte[] state, byte[] key) =>
        Xor16(AesMixColumnsRef(AesShiftRowsRef(AesSubBytesRef(state))), key);

    private static byte[] AesEfRef(byte[] state, byte[] key) =>
        Xor16(AesShiftRowsRef(AesSubBytesRef(state)), key);

    // ── Independent AES inverse-cipher reference — built by inverting the forward reference
    // above (functional S-box/permutation inversion, not a transcribed inverse formula), so an
    // error in the forward reference wouldn't be silently mirrored into the inverse one ────────

    private static readonly byte[] SboxInvRefTable = BuildSboxInvRefTable();

    private static byte[] BuildSboxInvRefTable() {
        var table = new byte[256];
        for (var x = 0; x < 256; x++) table[AesSboxFwdRef((byte)x)] = (byte)x;
        return table;
    }

    private static byte AesSboxInvRef(byte x) => SboxInvRefTable[x];

    private static readonly int[] ShiftRowsInvPermutation = BuildInvPermutation(ZvkTests.ShiftRowsPermutation);

    private static int[] BuildInvPermutation(int[] perm) {
        var inv = new int[perm.Length];
        for (var i = 0; i < perm.Length; i++) inv[perm[i]] = i;
        return inv;
    }

    private static byte[] AesSubBytesInvRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = AesSboxInvRef(state[i]);
        return result;
    }

    private static byte[] AesShiftRowsInvRef(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = state[ShiftRowsInvPermutation[i]];
        return result;
    }

    // Standard AES InvMixColumns matrix constants (0e,0b,0d,09) — independent of the forward
    // reference's (02,03,01,01) matrix, not derived from it.
    private static byte[] AesMixColumnsInvRef(byte[] state) {
        var result = new byte[16];
        for (var c = 0; c < 4; c++) {
            byte s0 = state[4 * c], s1 = state[4 * c + 1], s2 = state[4 * c + 2], s3 = state[4 * c + 3];
            result[4 * c] = (byte)(GfMulSmall(s0, 0xE) ^ GfMulSmall(s1, 0xB) ^ GfMulSmall(s2, 0xD) ^ GfMulSmall(s3, 0x9));
            result[4 * c + 1] = (byte)(GfMulSmall(s0, 0x9) ^ GfMulSmall(s1, 0xE) ^ GfMulSmall(s2, 0xB) ^ GfMulSmall(s3, 0xD));
            result[4 * c + 2] = (byte)(GfMulSmall(s0, 0xD) ^ GfMulSmall(s1, 0x9) ^ GfMulSmall(s2, 0xE) ^ GfMulSmall(s3, 0xB));
            result[4 * c + 3] = (byte)(GfMulSmall(s0, 0xB) ^ GfMulSmall(s1, 0xD) ^ GfMulSmall(s2, 0x9) ^ GfMulSmall(s3, 0xE));
        }

        return result;
    }

    private static byte[] AesDmRef(byte[] state, byte[] key) =>
        AesMixColumnsInvRef(Xor16(AesSubBytesInvRef(AesShiftRowsInvRef(state)), key));

    private static byte[] AesDfRef(byte[] state, byte[] key) =>
        Xor16(AesSubBytesInvRef(AesShiftRowsInvRef(state)), key);

    private static byte[] Block(uint seed) {
        var b = new byte[16];
        var rng = new Random((int)seed);
        rng.NextBytes(b);
        return b;
    }

    // ── vaesem.vv / vaesef.vv at LMUL=4: 4 element groups across vd..vd+3 ──────────────────────

    [Fact]
    public void VaesemVv_Lmul4_MatchesIndependentReferencePerGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        var states = new byte[4][];
        var keys = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(100 + g));
            keys[g] = Block((uint)(200 + g));
            WriteBlock(s, 8 + g, states[g]);
            WriteBlock(s, 12 + g, keys[g]);
        }

        ExecuteResult r = Exec(VaesEmVv(8, 12), s);
        ApplySideEffect(r, s);

        for (var g = 0; g < 4; g++) {
            byte[] expected = AesEmRef(states[g], keys[g]);
            Assert.Equal(expected, ReadBlock(s, 8 + g));
        }
    }

    [Fact]
    public void VaesefVv_Lmul4_MatchesIndependentReferencePerGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        var states = new byte[4][];
        var keys = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(300 + g));
            keys[g] = Block((uint)(400 + g));
            WriteBlock(s, 16 + g, states[g]);
            WriteBlock(s, 20 + g, keys[g]);
        }

        ExecuteResult r = Exec(VaesEfVv(16, 20), s);
        ApplySideEffect(r, s);

        for (var g = 0; g < 4; g++) {
            byte[] expected = AesEfRef(states[g], keys[g]);
            Assert.Equal(expected, ReadBlock(s, 16 + g));
        }
    }

    [Fact]
    public void VaesemVv_Lmul4_ActuallyWritesAllFourRegisters() {
        // Guards against a regression to the pre-element-group single-register behavior: if the
        // executor only ever touched vd (ignoring LMUL entirely), this would still incorrectly
        // pass a same-value-everywhere test, so assert the four results are pairwise distinct
        // (guaranteed here since each group used a different pseudorandom state/key seed).
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        for (var g = 0; g < 4; g++) {
            WriteBlock(s, 8 + g, Block((uint)(500 + g)));
            WriteBlock(s, 12 + g, Block((uint)(600 + g)));
        }

        ApplySideEffect(Exec(VaesEmVv(8, 12), s), s);

        byte[][] results = [ReadBlock(s, 8), ReadBlock(s, 9), ReadBlock(s, 10), ReadBlock(s, 11),];
        for (var i = 0; i < 4; i++)
        for (int j = i + 1; j < 4; j++)
            Assert.NotEqual(results[i], results[j]);
    }

    // ── FIPS-197 known-answer tests (AES-128, Nk=4, Nr=10) ──────────────────────────────────────
    // Round-key schedule transcribed verbatim from the published NIST FIPS-197 Appendix A.1 key
    // expansion example (key 2b7e1516...), and the cipher trace from Appendix B (which explicitly
    // reuses the Appendix A.1 schedule) — not derived from this codebase or from ZvkTests' own
    // from-scratch reference. This validates SubBytes+ShiftRows+MixColumns+round-composition
    // against an external authority instead of only against ourselves.

    private static readonly byte[][] Fips197A1RoundKeys = [
        Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c"), // rk0 (the raw key)
        Convert.FromHexString("a0fafe1788542cb123a339392a6c7605"), // rk1
        Convert.FromHexString("f2c295f27a96b9435935807a7359f67f"), // rk2
        Convert.FromHexString("3d80477d4716fe3e1e237e446d7a883b"), // rk3
        Convert.FromHexString("ef44a541a8525b7fb671253bdb0bad00"), // rk4
        Convert.FromHexString("d4d1c6f87c839d87caf2b8bc11f915bc"), // rk5
        Convert.FromHexString("6d88a37a110b3efddbf98641ca0093fd"), // rk6
        Convert.FromHexString("4e54f70e5f5fc9f384a64fb24ea6dc4f"), // rk7
        Convert.FromHexString("ead27321b58dbad2312bf5607f8d292f"), // rk8
        Convert.FromHexString("ac7766f319fadc2128d12941575c006e"), // rk9
        Convert.FromHexString("d014f9a8c9ee2589e13f0cc8b6630ca6"), // rk10
    ];

    private static readonly byte[] Fips197BPlaintext = Convert.FromHexString("3243f6a8885a308d313198a2e0370734");
    private static readonly byte[] Fips197BCiphertext = Convert.FromHexString("3925841d02dc09fbdc118597196a0b32");

    // round[r].start is the state already AddRoundKey'd with rk[r-1] (round[1].start = plaintext
    // XOR rk[0]), so vaesem.vv(round[r].start, rk[r]) == round[r+1].start for r=1..9, and
    // vaesef.vv(round[10].start, rk[10]) == the ciphertext — exactly the SubBytes->ShiftRows->
    // MixColumns(->none for the final round)->AddRoundKey shape vaesem/vaesef implement.
    [Fact]
    public void VaesemVv_VaesefVv_ChainedRounds_MatchFips197KnownAnswerTrace() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, Xor16(ZvkTests.Fips197BPlaintext, ZvkTests.Fips197A1RoundKeys[0]));

        for (var round = 1; round <= 9; round++) {
            WriteBlock(s, 12, ZvkTests.Fips197A1RoundKeys[round]);
            ApplySideEffect(Exec(VaesEmVv(8, 12), s), s);
        }

        WriteBlock(s, 12, ZvkTests.Fips197A1RoundKeys[10]);
        ApplySideEffect(Exec(VaesEfVv(8, 12), s), s);

        Assert.Equal(ZvkTests.Fips197BCiphertext, ReadBlock(s, 8));
    }

    // ── Element-group constraint traps (spec §1.5) ──────────────────────────────────────────

    [Fact]
    public void VaesemVv_LmulTimesVlenBelowEgw_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32Mf2Tama); // LMUL=1/2 -> 1/2*128=64 < EGW=128
        ExecuteResult r = Exec(VaesEmVv(8, 12), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Theory]
    [InlineData(16)] // e16: SEW=16, required 32
    [InlineData(64)] // e64: SEW=64, required 32
    public void VaesemVv_WrongSew_Traps(int sew) {
        Rv32ArchState s = MakeState();
        int vtypei = sew == 16 ? ZvkTests.VtypeiE16M4Tama : ZvkTests.VtypeiE64M4Tama;
        Vsetivli(s, 8, vtypei);
        ExecuteResult r = Exec(VaesEmVv(8, 12), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void VaesemVv_VlNotMultipleOfEgs_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 15, ZvkTests.VtypeiE32M4Tama); // 15 % EGS(4) != 0
        ExecuteResult r = Exec(VaesEmVv(8, 12), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void VaesemVv_VstartNotMultipleOfEgs_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);
        s.SystemRegisters.Write(CsrFile.Vstart, 2, RvPrivilege.Machine); // 2 % EGS(4) != 0
        ExecuteResult r = Exec(VaesEmVv(8, 12), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void VaesemVv_Lmul4_HonorsNonzeroVstart() {
        // vstart=8 (2 groups) skips groups 0-1; only groups 2-3 (registers 10-11) get written.
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);
        s.SystemRegisters.Write(CsrFile.Vstart, 8, RvPrivilege.Machine);

        var originalG0 = new byte[16];
        var originalG1 = new byte[16];
        Array.Copy(Block(700), originalG0, 16);
        Array.Copy(Block(701), originalG1, 16);
        WriteBlock(s, 8, originalG0);
        WriteBlock(s, 9, originalG1);
        byte[] state2 = Block(702);
        byte[] state3 = Block(703);
        byte[] key2 = Block(802);
        byte[] key3 = Block(803);
        WriteBlock(s, 10, state2);
        WriteBlock(s, 11, state3);
        WriteBlock(s, 12, Block(800));
        WriteBlock(s, 13, Block(801));
        WriteBlock(s, 14, key2);
        WriteBlock(s, 15, key3);

        ApplySideEffect(Exec(VaesEmVv(8, 12), s), s);

        Assert.Equal(originalG0, ReadBlock(s, 8));
        Assert.Equal(originalG1, ReadBlock(s, 9));
        Assert.Equal(AesEmRef(state2, key2), ReadBlock(s, 10));
        Assert.Equal(AesEmRef(state3, key3), ReadBlock(s, 11));
    }

    // ── vaesdm.vv / vaesdf.vv at LMUL=4: per-group independent-reference correctness ───────────

    [Fact]
    public void VaesdmVv_Lmul4_MatchesIndependentReferencePerGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        var states = new byte[4][];
        var keys = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(900 + g));
            keys[g] = Block((uint)(1000 + g));
            WriteBlock(s, 8 + g, states[g]);
            WriteBlock(s, 12 + g, keys[g]);
        }

        ApplySideEffect(Exec(VaesDmVv(8, 12), s), s);

        for (var g = 0; g < 4; g++) Assert.Equal(AesDmRef(states[g], keys[g]), ReadBlock(s, 8 + g));
    }

    [Fact]
    public void VaesdfVv_Lmul4_MatchesIndependentReferencePerGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        var states = new byte[4][];
        var keys = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(1100 + g));
            keys[g] = Block((uint)(1200 + g));
            WriteBlock(s, 16 + g, states[g]);
            WriteBlock(s, 20 + g, keys[g]);
        }

        ApplySideEffect(Exec(VaesDfVv(16, 20), s), s);

        for (var g = 0; g < 4; g++) Assert.Equal(AesDfRef(states[g], keys[g]), ReadBlock(s, 16 + g));
    }

    // ── FIPS-197 known-answer trace, run in reverse: vaesdm.vv/vaesdf.vv must recover the
    // Appendix B plaintext from its ciphertext through the same Appendix A.1 round-key schedule
    // used above — exercising the InvShiftRows/InvSubBytes/InvMixColumns path end to end against
    // an external authority, not just against AesDmRef/AesDfRef.
    //
    // vaesdm.vv's pseudocode (ShiftRowsInv -> SubBytesInv -> XOR key -> MixColumnsInv) is the
    // "equivalent inverse cipher" structure FIPS-197 §5.3.5 describes (and what x86 AES-NI's
    // aesdec implements) — not a literal step-by-step reversal of vaesem.vv's per-round state.
    // Composed with the *unmodified* round keys, it requires the surrounding boundary steps to
    // match §5.3.5's EqInvCipher exactly: a bare AddRoundKey(rk[10]) first (no vaesdf — the very
    // first step has no accompanying ShiftRows/SubBytes), then vaesdm.vv for rounds 9 downto 1,
    // then one vaesdf.vv-shaped step at the very end using rk[0] (matching EqInvCipher's tail:
    // InvShiftRows, InvSubBytes, AddRoundKey(dw[0]), with dw[0]=rk[0] unmodified). Verified
    // algebraically: since MixColumnsInv is linear over XOR, vaesdm.vv(y, rk[round]) equals
    // EqInvCipher's per-round MixColumnsInv(...) XOR InvMixColumns(rk[round]) — i.e. exactly
    // EqInvCipher's modified-key-schedule step, without needing to pre-transform rk[round] at all ─

    [Fact]
    public void VaesdmVv_VaesdfVv_ChainedRounds_InvertFips197KnownAnswerTrace() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, Xor16(ZvkTests.Fips197BCiphertext, ZvkTests.Fips197A1RoundKeys[10]));

        for (var round = 9; round >= 1; round--) {
            WriteBlock(s, 12, ZvkTests.Fips197A1RoundKeys[round]);
            ApplySideEffect(Exec(VaesDmVv(8, 12), s), s);
        }

        WriteBlock(s, 12, ZvkTests.Fips197A1RoundKeys[0]);
        ApplySideEffect(Exec(VaesDfVv(8, 12), s), s);

        Assert.Equal(ZvkTests.Fips197BPlaintext, ReadBlock(s, 8));
    }

    // ── vaes*.vs (scalar-broadcast form) at LMUL=4: one round-key group broadcast to every
    // state group, unlike .vv's per-group independent round key ────────────────────────────────

    [Fact]
    public void VaesemVs_Lmul4_BroadcastsSingleKeyToEveryGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        byte[] key = Block(1300);
        WriteBlock(s, 16, key);
        var states = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(1400 + g));
            WriteBlock(s, 8 + g, states[g]);
        }

        ApplySideEffect(Exec(VaesEmVs(8, 16), s), s);

        for (var g = 0; g < 4; g++) Assert.Equal(AesEmRef(states[g], key), ReadBlock(s, 8 + g));
    }

    [Fact]
    public void VaesemVs_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);
        // vd=8 at LMUL=4 spans registers 8-11; vs2=10 falls inside that range without being
        // vd itself, exercising the register-group-range check rather than a plain vd==vs2 check.
        ExecuteResult r = Exec(VaesEmVs(8, 10), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    // ── vaesz.vs: round-0 op, broadcasts vs2 as a plain XOR (no S-box/ShiftRows/MixColumns) ────

    [Fact]
    public void VaesZVs_Lmul4_BroadcastsSingleKeyToEveryGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        byte[] key = Block(1500);
        WriteBlock(s, 16, key);
        var states = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(1600 + g));
            WriteBlock(s, 8 + g, states[g]);
        }

        ApplySideEffect(Exec(VaesZVs(8, 16), s), s);

        for (var g = 0; g < 4; g++) Assert.Equal(Xor16(states[g], key), ReadBlock(s, 8 + g));
    }

    [Fact]
    public void VaesZVs_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);
        ExecuteResult r = Exec(VaesZVs(8, 9), s); // vs2=9 inside vd's [8,11] LMUL=4 group
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    // ── vaeskf1.vi: AES-128 forward key schedule, validated against the same FIPS-197 Appendix
    // A.1 key-expansion trace (key 2b7e1516..., Nk=4) used by the encrypt/decrypt KATs above ────

    [Fact]
    public void VaesKf1Vi_ChainedRounds_MatchFips197AppendixA1KeyExpansion() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, ZvkTests.Fips197A1RoundKeys[0]);

        for (var round = 1; round <= 10; round++) {
            ApplySideEffect(Exec(VaesKf1Vi(8, 8, round), s), s);
            Assert.Equal(ZvkTests.Fips197A1RoundKeys[round], ReadBlock(s, 8));
        }
    }

    [Fact]
    public void VaesKf1Vi_OutOfRangeRound_ProjectsOntoInRangeRound() {
        // §3.5: round=0 is invalid input but must behave identically to round=8 (bit 3 flipped).
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        byte[] currentKey = Block(1700);
        WriteBlock(s, 12, currentKey);
        WriteBlock(s, 8, currentKey);
        WriteBlock(s, 9, currentKey);

        ApplySideEffect(Exec(VaesKf1Vi(8, 12, 0), s), s);
        ApplySideEffect(Exec(VaesKf1Vi(9, 12, 8), s), s);

        Assert.Equal(ReadBlock(s, 9), ReadBlock(s, 8));
    }

    // ── vaeskf2.vi: AES-256 forward key schedule, validated against the FIPS-197 Appendix A.3
    // key-expansion trace (key 603deb10..., Nk=8). Ping-pongs between two registers exactly as a
    // real assembly loop would: producing round-key group r needs the group one back (vs2) and
    // the group two back (vd, overwritten with group r) ─────────────────────────────────────────

    [Fact]
    public void VaesKf2Vi_ChainedRounds_MatchFips197AppendixA3KeyExpansion() {
        byte[][] roundKeys = [
            Convert.FromHexString("603deb1015ca71be2b73aef0857d7781"), // rk0
            Convert.FromHexString("1f352c073b6108d72d9810a30914dff4"), // rk1
            Convert.FromHexString("9ba354118e6925afa51a8b5f2067fcde"), // rk2
            Convert.FromHexString("a8b09c1a93d194cdbe49846eb75d5b9a"), // rk3
            Convert.FromHexString("d59aecb85bf3c917fee94248de8ebe96"), // rk4
            Convert.FromHexString("b5a9328a2678a647983122292f6c79b3"), // rk5
            Convert.FromHexString("812c81addadf48ba24360af2fab8b464"), // rk6
            Convert.FromHexString("98c5bfc9bebd198e268c3ba709e04214"), // rk7
            Convert.FromHexString("68007bacb2df331696e939e46c518d80"), // rk8
            Convert.FromHexString("c814e20476a9fb8a5025c02d59c58239"), // rk9
            Convert.FromHexString("de1369676ccc5a71fa2563959674ee15"), // rk10
            Convert.FromHexString("5886ca5d2e2f31d77e0af1fa27cf73c3"), // rk11
            Convert.FromHexString("749c47ab18501ddae2757e4f7401905a"), // rk12
            Convert.FromHexString("cafaaae3e4d59b349adf6acebd10190d"), // rk13
            Convert.FromHexString("fe4890d1e6188d0b046df344706c631e"), // rk14
        ];

        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, roundKeys[0]); // regA
        WriteBlock(s, 12, roundKeys[1]); // regB

        for (var round = 2; round <= 14; round++) {
            (int vs2, int vd) = round % 2 == 0 ? (12, 8) : (8, 12);
            ApplySideEffect(Exec(VaesKf2Vi(vd, vs2, round), s), s);
            Assert.Equal(roundKeys[round], ReadBlock(s, vd));
        }
    }

    [Theory]
    [InlineData(0, 8)] // §3.6: 0-1 maps to 8-9
    [InlineData(1, 9)]
    [InlineData(15, 7)] // 15 maps to 7
    public void VaesKf2Vi_OutOfRangeRound_ProjectsOntoInRangeRound(int outOfRange, int inRange) {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        byte[] current = Block(1800);
        byte[] previous = Block(1900);
        WriteBlock(s, 16, current);
        WriteBlock(s, 8, previous);
        WriteBlock(s, 9, previous);

        ApplySideEffect(Exec(VaesKf2Vi(8, 16, outOfRange), s), s);
        ApplySideEffect(Exec(VaesKf2Vi(9, 16, inRange), s), s);

        Assert.Equal(ReadBlock(s, 9), ReadBlock(s, 8));
    }

    // ── Zvksed: SM4 block cipher, validated against GB/T 32907-2016 Example 1 (the standard
    // "key==plaintext" SM4 known-answer test, via draft-ribose-cfrg-sm4's transcription of the
    // published round-key/round-state trace) — key expansion (vsm4k.vi) and both encrypt and
    // decrypt directions of the round function (vsm4r.vv), which are identical except for the
    // order round keys are consumed in.
    //
    // SM4 applies a final "reverse transformation R" (swap word0<->word3, word1<->word2) that is
    // NOT part of vsm4r.vv/vsm4k.vi themselves — confirmed empirically via a throwaway `dotnet fsi`
    // script implementing the algorithm independently, since guessing the input/output word-order
    // convention by hand (rather than testing it) proved unreliable. Encryption's current state
    // starts as the plaintext directly (no pre-reversal) and needs R applied to the final result;
    // decryption is exactly symmetric (ciphertext directly as the starting state, R applied to the
    // final result) with round-key groups consumed in reverse group order *and* reverse word order
    // within each group (the last group's words are consumed key-by-key from its own end backwards).

    private static readonly byte[] Sm4Fk = Convert.FromHexString("A3B1BAC656AA3350677D9197B27022DC");
    private static readonly byte[] Sm4Key = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
    private static readonly byte[] Sm4Ciphertext = Convert.FromHexString("681EDF34D206965E86B3E94F536E4246");

    private static readonly byte[][] Sm4RoundKeyGroups = [
        Convert.FromHexString("F12186F941662B615A6AB19A7BA92077"), // rk[0:3]
        Convert.FromHexString("367360F4776A0C61B6BB89B324763151"), // rk[4:7]
        Convert.FromHexString("A520307CB7584DBDC30753ED7EE55B57"), // rk[8:11]
        Convert.FromHexString("6988608C30D895B744BA14AF104495A1"), // rk[12:15]
        Convert.FromHexString("D120B42873B55FA3CC87496692244439"), // rk[16:19]
        Convert.FromHexString("E89E641F98CA015AC715906099E1FD2E"), // rk[20:23]
        Convert.FromHexString("B79BD80C1D2115B00E228AEBF1780C81"), // rk[24:27]
        Convert.FromHexString("428D36546229349601CF72E59124A012"), // rk[28:31]
    ];

    private static byte[] ReverseWordOrder(byte[] block16) {
        var result = new byte[16];
        for (var w = 0; w < 4; w++) Array.Copy(block16, w * 4, result, (3 - w) * 4, 4);
        return result;
    }

    [Fact]
    public void Sm4KVi_ChainedGroups_MatchGbt32907KeyExpansion() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, Xor16(ZvkTests.Sm4Key, ZvkTests.Sm4Fk));

        for (var rnd = 0; rnd <= 7; rnd++) {
            ApplySideEffect(Exec(Sm4KVi(8, 8, rnd), s), s);
            Assert.Equal(ZvkTests.Sm4RoundKeyGroups[rnd], ReadBlock(s, 8));
        }
    }

    [Fact]
    public void Sm4RVv_ChainedGroups_EncryptsGbt32907Example1() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, ZvkTests.Sm4Key); // plaintext == key in this example

        for (var g = 0; g < 8; g++) {
            WriteBlock(s, 12, ZvkTests.Sm4RoundKeyGroups[g]);
            ApplySideEffect(Exec(Sm4RVv(8, 12), s), s);
        }

        Assert.Equal(ZvkTests.Sm4Ciphertext, ReverseWordOrder(ReadBlock(s, 8)));
    }

    [Fact]
    public void Sm4RVv_ChainedGroups_DecryptsGbt32907Example1() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, ZvkTests.Sm4Ciphertext); // symmetric with encryption: no pre-reversal

        for (var g = 7; g >= 0; g--) {
            WriteBlock(s, 12, ReverseWordOrder(ZvkTests.Sm4RoundKeyGroups[g]));
            ApplySideEffect(Exec(Sm4RVv(8, 12), s), s);
        }

        Assert.Equal(ZvkTests.Sm4Key, ReverseWordOrder(ReadBlock(s, 8))); // == plaintext here
    }

    [Fact]
    public void Sm4RVs_Lmul4_BroadcastsSingleKeyGroupToEveryStateGroup() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);

        byte[] keys = ZvkTests.Sm4RoundKeyGroups[0];
        WriteBlock(s, 16, keys);
        var states = new byte[4][];
        for (var g = 0; g < 4; g++) {
            states[g] = Block((uint)(2000 + g));
            WriteBlock(s, 8 + g, states[g]);
        }

        ApplySideEffect(Exec(Sm4RVs(8, 16), s), s);
        byte[][] vsResults = [ReadBlock(s, 8), ReadBlock(s, 9), ReadBlock(s, 10), ReadBlock(s, 11),];

        // Cross-check against the .vv form applied independently per group with the same
        // broadcast key group in vs2 — isolates the .vs addressing-mode logic (the single
        // scalarKeys read reused across every group) from the round math itself, already
        // validated by the KATs above.
        for (var g = 0; g < 4; g++) {
            Rv32ArchState vvState = MakeState();
            Vsetivli(vvState, 4, ZvkTests.VtypeiE32M1Tama);
            WriteBlock(vvState, 8, states[g]);
            WriteBlock(vvState, 12, keys);
            ApplySideEffect(Exec(Sm4RVv(8, 12), vvState), vvState);
            Assert.Equal(ReadBlock(vvState, 8), vsResults[g]);
        }
    }

    [Fact]
    public void Sm4RVs_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 16, ZvkTests.VtypeiE32M4Tama);
        ExecuteResult r = Exec(Sm4RVs(8, 10), s); // vs2=10 inside vd's [8,11] LMUL=4 group
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }
}