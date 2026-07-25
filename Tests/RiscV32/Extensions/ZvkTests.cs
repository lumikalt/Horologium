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

    // ── FIPS-197 Appendix C.1 known-answer test (AES-128, Nk=4, Nr=10) ─────────────────────────
    // Round-key and round-state constants transcribed verbatim from the published NIST FIPS-197
    // reference trace (Appendix C.1), not derived from this codebase or from ZvkTests' own
    // from-scratch reference — this validates SubBytes+ShiftRows+MixColumns+round-composition
    // against an external authority instead of only against ourselves. round[r].start is the
    // state already AddRoundKey'd with k_sch[r-1] (round[1].start = plaintext XOR round[0]
    // k_sch), so vaesem.vv(round[r].start, k_sch[r]) == round[r+1].start for r=1..9, and
    // vaesef.vv(round[10].start, k_sch[10]) == round[10].output (the ciphertext) — exactly the
    // SubBytes->ShiftRows->MixColumns(->none for the final round)->AddRoundKey shape vaesem/
    // vaesef implement, so no independent key-schedule implementation is needed here at all.

    [Fact]
    public void VaesemVv_VaesefVv_ChainedRounds_MatchFips197KnownAnswerTrace() {
        byte[] round1Start = Convert.FromHexString("00102030405060708090a0b0c0d0e0f0");
        byte[][] kSch = [
            Convert.FromHexString("d6aa74fdd2af72fadaa678f1d6ab76fe"), // k_sch[1]
            Convert.FromHexString("b692cf0b643dbdf1be9bc5006830b3fe"), // k_sch[2]
            Convert.FromHexString("b6ff744ed2c2c9bf6c590cbf0469bf41"), // k_sch[3]
            Convert.FromHexString("47f7f7bc95353e03f96c32bcfd058dfd"), // k_sch[4]
            Convert.FromHexString("3caaa3e8a99f9deb50f3af57adf622aa"), // k_sch[5]
            Convert.FromHexString("5e390f7df7a69296a7553dc10aa31f6b"), // k_sch[6]
            Convert.FromHexString("14f9701ae35fe28c440adf4d4ea9c026"), // k_sch[7]
            Convert.FromHexString("47438735a41c65b9e016baf4aebf7ad2"), // k_sch[8]
            Convert.FromHexString("549932d1f08557681093ed9cbe2c974e"), // k_sch[9]
            Convert.FromHexString("13111d7fe3944a17f307a78b4d2b30c5"), // k_sch[10]
        ];
        byte[] expectedCiphertext = Convert.FromHexString("69c4e0d86a7b0430d8cdb78070b4c55a");

        Rv32ArchState s = MakeState();
        Vsetivli(s, 4, ZvkTests.VtypeiE32M1Tama);
        WriteBlock(s, 8, round1Start);

        for (var round = 1; round <= 9; round++) {
            WriteBlock(s, 12, kSch[round - 1]);
            ApplySideEffect(Exec(VaesEmVv(8, 12), s), s);
        }

        WriteBlock(s, 12, kSch[9]);
        ApplySideEffect(Exec(VaesEfVv(8, 12), s), s);

        Assert.Equal(expectedCiphertext, ReadBlock(s, 8));
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

    // ── Decoder regression: decrypt forms (vs1=0/1) are deferred, must not silently misdecode ──

    [Fact]
    public void VaesDmVv_IsNotYetImplemented_IsIllegalInstruction() {
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, VaesDmVv(8, 12)));
    }
}