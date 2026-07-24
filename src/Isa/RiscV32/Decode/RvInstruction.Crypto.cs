namespace RiscV32.Decode;

// RISC-V Cryptography Extensions Volume I: Scalar & Entropy Source Instructions, v1.0.1.
// RV32 scope only; the RV64-only aes64*/sha512sig0/sig1/sum0/sum1 forms are a follow-up (see TODO.md).

// ── Zknd extension (NIST AES decryption, RV32) ────────────────────────────────
// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b10101}): aes32dsi rd, rs1, rs2, bs
public record RvAes32Dsi(int Rd, int Rs1, int Rs2, int Bs) : RvOp;

// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b10111}): aes32dsmi rd, rs1, rs2, bs
public record RvAes32Dsmi(int Rd, int Rs1, int Rs2, int Bs) : RvOp;

// ── Zkne extension (NIST AES encryption, RV32) ────────────────────────────────
// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b10001}): aes32esi rd, rs1, rs2, bs
public record RvAes32Esi(int Rd, int Rs1, int Rs2, int Bs) : RvOp;

// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b10011}): aes32esmi rd, rs1, rs2, bs
public record RvAes32Esmi(int Rd, int Rs1, int Rs2, int Bs) : RvOp;

// ── Zknh extension (NIST SHA2 hash function instructions) ────────────────────
// I-type unary (opcode=0x13, funct3=1, funct7=0x08, shamt-field selects sub-op)
public record RvSha256Sig0(int Rd, int Rs1) : RvOp; // shamt=2

public record RvSha256Sig1(int Rd, int Rs1) : RvOp; // shamt=3

public record RvSha256Sum0(int Rd, int Rs1) : RvOp; // shamt=0

public record RvSha256Sum1(int Rd, int Rs1) : RvOp; // shamt=1

// R-type (opcode=0x33, funct3=0): SHA2-512 on RV32, 64-bit value split across two 32-bit registers.
// Note the funct7-encoded reversed source-register convention documented per-instruction in the spec.
public record RvSha512Sig0H(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x2E

public record RvSha512Sig0L(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x2A

public record RvSha512Sig1H(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x2F

public record RvSha512Sig1L(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x2B

public record RvSha512Sum0R(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x28

public record RvSha512Sum1R(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x29

// ── Zksh extension (ShangMi SM3 hash function instructions) ──────────────────
// I-type unary (opcode=0x13, funct3=1, funct7=0x08, shamt-field selects sub-op)
public record RvSm3P0(int Rd, int Rs1) : RvOp; // shamt=8

public record RvSm3P1(int Rd, int Rs1) : RvOp; // shamt=9

// ── Zksed extension (ShangMi SM4 block cipher instructions) ──────────────────
// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b11000}): sm4ed rd, rs1, rs2, bs
public record RvSm4Ed(int Rd, int Rs1, int Rs2, int Bs) : RvOp;

// R-type (opcode=0x33, funct3=0, funct7={bs[1:0],5'b11010}): sm4ks rd, rs1, rs2, bs
public record RvSm4Ks(int Rd, int Rs1, int Rs2, int Bs) : RvOp;
