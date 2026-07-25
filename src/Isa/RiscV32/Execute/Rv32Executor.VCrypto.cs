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

    // ── Zvkned: vaeskf1.vi / vaeskf2.vi (AES-128/256 forward key schedule, spec §3.5/§3.6) ─────
    // Both generate one 128-bit round-key element group, word by word, from the previous round
    // key word and a round-number-selected function of the current round key's last word.
    // Packed-word convention matches AesMixColumnsFwdBlock/AesRcon: word j of a 16-byte element
    // group is bytes[4j..4j+3] with bytes[4j] as the packed uint's low byte (LSB) — the same
    // "aes_get_column"-style layout the spec's own Sail helpers use, already proven consistent
    // with AesRcon/AesSubwordFwd by the RV32/RV64 scalar key-schedule instructions.

    private static uint AesGetWord(byte[] block16, int wordIndex) {
        int o = wordIndex * 4;
        return (uint)(block16[o] | (block16[o + 1] << 8) | (block16[o + 2] << 16) | (block16[o + 3] << 24));
    }

    private static void AesSetWord(byte[] block16, int wordIndex, uint value) {
        int o = wordIndex * 4;
        block16[o] = (byte)value;
        block16[o + 1] = (byte)(value >> 8);
        block16[o + 2] = (byte)(value >> 16);
        block16[o + 3] = (byte)(value >> 24);
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
            uint w0 = AesSubwordFwd(BitOperations.RotateRight(AesGetWord(currentRoundKey, 3), 8))
                    ^ AesRcon[rconIndex] ^ AesGetWord(currentRoundKey, 0);
            uint w1 = w0 ^ AesGetWord(currentRoundKey, 1);
            uint w2 = w1 ^ AesGetWord(currentRoundKey, 2);
            uint w3 = w2 ^ AesGetWord(currentRoundKey, 3);
            var next = new byte[16];
            AesSetWord(next, 0, w0);
            AesSetWord(next, 1, w1);
            AesSetWord(next, 2, w2);
            AesSetWord(next, 3, w3);
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
            uint w3Current = AesGetWord(currentRoundKey, 3);
            uint w0 = (rnd & 1) == 1
                ? AesSubwordFwd(w3Current) ^ AesGetWord(previousRoundKey, 0)
                : AesSubwordFwd(BitOperations.RotateRight(w3Current, 8)) ^ AesRcon[(rnd >> 1) - 1]
                ^ AesGetWord(previousRoundKey, 0);
            uint w1 = w0 ^ AesGetWord(previousRoundKey, 1);
            uint w2 = w1 ^ AesGetWord(previousRoundKey, 2);
            uint w3 = w2 ^ AesGetWord(previousRoundKey, 3);
            var next = new byte[16];
            AesSetWord(next, 0, w0);
            AesSetWord(next, 1, w1);
            AesSetWord(next, 2, w2);
            AesSetWord(next, 3, w3);
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
}