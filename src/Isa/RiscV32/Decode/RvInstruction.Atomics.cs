using Mechanism;

namespace RiscV32.Decode;

// ── A extension (atomics) ─────────────────────────────────────────────────────
public record RvLrW(int Rd, int Rs1) : RvOp;

public record RvScW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoswapW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuW(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zacas extension (compare-and-swap) ───────────────────────────────────────
// rd is both comparand (source) and destination for the old value.
public record RvAmocasW(int Rd, int Rs1, int Rs2) : RvOp;

// Zabha+Zacas: narrow (byte/halfword) compare-and-swap. Same rd-is-source-and-dest shape as
// amocas.w, just at 1/2-byte width.
public record RvAmocasB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmocasH(int Rd, int Rs1, int Rs2) : RvOp;

// Zacas doubleword compare-and-swap. RV64 decodes this as a native single-register 64-bit
// CAS (see Rv64Decoder/Rv64Executor). RV32 has no 64-bit register to hold the compare/swap
// values, so amocas.d there is not yet decoded — see TODO.md.
public record RvAmocasD(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zabha extension (byte/halfword atomics) ───────────────────────────────────
public record RvAmoswapB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoswapH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuH(int Rd, int Rs1, int Rs2) : RvOp;