#region

using Mechanism;
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

    // ── Zvkned: AES round instructions (vaesem.vv/vaesef.vv) ───────────────────────────────────
    // EGW=128, EGS=4, EEW=SEW=32 (spec §2.5/§3.3/§3.4). Shared shape across all vaes*.vv round
    // instructions: read the current-state and round-key element groups at the same index
    // (the ".vv" form — round key comes from the matching vs2 group, unlike ".vs" which reuses a
    // single scalar element group for every state group; deferred), apply the
    // instruction-specific round transform, XOR in the round key, write back.

    private static ExecuteResult ExecuteVAesRoundVv(
        IArchState state,
        ulong pc,
        int vd,
        int vs2,
        Func<byte[], byte[]> roundTransform
    ) {
        const int egw = 128;
        const int egs = 4;
        const int requiredSew = 32;

        ExecuteResult? trap = CheckElementGroupConstraints(state, pc, egw, egs, requiredSew);
        if (trap != null) return trap;

        (uint vl, _) = VGetVlEw(state);
        uint vstart = VState(state).CsrFile.DirectRead(CsrFile.Vstart);
        var egStart = (int)(vstart / egs);
        var egLen = (int)(vl / egs);

        var groups = new (int Index, byte[] Value)[egLen - egStart];
        for (int i = egStart; i < egLen; i++) {
            byte[] roundState = ReadElementGroup(state, vd, i, egw);
            byte[] roundKey = ReadElementGroup(state, vs2, i, egw);
            byte[] transformed = roundTransform(roundState);
            var afterKey = new byte[16];
            for (var b = 0; b < 16; b++) afterKey[b] = (byte)(transformed[b] ^ roundKey[b]);
            groups[i - egStart] = (i, afterKey);
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

    private static byte[] AesShiftRowsFwdBlock(byte[] state) {
        var result = new byte[16];
        for (var i = 0; i < 16; i++) {
            int r = i % 4, c = i / 4;
            result[i] = state[4 * ((c + r) % 4) + r];
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
}