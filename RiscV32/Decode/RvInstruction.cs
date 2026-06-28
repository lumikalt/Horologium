using Mechanism;

namespace RiscV32.Decode;

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
    object? payload,
    int sizeBytes = 4
)
    : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = raw;
    public int SizeBytes { get; } = sizeBytes;
    public int DestinationRegister { get; } = dest;
    public IReadOnlyList<int> SourceRegisters { get; } = sources;
    public ToothClass Class { get; } = cls;
    public object? Payload { get; } = payload;

    public int VectorDestinationRegister => Payload switch {
        RvVIntAluVv op  => op.Vd,
        RvVIntAluVx op  => op.Vd,
        RvVIntAluVi op  => op.Vd,
        RvVMaskCmpVv op => op.Vd,
        RvVMaskCmpVx op => op.Vd,
        RvVMaskCmpVi op => op.Vd,
        RvVleVv op      => op.Vd,
        RvVlm op        => op.Vd,
        _               => -1,
    };

    public IReadOnlyList<int> UveStreamSources => Payload switch {
        // so.a.fp consumes one element from each source u-reg (if they are load streams).
        RvUveSoAFp op => [op.Usrc1, op.Usrc2,],
        _             => [],
    };

    public IReadOnlyList<int> UveBranchStreams => Payload switch {
        RvUveSoBNc op => [op.Urs,],
        _             => [],
    };

    public IReadOnlyList<(int StreamId, int Dim)> UveDimBranchSources => Payload switch {
        RvUveSoBNdc op => [(op.Urs, op.Dim),],
        _              => [],
    };

    public IReadOnlyList<int> VectorSourceRegisters => Payload switch {
        RvVIntAluVv op  => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVIntAluVx op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVIntAluVi op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVMaskCmpVv op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVMaskCmpVx op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVMaskCmpVi op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVseVv op      => op.Masked ? [op.Vs3, 0,] : [op.Vs3,],
        RvVsm op        => [op.Vs3,],
        _               => [],
    };

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

public record RvSret : RvOp;

public record RvWfi : RvOp;

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

// ── V extension (vector) ──────────────────────────────────────────────────────

// Config: rd = new vl (integer), vtypei/rs2 = new vtype
public record RvVsetvli(int Rd, int Rs1, int Vtypei) : RvOp;

public record RvVsetivli(int Rd, int Zimm, int Vtypei) : RvOp;

public record RvVsetvl(int Rd, int Rs1, int Rs2) : RvOp;

// Unit-stride loads: Vd = destination vector register, Rs1 = base address, Sew = element width in bits
public record RvVleVv(int Vd, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVlm(int Vd, int Rs1) : RvOp;

// Unit-stride stores: Vs3 = source vector register, Rs1 = base address, Sew = element width in bits
public record RvVseVv(int Vs3, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVsm(int Vs3, int Rs1) : RvOp;

// Integer ALU — split by source variant
public enum VIntOp {
    Add,
    Sub,
    And,
    Or,
    Xor,
    Sll,
    Srl,
    Sra,
}

public enum VMaskCmpOp {
    Eq,
    Ne,
    Ltu,
    Lt,
    Gtu,
    Gt,
}

public record RvVIntAluVv(VIntOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVIntAluVx(VIntOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVIntAluVi(VIntOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// Mask comparisons (result: 1 bit per element packed in vd)
public record RvVMaskCmpVv(VMaskCmpOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVMaskCmpVx(VMaskCmpOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVMaskCmpVi(VMaskCmpOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

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

// ── UVE extension ─────────────────────────────────────────────────────────────
// Stream setup (custom-0, opcode=0x0B, R4-type):
//   bits[31:27]=rs3_stride, bits[26:25]=funct2, bits[24:20]=rs2_count,
//   bits[19:15]=rs1_base, bits[14:12]=funct3, bits[11:7]=ud, bits[6:0]=0x0B
//   funct3=0x0 → ss.ld.w (1D)
//   funct3=0x1 → ss.st.w (1D)
//   funct3=0x2 → ss.sta.ld.w (start multi-dim load stream, first/innermost dimension)
//   funct3=0x3 → ss.sta.st.w (start multi-dim store stream)
//   funct3=0x4 → ss.app ud, _, rs2_count, rs3_stride (append next dimension)
//   funct3=0x5 → ss.end ud, _, rs2_count, rs3_stride (outermost dimension + activate)
//   funct3=0x6 → ss.cfg.vec ud (mark pending stream as vector-mode)
public record RvUveSsLdW(int Ud, int Rs1Base, int Rs2Count, int Rs3Stride) : RvOp;

public record RvUveSsStW(int Ud, int Rs1Base, int Rs2Count, int Rs3Stride) : RvOp;

public record RvUveSsStaLdW(int Ud, int Rs1Base, int Rs2Count, int Rs3Stride) : RvOp;

public record RvUveSsStaStW(int Ud, int Rs1Base, int Rs2Count, int Rs3Stride) : RvOp;

// Rs2Count/Rs3Stride are the count and stride for this additional dimension.
public record RvUveSsApp(int Ud, int Rs2Count, int Rs3Stride) : RvOp;

// Same field layout as ss.app; also returns StreamConfig when IsLoad.
public record RvUveSsEnd(int Ud, int Rs2Count, int Rs3Stride) : RvOp;

// ss.cfg.vec ud — flag the pending stream as vector-mode (no-op until vector streaming).
public record RvUveSsCfgVec(int Ud) : RvOp;

// Scalar broadcast (custom-1, opcode=0x2B, R-type, funct3=0x0, funct7=0x00):
//   so.v.dp.w ud, rs1 — broadcast float32 bits from int reg rs1 into u-reg ud
public record RvUveSoVDpW(int Ud, int Rs1) : RvOp;

// Arithmetic on stream elements (custom-1, opcode=0x2B, R-type, funct3=0x1):
//   funct7[6:4] selects the FP operation; ud=dest u-reg, usrc1/usrc2=source u-regs
public enum UveFpOp {
    Mul = 0,
    Add = 1,
    Mac = 2,
    Sub = 3,
    Div = 4,
}

public record RvUveSoAFp(UveFpOp Op, int Ud, int Usrc1, int Usrc2) : RvOp;

// Stream branch (custom-1, opcode=0x2B, B-type, funct3=0x4):
//   so.b.nc urs, imm — taken (PC += imm) while whole stream urs is not exhausted
public record RvUveSoBNc(int Urs, int Imm) : RvOp;

// Per-dimension branch (custom-1, opcode=0x2B, B-type, funct3=0x5):
//   so.b.ndc.D urs, imm — taken while dimension D of stream urs has not completed its pass
//   Dim = rs2 field interpreted as a literal 0-based dimension index (0 = innermost)
public record RvUveSoBNdc(int Urs, int Dim, int Imm) : RvOp;

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