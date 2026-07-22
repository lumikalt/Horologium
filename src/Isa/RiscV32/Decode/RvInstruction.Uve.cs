#region

using Mechanism;

#endregion

namespace RiscV32.Decode;

// ── UVE extension ─────────────────────────────────────────────────────────────
// Stream setup (custom-0, opcode=0x0B, R4-type):
//   bits[31:27]=rs3, bits[26:25]=funct2, bits[24:20]=rs2, bits[19:15]=rs1, bits[14:12]=funct3, bits[11:7]=ud
//   funct2=0: ss.sta.{ld|st}.* — funct3[2]=1→load,0→store; ew=1<<(funct3&3); only rs1(base) used
//     rs3=0: scalar mode; rs3=0x8..0xE: vector mode (VecCfgDim=rs3-8); rs3=0xF: vector, innermost dim (VecCfgDim=-1)
//   Header fields (UVE2): pm[31]=merging predication (default zeroing), vec[30]+vdim[29:27]=vector
//   mode/coupled dim, inds[24]=IndSource, mem[23:22]=cache-level routing. MergingPredication and
//   MemLevel are decoded but not yet consumed (predication policy → vector-width model; mem → cache routing).
//   funct2=1, funct3=0: ss.app     — rs1=offset reg, rs2=count reg, rs3=stride reg
//   funct2=1, funct3=4: ss.app.mod — static modifier: b[24:22], ta[21:20], tdim[17:15], rs3=disp reg
//     tdim=7 (".L") targets the last configured dimension of the stream (author-confirmed), not
//     "the dimension configured right after the trigger" — resolved at ss.end time since the final
//     dimension count isn't known until then.
//   funct2=1, funct3=6: ss.app.ind — indirect modifier: tdim[30:28], b[24:22], ta[21:20], rs1=IndSource reg
//   funct2=2, funct3=0: ss.end     — rs1=offset reg, rs2=count reg, rs3=stride reg; activates stream
// VecCfgDim: -1 = innermost dimension; 0..6 = explicit dimension (outermost-first, Spike order).
public record RvUveSsStaLdW(
    int Ud,
    int Rs1Base,
    int ElementBytes = 4,
    bool IsVectorMode = false,
    int VecCfgDim = -1,
    bool MergingPredication = false,
    int MemLevel = 0
) : RvOp;

public record RvUveSsStaStW(
    int Ud,
    int Rs1Base,
    int ElementBytes = 4,
    bool IsVectorMode = false,
    int VecCfgDim = -1,
    bool MergingPredication = false,
    int MemLevel = 0
) : RvOp;

// ss.sta.ld.*_inds ud, rs1 — IndSource stream: backed by a load stream but provides values for indirect modifiers.
// Encoded as ss.sta.ld.* with inds bit[24]=1. No vector mode; follows with ss.app*/ss.end like a normal load stream.
public record RvUveSsStaLdWInds(int Ud, int Rs1Base, int ElementBytes = 4, int MemLevel = 0) : RvOp;

// ss.app.sgi ud, rs1_indsrc — attach a scatter-gather modifier to the pending config.
// Fires per element (before each address generation), always targeting Offset of dimension 0.
// Rs1Source is the UVE register number of the IndSource stream; behavior = (rs2_literal >> 2) & 7.
// Discriminated from ss.app.ind by bit27=1 in the instruction encoding.
public record RvUveSsAppSgi(int Ud, int Rs1Source, StreamModifierBehavior Behavior) : RvOp;

// ss.end.sgi ud, rs1_indsrc — attach scatter-gather modifier + activate the pending stream config.
// Like ss.app.sgi but also ends the configuration. Does NOT add a new dimension.
public record RvUveSsEndSgi(int Ud, int Rs1Source, StreamModifierBehavior Behavior) : RvOp;

// ss.app.ind ud, rs1_indsrc — attach one indirect (dynamic) modifier to the pending stream config.
// The trigger dimension is positional (the most recently appended dimension at execute time);
// TargetDimRaw is the tdim field (outermost-first, Spike order; 7 = ".L", the last configured
// dimension of the stream). Both are remapped to engine indices in ExecuteUveSsEnd.
// SourceStreamId = rs1 field = UVE register number of the IndSource stream.
public record RvUveSsAppInd(
    int Ud,
    int TargetDimRaw,
    StreamModifierTarget Target,
    StreamModifierBehavior Behavior,
    int SourceStreamId
) : RvOp;

// Rs1Offset is the offset register (Spike adds offset*ew to base); ignored — no offset field in StreamDimension.
public record RvUveSsApp(int Ud, int Rs1Offset, int Rs2Count, int Rs3Stride) : RvOp;

// Same field layout as ss.app; activates the stream after appending the innermost dimension.
public record RvUveSsEnd(int Ud, int Rs1Offset, int Rs2Count, int Rs3Stride) : RvOp;

// ss.app.mod: append a static modifier. Trigger dimension is positional (like ss.app.ind);
// TargetDimRaw = tdim field [17:15] (outermost-first; 7 = ".L", the last configured dimension).
// rs3 = displacement register.
public record RvUveSsAppMod(
    int Ud,
    int TargetDimRaw,
    StreamModifierTarget Target,
    StreamModifierBehavior Behavior,
    int Rs3Disp
) : RvOp;

// so.v.dp.(width) ud, rs1 — broadcast integer register rs1 bits (masked to ElementBytes) into u-reg scalar slot
// (custom-1, opcode=0x2B, funct7=0x56; funct3: 0=b, 1=h, 2=w, 3=d)
public record RvUveSoVDp(int Ud, int Rs1, int ElementBytes) : RvOp;

// so.v.mvvs rd, us1 — write first element of UVE register us1 into integer register rd
// (custom-1, opcode=0x2B, funct7=0x54, rs2=16, Rd=integer dest)
public record RvUveSoVMvvs(int Us1, int Rd) : RvOp;

// so.v.mvsv.(width) ud, rs1 — move integer register rs1 (masked to ElementBytes) into UVE register ud as scalar
// (custom-1, opcode=0x2B, funct7=0x54, rs2=24; funct3: 0=b, 1=h, 2=w, 3=d)
public record RvUveSoVMvsv(int Ud, int Rs1, int ElementBytes) : RvOp;

// Arithmetic on stream elements (custom-1, opcode=0x2B):
//   (funct7>>3, funct3): Add=(0,1), Sub=(0,5), Mul=(1,1), Div=(1,5), Mac=(3,5)
public enum UveFpOp {
    Mul = 0,
    Add = 1,
    Mac = 2,
    Sub = 3,
    Div = 4,
    Min = 5,
    Max = 6,
    Abs = 7,
    Inc = 8,
    Dec = 9,
    Sqrt = 10,
    Adde = 11,    // accumulate stream element into ud (overwrite)
    AddeAcc = 12, // accumulate stream element into ud (add)
    Mine = 13,    // ud = min(ud, stream_elem)
    Maxe = 14,    // ud = max(ud, stream_elem)
}

// FP arithmetic on stream elements; Usrc2=-1 for unary ops (Abs, Inc, Dec, Sqrt).
// Ps3 = bits[27:25] of the instruction — governing predicate register index (0 = p0 = all-ones).
public record RvUveSoAFp(UveFpOp Op, int Ud, int Usrc1, int Usrc2, int Ps3 = 0) : RvOp;

public enum UveIntOp {
    Add = 0,
    Sub = 1,
    Mul = 2,
    Div = 3,
    Mac = 4,
    Min = 5,
    Max = 6,
    Abs = 7,
    Inc = 8,
    Dec = 9,
    Adde = 10,
    AddeAcc = 11,
    Mine = 12,
    Maxe = 13,
}

// Integer arithmetic on stream elements; Usrc2=-1 for unary ops (Abs, Inc, Dec).
// Ps3 = governing predicate register index.
public record RvUveSoAInt(UveIntOp Op, bool Signed, int Ud, int Usrc1, int Usrc2, int Ps3 = 0) : RvOp;

public enum UveLogicOp {
    Nand,
    And,
    Nor,
    Or,
    Not,
    Xor,
}

// Bitwise logic on stream elements; Usrc2=-1 for Not (unary).
// Ps3 = governing predicate register index.
public record RvUveSoALogic(UveLogicOp Op, int Ud, int Usrc1, int Usrc2, int Ps3 = 0) : RvOp;

public enum UveShiftOp {
    Sll, Srl, Sra,
}

// Element-wise shift with amount from another u-reg.
// Ps3 = governing predicate register index.
public record RvUveSoAShiftV(UveShiftOp Op, int Ud, int Usrc1, int Usrc2, int Ps3 = 0) : RvOp;

// Element-wise shift with amount from integer register Rs2.
// Ps3 = governing predicate register index.
public record RvUveSoAShiftS(UveShiftOp Op, int Ud, int Usrc1, int Rs2, int Ps3 = 0) : RvOp;

// Scalar-write reduction: accumulate stream element into integer (sadde, IsFp=false) or FP (fsadde, IsFp=true).
// Rd is the unified-file index: integer reg (0–31) for sadde; FP reg (32–63, pre-offset) for fsadde.
// Ps3 = governing predicate register index (gates which elements contribute to the sum).
public record RvUveSoASadde(bool IsFp, bool Acc, int Rd, int Usrc1, int Ps3 = 0) : RvOp;

// SO_C group (custom-1, funct7=0x58): stream lifecycle and vector-length control.
// ss.stop ud — terminate stream in u-reg ud (SO_C_BREAK, funct3=3).
public record RvUveSoCBreak(int Ud) : RvOp;

// ss.suspend ud — suspend stream in u-reg ud (SO_C_SUSPD, funct3=1).
public record RvUveSoCSuspd(int Ud) : RvOp;

// ss.resume ud — resume suspended stream in u-reg ud (SO_C_RESUM, funct3=2).
public record RvUveSoCResum(int Ud) : RvOp;

// ss.getvl rd — read current vector length into integer register rd (SO_C_GETVL, funct3=7).
public record RvUveSoCGetvl(int Rd) : RvOp;

// ss.setvl rd, rs1 — set vector length from integer rs1, return old VL in rd (SO_C_SETVL, funct3=0).
public record RvUveSoCSetvl(int Rd, int Rs1) : RvOp;

// Stream branch (custom-1, opcode=0x2B, UVE B-type: bits[31:29]=111, bit28=imm[12]):
//   funct3=7:     so.b.nc urs, imm — not exhausted (bit20=1) / so.b.c urs, imm — exhausted (bit20=0)
//   funct3=D-1:   so.b.ndc.D urs, imm — dim not complete (bit20=1) / so.b.dc.D — dim complete (bit20=0)
//                 (D = 1..7, funct3 = 0..6 — dc.1 is reachable, dc.8 no longer exists)
// Dim = funct3 counts dimensions from the OUTERMOST (Spike: EODTable.at(funct3),
// dimensions[0] = outermost). The innermost dim of an N-dim stream is so.b.ndc.N
// (funct3 = N-1). The pipeline remaps to the engine's innermost-first index when
// syncing DimDone; DimDone itself is keyed by the raw funct3 value.
// This funct3=7/0..6 split is the UVE2 author's authoritative correction (2026-07-22):
// Appendix B's original listing and Spike both instead put the EOS-equivalent form at
// funct3=0 and only support dc.2..dc.8 (funct3=1..7) — see SPEC_NOTES.md's "Branch `d`
// field" entry for the full resolution and why Spike is overruled here.
public record RvUveSoBNc(int Urs, int Imm) : RvOp;

public record RvUveSoBNdc(int Urs, int Dim, int Imm) : RvOp;

public record RvUveSoBc(int Urs, int Imm) : RvOp;

public record RvUveSoBdc(int Urs, int Dim, int Imm) : RvOp;

// ── SO_P predicate register group (custom-1, opcode=0x2B, bits[31:28]=1000/1001) ─────────
// Predicate register file: 16 regs (uve_pred_rd = bits[10:7]), each with PredBytes (16) entries.
// Governing predicate GovPred = bits[27:25] (3-bit → regs 0-7).
// Zeroing flag Zeroing = bit[24]: 1 → inactive elements → 0; 0 → merge (keep old dest).

// Simple predicate ops: zero/one/vr/not/mv/mvt (group=8, funct3 bit[2]=0)
//   Encoding summary:
//   (funct3[1:0], bit11): (0,0)=zero, (0,1)=one, (1,0)=vr, (1,1)=not, (2,0)=mv, (2,1)=mvt
//   Ps1 = uve_pred_rs1 = bits[18:15] (source pred reg for not/mv/mvt; -1 otherwise)
//   Vs1 = uve_pred_vs1 = bits[19:15] (source ud reg for vr; -1 otherwise)
public enum UveSoPSimpleOp {
    Zero,
    One,
    Vr,
    Not,
    Mv,
    Mvt,
}

public record RvUveSoPSimple(UveSoPSimpleOp Op, int Pd, int GovPred, bool Zeroing, int Ps1, int Vs1) : RvOp;

// Comparison predicate ops: ge (group=8, funct3[2]=1), eq/lt (group=9, funct3[2]=0/1)
//   CmpType = funct3[1:0]: 0=Us, 1=Fp, 2=Sg
//   Vs1 = bits[19:15], Vs2 = bits[24:20] (ud register source indices; bit24 is NOT zeroing here)
//   Inactive elements always merge (keep old dest) during the comparison itself.
//   Zeroing = bit[11]: 1 → _z variant; sets PredZeroing[Pd]=true on the output predicate register,
//   so future SO_A ops using it as governing pred will zero inactive elements (Spike: predMode tag).
public enum UveSoPCmpOp {
    Ge, Eq, Lt,
}

public enum UveSoPCmpType {
    Us, Fp, Sg,
}

public record RvUveSoPCmp(
    UveSoPCmpOp Op,
    UveSoPCmpType CmpType,
    int Pd,
    int GovPred,
    int Vs1,
    int Vs2,
    bool Zeroing = false
) : RvOp;

// so.v.mv/mvt — move (or transpose-move) vector register vs1 into vd, gated by predicate PredIdx.
// funct7=0x54, rs2[4:3]: 0=mv, 1=mvt; rs2[2:0]=uve_v_pred (bits[22:20])
public record RvUveSoVMv(bool Transpose, int Vd, int Vs1, int PredIdx) : RvOp;

// so.p.cv.<srcW>.<destW>[.z] pd, ps1 — predicate register width conversion.
// group=8, funct3=3; rs2[1:0]=srcWidthIdx (0=b,1=h,2=w,3=d), rs2[3:2]=destWidthIdx, rs2[4]=zeroing flag.
// rs1[3:0]=src pred reg, rd[3:0]=dest pred reg.
public record RvUveSoPCv(int Pd, int Ps1, int SrcBytes, int DestBytes, bool Zeroing) : RvOp;

// so.v.cv.{fp,sg,us}.<destW> vd, vs1 — vector element type conversion.
// group=10; funct3=destWidthIdx (0=b,1=h,2=w,3=d); rs2: 0=US, 8=FP, 16=SG.
// Reads ValidElements[vs1] lanes from vs1, converts each, writes to vd.
public record RvUveSoVCv(int Vd, int Vs1, int DestBytes, bool IsFp, bool IsSigned) : RvOp;