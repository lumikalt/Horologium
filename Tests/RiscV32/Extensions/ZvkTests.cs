#region

using System.Security.Cryptography;
using System.Text;
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

    // vtypei for e64,m2,ta,ma: SHA-512's EGW=256 needs LMUL(2)*VLEN(128)=256 — the minimal LMUL
    // satisfying the element-group constraint, so each element group spans exactly 2 registers.
    private const int VtypeiE64M2Tama = (1 << 7) | (1 << 6) | (3 << 3) | 1;

    // vtypei for e32,m2,ta,ma: SM3's EGW=256 at SEW=32 needs the same LMUL=2 minimum.
    private const int VtypeiE32M2Tama = (1 << 7) | (1 << 6) | (2 << 3) | 1;

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

    // vs1 is a genuine register operand for these three (not a sub-op selector or immediate).
    private static uint Sha2MsVv(int vd, int vs2, int vs1) => VopMvv(0x2D, vd, vs2, vs1);
    private static uint Sha2ChVv(int vd, int vs2, int vs1) => VopMvv(0x2E, vd, vs2, vs1);
    private static uint Sha2ClVv(int vd, int vs2, int vs1) => VopMvv(0x2F, vd, vs2, vs1);

    private static uint Sm3MeVv(int vd, int vs2, int vs1) => VopMvv(0x20, vd, vs2, vs1);
    private static uint Sm3CVi(int vd, int vs2, int round) => VopMvv(0x2B, vd, vs2, round);

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

    // ── Zvknha/Zvknhb: SHA-2 compression (vsha2c[hl].vv) + message schedule (vsha2ms.vv) ────────
    // Validated end-to-end against System.Security.Cryptography.SHA256/SHA512 — an independently
    // implemented, real oracle — rather than a hand-transcribed round-by-round trace, since
    // NIST FIPS 180-4's on-disk text does not include a worked example with intermediate values
    // (same situation as FIPS-197's Appendix C, see the Zvkned AES tests above) and manually
    // re-deriving 64/80 rounds of intermediate arithmetic would itself be transcription-error
    // prone. The K/H0 constant tables below *are* transcribed from FIPS 180-4 §4.2.2/§4.2.3/§5.3.3
    // (~/dl/NIST.FIPS.180-4.pdf) — but only feed the SETUP of a real multi-round computation
    // exercised through the actual instructions under test, so a wrong end-to-end digest would
    // still be caught even if a single constant were mistyped.
    //
    // Word-index-to-named-variable mapping (vs2={a,b,e,f} at idx3,2,1,0; vd={c,d,g,h} at
    // idx3,2,1,0; vsha2ms's vs2={W11,W10,W9,W4}/vs1={W15,W14,-,W12} at idx3,2,1,0) was confirmed
    // against the RISC-V Sail reference model (github.com/riscv/sail-riscv,
    // model/extensions/vector_crypto/zvknhab_insts.sail), not derived from the spec's prose
    // concatenation notation alone — that notation is genuinely ambiguous without seeing how
    // get_velem/read_vreg actually index elements.

    private static readonly ulong[] Sha256K = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    private static readonly ulong[] Sha256H0 = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ];

    private static readonly ulong[] Sha512K = [
        0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc,
        0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
        0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2,
        0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
        0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65,
        0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
        0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4,
        0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
        0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df,
        0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
        0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30,
        0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
        0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8,
        0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
        0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec,
        0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
        0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178,
        0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
        0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c,
        0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
    ];

    private static readonly ulong[] Sha512H0 = [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    private static void SetGroupWord(byte[] group, int wordIndex, int wordBytes, ulong value) {
        int o = wordIndex * wordBytes;
        for (var b = 0; b < wordBytes; b++) group[o + b] = (byte)(value >> (8 * b));
    }

    private static ulong GetGroupWord(byte[] group, int wordIndex, int wordBytes) {
        int o = wordIndex * wordBytes;
        ulong v = 0;
        for (var b = 0; b < wordBytes; b++) v |= (ulong)group[o + b] << (8 * b);
        return v;
    }

    private static void WriteGroup(Rv32ArchState state, int baseVreg, byte[] data, int egwBytes) {
        int regsPerGroup = egwBytes / 16;
        for (var r = 0; r < regsPerGroup; r++) {
            var chunk = new byte[16];
            Array.Copy(data, r * 16, chunk, 0, 16);
            WriteBlock(state, baseVreg + r, chunk);
        }
    }

    private static byte[] ReadGroup(Rv32ArchState state, int baseVreg, int egwBytes) {
        int regsPerGroup = egwBytes / 16;
        var result = new byte[egwBytes];
        for (var r = 0; r < regsPerGroup; r++) Array.Copy(ReadBlock(state, baseVreg + r), 0, result, r * 16, 16);
        return result;
    }

    // FIPS 180-4 §5.1.1/§5.1.2 message padding, generalized over word size (append 0x80, zero-pad,
    // then an 8-byte big-endian bit-length — SHA-512's 16-byte length field's high 8 bytes stay
    // zero, which is correct for every message length used in these tests).
    private static byte[] Sha2Pad(byte[] message, int wordBytes) {
        int blockBytes = wordBytes * 16;
        int lengthFieldBytes = wordBytes * 2;
        int msgLen = message.Length;
        int numBlocks = (msgLen + 1 + lengthFieldBytes + blockBytes - 1) / blockBytes;
        var padded = new byte[numBlocks * blockBytes];
        Array.Copy(message, padded, msgLen);
        padded[msgLen] = 0x80;
        var bitLen = (ulong)msgLen * 8;
        for (var i = 0; i < 8; i++) padded[padded.Length - 1 - i] = (byte)(bitLen >> (8 * i));
        return padded;
    }

    // Runs a full SHA-2 hash (arbitrary length, multi-block) through the actual vsha2ms.vv/
    // vsha2ch.vv/vsha2cl.vv instructions. vsha2ch[cl]'s vs2/vd ping-pong across two registers each
    // call (rHi/rLo): per-call, only the "vd" register is written with the new {a,b,e,f}, while
    // the untouched "vs2" register keeps its pre-call {a,b,e,f} value — which, after 2 rounds of
    // real SHA-2 compression, is exactly the new {c,d,g,h} the *next* call needs as its vd input
    // (the standard word-shift identity: after 2 rounds, new-c/d/g/h == old-a/b/e/f). Swapping
    // which register plays "vd" vs "vs2" every call is therefore sufficient — no extra copying.
    private ulong[] RunSha2(byte[] message, int sewBits, ulong[] k, ulong[] h0, int numRounds) {
        int wordBytes = sewBits / 8;
        int egwBytes = wordBytes * 4;
        int regsPerGroup = egwBytes / 16;
        int vtypei = sewBits == 32 ? VtypeiE32M1Tama : VtypeiE64M2Tama;

        int RegBase(int slot) => 1 + slot * regsPerGroup;
        int rHi = RegBase(0), rLo = RegBase(1), rMsWordsA = RegBase(2), rMsWordsB = RegBase(3), rMsWordsC = RegBase(4),
            rMsgConst = RegBase(5);

        byte[] padded = Sha2Pad(message, wordBytes);
        var h = (ulong[])h0.Clone();
        ulong mask = sewBits == 64 ? ulong.MaxValue : (1UL << 32) - 1;

        Rv32ArchState state = MakeState();
        Vsetivli(state, 4, vtypei);

        for (var blockOff = 0; blockOff < padded.Length; blockOff += wordBytes * 16) {
            var w = new ulong[numRounds];
            for (var t = 0; t < 16; t++) w[t] = BigEndianWord(padded, blockOff + t * wordBytes, wordBytes);

            int numMsCalls = (numRounds - 16) / 4;
            for (var kk = 0; kk < numMsCalls; kk++) {
                int t = 4 * kk;

                var vdIn = new byte[egwBytes];
                SetGroupWord(vdIn, 0, wordBytes, w[t + 0]);
                SetGroupWord(vdIn, 1, wordBytes, w[t + 1]);
                SetGroupWord(vdIn, 2, wordBytes, w[t + 2]);
                SetGroupWord(vdIn, 3, wordBytes, w[t + 3]);
                WriteGroup(state, rMsWordsA, vdIn, egwBytes);

                var vs2 = new byte[egwBytes];
                SetGroupWord(vs2, 0, wordBytes, w[t + 4]);
                SetGroupWord(vs2, 1, wordBytes, w[t + 9]);
                SetGroupWord(vs2, 2, wordBytes, w[t + 10]);
                SetGroupWord(vs2, 3, wordBytes, w[t + 11]);
                WriteGroup(state, rMsWordsB, vs2, egwBytes);

                var vs1 = new byte[egwBytes];
                SetGroupWord(vs1, 0, wordBytes, w[t + 12]);
                SetGroupWord(vs1, 2, wordBytes, w[t + 14]);
                SetGroupWord(vs1, 3, wordBytes, w[t + 15]);
                WriteGroup(state, rMsWordsC, vs1, egwBytes);

                ApplySideEffect(Exec(Sha2MsVv(rMsWordsA, rMsWordsB, rMsWordsC), state), state);

                byte[] outg = ReadGroup(state, rMsWordsA, egwBytes);
                w[t + 16] = GetGroupWord(outg, 0, wordBytes);
                w[t + 17] = GetGroupWord(outg, 1, wordBytes);
                w[t + 18] = GetGroupWord(outg, 2, wordBytes);
                w[t + 19] = GetGroupWord(outg, 3, wordBytes);
            }

            var hi = new byte[egwBytes];
            SetGroupWord(hi, 3, wordBytes, h[0]); // a
            SetGroupWord(hi, 2, wordBytes, h[1]); // b
            SetGroupWord(hi, 1, wordBytes, h[4]); // e
            SetGroupWord(hi, 0, wordBytes, h[5]); // f
            WriteGroup(state, rHi, hi, egwBytes);

            var lo = new byte[egwBytes];
            SetGroupWord(lo, 3, wordBytes, h[2]); // c
            SetGroupWord(lo, 2, wordBytes, h[3]); // d
            SetGroupWord(lo, 1, wordBytes, h[6]); // g
            SetGroupWord(lo, 0, wordBytes, h[7]); // h
            WriteGroup(state, rLo, lo, egwBytes);

            int vdReg = rLo, vs2Reg = rHi;
            for (var p = 0; p < numRounds / 2; p++) {
                int t = 2 * p;
                int groupStart = t - t % 4;
                int relPos = t % 4; // 0 -> cl (idx0,1), 2 -> ch (idx2,3)

                var msgConst = new byte[egwBytes];
                SetGroupWord(msgConst, 0, wordBytes, (w[groupStart + 0] + k[groupStart + 0]) & mask);
                SetGroupWord(msgConst, 1, wordBytes, (w[groupStart + 1] + k[groupStart + 1]) & mask);
                SetGroupWord(msgConst, 2, wordBytes, (w[groupStart + 2] + k[groupStart + 2]) & mask);
                SetGroupWord(msgConst, 3, wordBytes, (w[groupStart + 3] + k[groupStart + 3]) & mask);
                WriteGroup(state, rMsgConst, msgConst, egwBytes);

                uint raw = relPos == 0
                    ? Sha2ClVv(vdReg, vs2Reg, rMsgConst)
                    : Sha2ChVv(vdReg, vs2Reg, rMsgConst);
                ApplySideEffect(Exec(raw, state), state);

                (vdReg, vs2Reg) = (vs2Reg, vdReg);
            }

            // vs2Reg (post-loop) always equals the register the *last* call wrote as vd — the
            // swap sets the new vs2Reg to the old vdReg every iteration.
            byte[] finalHi = ReadGroup(state, vs2Reg, egwBytes);
            byte[] finalLo = ReadGroup(state, vdReg, egwBytes);

            h[0] = (h[0] + GetGroupWord(finalHi, 3, wordBytes)) & mask;
            h[1] = (h[1] + GetGroupWord(finalHi, 2, wordBytes)) & mask;
            h[2] = (h[2] + GetGroupWord(finalLo, 3, wordBytes)) & mask;
            h[3] = (h[3] + GetGroupWord(finalLo, 2, wordBytes)) & mask;
            h[4] = (h[4] + GetGroupWord(finalHi, 1, wordBytes)) & mask;
            h[5] = (h[5] + GetGroupWord(finalHi, 0, wordBytes)) & mask;
            h[6] = (h[6] + GetGroupWord(finalLo, 1, wordBytes)) & mask;
            h[7] = (h[7] + GetGroupWord(finalLo, 0, wordBytes)) & mask;
        }

        return h;
    }

    private static ulong BigEndianWord(byte[] data, int offset, int wordBytes) {
        ulong v = 0;
        for (var b = 0; b < wordBytes; b++) v = (v << 8) | data[offset + b];
        return v;
    }

    private static byte[] WordsToBigEndianBytes(ulong[] words, int wordBytes) {
        var result = new byte[words.Length * wordBytes];
        for (var i = 0; i < words.Length; i++)
        for (var b = 0; b < wordBytes; b++)
            result[i * wordBytes + b] = (byte)(words[i] >> (8 * (wordBytes - 1 - b)));
        return result;
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData(
        "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq" // FIPS 180-4 two-block example
    )]
    public void Sha256_EndToEnd_MatchesDotNetSha256(string messageText) {
        byte[] message = Encoding.ASCII.GetBytes(messageText);
        ulong[] actual = RunSha2(message, 32, Sha256K, Sha256H0, 64);
        byte[] actualBytes = WordsToBigEndianBytes(actual, 4);

        Assert.Equal(SHA256.HashData(message), actualBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    public void Sha512_EndToEnd_MatchesDotNetSha512(string messageText) {
        byte[] message = Encoding.ASCII.GetBytes(messageText);
        ulong[] actual = RunSha2(message, 64, Sha512K, Sha512H0, 80);
        byte[] actualBytes = WordsToBigEndianBytes(actual, 8);

        Assert.Equal(SHA512.HashData(message), actualBytes);
    }

    [Fact]
    public void Sha2MsVv_Lmul4_TwoElementGroups_MatchIndependentSingleGroupCalls() {
        // Every KAT above uses vl=EGS=4 (exactly one element group), so none of them exercise
        // `groupIndex*regsPerGroup` addressing with regsPerGroup>1 — the one genuinely new code
        // path SHA-512 (EGW=256, 2 registers/group) introduces. This drives vl=8 (two groups) at
        // LMUL=4 and cross-checks each group's output against an independent single-group (vl=4)
        // call with the same inputs, isolating the multi-group addressing from the round math
        // (already end-to-end KAT-validated above).
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE64M4Tama);

        ulong[][] oldWords = [[1, 2, 3, 4], [100, 200, 300, 400],];
        ulong[][] midWords = [[5, 6, 7, 8], [500, 600, 700, 800],];
        ulong[][] newWords = [[9, 0, 10, 11], [900, 0, 1000, 1100],];

        var vdIn = new byte[64];
        var vs2 = new byte[64];
        var vs1 = new byte[64];
        for (var g = 0; g < 2; g++) {
            var vdGroup = new byte[32];
            for (var i = 0; i < 4; i++) SetGroupWord(vdGroup, i, 8, oldWords[g][i]);
            Array.Copy(vdGroup, 0, vdIn, g * 32, 32);

            var vs2Group = new byte[32];
            for (var i = 0; i < 4; i++) SetGroupWord(vs2Group, i, 8, midWords[g][i]);
            Array.Copy(vs2Group, 0, vs2, g * 32, 32);

            var vs1Group = new byte[32];
            SetGroupWord(vs1Group, 0, 8, newWords[g][0]);
            SetGroupWord(vs1Group, 2, 8, newWords[g][2]);
            SetGroupWord(vs1Group, 3, 8, newWords[g][3]);
            Array.Copy(vs1Group, 0, vs1, g * 32, 32);
        }

        WriteGroup(s, 4, vdIn, 64); // regs 4-7
        WriteGroup(s, 8, vs2, 64); // regs 8-11
        WriteGroup(s, 12, vs1, 64); // regs 12-15
        ApplySideEffect(Exec(Sha2MsVv(4, 8, 12), s), s);
        byte[] outAll = ReadGroup(s, 4, 64);

        for (var g = 0; g < 2; g++) {
            Rv32ArchState single = MakeState();
            Vsetivli(single, 4, ZvkTests.VtypeiE64M2Tama);
            WriteGroup(single, 1, vdIn[(g * 32)..((g + 1) * 32)], 32);
            WriteGroup(single, 3, vs2[(g * 32)..((g + 1) * 32)], 32);
            WriteGroup(single, 5, vs1[(g * 32)..((g + 1) * 32)], 32);
            ApplySideEffect(Exec(Sha2MsVv(1, 3, 5), single), single);
            byte[] expected = ReadGroup(single, 1, 32);

            Assert.Equal(expected, outAll[(g * 32)..((g + 1) * 32)]);
        }
    }

    [Fact]
    public void Sha2MsVv_VdOverlapsVs1_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        ExecuteResult r = Exec(Sha2MsVv(8, 12, 8), s); // vs1==vd
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void Sha2ChVv_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        ExecuteResult r = Exec(Sha2ChVv(8, 8, 12), s); // vs2==vd
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void Sha2MsVv_UnsupportedSew_Traps() {
        // Only e32/e64 are valid for vsha2* (this codebase implements the Zvknhb superset
        // unconditionally, so both are accepted); e16 must still trap.
        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE16M4Tama);
        ExecuteResult r = Exec(Sha2MsVv(8, 12, 16), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    // ── Zvksh: SM3 compression (vsm3c.vi) + message schedule (vsm3me.vv) ────────────────────────
    // GB/T 32905-2016 Example 1 (message "abc"), transcribed via IETF draft-sca-cfrg-sm3 (which
    // reproduces the padded message, the full W[0..67] expansion trace, the round-by-round A-H
    // compression trace, and the final hash) rather than FIPS-180-4-style just-a-final-digest, so
    // each instruction can be checked in isolation — essential here since the element-ordering
    // conventions of vsm3c.vi and vsm3me.vv turned out to disagree with each other (see
    // Rv32Executor.VCrypto.cs's Zvksh section header) and a single wrong full-hash-only test
    // would not localize which instruction (or which direction) was wrong.

    [Fact]
    public void Sm3MeVv_OneCall_MatchesGbt32905MessageExpansionTrace() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE32M2Tama);

        // vs1 = W[7:0], vs2 = W[15:8] — element j maps directly to word j for both (no reversal
        // on the input side; confirmed against the Sail model's plain read_vreg-indexed access).
        WriteGroup(s, 8, Convert.FromHexString("6162638000000000000000000000000000000000000000000000000000000000"), 32);
        WriteGroup(s, 10, Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000018"), 32);

        ApplySideEffect(Exec(Sm3MeVv(12, 10, 8), s), s);

        // Expected W[16..23] = 9092e200 00000000 000c0606 719c70ed 00000000 8001801f 939f7da9
        // 00000000, but vsm3me.vv's write side reverses element order relative to word order
        // (physical position p <- W[23-p] — the *opposite* of vsha2ms's output convention), so the
        // expected raw bytes are that word list in reverse.
        byte[] expected = Convert.FromHexString(
            "00000000939f7da98001801f00000000719c70ed000c060600000000" + "9092e200"
        );
        Assert.Equal(expected, ReadGroup(s, 12, 32));
    }

    [Fact]
    public void Sm3CVi_OneCall_MatchesGbt32905CompressionTrace() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE32M2Tama);

        // vd = current state {A..H} = the SM3 IV. vsm3c.vi's read side (get_velem_oct_vec)
        // reverses element order, so physical element k holds state-word H,G,F,E,D,C,B,A in that
        // order (idx0=H,...,idx7=A) — the reverse of the natural A-to-H listing.
        WriteGroup(
            s, 8,
            Convert.FromHexString("b0fb0e4ee38dee4d163138aaa96f30bcda8a0600172442d74914b2b97380166f"), 32
        );

        // vs2 = message words; only w0,w1,w4,w5 are read (physical idx7,6,3,2 respectively per
        // the same element-order reversal), the rest are don't-care (zeroed here).
        WriteGroup(s, 10, Convert.FromHexString("0000000000000000000000000000000000000000000000000000000061626380"), 32);

        ApplySideEffect(Exec(Sm3CVi(8, 10, 0), s), s); // rnds=0 -> rounds 0 and 1

        // Expected: the trace's j=1 state row (A..H after both rounds), physically stored using
        // the same idx0=H..idx7=A layout as the input — i.e. that row's words in reverse.
        byte[] expected = Convert.FromHexString(
            "c550b18985e54b79b2ad29f4ac353a2329657292002cdee7b9edc12bea52428c"
        );
        Assert.Equal(expected, ReadGroup(s, 8, 32));
    }

    private static void SetWordBe(byte[] group, int wordIndex, uint value) {
        int o = wordIndex * 4;
        group[o] = (byte)(value >> 24);
        group[o + 1] = (byte)(value >> 16);
        group[o + 2] = (byte)(value >> 8);
        group[o + 3] = (byte)value;
    }

    private static uint GetWordBe(byte[] group, int wordIndex) {
        int o = wordIndex * 4;
        return (uint)((group[o] << 24) | (group[o + 1] << 16) | (group[o + 2] << 8) | group[o + 3]);
    }

    // Full multi-block SM3 hash, chaining the actual vsm3me.vv/vsm3c.vi instructions. Reuses
    // Sha2Pad (identical padding scheme: 0x80, zero-pad, 64-bit big-endian bit length in a 64-byte
    // block). Message-schedule generation slides by 8 words per vsm3me.vv call (not 4, unlike
    // vsha2ms) and runs one call short of a whole number of groups for a single 64-byte block (52
    // words needed from 7 calls of 8 = 56, discarding the last 4) — handled here by just
    // generating past W[67] and ignoring the extras, rather than trying to special-case the final
    // call. Finalizes each block with the SM3-specific XOR-with-previous-state feed-forward
    // (V_{i+1} = CF(V_i, B_i) xor V_i — GB/T 32905-2016 §5.3.3), unlike SHA-2's modular addition.
    private byte[] RunSm3(byte[] message) {
        byte[] padded = Sha2Pad(message, 4);
        uint[] iv = [0x7380166fu, 0x4914b2b9u, 0x172442d7u, 0xda8a0600u, 0xa96f30bcu, 0x163138aau, 0xe38dee4du, 0xb0fb0e4eu,];
        var state = (uint[])iv.Clone();

        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE32M2Tama);

        for (var blockOff = 0; blockOff < padded.Length; blockOff += 64) {
            var w = new uint[72];
            for (var t = 0; t < 16; t++) w[t] = (uint)BigEndianWord(padded, blockOff + t * 4, 4);

            for (var t2 = 0; t2 + 16 <= 67; t2 += 8) {
                var vs1Group = new byte[32];
                var vs2Group = new byte[32];
                for (var j = 0; j < 8; j++) {
                    SetWordBe(vs1Group, j, w[t2 + j]);
                    SetWordBe(vs2Group, j, w[t2 + 8 + j]);
                }

                WriteGroup(s, 16, vs1Group, 32);
                WriteGroup(s, 18, vs2Group, 32);
                ApplySideEffect(Exec(Sm3MeVv(20, 18, 16), s), s);
                byte[] outGroup = ReadGroup(s, 20, 32);
                for (var r = 0; r < 8; r++) w[t2 + 16 + r] = GetWordBe(outGroup, 7 - r);
            }

            var blockIv = (uint[])state.Clone();
            for (var rnds = 0; rnds < 32; rnds++) {
                int r2 = 2 * rnds;
                var stateGroup = new byte[32];
                for (var k = 0; k < 8; k++) SetWordBe(stateGroup, 7 - k, state[k]);
                WriteGroup(s, 8, stateGroup, 32);

                var msgGroup = new byte[32];
                SetWordBe(msgGroup, 7, w[r2]);
                SetWordBe(msgGroup, 6, w[r2 + 1]);
                SetWordBe(msgGroup, 3, w[r2 + 4]);
                SetWordBe(msgGroup, 2, w[r2 + 5]);
                WriteGroup(s, 10, msgGroup, 32);

                ApplySideEffect(Exec(Sm3CVi(8, 10, rnds), s), s);
                byte[] newStateGroup = ReadGroup(s, 8, 32);
                for (var k = 0; k < 8; k++) state[k] = GetWordBe(newStateGroup, 7 - k);
            }

            for (var k = 0; k < 8; k++) state[k] ^= blockIv[k];
        }

        var result = new byte[32];
        for (var k = 0; k < 8; k++) SetWordBe(result, k, state[k]);
        return result;
    }

    [Fact]
    public void Sm3_EndToEnd_MatchesGbt32905Example1Hash() {
        byte[] message = Encoding.ASCII.GetBytes("abc");
        byte[] actual = RunSm3(message);
        byte[] expected = Convert.FromHexString("66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sm3_EndToEnd_MatchesGbt32905Example2Hash() {
        // GB/T 32905-2016 Example 2: a 64-byte message pads to exactly 2 blocks — the only path
        // in this suite that exercises cross-block state carry (the feed-forward XOR against a
        // non-IV running state).
        byte[] message = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcd", 16)));
        byte[] actual = RunSm3(message);
        byte[] expected = Convert.FromHexString("debe9ff92275b8a138604889c18e5a4d6fdb70e5387e5765293dcba39c0c5732");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sm3CVi_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE32M2Tama);
        ExecuteResult r = Exec(Sm3CVi(8, 8, 0), s); // vs2==vd
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void Sm3MeVv_VdOverlapsVs2_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE32M2Tama);
        ExecuteResult r = Exec(Sm3MeVv(8, 8, 12), s); // vs2==vd
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void Sm3CVi_WrongSew_Traps() {
        Rv32ArchState s = MakeState();
        Vsetivli(s, 8, ZvkTests.VtypeiE64M2Tama); // e64, not e32
        ExecuteResult r = Exec(Sm3CVi(8, 10, 0), s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }
}