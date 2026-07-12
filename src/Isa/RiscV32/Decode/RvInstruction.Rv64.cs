using Mechanism;

namespace RiscV32.Decode;

// ── RV64I W-suffix instructions (opcode=0x3B: OP-32; opcode=0x1B: OP-IMM-32) ──────────────
// Each performs the operation on the lower 32 bits and sign-extends the 32-bit result to 64.
public record RvAddw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSubw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSllw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSrlw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSraw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAddiw(int Rd, int Rs1, int Imm) : RvOp;

public record RvSlliw(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrliw(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSraiw(int Rd, int Rs1, int Shamt) : RvOp;

// ── RV64I new load/store variants ───────────────────────────────────────────────────────────
public record RvLwu(int Rd, int Rs1, int Imm) : RvOp; // load word unsigned — zero-extend to 64 bits

public record RvLd(int Rd, int Rs1, int Imm) : RvOp; // load doubleword

public record RvSd(int Rs1, int Rs2, int Imm) : RvOp; // store doubleword

// ── RV64M W-suffix instructions (opcode=0x3B, funct7=0x01) ───────────────────────────────────
// Each operates on the lower 32 bits of both operands and sign-extends the 32-bit result to 64.
public record RvMulw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDivw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDivuw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRemw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRemuw(int Rd, int Rs1, int Rs2) : RvOp;

// ── RV64F/D: 64-bit integer conversions and moves (opcode=0x53) ──────────────────────────────
public record RvFcvtLs(int Rd, int Rs1, int Rm) : RvOp; // float→signed int64

public record RvFcvtLuS(int Rd, int Rs1, int Rm) : RvOp; // float→unsigned int64

public record RvFcvtSl(int Rd, int Rs1, int Rm) : RvOp; // signed int64→float

public record RvFcvtSLu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int64→float

public record RvFcvtLd(int Rd, int Rs1, int Rm) : RvOp; // double→signed int64

public record RvFcvtLuD(int Rd, int Rs1, int Rm) : RvOp; // double→unsigned int64

public record RvFcvtDl(int Rd, int Rs1, int Rm) : RvOp; // signed int64→double

public record RvFcvtDLu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int64→double

public record RvFmvXd(int Rd, int Rs1) : RvOp; // double bits→int reg (full 64 bits)

public record RvFmvDx(int Rd, int Rs1) : RvOp; // int reg bits→double reg

// ── RV64 Zfh: 64-bit integer conversions (opcode=0x53) ────────────────────────
public record RvFcvtLh(int Rd, int Rs1, int Rm) : RvOp; // half→signed int64

public record RvFcvtLuH(int Rd, int Rs1, int Rm) : RvOp; // half→unsigned int64

public record RvFcvtHl(int Rd, int Rs1, int Rm) : RvOp; // signed int64→half

public record RvFcvtHLu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int64→half

// ── RV64A doubleword atomics (opcode=0x2F, funct3=0x3) ────────────────────────────────────────
public record RvLrD(int Rd, int Rs1) : RvOp;

public record RvScD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoswapD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuD(int Rd, int Rs1, int Rs2) : RvOp;

// ── RV64-only Zba ops: operate on the zero-extended low 32 bits of rs1 (opcode=0x3B/0x1B) ─────
public record RvAdduw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh1AddUw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh2AddUw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh3AddUw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSlliUw(int Rd, int Rs1, int Sh) : RvOp;

// ── RV64-only Zbb W-suffix ops: operate on the lower 32 bits, result is a small
// non-negative count (CLZW/CTZW/CPOPW, max 32) or a rotated 32-bit value sign-extended
// to 64 (ROLW/RORW/RORIW) — opcode=0x3B (OP-32) / 0x1B (OP-IMM-32).
public record RvRolw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRorw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRoriw(int Rd, int Rs1, int Shamt) : RvOp;

public record RvClzw(int Rd, int Rs1) : RvOp;

public record RvCtzw(int Rd, int Rs1) : RvOp;

public record RvCpopw(int Rd, int Rs1) : RvOp;