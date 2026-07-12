using Mechanism;

namespace RiscV32.Decode;

// ── V extension (vector) ──────────────────────────────────────────────────────

// Config: rd = new vl (integer), vtypei/rs2 = new vtype
public record RvVsetvli(int Rd, int Rs1, int Vtypei) : RvOp;

public record RvVsetivli(int Rd, int Zimm, int Vtypei) : RvOp;

public record RvVsetvl(int Rd, int Rs1, int Rs2) : RvOp;

// Unit-stride loads: Vd = destination vector register, Rs1 = base address, Sew = element width in bits
public record RvVleVv(int Vd, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVlm(int Vd, int Rs1) : RvOp;

// Segment loads: NumFields = 2..8; writes to vd, vd+1, ..., vd+NumFields-1 (one reg per field per element).
public record RvVlsegVv(int NumFields, int Vd, int Rs1, int Sew, bool Masked) : RvOp;

// Strided loads: Rs2 = byte stride (may be negative)
public record RvVlseVv(int Vd, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// Unit-stride stores: Vs3 = source vector register, Rs1 = base address, Sew = element width in bits
public record RvVseVv(int Vs3, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVsm(int Vs3, int Rs1) : RvOp;

// Segment stores: NumFields = 2..8; reads from vs3, vs3+1, ..., vs3+NumFields-1.
public record RvVssegVv(int NumFields, int Vs3, int Rs1, int Sew, bool Masked) : RvOp;

// Strided stores: Rs2 = byte stride
public record RvVsseVv(int Vs3, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// Slide ops: shift elements up (towards higher indices) or down (towards lower indices).
// Is1=true for vslide1up/vslide1down (insert scalar at boundary; .vx only).
public enum VSlideDir { Up, Down, }

public record RvVSlideVx(VSlideDir Dir, bool Is1, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVFpSlide1Vf(VSlideDir Dir, int Vd, int Vs2, int FpRs1, bool Masked) : RvOp;

public record RvVSlideVi(VSlideDir Dir, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// Indexed gather: vd[i] = vs2[index] or 0 if index >= vl.
public record RvVRgatherVv(int Vd, int Vs2, int Vs1, bool Masked) : RvOp; // SEW-wide indices

public record RvVRgatherEi16Vv(int Vd, int Vs2, int Vs1, bool Masked) : RvOp; // u16 indices regardless of SEW

public record RvVRgatherVx(int Vd, int Vs2, int Rs1, bool Masked) : RvOp; // broadcast scalar index

public record RvVRgatherVi(int Vd, int Vs2, int Imm, bool Masked) : RvOp; // broadcast immediate index

// Indexed loads: Vs2 = index vector (byte offsets), IndexSew = index element width in bits,
// data element width comes from runtime vtype CSR. Ordered=true → vloxei (ordered/faulting-only).
public record RvVlxeiVv(int Vd, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Indexed stores: Vs3 = data vector, Vs2 = index vector (byte offsets), IndexSew = index element width.
public record RvVsxeiVv(int Vs3, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Integer ALU — split by source variant
public enum VIntOp {
    Add,
    Sub,
    Rsub, // vrsub: vd[i] = scalar/imm - vs2[i] (VX and VI only)
    And,
    Or,
    Xor,
    Sll,
    Srl,
    Sra,
    Mov, // vmv.v.v / vmv.v.x / vmv.v.i: vd[i] = source[i] (broadcast)
    Minu,
    Min,
    Maxu,
    Max,
}

public enum VIntMacOp {
    Macc,  // vmacc:  vd[i] = vd[i] + vs2[i]*vs1[i]
    Nmsac, // vnmsac: vd[i] = vd[i] - vs2[i]*vs1[i]
    Madd,  // vmadd:  vd[i] = vs2[i] + vd[i]*vs1[i]
    Nmsub, // vnmsub: vd[i] = vs2[i] - vd[i]*vs1[i]
}

public enum VMaskLogOp {
    Andn, // vmandn.mm: vd = vs2 & ~vs1
    And,  // vmand.mm:  vd = vs2 &  vs1
    Or,   // vmor.mm:   vd = vs2 |  vs1
    Xor,  // vmxor.mm:  vd = vs2 ^  vs1
    Orn,  // vmorn.mm:  vd = vs2 | ~vs1
    Nand, // vmnand.mm: vd = ~(vs2 &  vs1)
    Nor,  // vmnor.mm:  vd = ~(vs2 |  vs1)
    Xnor, // vmxnor.mm: vd = ~(vs2 ^  vs1)
}

public enum VMaskUnaryOp {
    Msbf = 1,  // vmsbf.m: 1 for elements before first set bit in vs2
    Msof = 2,  // vmsof.m: 1 only at the position of first set bit
    Msif = 3,  // vmsif.m: 1 for elements up to and including first set bit
    Iota = 16, // viota.m: exclusive prefix-sum of vs2 bits written per element
    Id = 17,   // vid.v:   write element index i into vd[i]
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

// vmv.x.s rd, vs2: extract element 0 from vs2 into integer rd (OPMVV, funct6=16)
public record RvVMvXs(int Rd, int Vs2) : RvOp;

// vmv.s.x vd, rs1: move integer rs1 into element 0 of vd (OPMVX, funct6=0x10, vs2=0)
public record RvVMvSx(int Vd, int Rs1) : RvOp;

// Integer multiply-accumulate (OPMVV funct3=2 / OPMVX funct3=6): vd is accumulator.
// vmacc.vv:  vd[i] = vd[i] + vs2[i]*vs1[i]
// vnmsac.vv: vd[i] = vd[i] - vs2[i]*vs1[i]
public record RvVIntMacVv(VIntMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVIntMacVx(VIntMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vmerge.vvm/vxm/vim: funct6=0x17, vm=0. mask=1 → active source, mask=0 → vs2[i].
public record RvVMergeVv(int Vd, int Vs2, int Vs1) : RvOp;

public record RvVMergeVx(int Vd, int Vs2, int Rs1) : RvOp;

public record RvVMergeVi(int Vd, int Vs2, int Imm) : RvOp;

// vfmerge.vfm: FP conditional merge; always uses v0 mask. mask=1 → fpRs1 (scalar float bits), mask=0 → vs2[i].
public record RvVFpMergeVf(int Vd, int Vs2, int FpRs1) : RvOp;

// Reduction ops — OPMVV (funct3=2); result lands in vd[0].
// Format: vredop.vs vd, vs2, vs1  (vs2=source vector, vs1=scalar initial accumulator)
public enum VRedOp {
    Sum,  // vredsum
    And,  // vredand
    Or,   // vredor
    Xor,  // vredxor
    Minu, // vredminu
    Min,  // vredmin
    Maxu, // vredmaxu
    Max,  // vredmax
}

public record RvVRedVs(VRedOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// Widening integer sum reduction: vd[0] = 2×SEW(vs1[0]) + Σ zero/sign-extend(vs2[i])
public record RvVWideRedVs(bool Signed, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// Integer multiply/divide — OPMVV (VV) and OPMVX (VX); no VI variant.
public enum VMulOp {
    Mul,    // vmul:    low half of signed product
    MulH,   // vmulh:   signed high half
    MulHu,  // vmulhu:  unsigned high half
    MulHsu, // vmulhsu: vs2 signed × vs1/rs1 unsigned, high half
    Div,    // vdiv:    signed truncated division
    Divu,   // vdivu:   unsigned division
    Rem,    // vrem:    signed remainder
    Remu,   // vremu:   unsigned remainder
}

public record RvVMulVv(VMulOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVMulVx(VMulOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// Widening integer arithmetic: vwaddu/vwadd/vwsubu/vwsub/vwmulu/vwmulsu/vwmul.
// Vs2IsWide=true for the .wv/.wx variants where vs2 is already 2*SEW.
public enum VWideOp {
    AddU,
    Add,
    SubU,
    Sub,
    MulU,
    MulSu,
    Mul,
}

public record RvVWideVv(VWideOp Op, int Vd, int Vs2, int Vs1, bool Masked, bool Vs2IsWide) : RvOp;

public record RvVWideVx(VWideOp Op, int Vd, int Vs2, int Rs1, bool Masked, bool Vs2IsWide) : RvOp;

// Narrowing shift: vs2 is 2*SEW, result vd is SEW.  vnsrl=logical, vnsra=arithmetic.
public enum VNarrOp { Srl, Sra, }

public record RvVNarrVv(VNarrOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVNarrVx(VNarrOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVNarrVi(VNarrOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// Mask logical ops (OPMVV, mm form): bitwise ops on full mask registers, not limited by vl
public record RvVMaskLogMm(VMaskLogOp Op, int Vd, int Vs2, int Vs1) : RvOp;

// vcpop.m rd, vs2: count active mask bits in vs2 (OPMVV funct6=0x10 vs1=16); writes integer rd
public record RvVcpop(int Rd, int Vs2, bool Masked) : RvOp;

// vfirst.m rd, vs2: first active set bit index in vs2 (OPMVV funct6=0x10 vs1=17); -1 if none; writes integer rd
public record RvVfirst(int Rd, int Vs2, bool Masked) : RvOp;

// vmsbf/vmsof/vmsif/viota/vid — OPMVV funct6=0x14 (VMUNARY0); vs2 field unused for vid.v
public record RvVMaskUnary(VMaskUnaryOp Op, int Vd, int Vs2, bool Masked) : RvOp;

// vcompress.vm vd, vs2, vs1: pack elements of vs2 where vs1[i]=1 into vd; vs1 is the explicit mask
public record RvVCompress(int Vd, int Vs2, int Vs1) : RvOp;

// vmv{N}r.v vd, vs2: copy N consecutive vector registers (OPIVI funct6=0x27); NumRegs = 1/2/4/8
public record RvVMvNr(int NumRegs, int Vd, int Vs2) : RvOp;

// vl{N}r.v: whole-register load — loads NumRegs*VLenB bytes, ignores vtype/vl; NumRegs = 1/2/4/8
public record RvVlrV(int NumRegs, int Vd, int Rs1) : RvOp;

// vs{N}r.v: whole-register store — stores NumRegs*VLenB bytes, ignores vtype/vl; NumRegs = 1/2/4/8
public record RvVsrV(int NumRegs, int Vs3, int Rs1) : RvOp;

// vle{SEW}ff.v: fault-only-first load — like vle but trims vl on fault; modeled as regular vle.
public record RvVleFf(int Vd, int Rs1, int Sew, bool Masked) : RvOp;

// vzext.vfN / vsext.vfN: zero/sign-extend each element from SEW/Factor bits to SEW bits.
// Factor=2 → vf2, Factor=4 → vf4, Factor=8 → vf8.
public record RvVExt(bool Signed, int Factor, int Vd, int Vs2, bool Masked) : RvOp;

// vaaddu/vaadd/vasubu/vasub: fixed-point averaging add/sub (OPMVV/OPMVX).
// Result = (vs2 ± vs1/rs1 + round) >> 1, where round comes from vxrm.
public enum VAvgOp {
    Addu,
    Add,
    Subu,
    Sub,
}

public record RvVAvgVv(VAvgOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVAvgVx(VAvgOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vfwredusum.vs / vfwredosum.vs: widening FP sum reduction (OPFVV only).
// Accumulator in vs1[0] is 2×SEW; elements in vs2 are SEW; result in vd[0] is 2×SEW.
public record RvVFpWideRedVs(bool Ordered, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// vlsseg{NF}e{SEW}.v / vssseg{NF}e{SEW}.v — strided segment loads/stores.
public record RvVlssegVv(int NumFields, int Vd, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

public record RvVsssegVv(int NumFields, int Vs3, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// vluxseg/vloxseg/vsuxseg/vsoxseg — indexed segment loads/stores.
// IndexSew = index element width; data element width from runtime vtype.
// Ordered=true → ordered (vloxseg/vsoxseg); false → unordered (vluxseg/vsuxseg).
public record RvVlxsegVv(int NumFields, int Vd, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

public record RvVsxsegVv(int NumFields, int Vs3, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Widening integer multiply-accumulate — OPMVV (funct3=2) / OPMVX (funct3=6).
// vd is the 2×SEW accumulator (both source and destination).
public enum VwMacOp {
    Macc,   // vwmacc:   vd += signed(vs2) * signed(vs1/rs1)
    Maccu,  // vwmaccu:  vd += unsigned(vs2) * unsigned(vs1/rs1)
    Maccsu, // vwmaccsu: vd += signed(vs2) * unsigned(vs1/rs1)
    Maccus, // vwmaccus: vd += unsigned(vs2) * signed(rs1)  (VX only)
}

public record RvVwMacVv(VwMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVwMacVx(VwMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// Mask comparisons (result: 1 bit per element packed in vd)
public record RvVMaskCmpVv(VMaskCmpOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVMaskCmpVx(VMaskCmpOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVMaskCmpVi(VMaskCmpOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── Vector FP extension (V 1.0, OPFVV funct3=1 / OPFVF funct3=5) ─────────────

public enum VFpBinOp {
    Add,
    Sub,
    Mul,
    Div,
    Min,
    Max,
    Sgnj,
    Sgnjn,
    Sgnjx,
}

// Rs1/Vs1 in VF variants stores the unified FRF index (float reg + 32).
public record RvVFpBinVv(VFpBinOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpBinVf(VFpBinOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVFpSqrt(int Vd, int Vs2, bool Masked) : RvOp;

// vfclass.v: each element → 10-bit classification mask written as float-width integer.
public record RvVFpClass(int Vd, int Vs2, bool Masked) : RvOp;

public enum VFpCvtOp {
    XuFromF,
    XFromF,
    FFromXu,
    FFromX,
    RtzXuFromF, // vfcvt.rtz.xu.f.v: truncate-to-zero float→uint
    RtzXFromF,  // vfcvt.rtz.x.f.v:  truncate-to-zero float→int
}

public record RvVFpCvt(VFpCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;

public enum VFpFmaOp {
    Macc,
    Nmacc,
    Msac,
    Nmsac,
    Madd,
    Nmadd,
    Msub,
    Nmsub,
}

// FMA: vd = f(vd, vs2, vs1/rs1). vd is both source and destination.
public record RvVFpFmaVv(VFpFmaOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpFmaVf(VFpFmaOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public enum VFpCmpOp {
    Eq,
    Le,
    Lt,
    Ne,
    Gt,
    Ge,
}

// FP compare: result is a mask register (1 bit per element).
public record RvVmFpCmpVv(VFpCmpOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVmFpCmpVf(VFpCmpOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vfmv.f.s rd, vs2: scalar float rd ← vs2[0]. Rd is unified FRF index (float reg + 32).
public record RvVFpMvFs(int Rd, int Vs2) : RvOp;

// vfmv.s.f vd, rs1: vd[0] ← scalar float rs1 (unified FRF index). Other elements undisturbed.
public record RvVFpMvSf(int Vd, int Rs1) : RvOp;

// vfmv.v.f vd, rs1: broadcast scalar float to all active elements.
public record RvVFpMvVf(int Vd, int Rs1, bool Masked) : RvOp;

// FP reduction ops — OPFVV (funct3=1); result lands in vd[0].
// Format: vfredop.vs vd, vs2, vs1  (vs2=source vector, vs1[0]=scalar initial accumulator)
public enum VFpRedOp {
    Usum, // vfredusum: unordered floating-point sum
    Osum, // vfredosum: ordered floating-point sum
    Min,  // vfredmin
    Max,  // vfredmax
}

public record RvVFpRedVs(VFpRedOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// ── V saturating integer arithmetic (OPIVV/OPIVX/OPIVI) ──────────────────────
public enum VSatIntOp {
    Sadd,  // vsadd:   signed saturating add
    Saddu, // vsaddu:  unsigned saturating add
    Ssub,  // vssub:   signed saturating subtract
    Ssubu, // vssubu:  unsigned saturating subtract
    Smul,  // vsmul:   signed saturating fixed-point multiply (round by SEW-1)
    Ssrl,  // vssrl:   scaled (rounded) shift right logical
    Ssra,  // vssra:   scaled (rounded) shift right arithmetic
}

public record RvVSatIntVv(VSatIntOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVSatIntVx(VSatIntOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVSatIntVi(VSatIntOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── V narrowing saturating clip (vnclipu/vnclip) ─────────────────────────────
// Input is 2×SEW wide; shift+round; saturate to SEW-wide output.
public enum VnClipOp {
    Clipu, // vnclipu: unsigned narrowing clip
    Clip,  // vnclip:  signed narrowing clip
}

public record RvVnClipVv(VnClipOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVnClipVx(VnClipOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVnClipVi(VnClipOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── V widening FP arithmetic (vfwadd/vfwsub VV/VF, vfwadd/vfwsub WV/WF, vfwmul VV/VF) ──
public enum VFpWideArithOp {
    Add, Sub, Mul,
}

// Vs2Wide=true → WV/WF form (vs2 is already 2×SEW); false → VV/VF form
public record RvVFpWArithVv(VFpWideArithOp Op, int Vd, int Vs2, int Vs1, bool Vs2Wide, bool Masked) : RvOp;

public record RvVFpWArithVf(VFpWideArithOp Op, int Vd, int Vs2, int Rs1, bool Vs2Wide, bool Masked) : RvOp;

// ── V widening FP MAC (vd is 2×SEW accumulator — both read and written) ──────
public enum VFpWMacOp {
    Macc,  // vfwmacc:  vd += vs2*vs1
    Nmacc, // vfwnmacc: vd = -(vs2*vs1) - vd
    Msac,  // vfwmsac:  vd = (vs2*vs1) - vd
    Nmsac, // vfwnmsac: vd = -(vs2*vs1) + vd
}

public record RvVFpWMacVv(VFpWMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpWMacVf(VFpWMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// ── V widening FP converts (funct6=0x12, vs1 field 8-15) ─────────────────────
public enum VFpWCvtOp {
    XuFromF,    // vfwcvt.xu.f.v    (vs1=8):  f32 → u64
    XFromF,     // vfwcvt.x.f.v     (vs1=9):  f32 → i64
    FFromXu,    // vfwcvt.f.xu.v    (vs1=10): u32 → f64
    FFromX,     // vfwcvt.f.x.v     (vs1=11): i32 → f64
    FFromF,     // vfwcvt.f.f.v     (vs1=12): f32 → f64
    RtzXuFromF, // vfwcvt.rtz.xu.f.v (vs1=14): f32 → u64 truncate
    RtzXFromF,  // vfwcvt.rtz.x.f.v  (vs1=15): f32 → i64 truncate
}

public record RvVFpWCvt(VFpWCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;

// ── V narrowing FP converts (funct6=0x12, vs1 field 16-23) ───────────────────
public enum VFpNCvtOp {
    XuFromF,    // vfncvt.xu.f.w     (vs1=16): f64 → u32
    XFromF,     // vfncvt.x.f.w      (vs1=17): f64 → i32
    FFromXu,    // vfncvt.f.xu.w     (vs1=18): u64 → f32
    FFromX,     // vfncvt.f.x.w      (vs1=19): i64 → f32
    FFromF,     // vfncvt.f.f.w      (vs1=20): f64 → f32
    RodFFromF,  // vfncvt.rod.f.f.w  (vs1=21): f64 → f32 round-to-odd
    RtzXuFromF, // vfncvt.rtz.xu.f.w (vs1=22): f64 → u32 truncate
    RtzXFromF,  // vfncvt.rtz.x.f.w  (vs1=23): f64 → i32 truncate
}

public record RvVFpNCvt(VFpNCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;