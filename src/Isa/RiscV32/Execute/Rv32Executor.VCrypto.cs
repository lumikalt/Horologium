#region

using System.Numerics;
using Mechanism;
using RiscV32.Decode;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // ── Vector Crypto: element-group infrastructure (RISC-V Cryptography Extensions Volume II,
    // §1.4/§1.5) ─────────────────────────────────────────────────────────────────────────────
    //
    // An "element group" is EGS consecutive SEW-wide elements treated as one EGW-bit operand
    // (e.g. a 128-bit AES block = 4 x 32-bit words). VLEN is fixed at 128 in this codebase
    // (VectorRegisterFile.VLen), and every EGW this spec defines (128 or 256) is an exact
    // multiple of VLEN, so an element group always lands on physical-register boundaries: group
    // g occupies exactly RegsPerGroup(egw) consecutive registers starting at
    // `baseVreg + g*RegsPerGroup(egw)` — no sub-register byte offsetting is ever needed here,
    // unlike general (non-crypto) LMUL element addressing, which this codebase does not
    // implement.

    private static int RegsPerGroup(int egwBits) => egwBits / VectorRegisterFile.VLen;

    private static byte[] ReadElementGroup(IArchState state, int baseVreg, int groupIndex, int egwBits) {
        int regsPerGroup = RegsPerGroup(egwBits);
        var result = new byte[egwBits / 8];
        for (var r = 0; r < regsPerGroup; r++) {
            byte[] reg = VState(state).VectorRegisters.Read(baseVreg + groupIndex * regsPerGroup + r);
            Array.Copy(reg, 0, result, r * VectorRegisterFile.VLenB, VectorRegisterFile.VLenB);
        }

        return result;
    }

    private static void WriteElementGroup(Rv32ArchState state, int baseVreg, int groupIndex, int egwBits, byte[] data) {
        int regsPerGroup = RegsPerGroup(egwBits);
        for (var r = 0; r < regsPerGroup; r++) {
            var reg = new byte[VectorRegisterFile.VLenB];
            Array.Copy(data, r * VectorRegisterFile.VLenB, reg, 0, VectorRegisterFile.VLenB);
            state.VectorRegisters.Write(baseVreg + groupIndex * regsPerGroup + r, reg);
        }
    }

    // Integer LMUL (1/2/4/8), or 0 for fractional/reserved vlmul fields — fractional LMUL always
    // fails the EGW>=128 element-group ops' `LMUL*VLEN>=EGW` check, so 0 (rather than modelling
    // eighths/quarters/halves numerically) is sufficient to make that check correct.
    private static int VGetLmulInt(IArchState state) {
        uint field = VState(state).CsrFile.DirectRead(CsrFile.Vtype) & 0x7;
        return field switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 0, };
    }

    // The three element-group constraints from spec §1.5: LMUL*VLEN>=EGW (checked unconditionally,
    // even at vl=0), vl/vstart must be integer multiples of EGS, and SEW must match the
    // instruction's required width. Returns a trap ExecuteResult if any is violated, else null.
    private static ExecuteResult? CheckElementGroupConstraints(
        IArchState state,
        ulong pc,
        int egwBits,
        int egs,
        int requiredSewBits
    ) {
        if (VGetLmulInt(state) * VectorRegisterFile.VLen < egwBits)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes * 8 != requiredSewBits)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        if (vl % (uint)egs != 0 || vstart % (uint)egs != 0)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        return null;
    }

    // ── Zvkned: AES round instructions (vaes{d,e}{m,f}.[vv,vs]) ────────────────────────────────
    // EGW=128, EGS=4, EEW=SEW=32 (spec §3.1-3.4). Shared shape across all four round ops in both
    // addressing forms: read the current-state element group from vd and the round-key element
    // group (matching index for .vv, the single scalar group at vs2 for .vs), apply the
    // instruction-specific round transform, XOR in the round key, write back to vd.

    private static ExecuteResult ExecuteVAesRound(
        IArchState state,
        ulong pc,
        int vd,
        int vs2,
        bool scalar,
        VAesRoundKind kind
    ) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        // Reserved encoding (.vs form only): vd's LMUL register group must not overlap the
        // single vs2 scalar-element-group register (spec §3.1-3.4, "Reserved Encodings").
        if (scalar && vs2 >= vd && vs2 < vd + VGetLmulInt(state))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        byte[]? scalarKey = scalar ? ReadElementGroup(state, vs2, 0, egw) : null;
        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] roundState = ReadElementGroup(state, vd, i, egw);
            byte[] roundKey = scalarKey ?? ReadElementGroup(state, vs2, i, egw);
            byte[] transformed = AesRoundPreKeyTransform(kind, roundState);
            var afterKey = new byte[16];
            for (var b = 0; b < 16; b++) afterKey[b] = (byte)(transformed[b] ^ roundKey[b]);
            groups[i - egStart] = (i, AesRoundPostKeyTransform(kind, afterKey));
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // Steps applied before the round-key XOR. Encrypt-middle/final and decrypt-final all XOR the
    // key as their last step, so their whole transform lives here; decrypt-middle's MixColumns
    // step comes *after* the XOR (spec §3.2 pseudocode: sr/sb/ark/mix, in that order) and is
    // handled separately by AesRoundPostKeyTransform.
    private static byte[] AesRoundPreKeyTransform(VAesRoundKind kind, byte[] state) => kind switch {
        VAesRoundKind.EncryptMiddle =>
            AesMixColumnsFwdBlock(AesShiftRowsFwdBlock(AesSubBytesFwdBlock(state))),
        VAesRoundKind.EncryptFinal => AesShiftRowsFwdBlock(AesSubBytesFwdBlock(state)),
        // Inverse cipher applies InvShiftRows before InvSubBytes (spec §3.1/§3.2 pseudocode),
        // the reverse order from the forward steps.
        VAesRoundKind.DecryptMiddle or VAesRoundKind.DecryptFinal =>
            AesSubBytesInvBlock(AesShiftRowsInvBlock(state)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] AesRoundPostKeyTransform(VAesRoundKind kind, byte[] afterKey) =>
        kind == VAesRoundKind.DecryptMiddle ? AesMixColumnsInvBlock(afterKey) : afterKey;

    // ── Zvkned: vaesz.vs (round-0 AES, spec §3.7) ───────────────────────────────────────────────
    // XORs the single scalar element group at vs2 into every state element group at vd — the
    // ".vs"-only "splat avoidance" op for when the same round key applies to every lane.

    private static ExecuteResult ExecuteVAesZ(IArchState state, ulong pc, int vd, int vs2) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        // Reserved encoding: vd's LMUL register group must not overlap the vs2 register.
        if (vs2 >= vd && vs2 < vd + VGetLmulInt(state))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        byte[] roundKey = ReadElementGroup(state, vs2, 0, egw);
        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] roundState = ReadElementGroup(state, vd, i, egw);
            var ark = new byte[16];
            for (var b = 0; b < 16; b++) ark[b] = (byte)(roundState[b] ^ roundKey[b]);
            groups[i - egStart] = (i, ark);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // ── Zvkned/Zvksed: word-level key-schedule/round-function helpers ─────────────────────────
    // Word j of a 16-byte element group is bytes[4j..4j+3], packed **natural big-endian**
    // (bytes[4j] is the packed uint's most significant byte) so that arbitrary-bit-count
    // BitOperations.RotateLeft/RotateRight calls on the unpacked word match the spec's own
    // ROL32/ROTWORD semantics directly. AES's key schedule only ever rotates by a whole byte
    // (8 bits), so it can be made to agree with *either* byte-order convention just by picking
    // the matching rotate direction and Rcon byte position (see ExecuteVAesKf1/Kf2's
    // RotateLeft(w,8) and `AesRcon[i] << 24`, chosen to match the convention here) — but SM4's
    // round function and key schedule (Sm4RoundWord/Sm4KeyScheduleWord) rotate by 2/10/13/18/23
    // bits, which have no such freedom: a byte-order mismatch silently produces a different
    // (wrong) result for any non-byte-aligned rotation amount. Caught by a GB/T 32907 SM4
    // known-answer test failing under the byte-order convention this file originally shipped
    // with (which the byte-aligned-only AES tests had passed under regardless).

    private static uint ElementGroupGetWord(byte[] block16, int wordIndex) {
        int o = wordIndex * 4;
        return (uint)((block16[o] << 24) | (block16[o + 1] << 16) | (block16[o + 2] << 8) | block16[o + 3]);
    }

    private static void ElementGroupSetWord(byte[] block16, int wordIndex, uint value) {
        int o = wordIndex * 4;
        block16[o] = (byte)(value >> 24);
        block16[o + 1] = (byte)(value >> 16);
        block16[o + 2] = (byte)(value >> 8);
        block16[o + 3] = (byte)value;
    }

    // §3.5: out-of-range uimm[3:0] (0, or 11-15) is projected onto a valid round by inverting
    // bit 3 — 0 maps to 8, 11-15 maps to 3-7 — so the instruction is fully defined for all
    // uimm[3:0], not just the documented 1-10 range.
    private static ExecuteResult ExecuteVAesKf1(IArchState state, ulong pc, int vd, int vs2, int uimm) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        int rnd = uimm & 0xF;
        if (rnd > 10 || rnd == 0) rnd ^= 0x8;
        int rconIndex = rnd - 1;

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] currentRoundKey = ReadElementGroup(state, vs2, i, egw);
            // AesRcon is a scalar-crypto table shared with aes64ks1i (Rv32Executor.Crypto.cs),
            // stored as the round constant's *low* byte — shift it into the high byte and rotate
            // left (rather than right) to match ElementGroupGetWord's natural-big-endian words.
            uint w0 = AesSubwordFwd(BitOperations.RotateLeft(ElementGroupGetWord(currentRoundKey, 3), 8))
                    ^ (AesRcon[rconIndex] << 24) ^ ElementGroupGetWord(currentRoundKey, 0);
            uint w1 = w0 ^ ElementGroupGetWord(currentRoundKey, 1);
            uint w2 = w1 ^ ElementGroupGetWord(currentRoundKey, 2);
            uint w3 = w2 ^ ElementGroupGetWord(currentRoundKey, 3);
            var next = new byte[16];
            ElementGroupSetWord(next, 0, w0);
            ElementGroupSetWord(next, 1, w1);
            ElementGroupSetWord(next, 2, w2);
            ElementGroupSetWord(next, 3, w3);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // §3.6: out-of-range uimm[3:0] (0-1, or 15) is projected by inverting bit 3 — 0-1 maps to
    // 8-9, 15 maps to 7 — analogous to vaeskf1.vi's projection but over the 2-14 valid range.
    private static ExecuteResult ExecuteVAesKf2(IArchState state, ulong pc, int vd, int vs2, int uimm) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        int rnd = uimm & 0xF;
        if (rnd < 2 || rnd > 14) rnd ^= 0x8;

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] currentRoundKey = ReadElementGroup(state, vs2, i, egw);
            byte[] previousRoundKey = ReadElementGroup(state, vd, i, egw);
            uint w3Current = ElementGroupGetWord(currentRoundKey, 3);
            uint w0 = (rnd & 1) == 1
                ? AesSubwordFwd(w3Current) ^ ElementGroupGetWord(previousRoundKey, 0)
                : AesSubwordFwd(BitOperations.RotateLeft(w3Current, 8)) ^ (AesRcon[(rnd >> 1) - 1] << 24)
                ^ ElementGroupGetWord(previousRoundKey, 0);
            uint w1 = w0 ^ ElementGroupGetWord(previousRoundKey, 1);
            uint w2 = w1 ^ ElementGroupGetWord(previousRoundKey, 2);
            uint w3 = w2 ^ ElementGroupGetWord(previousRoundKey, 3);
            var next = new byte[16];
            ElementGroupSetWord(next, 0, w0);
            ElementGroupSetWord(next, 1, w1);
            ElementGroupSetWord(next, 2, w2);
            ElementGroupSetWord(next, 3, w3);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // 128-bit-block forms of the AES round steps, operating on the standard state[4*col+row]
    // byte layout (byte i = row i%4, column i/4) — reusing the already-independently-verified
    // per-byte S-box table and per-column MixColumns math from Rv32Executor.Crypto.cs, which the
    // RISC-V Vector Crypto spec's own Sail helpers (aes_get_column, aes_subbytes_fwd,
    // aes_mixcolumns_fwd — Appendix C) confirm use the identical column/byte convention.

    private static byte[] AesSubBytesFwdBlock(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = AesSboxFwd[state[i]];
        return result;
    }

    private static byte[] AesSubBytesInvBlock(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) result[i] = AesSboxInv[state[i]];
        return result;
    }

    private static byte[] AesShiftRowsFwdBlock(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            result[i] = state[4 * ((c + r) % 4) + r];
        }

        return result;
    }

    private static byte[] AesShiftRowsInvBlock(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            result[i] = state[4 * (((c - r) % 4 + 4) % 4) + r];
        }

        return result;
    }

    // Column packing here is LSB-first — the opposite convention from ElementGroupGetWord/SetWord
    // above — and deliberately local: MixColumns never bit-rotates, so it's convention-agnostic and
    // must not be unified with the element-group word helpers (that unification is exactly what
    // broke SM4's non-byte-aligned rotations; see ElementGroupGetWord's doc comment).
    private static byte[] AesMixColumnsFwdBlock(byte[] state) {
        var result = new byte[16];
        for (var c = 0; c < 4; c++) {
            var col = (uint)(state[4 * c] | (state[4 * c + 1] << 8) | (state[4 * c + 2] << 16)
                           | (state[4 * c + 3] << 24));
            uint mixed = AesMixColumnFwd(col);
            result[4 * c] = (byte)mixed;
            result[4 * c + 1] = (byte)(mixed >> 8);
            result[4 * c + 2] = (byte)(mixed >> 16);
            result[4 * c + 3] = (byte)(mixed >> 24);
        }

        return result;
    }

    private static byte[] AesMixColumnsInvBlock(byte[] state) {
        var result = new byte[16];
        for (var c = 0; c < 4; c++) {
            var col = (uint)(state[4 * c] | (state[4 * c + 1] << 8) | (state[4 * c + 2] << 16)
                           | (state[4 * c + 3] << 24));
            uint mixed = AesMixColumnInv(col);
            result[4 * c] = (byte)mixed;
            result[4 * c + 1] = (byte)(mixed >> 8);
            result[4 * c + 2] = (byte)(mixed >> 16);
            result[4 * c + 3] = (byte)(mixed >> 24);
        }

        return result;
    }

    // ── Zvksed: SM4 block cipher (vsm4r.[vv,vs] rounds, vsm4k.vi key expansion) ─────────────────
    // EGW=128, EGS=4, EEW=SEW=32 (spec §3.25/§3.26) — same element-group shape as Zvkned, reusing
    // ReadElementGroup/WriteElementGroup/CheckElementGroupConstraints/ElementGroupGetWord/
    // ElementGroupSetWord unchanged. SM4's round function operates word-at-a-time (unlike AES's
    // whole-block SubBytes/ShiftRows/MixColumns steps): each of the 4 output words is generated
    // from the 3 preceding state/key words already just computed, substituted through the S-box,
    // then run through one of SM4's two linear transforms (L for rounds, L' for key expansion).

    // SM4 S-box applied independently to each of the 4 bytes of a word, position-preserving
    // (spec Appendix C sm4_subword) — reuses the already-validated Sm4Sbox table from the scalar
    // sm4ed/sm4ks instructions (Rv32Executor.Crypto.cs).
    private static uint Sm4SubwordFull(uint x) {
        uint result = 0;
        for (var i = 0; i < 4; i++) result |= (uint)Sm4Sbox[(byte)(x >> (i * 8))] << (i * 8);
        return result;
    }

    // SM4 linear transform L (spec Appendix C sm4_round), used by vsm4r's round function.
    private static uint Sm4RoundWord(uint x, uint s) =>
        x ^ s ^ BitOperations.RotateLeft(s, 2) ^ BitOperations.RotateLeft(s, 10)
          ^ BitOperations.RotateLeft(s, 18) ^ BitOperations.RotateLeft(s, 24);

    // SM4 linear transform L' (spec Appendix C round_key), used by vsm4k's key expansion.
    private static uint Sm4KeyScheduleWord(uint x, uint s) =>
        x ^ s ^ BitOperations.RotateLeft(s, 13) ^ BitOperations.RotateLeft(s, 23);

    // SM4 system constant table CK (spec §3.25 Table 1 / Appendix C), indexed 0-31.
    private static readonly uint[] Sm4Ck = [
        0x00070E15, 0x1C232A31, 0x383F464D, 0x545B6269,
        0x70777E85, 0x8C939AA1, 0xA8AFB6BD, 0xC4CBD2D9,
        0xE0E7EEF5, 0xFC030A11, 0x181F262D, 0x343B4249,
        0x50575E65, 0x6C737A81, 0x888F969D, 0xA4ABB2B9,
        0xC0C7CED5, 0xDCE3EAF1, 0xF8FF060D, 0x141B2229,
        0x30373E45, 0x4C535A61, 0x686F767D, 0x848B9299,
        0xA0A7AEB5, 0xBCC3CAD1, 0xD8DFE6ED, 0xF4FB0209,
        0x10171E25, 0x2C333A41, 0x484F565D, 0x646B7279,
    ];

    private static ExecuteResult ExecuteSm4R(IArchState state, ulong pc, int vd, int vs2, bool scalar) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        // Reserved encoding (.vs form only): vd's LMUL register group must not overlap the
        // single vs2 scalar-element-group register (spec §3.26, "Reserved Encodings").
        if (scalar && vs2 >= vd && vs2 < vd + VGetLmulInt(state))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        byte[]? scalarKeys = scalar ? ReadElementGroup(state, vs2, 0, egw) : null;
        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] keys = scalarKeys ?? ReadElementGroup(state, vs2, i, egw);
            byte[] xstate = ReadElementGroup(state, vd, i, egw);

            uint rk0 = ElementGroupGetWord(keys, 0);
            uint rk1 = ElementGroupGetWord(keys, 1);
            uint rk2 = ElementGroupGetWord(keys, 2);
            uint rk3 = ElementGroupGetWord(keys, 3);
            uint x0 = ElementGroupGetWord(xstate, 0);
            uint x1 = ElementGroupGetWord(xstate, 1);
            uint x2 = ElementGroupGetWord(xstate, 2);
            uint x3 = ElementGroupGetWord(xstate, 3);

            uint x4 = Sm4RoundWord(x0, Sm4SubwordFull(x1 ^ x2 ^ x3 ^ rk0));
            uint x5 = Sm4RoundWord(x1, Sm4SubwordFull(x2 ^ x3 ^ x4 ^ rk1));
            uint x6 = Sm4RoundWord(x2, Sm4SubwordFull(x3 ^ x4 ^ x5 ^ rk2));
            uint x7 = Sm4RoundWord(x3, Sm4SubwordFull(x4 ^ x5 ^ x6 ^ rk3));

            var next = new byte[16];
            ElementGroupSetWord(next, 0, x4);
            ElementGroupSetWord(next, 1, x5);
            ElementGroupSetWord(next, 2, x6);
            ElementGroupSetWord(next, 3, x7);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    private static ExecuteResult ExecuteSm4K(IArchState state, ulong pc, int vd, int vs2, int uimm) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        int rnd = uimm & 0x7; // uimm[2:0]; uimm[4:3] ignored (spec §3.25)

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] currentKeys = ReadElementGroup(state, vs2, i, egw);
            uint rk0 = ElementGroupGetWord(currentKeys, 0);
            uint rk1 = ElementGroupGetWord(currentKeys, 1);
            uint rk2 = ElementGroupGetWord(currentKeys, 2);
            uint rk3 = ElementGroupGetWord(currentKeys, 3);

            uint rk4 = Sm4KeyScheduleWord(rk0, Sm4SubwordFull(rk1 ^ rk2 ^ rk3 ^ Sm4Ck[4 * rnd]));
            uint rk5 = Sm4KeyScheduleWord(rk1, Sm4SubwordFull(rk2 ^ rk3 ^ rk4 ^ Sm4Ck[4 * rnd + 1]));
            uint rk6 = Sm4KeyScheduleWord(rk2, Sm4SubwordFull(rk3 ^ rk4 ^ rk5 ^ Sm4Ck[4 * rnd + 2]));
            uint rk7 = Sm4KeyScheduleWord(rk3, Sm4SubwordFull(rk4 ^ rk5 ^ rk6 ^ Sm4Ck[4 * rnd + 3]));

            var next = new byte[16];
            ElementGroupSetWord(next, 0, rk4);
            ElementGroupSetWord(next, 1, rk5);
            ElementGroupSetWord(next, 2, rk6);
            ElementGroupSetWord(next, 3, rk7);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // ── Zvknha/Zvknhb: SHA-2 compression (vsha2c[hl].vv) and message schedule (vsha2ms.vv) ──────
    // EGS=4 always; EGW=4*SEW, with SEW=32 (SHA-256) or SEW=64 (SHA-512) both accepted here — this
    // codebase does not gate Zvknha vs Zvknhb as separate hart extensions, so it implements the
    // Zvknhb superset unconditionally rather than restricting SEW=64 behind a second flag. Word
    // extraction is a *third* distinct convention alongside AesMixColumnsFwdBlock's and
    // ElementGroupGetWord's (see those two helpers' doc comments): confirmed against the RISC-V
    // Sail reference model (github.com/riscv/sail-riscv,
    // model/extensions/vector_crypto/zvknhab_insts.sail), which reads/writes each SEW-bit register
    // element directly via read_vreg/write_velem_quad with no extra byte-level repacking — i.e.
    // the same natural little-endian element interpretation this codebase already uses for every
    // non-crypto vector op (see Rv32Executor.V.cs's BitConverter.ToUInt64 element read). AES/SM4's
    // big-endian ElementGroupGetWord is the special case that needed discovering by KAT; this one
    // is just the codebase's default convention, used here because the Sail model confirms it
    // (not because it was assumed).

    private static ulong Sha2Mask(int sewBits) => sewBits == 64 ? ulong.MaxValue : (1UL << sewBits) - 1;

    private static ulong Sha2GetWord(byte[] group, int wordIndex, int sewBits) {
        int wordBytes = sewBits / 8;
        int o = wordIndex * wordBytes;
        ulong v = 0;
        for (var b = 0; b < wordBytes; b++) v |= (ulong)group[o + b] << (8 * b);
        return v;
    }

    private static void Sha2SetWord(byte[] group, int wordIndex, int sewBits, ulong value) {
        int wordBytes = sewBits / 8;
        int o = wordIndex * wordBytes;
        for (var b = 0; b < wordBytes; b++) group[o + b] = (byte)(value >> (8 * b));
    }

    private static ulong Sha2RotR(ulong x, int n, int sewBits) => ((x >> n) | (x << (sewBits - n))) & Sha2Mask(sewBits);

    // spec §3.22 sig0/sig1 (message schedule) and §3.21 sum0/sum1 (compression); rotate/shift
    // amounts differ by SEW (FIPS 180-4 §4.1.2 SHA-256 vs §4.1.3 SHA-512 lowercase-sigma/Sigma).
    private static ulong Sha2Sig0(ulong x, int sewBits) => sewBits == 32
        ? Sha2RotR(x, 7, 32) ^ Sha2RotR(x, 18, 32) ^ (x >> 3)
        : Sha2RotR(x, 1, 64) ^ Sha2RotR(x, 8, 64) ^ (x >> 7);

    private static ulong Sha2Sig1(ulong x, int sewBits) => sewBits == 32
        ? Sha2RotR(x, 17, 32) ^ Sha2RotR(x, 19, 32) ^ (x >> 10)
        : Sha2RotR(x, 19, 64) ^ Sha2RotR(x, 61, 64) ^ (x >> 6);

    private static ulong Sha2Sum0(ulong x, int sewBits) => sewBits == 32
        ? Sha2RotR(x, 2, 32) ^ Sha2RotR(x, 13, 32) ^ Sha2RotR(x, 22, 32)
        : Sha2RotR(x, 28, 64) ^ Sha2RotR(x, 34, 64) ^ Sha2RotR(x, 39, 64);

    private static ulong Sha2Sum1(ulong x, int sewBits) => sewBits == 32
        ? Sha2RotR(x, 6, 32) ^ Sha2RotR(x, 11, 32) ^ Sha2RotR(x, 25, 32)
        : Sha2RotR(x, 14, 64) ^ Sha2RotR(x, 18, 64) ^ Sha2RotR(x, 41, 64);

    private static ulong Sha2Ch(ulong x, ulong y, ulong z, int sewBits) => (x & y) ^ (~x & z & Sha2Mask(sewBits));
    private static ulong Sha2Maj(ulong x, ulong y, ulong z) => (x & y) ^ (x & z) ^ (y & z);

    private static bool VGroupOverlap(int r1, int r2, int lmul) => r1 < r2 + lmul && r2 < r1 + lmul;

    // Element-group constraint check for vsha2*: unlike every other Zvk* op (fixed EGW=128), here
    // EGW=4*SEW is itself runtime-dependent, so this can't reuse CheckElementGroupConstraints
    // (which takes a single required-SEW compile-time constant).
    private static ExecuteResult? CheckSha2Constraints(IArchState state, ulong pc, out int sewBits, out int egw) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        sewBits = ewBytes * 8;
        egw = 4 * sewBits;

        if (sewBits != 32 && sewBits != 64)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        if (VGetLmulInt(state) * VectorRegisterFile.VLen < egw)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        if (vl % 4 != 0 || vstart % 4 != 0)
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        return null;
    }

    // One round of SHA-2 compression (FIPS 180-4 §6.2.2/§6.4.2), parameterized by SEW so the same
    // code serves SHA-256 (32-bit words) and SHA-512 (64-bit words).
    private static (ulong A, ulong B, ulong C, ulong D, ulong E, ulong F, ulong G, ulong H) Sha2Round(
        ulong a, ulong b, ulong c, ulong d, ulong e, ulong f, ulong g, ulong h, ulong w, int sewBits
    ) {
        ulong mask = Sha2Mask(sewBits);
        ulong t1 = (h + Sha2Sum1(e, sewBits) + Sha2Ch(e, f, g, sewBits) + w) & mask;
        ulong t2 = (Sha2Sum0(a, sewBits) + Sha2Maj(a, b, c)) & mask;
        return ((t1 + t2) & mask, a, b, c, (d + t1) & mask, e, f, g);
    }

    // vsha2ch.vv/vsha2cl.vv: two rounds of SHA-2 compression (spec §3.21). vs2 holds {a,b,e,f} at
    // element indices 3,2,1,0; vd holds {c,d,g,h} at indices 3,2,1,0 (read as input) and is
    // overwritten with the *new* {a,b,e,f} — vs2's register is left untouched, which is what makes
    // ping-ponging the two registers across successive calls correct (see ZvkTests' RunSha2).
    private static ExecuteResult ExecuteSha2Compress(
        IArchState state, ulong pc, Sha2CompressKind kind, int vd, int vs1, int vs2
    ) {
        ExecuteResult? trap = CheckSha2Constraints(state, pc, out int sewBits, out int egw);
        if (trap != null) return trap;

        int lmul = VGetLmulInt(state);
        if (VGroupOverlap(vd, vs1, lmul) || VGroupOverlap(vd, vs2, lmul))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / 4);
        var egLen = (int)(vl / 4);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] hiState = ReadElementGroup(state, vs2, i, egw);
            byte[] loState = ReadElementGroup(state, vd, i, egw);
            byte[] msgPlusC = ReadElementGroup(state, vs1, i, egw);

            ulong a = Sha2GetWord(hiState, 3, sewBits);
            ulong b = Sha2GetWord(hiState, 2, sewBits);
            ulong e = Sha2GetWord(hiState, 1, sewBits);
            ulong f = Sha2GetWord(hiState, 0, sewBits);
            ulong c = Sha2GetWord(loState, 3, sewBits);
            ulong d = Sha2GetWord(loState, 2, sewBits);
            ulong g = Sha2GetWord(loState, 1, sewBits);
            ulong h = Sha2GetWord(loState, 0, sewBits);

            // vsha2ch uses the two most-significant MessageSchedPlusC words (idx3,2); vsha2cl the
            // two least-significant (idx1,0) — otherwise identical (spec §3.21).
            ulong w0 = Sha2GetWord(msgPlusC, kind == Sha2CompressKind.Low ? 0 : 2, sewBits);
            ulong w1 = Sha2GetWord(msgPlusC, kind == Sha2CompressKind.Low ? 1 : 3, sewBits);

            (a, b, c, d, e, f, g, h) = Sha2Round(a, b, c, d, e, f, g, h, w0, sewBits);
            (a, b, c, d, e, f, g, h) = Sha2Round(a, b, c, d, e, f, g, h, w1, sewBits);

            var next = new byte[egw / 8];
            Sha2SetWord(next, 3, sewBits, a);
            Sha2SetWord(next, 2, sewBits, b);
            Sha2SetWord(next, 1, sewBits, e);
            Sha2SetWord(next, 0, sewBits, f);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }

    // vsha2ms.vv: four rounds of SHA-2 message-schedule expansion (spec §3.22). vd holds the
    // oldest 4 schedule words {W3,W2,W1,W0} (read as input, overwritten with {W19,W18,W17,W16});
    // vs2 holds {W11,W10,W9,W4}; vs1 holds {W15,W14,-,W12} (the idx1 slot, W13, is never read).
    private static ExecuteResult ExecuteSha2Ms(IArchState state, ulong pc, int vd, int vs1, int vs2) {
        ExecuteResult? trap = CheckSha2Constraints(state, pc, out int sewBits, out int egw);
        if (trap != null) return trap;

        int lmul = VGetLmulInt(state);
        if (VGroupOverlap(vd, vs1, lmul) || VGroupOverlap(vd, vs2, lmul))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / 4);
        var egLen = (int)(vl / 4);
        ulong mask = Sha2Mask(sewBits);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] oldWords = ReadElementGroup(state, vd, i, egw);
            byte[] midWords = ReadElementGroup(state, vs2, i, egw);
            byte[] newWords = ReadElementGroup(state, vs1, i, egw);

            ulong w0 = Sha2GetWord(oldWords, 0, sewBits);
            ulong w1 = Sha2GetWord(oldWords, 1, sewBits);
            ulong w2 = Sha2GetWord(oldWords, 2, sewBits);
            ulong w3 = Sha2GetWord(oldWords, 3, sewBits);
            ulong w4 = Sha2GetWord(midWords, 0, sewBits);
            ulong w9 = Sha2GetWord(midWords, 1, sewBits);
            ulong w10 = Sha2GetWord(midWords, 2, sewBits);
            ulong w11 = Sha2GetWord(midWords, 3, sewBits);
            ulong w12 = Sha2GetWord(newWords, 0, sewBits);
            ulong w14 = Sha2GetWord(newWords, 2, sewBits);
            ulong w15 = Sha2GetWord(newWords, 3, sewBits);

            ulong w16 = (Sha2Sig1(w14, sewBits) + w9 + Sha2Sig0(w1, sewBits) + w0) & mask;
            ulong w17 = (Sha2Sig1(w15, sewBits) + w10 + Sha2Sig0(w2, sewBits) + w1) & mask;
            ulong w18 = (Sha2Sig1(w16, sewBits) + w11 + Sha2Sig0(w3, sewBits) + w2) & mask;
            ulong w19 = (Sha2Sig1(w17, sewBits) + w12 + Sha2Sig0(w4, sewBits) + w3) & mask;

            var next = new byte[egw / 8];
            Sha2SetWord(next, 0, sewBits, w16);
            Sha2SetWord(next, 1, sewBits, w17);
            Sha2SetWord(next, 2, sewBits, w18);
            Sha2SetWord(next, 3, sewBits, w19);
            groups[i - egStart] = (i, next);
        }

        return new ExecuteResult {
            SideEffect = s => {
                var s32 = (Rv32ArchState)s;
                foreach ((int index, byte[] value) in groups) WriteElementGroup(s32, vd, index, egw, value);
            },
        };
    }
}