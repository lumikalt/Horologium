namespace RiscV32.Decode;

// RISC-V Cryptography Extensions Volume I: Scalar & Entropy Source Instructions, v1.0.1.
// RV64-only forms — disjoint encodings from the RV32 aes32*/sha512sig*h/l/sum*r forms in
// RvInstruction.Crypto.cs (both widths implement the same Zknd/Zkne/Zknh extensions, but RV32 and
// RV64 each get a dedicated instruction set suited to their register width).

// ── Zknd/Zkne extension (NIST AES, RV64 full-register-pair form) ─────────────
// R-type (opcode=0x33, funct3=0, fixed funct7 — no bs field; both source registers together
// represent the full 128-bit AES state, so each instruction produces half of the next round).
public record RvAes64Ds(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x1D

public record RvAes64Dsm(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x1F

public record RvAes64Es(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x19

public record RvAes64Esm(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x1B

public record RvAes64Ks2(int Rd, int Rs1, int Rs2) : RvOp; // funct7=0x3F

// I-type unary (opcode=0x13, funct3=1, funct7=0x18, shamt=0)
public record RvAes64Im(int Rd, int Rs1) : RvOp;

// I-type with a 4-bit round-number immediate (opcode=0x13, funct3=1, funct7=0x18 — same funct7
// as aes64im — shamt=0x10|rnum distinguishes it); rnum must be 0x0-0xA, else the executor traps.
public record RvAes64Ks1I(int Rd, int Rs1, int Rnum) : RvOp;

// ── Zknh extension (NIST SHA2, RV64 direct 64-bit form) ───────────────────────
// I-type unary (opcode=0x13, funct3=1, funct7=0x08, shamt selects sub-op: sum0=4,sum1=5,sig0=6,sig1=7)
public record RvSha512Sig0(int Rd, int Rs1) : RvOp;

public record RvSha512Sig1(int Rd, int Rs1) : RvOp;

public record RvSha512Sum0(int Rd, int Rs1) : RvOp;

public record RvSha512Sum1(int Rd, int Rs1) : RvOp;