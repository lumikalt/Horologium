using Mechanism;

namespace RiscV32.Decode;

// ── Zbc extension (carry-less multiplication) ─────────────────────────────────
// R-type (opcode=0x33, funct7=0x05, funct3=1/2/3)
public record RvClmul(int Rd, int Rs1, int Rs2) : RvOp; // lower 32 bits of carry-less product

public record RvClmulh(int Rd, int Rs1, int Rs2) : RvOp; // upper 32 bits

public record RvClmulr(int Rd, int Rs1, int Rs2) : RvOp; // bits [62:31]

// ── Zba extension (address generation) ───────────────────────────────────────
// R-type (opcode=0x33, funct7=0x10): rd = rs2 + (rs1 << N)
public record RvSh1Add(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh2Add(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh3Add(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zbs extension (single-bit ops) ────────────────────────────────────────────
// R-type (opcode=0x33): register-indexed single-bit operations
public record RvBclr(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 & ~(1 << (rs2 & 31))

public record RvBext(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs1 >> (rs2 & 31)) & 1

public record RvBinv(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 ^ (1 << (rs2 & 31))

public record RvBset(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 | (1 << (rs2 & 31))

// I-type (opcode=0x13): immediate-indexed single-bit operations
public record RvBclri(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBexti(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBinvi(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBseti(int Rd, int Rs1, int Shamt) : RvOp;

// ── Zicond extension (integer conditional operations) ─────────────────────────
// R-type (opcode=0x33, funct7=0x07)
public record RvCzeroEqz(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs2 == 0) ? 0 : rs1

public record RvCzeroNez(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs2 != 0) ? 0 : rs1

// ── Zbb extension (basic bit manipulation) ────────────────────────────────────
// R-type (opcode=0x33)
public record RvAndn(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 & ~rs2

public record RvOrn(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 | ~rs2

public record RvXnor(int Rd, int Rs1, int Rs2) : RvOp; // rd = ~(rs1 ^ rs2)

public record RvMax(int Rd, int Rs1, int Rs2) : RvOp; // signed maximum

public record RvMaxu(int Rd, int Rs1, int Rs2) : RvOp; // unsigned maximum

public record RvMin(int Rd, int Rs1, int Rs2) : RvOp; // signed minimum

public record RvMinu(int Rd, int Rs1, int Rs2) : RvOp; // unsigned minimum

public record RvRol(int Rd, int Rs1, int Rs2) : RvOp; // rotate left

public record RvRor(int Rd, int Rs1, int Rs2) : RvOp; // rotate right

public record RvZextH(int Rd, int Rs1) : RvOp; // zero-extend halfword

// I-type unary ops (opcode=0x13, funct3=1, funct7=0x30)
public record RvClz(int Rd, int Rs1) : RvOp; // count leading zeros

public record RvCtz(int Rd, int Rs1) : RvOp; // count trailing zeros

public record RvCpop(int Rd, int Rs1) : RvOp; // population count

public record RvSextB(int Rd, int Rs1) : RvOp; // sign-extend byte

public record RvSextH(int Rd, int Rs1) : RvOp; // sign-extend halfword

// I-type shift-space ops (opcode=0x13, funct3=5)
public record RvRori(int Rd, int Rs1, int Shamt) : RvOp; // rotate right immediate

public record RvOrcB(int Rd, int Rs1) : RvOp; // OR-combine bytes (0 → 0x00, nonzero → 0xFF per byte)

public record RvRev8(int Rd, int Rs1) : RvOp; // byte-reverse

// ── Zawrs extension (wait-on-reservation-set) ─────────────────────────────────
// SYSTEM space (opcode=0x73, funct3=0): NOP in single-core simulation.
public record RvWrsNto : RvOp; // wrs.nto (imm=0x00D): wait for reservation set, no timeout

public record RvWrsSto : RvOp; // wrs.sto (imm=0x01D): wait for reservation set, short timeout

// ── Zicbom extension (cache block management) ─────────────────────────────────
// opcode=0x0F, funct3=2, bits[24:20] selects operation; rs1 = base address.
// NOP in simulation (no cache coherence model).
public record RvCboInval(int Rs1) : RvOp; // cbo.inval (bits[24:20]=0x00): invalidate cache block

public record RvCboClean(int Rs1) : RvOp; // cbo.clean (bits[24:20]=0x01): clean cache block

public record RvCboFlush(int Rs1) : RvOp; // cbo.flush (bits[24:20]=0x02): flush cache block

// ── Zicboz extension (cache block zero) ───────────────────────────────────────
// opcode=0x0F, funct3=2, bits[24:20]=0x04; zeros 64 bytes at cache-line-aligned address.
public record RvCboZero(int Rs1) : RvOp;

// ── Zimop extension (may-be-operations) ───────────────────────────────────────
// opcode=0x73, funct3=4; always return 0 in rd (reserved NOP encodings).
public record RvMopR(int Rd) : RvOp; // mop.r.N: read-only may-be-op

public record RvMopRr(int Rd) : RvOp; // mop.rr.N: register-register may-be-op

// ── Zcmop extension (compressed may-be-operations) ────────────────────────────
// Q1/funct3=3, nzimm=0, rd=odd 1..15; 8 variants: c.mop.N, N ∈ {1,3,5,...,15}.
// Pattern: (c & 0xF8FF) == 0x6081; N = 2*(bits[10:8])+1.
// All are hint NOPs with no architectural effect.
public record RvCMopN(int N) : RvOp; // c.mop.N (N odd, 1..15)

