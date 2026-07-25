namespace RiscV32.Decode;

// ── Zvkned extension (Vector AES Block Cipher, RISC-V Cryptography Extensions Volume II) ──────
// Dedicated major opcode 0x77 (funct3=2), NOT the standard OP-V opcode 0x57 despite the field
// layout otherwise matching OPMVV — see DecodeVCryptoOp for the full opcode-space breakdown.
// EGW=128, EGS=4, EEW=SEW=32 — each element group is one full 128-bit AES block, and (since
// VLEN=128 in this codebase) always occupies exactly one physical vector register regardless of
// LMUL.

public enum VAesRoundKind { DecryptMiddle, DecryptFinal, EncryptMiddle, EncryptFinal }

// vaes{d,e}{m,f}.vv: vd is both the round-state source and the new-round-state destination; the
// round key comes from the element group in vs2 matching vd's group index.
public record RvVaesRoundVv(VAesRoundKind Kind, int Vd, int Vs2) : RvOp;

// vaes{d,e}{m,f}.vs: same round transform as the .vv form, but vs2 is a single scalar element
// group (register vs2 itself, index 0 always) broadcast as the round key for every vd group.
// Reserved encoding: vd's LMUL register group must not overlap vs2.
public record RvVaesRoundVs(VAesRoundKind Kind, int Vd, int Vs2) : RvOp;

// vaesz.vs: round-0 AES cipher op (used for the initial AddRoundKey, both directions) — XORs the
// scalar element group in vs2 into every element group of vd. Reserved encoding: vd's LMUL
// register group must not overlap vs2.
public record RvVaesZVs(int Vd, int Vs2) : RvOp;

// vaeskf1.vi: one round of the AES-128 forward key schedule. vs2 holds the current round key,
// vd receives the next round key (pure output). Round holds the raw uimm[4:0] (bit 4 ignored,
// out-of-range uimm[3:0] projected onto a valid round per spec §3.5).
public record RvVaesKf1Vi(int Vd, int Vs2, int Round) : RvOp;

// vaeskf2.vi: one round of the AES-256 forward key schedule. vs2 holds the current round key
// (one element group back), vd holds the previous round key (two element groups back) as input
// and receives the next round key as output. Round holds the raw uimm[4:0] (§3.6 projection).
public record RvVaesKf2Vi(int Vd, int Vs2, int Round) : RvOp;
