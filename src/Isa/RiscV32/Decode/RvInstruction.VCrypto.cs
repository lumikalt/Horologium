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

// ── Zvksed extension (Vector SM4 Block Cipher) — same opcode space/EGW=128/EGS=4/EEW=32 shape ──

// vsm4r.vv: four rounds of SM4 encryption/decryption (identical operation either way — only the
// round-key order supplied by software differs). vd is both the current-state source and the
// next-state destination; the round keys come from the element group in vs2 matching vd's index.
public record RvSm4RVv(int Vd, int Vs2) : RvOp;

// vsm4r.vs: same round transform as the .vv form, but vs2 is a single scalar element group
// broadcast as the round keys for every vd group. Reserved encoding: vd's LMUL register group
// must not overlap vs2.
public record RvSm4RVs(int Vd, int Vs2) : RvOp;

// vsm4k.vi: four rounds of the SM4 key expansion, generating round keys rK[4*rnd..4*rnd+3] from
// vs2's rK[0:3] (pure output to vd). Round holds the raw uimm[4:0] (bits[4:3] ignored per §3.25).
public record RvSm4KVi(int Vd, int Vs2, int Round) : RvOp;

// ── Zvknha/Zvknhb extension (Vector SHA-2 compression + message schedule) — same opcode space,
// but EGW=4*SEW is runtime-dependent (128 for SEW=32/SHA-256, 256 for SEW=64/SHA-512, Zvknhb
// only) rather than the fixed 128 every other Zvk* op so far uses. Also the first Zvk* op where
// vs1 is a genuine third vector source register — funct6 alone selects the operation, vs1 is
// never repurposed as a sub-op selector or immediate here.

public enum Sha2CompressKind { High, Low }

// vsha2ch.vv/vsha2cl.vv: two rounds of SHA-2 compression (spec §3.21). vs2 holds working-state
// words {a,b,e,f}, vd holds {c,d,g,h} — both read as input and vd overwritten with the new
// {a,b,e,f}. vs1 holds four message-schedule-plus-round-constant words; High consumes the two
// most-significant, Low the two least-significant (otherwise identical). Reserved: vd's LMUL
// register group must not overlap vs1's or vs2's.
public record RvSha2CVv(Sha2CompressKind Kind, int Vd, int Vs1, int Vs2) : RvOp;

// vsha2ms.vv: four rounds of SHA-2 message-schedule expansion (spec §3.22). vd holds the oldest
// 4 schedule words (read as input, overwritten with the next 4 produced); vs2/vs1 hold the
// intervening words. Reserved: vd's LMUL register group must not overlap vs1's or vs2's.
public record RvSha2MsVv(int Vd, int Vs1, int Vs2) : RvOp;
