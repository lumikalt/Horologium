using Mechanism;

namespace RiscV.Decode;

/// <summary>
/// A decoded RV32I instruction.
/// The Payload carries the pre-decoded operation so the executor
/// doesn't need to re-decode from the raw encoding.
/// </summary>
public sealed class RvInstruction(
    ulong pc,
    uint raw,
    int dest,
    IReadOnlyList<int> sources,
    ToothClass cls,
    object? payload
)
    : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = raw;
    public int SizeBytes => 4;
    public int DestinationRegister { get; } = dest;
    public IReadOnlyList<int> SourceRegisters { get; } = sources;
    public ToothClass Class { get; } = cls;
    public object? Payload { get; } = payload;

    public override string ToString() =>
        $"[0x{Pc:X8}] {Payload?.GetType().Name ?? "?"} raw=0x{RawEncoding:X8}";
}

/// <summary>
/// The decoded operation carried as Payload in RvInstruction.
/// The executor pattern-matches on this.
/// </summary>
public abstract record RvOp;

// ── R-type ────────────────────────────────────────────────────────────────────
public record RvAdd(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSub(int Rd, int Rs1, int Rs2) : RvOp;

public record RvXor(int Rd, int Rs1, int Rs2) : RvOp;

public record RvOr(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAnd(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSll(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSrl(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSra(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSlt(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSltu(int Rd, int Rs1, int Rs2) : RvOp;

// ── I-type ALU ────────────────────────────────────────────────────────────────
public record RvAddi(int Rd, int Rs1, int Imm) : RvOp;

public record RvXori(int Rd, int Rs1, int Imm) : RvOp;

public record RvOri(int Rd, int Rs1, int Imm) : RvOp;

public record RvAndi(int Rd, int Rs1, int Imm) : RvOp;

public record RvSlli(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrli(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrai(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSlti(int Rd, int Rs1, int Imm) : RvOp;

public record RvSltiu(int Rd, int Rs1, int Imm) : RvOp;

// ── Loads ─────────────────────────────────────────────────────────────────────
public record RvLb(int Rd, int Rs1, int Imm) : RvOp;

public record RvLh(int Rd, int Rs1, int Imm) : RvOp;

public record RvLw(int Rd, int Rs1, int Imm) : RvOp;

public record RvLbu(int Rd, int Rs1, int Imm) : RvOp;

public record RvLhu(int Rd, int Rs1, int Imm) : RvOp;

// ── Stores ────────────────────────────────────────────────────────────────────
public record RvSb(int Rs1, int Rs2, int Imm) : RvOp;

public record RvSh(int Rs1, int Rs2, int Imm) : RvOp;

public record RvSw(int Rs1, int Rs2, int Imm) : RvOp;

// ── Branches ──────────────────────────────────────────────────────────────────
public record RvBeq(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBne(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBlt(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBge(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBltu(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBgeu(int Rs1, int Rs2, int Imm) : RvOp;

// ── Jumps ─────────────────────────────────────────────────────────────────────
public record RvJal(int Rd, int Imm) : RvOp;

public record RvJalr(int Rd, int Rs1, int Imm) : RvOp;

// ── Upper immediates ──────────────────────────────────────────────────────────
public record RvLui(int Rd, int Imm) : RvOp;

public record RvAuipc(int Rd, int Imm) : RvOp;

// ── System ────────────────────────────────────────────────────────────────────
public record RvEcall : RvOp;

public record RvEbreak : RvOp;

public record RvCsrrw(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrs(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrc(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrwi(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvCsrrsi(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvCsrrci(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvMret : RvOp;

public record RvFence : RvOp;

// ── M extension (multiply / divide) ──────────────────────────────────────────
public record RvMul(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulh(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulhsu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulhu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDiv(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDivu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRem(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRemu(int Rd, int Rs1, int Rs2) : RvOp;

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

public record RvFcvtWs(int Rd, int Rs1) : RvOp; // float→signed int

public record RvFcvtWuS(int Rd, int Rs1) : RvOp; // float→unsigned int

public record RvFcvtSw(int Rd, int Rs1) : RvOp; // signed int→float

public record RvFcvtSWu(int Rd, int Rs1) : RvOp; // unsigned int→float

public record RvFmvXw(int Rd, int Rs1) : RvOp; // fp bits→int reg

public record RvFmvWx(int Rd, int Rs1) : RvOp; // int bits→fp reg

public record RvFmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

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