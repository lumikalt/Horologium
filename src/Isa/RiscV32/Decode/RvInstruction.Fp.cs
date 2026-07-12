using Mechanism;

namespace RiscV32.Decode;

// ── F extension (single-precision float) ─────────────────────────────────────
// Register indices in all F records are unified: 0-31 = int, 32-63 = float.

public record RvFlw(int Rd, int Rs1, int Imm) : RvOp; // Rd=fp, Rs1=int

public record RvFsw(int Rs1, int Rs2, int Imm) : RvOp; // Rs1=int base, Rs2=fp data

public record RvFaddS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsubS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmulS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFdivS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsqrtS(int Rd, int Rs1) : RvOp;

public record RvFsgnjS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjnS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjxS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFminS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmaxS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFeqS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFltS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFleS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFclassS(int Rd, int Rs1) : RvOp; // Rd=int result

public record RvFcvtWs(int Rd, int Rs1, int Rm) : RvOp; // float→signed int

public record RvFcvtWuS(int Rd, int Rs1, int Rm) : RvOp; // float→unsigned int

public record RvFcvtSw(int Rd, int Rs1, int Rm) : RvOp; // signed int→float

public record RvFcvtSWu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int→float

public record RvFmvXw(int Rd, int Rs1) : RvOp; // fp bits→int reg

public record RvFmvWx(int Rd, int Rs1) : RvOp; // int bits→fp reg

public record RvFmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

// ── D extension ───────────────────────────────────────────────────────────────
// Register indices: 0-31 = int, 32-63 = fp (64-bit NaN-boxed when .S)

public record RvFld(int Rd, int Rs1, int Imm) : RvOp; // Rd=fp64, Rs1=int

public record RvFsd(int Rs1, int Rs2, int Imm) : RvOp; // Rs1=int base, Rs2=fp64 data

public record RvFaddD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsubD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmulD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFdivD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsqrtD(int Rd, int Rs1) : RvOp;

public record RvFsgnjD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjnD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjxD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFminD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmaxD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFeqD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFltD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFleD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFclassD(int Rd, int Rs1) : RvOp; // Rd=int result

public record RvFcvtWd(int Rd, int Rs1, int Rm) : RvOp; // double→signed int32

public record RvFcvtWuD(int Rd, int Rs1, int Rm) : RvOp; // double→unsigned int32

public record RvFcvtDw(int Rd, int Rs1, int Rm) : RvOp; // signed int32→double

public record RvFcvtDWu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int32→double

public record RvFcvtSd(int Rd, int Rs1, int Rm) : RvOp; // double→single (narrowing)

public record RvFcvtDs(int Rd, int Rs1, int Rm) : RvOp; // single→double (widening)

public record RvFmaddD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

// ── Zfh / Zfhmin extension (half-precision float) ─────────────────────────────
// Register indices: 0-31 = int, 32-63 = fp (64-bit NaN-boxed when .H)

public record RvFlh(int Rd, int Rs1, int Imm) : RvOp; // Rd=fp, Rs1=int

public record RvFsh(int Rs1, int Rs2, int Imm) : RvOp; // Rs1=int base, Rs2=fp data

public record RvFaddH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsubH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmulH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFdivH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsqrtH(int Rd, int Rs1) : RvOp;

public record RvFsgnjH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjnH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjxH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFminH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmaxH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFeqH(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFltH(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFleH(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFclassH(int Rd, int Rs1) : RvOp; // Rd=int result

public record RvFcvtWh(int Rd, int Rs1, int Rm) : RvOp; // half→signed int

public record RvFcvtWuH(int Rd, int Rs1, int Rm) : RvOp; // half→unsigned int

public record RvFcvtHw(int Rd, int Rs1, int Rm) : RvOp; // signed int→half

public record RvFcvtHWu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int→half

public record RvFmvXh(int Rd, int Rs1) : RvOp; // fp bits→int reg (sign-extended)

public record RvFmvHx(int Rd, int Rs1) : RvOp; // int bits→fp reg

public record RvFmaddH(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubH(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubH(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddH(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFcvtHs(int Rd, int Rs1, int Rm) : RvOp; // single→half (narrowing)

public record RvFcvtSh(int Rd, int Rs1, int Rm) : RvOp; // half→single (widening)

public record RvFcvtHd(int Rd, int Rs1, int Rm) : RvOp; // double→half (narrowing)

public record RvFcvtDh(int Rd, int Rs1, int Rm) : RvOp; // half→double (widening)