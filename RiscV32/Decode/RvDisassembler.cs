namespace RiscV32.Decode;

public static class RvDisassembler {
    private static string Xi(int r) => $"x{r & 31}";

    // FENCE ordering-set mask → "iorw" letters (bit 3 = I, 2 = O, 1 = R, 0 = W).
    private static string IoRw(uint mask) =>
        $"{((mask & 8) != 0 ? "i" : "")}{((mask & 4) != 0 ? "o" : "")}{((mask & 2) != 0 ? "r" : "")}{((mask & 1) != 0 ? "w" : "")}";

    private static string Xf(int r) => $"f{(r - 32) & 31}";
    private static string Tgt(ulong pc, int imm) => $"0x{(ulong)((long)pc + imm):X}";

    public static string Disassemble(object? payload, ulong pc) => payload switch {
        // R-type
        RvAdd op  => $"add {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSub op  => $"sub {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvXor op  => $"xor {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvOr op   => $"or {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvAnd op  => $"and {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSll op  => $"sll {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSrl op  => $"srl {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSra op  => $"sra {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSlt op  => $"slt {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvSltu op => $"sltu {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",

        // I-type ALU
        RvAddi op  => $"addi {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",
        RvXori op  => $"xori {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",
        RvOri op   => $"ori {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",
        RvAndi op  => $"andi {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",
        RvSlli op  => $"slli {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Shamt}",
        RvSrli op  => $"srli {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Shamt}",
        RvSrai op  => $"srai {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Shamt}",
        RvSlti op  => $"slti {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",
        RvSltiu op => $"sltiu {Xi(op.Rd)}, {Xi(op.Rs1)}, {op.Imm}",

        // Loads
        RvLb op  => $"lb {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",
        RvLh op  => $"lh {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",
        RvLw op  => $"lw {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",
        RvLbu op => $"lbu {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",
        RvLhu op => $"lhu {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",

        // Stores
        RvSb op => $"sb {Xi(op.Rs2)}, {op.Imm}({Xi(op.Rs1)})",
        RvSh op => $"sh {Xi(op.Rs2)}, {op.Imm}({Xi(op.Rs1)})",
        RvSw op => $"sw {Xi(op.Rs2)}, {op.Imm}({Xi(op.Rs1)})",

        // Branches
        RvBeq op  => $"beq {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",
        RvBne op  => $"bne {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",
        RvBlt op  => $"blt {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",
        RvBge op  => $"bge {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",
        RvBltu op => $"bltu {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",
        RvBgeu op => $"bgeu {Xi(op.Rs1)}, {Xi(op.Rs2)}, {Tgt(pc, op.Imm)}",

        // Jumps
        RvJal op  => $"jal {Xi(op.Rd)}, {Tgt(pc, op.Imm)}",
        RvJalr op => $"jalr {Xi(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",

        // Upper immediates
        RvLui op   => $"lui {Xi(op.Rd)}, 0x{(uint)op.Imm >> 12:X}",
        RvAuipc op => $"auipc {Xi(op.Rd)}, 0x{(uint)op.Imm >> 12:X}",

        // System
        RvEcall     => "ecall",
        RvEbreak    => "ebreak",
        RvFence op  => op.Fm == 0x8 ? "fence.tso" : $"fence {IoRw(op.Pred)},{IoRw(op.Succ)}",
        RvFenceI    => "fence.i",
        RvSfenceVma => "sfence.vma",
        RvMret      => "mret",
        RvSret      => "sret",
        RvWfi       => "wfi",
        RvCsrrw op  => $"csrrw {Xi(op.Rd)}, {CsrName(op.Csr)}, {Xi(op.Rs1)}",
        RvCsrrs op  => $"csrrs {Xi(op.Rd)}, {CsrName(op.Csr)}, {Xi(op.Rs1)}",
        RvCsrrc op  => $"csrrc {Xi(op.Rd)}, {CsrName(op.Csr)}, {Xi(op.Rs1)}",
        RvCsrrwi op => $"csrrwi {Xi(op.Rd)}, {CsrName(op.Csr)}, {op.Zimm}",
        RvCsrrsi op => $"csrrsi {Xi(op.Rd)}, {CsrName(op.Csr)}, {op.Zimm}",
        RvCsrrci op => $"csrrci {Xi(op.Rd)}, {CsrName(op.Csr)}, {op.Zimm}",

        // M extension
        RvMul op    => $"mul {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvMulh op   => $"mulh {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvMulhsu op => $"mulhsu {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvMulhu op  => $"mulhu {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvDiv op    => $"div {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvDivu op   => $"divu {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvRem op    => $"rem {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvRemu op   => $"remu {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",

        // F extension — loads/stores
        RvFlw op => $"flw {Xf(op.Rd)}, {op.Imm}({Xi(op.Rs1)})",
        RvFsw op => $"fsw {Xf(op.Rs2)}, {op.Imm}({Xi(op.Rs1)})",

        // F extension — arithmetic
        RvFaddS op   => $"fadd.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFsubS op   => $"fsub.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFmulS op   => $"fmul.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFdivS op   => $"fdiv.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFsqrtS op  => $"fsqrt.s {Xf(op.Rd)}, {Xf(op.Rs1)}",
        RvFsgnjS op  => $"fsgnj.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFsgnjnS op => $"fsgnjn.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFsgnjxS op => $"fsgnjx.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFminS op   => $"fmin.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFmaxS op   => $"fmax.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFeqS op    => $"feq.s {Xi(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFltS op    => $"flt.s {Xi(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFleS op    => $"fle.s {Xi(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}",
        RvFclassS op => $"fclass.s {Xi(op.Rd)}, {Xf(op.Rs1)}",
        RvFcvtWs op  => $"fcvt.w.s {Xi(op.Rd)}, {Xf(op.Rs1)}",
        RvFcvtWuS op => $"fcvt.wu.s {Xi(op.Rd)}, {Xf(op.Rs1)}",
        RvFcvtSw op  => $"fcvt.s.w {Xf(op.Rd)}, {Xi(op.Rs1)}",
        RvFcvtSWu op => $"fcvt.s.wu {Xf(op.Rd)}, {Xi(op.Rs1)}",
        RvFmvXw op   => $"fmv.x.w {Xi(op.Rd)}, {Xf(op.Rs1)}",
        RvFmvWx op   => $"fmv.w.x {Xf(op.Rd)}, {Xi(op.Rs1)}",
        RvFmaddS op  => $"fmadd.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}, {Xf(op.Rs3)}",
        RvFmsubS op  => $"fmsub.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}, {Xf(op.Rs3)}",
        RvFnmsubS op => $"fnmsub.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}, {Xf(op.Rs3)}",
        RvFnmaddS op => $"fnmadd.s {Xf(op.Rd)}, {Xf(op.Rs1)}, {Xf(op.Rs2)}, {Xf(op.Rs3)}",

        // A extension
        RvLrW op      => $"lr.w {Xi(op.Rd)}, ({Xi(op.Rs1)})",
        RvScW op      => $"sc.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoswapW op => $"amoswap.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoaddW op  => $"amoadd.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoxorW op  => $"amoxor.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoandW op  => $"amoand.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoorW op   => $"amoor.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominW op  => $"amomin.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxW op  => $"amomax.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominuW op => $"amominu.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxuW op => $"amomaxu.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",

        // Zacas extension
        RvAmocasW op => $"amocas.w {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",

        // Zabha extension — byte variants
        RvAmoswapB op => $"amoswap.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoaddB op  => $"amoadd.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoxorB op  => $"amoxor.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoandB op  => $"amoand.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoorB op   => $"amoor.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominB op  => $"amomin.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxB op  => $"amomax.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominuB op => $"amominu.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxuB op => $"amomaxu.b {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",

        // Zabha extension — halfword variants
        RvAmoswapH op => $"amoswap.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoaddH op  => $"amoadd.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoxorH op  => $"amoxor.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoandH op  => $"amoand.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmoorH op   => $"amoor.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominH op  => $"amomin.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxH op  => $"amomax.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmominuH op => $"amominu.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",
        RvAmomaxuH op => $"amomaxu.h {Xi(op.Rd)}, {Xi(op.Rs2)}, ({Xi(op.Rs1)})",

        // Zcmop extension
        RvCMopN op => $"c.mop.{op.N}",

        // V extension
        RvVsetvli op  => $"vsetvli {Xi(op.Rd)}, {Xi(op.Rs1)}, {VtypeStr(op.Vtypei)}",
        RvVsetivli op => $"vsetivli {Xi(op.Rd)}, {op.Zimm}, {VtypeStr(op.Vtypei)}",
        RvVsetvl op   => $"vsetvl {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvVleVv op    => $"vle{op.Sew}.v v{op.Vd}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVlm op      => $"vlm.v v{op.Vd}, ({Xi(op.Rs1)})",
        RvVlrV op     => $"vl{op.NumRegs}r.v v{op.Vd}, ({Xi(op.Rs1)})",
        RvVlsegVv op  => $"vlseg{op.NumFields}e{op.Sew}.v v{op.Vd}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVlseVv op   => $"vlse{op.Sew}.v v{op.Vd}, ({Xi(op.Rs1)}), {Xi(op.Rs2)}{MaskSuffix(op.Masked)}",
        RvVlxeiVv op =>
            $"{(op.Ordered ? "vlox" : "vlux")}ei{op.IndexSew}.v v{op.Vd}, ({Xi(op.Rs1)}), v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVleFf op => $"vle{op.Sew}ff.v v{op.Vd}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVlssegVv op =>
            $"vlsseg{op.NumFields}e{op.Sew}.v v{op.Vd}, ({Xi(op.Rs1)}), {Xi(op.Rs2)}{MaskSuffix(op.Masked)}",
        RvVlxsegVv op =>
            $"{(op.Ordered ? "vloxseg" : "vluxseg")}{op.NumFields}ei{op.IndexSew}.v v{op.Vd}, ({Xi(op.Rs1)}), v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVseVv op   => $"vse{op.Sew}.v v{op.Vs3}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVsm op     => $"vsm.v v{op.Vs3}, ({Xi(op.Rs1)})",
        RvVsrV op    => $"vs{op.NumRegs}r.v v{op.Vs3}, ({Xi(op.Rs1)})",
        RvVssegVv op => $"vsseg{op.NumFields}e{op.Sew}.v v{op.Vs3}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVsseVv op  => $"vsse{op.Sew}.v v{op.Vs3}, ({Xi(op.Rs1)}), {Xi(op.Rs2)}{MaskSuffix(op.Masked)}",
        RvVsxeiVv op =>
            $"{(op.Ordered ? "vsox" : "vsux")}ei{op.IndexSew}.v v{op.Vs3}, ({Xi(op.Rs1)}), v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVsssegVv op =>
            $"vssseg{op.NumFields}e{op.Sew}.v v{op.Vs3}, ({Xi(op.Rs1)}), {Xi(op.Rs2)}{MaskSuffix(op.Masked)}",
        RvVsxsegVv op =>
            $"{(op.Ordered ? "vsoxseg" : "vsuxseg")}{op.NumFields}ei{op.IndexSew}.v v{op.Vs3}, ({Xi(op.Rs1)}), v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVIntAluVv op  => $"{VIntStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVIntAluVx op  => $"{VIntStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVIntAluVi op  => $"{VIntStr(op.Op)}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVv op => $"vms{VMaskStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVx op => $"vms{VMaskStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVi op => $"vms{VMaskStr(op.Op)}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",
        RvVMulVv op     => $"{VMulStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVMulVx op     => $"{VMulStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVRedVs op     => $"{VRedStr(op.Op)}.vs v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVWideVv op =>
            $"{VWideStr(op.Op)}.{(op.Vs2IsWide ? "wv" : "vv")} v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVWideVx op =>
            $"{VWideStr(op.Op)}.{(op.Vs2IsWide ? "wx" : "vx")} v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVNarrVv op => $"{VNarrStr(op.Op)}.wv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVNarrVx op => $"{VNarrStr(op.Op)}.wx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVNarrVi op => $"{VNarrStr(op.Op)}.wi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",
        RvVSlideVx op =>
            $"vslide{(op.Dir == VSlideDir.Up ? "up" : "down")}{(op.Is1 ? "1" : "")}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVFpSlide1Vf op =>
            $"vfslide1{(op.Dir == VSlideDir.Up ? "up" : "down")}.vf v{op.Vd}, v{op.Vs2}, {Xf(op.FpRs1)}{MaskSuffix(op.Masked)}",
        RvVSlideVi op =>
            $"vslide{(op.Dir == VSlideDir.Up ? "up" : "down")}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",
        RvVRgatherVv op     => $"vrgather.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVRgatherEi16Vv op => $"vrgatherei16.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVRgatherVx op     => $"vrgather.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVRgatherVi op     => $"vrgather.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",

        RvVMvXs op      => $"vmv.x.s {Xi(op.Rd)}, v{op.Vs2}",
        RvVMvSx op      => $"vmv.s.x v{op.Vd}, {Xi(op.Rs1)}",
        RvVIntMacVv op  => $"{VMacStr(op.Op)}.vv v{op.Vd}, v{op.Vs1}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVIntMacVx op  => $"{VMacStr(op.Op)}.vx v{op.Vd}, {Xi(op.Rs1)}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVwMacVv op    => $"{VwMacStr(op.Op)}.vv v{op.Vd}, v{op.Vs1}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVwMacVx op    => $"{VwMacStr(op.Op)}.vx v{op.Vd}, {Xi(op.Rs1)}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVMergeVv op   => $"vmerge.vvm v{op.Vd}, v{op.Vs2}, v{op.Vs1}, v0",
        RvVMergeVx op   => $"vmerge.vxm v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}, v0",
        RvVMergeVi op   => $"vmerge.vim v{op.Vd}, v{op.Vs2}, {op.Imm}, v0",
        RvVFpMergeVf op => $"vfmerge.vfm v{op.Vd}, v{op.Vs2}, {Xf(op.FpRs1)}, v0",
        RvVMaskLogMm op => $"{VMaskLogStr(op.Op)}.mm v{op.Vd}, v{op.Vs2}, v{op.Vs1}",
        RvVcpop op      => $"vcpop.m {Xi(op.Rd)}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVfirst op     => $"vfirst.m {Xi(op.Rd)}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVMaskUnary op => op.Op == VMaskUnaryOp.Id
            ? $"vid.v v{op.Vd}{MaskSuffix(op.Masked)}"
            : $"{VMaskUnaryStr(op.Op)} v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVCompress op => $"vcompress.vm v{op.Vd}, v{op.Vs2}, v{op.Vs1}",
        RvVMvNr op     => $"vmv{op.NumRegs}r.v v{op.Vd}, v{op.Vs2}",

        RvVFpBinVv op  => $"{VFpBinStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVFpBinVf op  => $"{VFpBinStr(op.Op)}.vf v{op.Vd}, v{op.Vs2}, {Xf(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVFpFmaVv op  => $"{VFpFmaStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVFpFmaVf op  => $"{VFpFmaStr(op.Op)}.vf v{op.Vd}, v{op.Vs2}, {Xf(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVmFpCmpVv op => $"vmf{VFpCmpStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVmFpCmpVf op => $"vmf{VFpCmpStr(op.Op)}.vf v{op.Vd}, v{op.Vs2}, {Xf(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVFpSqrt op   => $"vfsqrt.v v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpClass op  => $"vfclass.v v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpCvt op    => $"{VFpCvtStr(op.Op)} v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpMvFs op   => $"vfmv.f.s {Xf(op.Rd)}, v{op.Vs2}",
        RvVFpMvSf op   => $"vfmv.s.f v{op.Vd}, {Xf(op.Rs1)}",
        RvVFpMvVf op   => $"vfmv.v.f v{op.Vd}, {Xf(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVFpRedVs op  => $"{VFpRedStr(op.Op)}.vs v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVWideRedVs op =>
            $"{(op.Signed ? "vwredsum" : "vwredsumu")}.vs v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVExt op   => $"{(op.Signed ? "vsext" : "vzext")}.vf{op.Factor} v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVAvgVv op => $"{VAvgStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVAvgVx op => $"{VAvgStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVFpWideRedVs op =>
            $"{(op.Ordered ? "vfwredosum" : "vfwredusum")}.vs v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",

        RvVSatIntVv op => $"{VSatIntStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVSatIntVx op => $"{VSatIntStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVSatIntVi op => $"{VSatIntStr(op.Op)}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",

        RvVnClipVv op => $"{VnClipStr(op.Op)}.wv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVnClipVx op => $"{VnClipStr(op.Op)}.wx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVnClipVi op => $"{VnClipStr(op.Op)}.wi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",

        RvVFpWArithVv op =>
            $"vfw{VFpWArithStr(op.Op)}.{(op.Vs2Wide ? "w" : "v")}v v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVFpWArithVf op =>
            $"vfw{VFpWArithStr(op.Op)}.{(op.Vs2Wide ? "w" : "v")}f v{op.Vd}, v{op.Vs2}, {Xf(op.Rs1)}{MaskSuffix(op.Masked)}",
        // vd, vs1, vs2 order matches assembly syntax for widening MAC
        RvVFpWMacVv op => $"{VFpWMacStr(op.Op)}.vv v{op.Vd}, v{op.Vs1}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpWMacVf op => $"{VFpWMacStr(op.Op)}.vf v{op.Vd}, {Xf(op.Rs1)}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpWCvt op   => $"{VFpWCvtStr(op.Op)} v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",
        RvVFpNCvt op   => $"{VFpNCvtStr(op.Op)} v{op.Vd}, v{op.Vs2}{MaskSuffix(op.Masked)}",

        null => "???",
        _    => payload.GetType().Name,
    };

    private static string MaskSuffix(bool masked) => masked ? ", v0.t" : "";

    private static string VIntStr(VIntOp op) => op switch {
        VIntOp.Add  => "vadd", VIntOp.Sub  => "vsub", VIntOp.Rsub => "vrsub",
        VIntOp.And  => "vand", VIntOp.Or   => "vor", VIntOp.Xor   => "vxor",
        VIntOp.Sll  => "vsll", VIntOp.Srl  => "vsrl", VIntOp.Sra  => "vsra",
        VIntOp.Minu => "vminu", VIntOp.Min => "vmin",
        VIntOp.Maxu => "vmaxu", VIntOp.Max => "vmax",
        _           => "v?",
    };

    private static string VMacStr(VIntMacOp op) => op switch {
        VIntMacOp.Macc  => "vmacc",
        VIntMacOp.Nmsac => "vnmsac",
        VIntMacOp.Madd  => "vmadd",
        VIntMacOp.Nmsub => "vnmsub",
        _               => "v?",
    };

    private static string VwMacStr(VwMacOp op) => op switch {
        VwMacOp.Macc   => "vwmacc", VwMacOp.Maccu    => "vwmaccu",
        VwMacOp.Maccsu => "vwmaccsu", VwMacOp.Maccus => "vwmaccus",
        _              => "vwm?",
    };

    private static string VMulStr(VMulOp op) => op switch {
        VMulOp.Mul   => "vmul", VMulOp.MulH     => "vmulh",
        VMulOp.MulHu => "vmulhu", VMulOp.MulHsu => "vmulhsu",
        VMulOp.Div   => "vdiv", VMulOp.Divu     => "vdivu",
        VMulOp.Rem   => "vrem", VMulOp.Remu     => "vremu",
        _            => "v?",
    };

    private static string VRedStr(VRedOp op) => op switch {
        VRedOp.Sum  => "vredsum", VRedOp.And  => "vredand",
        VRedOp.Or   => "vredor", VRedOp.Xor   => "vredxor",
        VRedOp.Minu => "vredminu", VRedOp.Min => "vredmin",
        VRedOp.Maxu => "vredmaxu", VRedOp.Max => "vredmax",
        _           => "v?",
    };

    private static string VWideStr(VWideOp op) => op switch {
        VWideOp.AddU => "vwaddu", VWideOp.Add   => "vwadd",
        VWideOp.SubU => "vwsubu", VWideOp.Sub   => "vwsub",
        VWideOp.MulU => "vwmulu", VWideOp.MulSu => "vwmulsu", VWideOp.Mul => "vwmul",
        _            => "vw?",
    };

    private static string VNarrStr(VNarrOp op) => op switch {
        VNarrOp.Srl => "vnsrl", VNarrOp.Sra => "vnsra",
        _           => "vn?",
    };

    private static string VMaskLogStr(VMaskLogOp op) => op switch {
        VMaskLogOp.Andn => "vmandn", VMaskLogOp.And => "vmand",
        VMaskLogOp.Or   => "vmor", VMaskLogOp.Xor   => "vmxor",
        VMaskLogOp.Orn  => "vmorn", VMaskLogOp.Nand => "vmnand",
        VMaskLogOp.Nor  => "vmnor", VMaskLogOp.Xnor => "vmxnor",
        _               => "vm?",
    };

    private static string VMaskUnaryStr(VMaskUnaryOp op) => op switch {
        VMaskUnaryOp.Msbf => "vmsbf.m", VMaskUnaryOp.Msof => "vmsof.m",
        VMaskUnaryOp.Msif => "vmsif.m", VMaskUnaryOp.Iota => "viota.m",
        VMaskUnaryOp.Id   => "vid.v",
        _                 => "vm?",
    };

    private static string VMaskStr(VMaskCmpOp op) => op switch {
        VMaskCmpOp.Eq  => "eq", VMaskCmpOp.Ne  => "ne",
        VMaskCmpOp.Ltu => "ltu", VMaskCmpOp.Lt => "lt",
        VMaskCmpOp.Gtu => "gtu", VMaskCmpOp.Gt => "gt",
        _              => "?",
    };

    private static string VFpBinStr(VFpBinOp op) => op switch {
        VFpBinOp.Add  => "vfadd", VFpBinOp.Sub    => "vfsub",
        VFpBinOp.Mul  => "vfmul", VFpBinOp.Div    => "vfdiv",
        VFpBinOp.Min  => "vfmin", VFpBinOp.Max    => "vfmax",
        VFpBinOp.Sgnj => "vfsgnj", VFpBinOp.Sgnjn => "vfsgnjn", VFpBinOp.Sgnjx => "vfsgnjx",
        _             => "vf?",
    };

    private static string VFpFmaStr(VFpFmaOp op) => op switch {
        VFpFmaOp.Macc => "vfmacc", VFpFmaOp.Nmacc => "vfnmacc",
        VFpFmaOp.Msac => "vfmsac", VFpFmaOp.Nmsac => "vfnmsac",
        VFpFmaOp.Madd => "vfmadd", VFpFmaOp.Nmadd => "vfnmadd",
        VFpFmaOp.Msub => "vfmsub", VFpFmaOp.Nmsub => "vfnmsub",
        _             => "vfm?",
    };

    private static string VFpCmpStr(VFpCmpOp op) => op switch {
        VFpCmpOp.Eq => "eq", VFpCmpOp.Le => "le", VFpCmpOp.Lt => "lt",
        VFpCmpOp.Ne => "ne", VFpCmpOp.Gt => "gt", VFpCmpOp.Ge => "ge",
        _           => "?",
    };

    private static string VFpRedStr(VFpRedOp op) => op switch {
        VFpRedOp.Usum => "vfredusum", VFpRedOp.Osum => "vfredosum",
        VFpRedOp.Min  => "vfredmin", VFpRedOp.Max   => "vfredmax",
        _             => "vfr?",
    };

    private static string VSatIntStr(VSatIntOp op) => op switch {
        VSatIntOp.Sadd => "vsadd", VSatIntOp.Saddu => "vsaddu",
        VSatIntOp.Ssub => "vssub", VSatIntOp.Ssubu => "vssubu",
        VSatIntOp.Smul => "vsmul",
        VSatIntOp.Ssrl => "vssrl", VSatIntOp.Ssra => "vssra",
        _              => "vs?",
    };

    private static string VnClipStr(VnClipOp op) => op switch {
        VnClipOp.Clipu => "vnclipu", VnClipOp.Clip => "vnclip",
        _              => "vnc?",
    };

    private static string VFpCvtStr(VFpCvtOp op) => op switch {
        VFpCvtOp.XuFromF    => "vfcvt.xu.f.v", VFpCvtOp.XFromF => "vfcvt.x.f.v",
        VFpCvtOp.FFromXu    => "vfcvt.f.xu.v", VFpCvtOp.FFromX => "vfcvt.f.x.v",
        VFpCvtOp.RtzXuFromF => "vfcvt.rtz.xu.f.v",
        VFpCvtOp.RtzXFromF  => "vfcvt.rtz.x.f.v",
        _                   => "vfcvt.?.?.v",
    };

    private static string VAvgStr(VAvgOp op) => op switch {
        VAvgOp.Addu => "vaaddu", VAvgOp.Add => "vaadd",
        VAvgOp.Subu => "vasubu", VAvgOp.Sub => "vasub",
        _           => "va?",
    };

    private static string VFpWArithStr(VFpWideArithOp op) => op switch {
        VFpWideArithOp.Add => "add", VFpWideArithOp.Sub => "sub",
        VFpWideArithOp.Mul => "mul", _                  => "?",
    };

    private static string VFpWMacStr(VFpWMacOp op) => op switch {
        VFpWMacOp.Macc => "vfwmacc", VFpWMacOp.Nmacc => "vfwnmacc",
        VFpWMacOp.Msac => "vfwmsac", VFpWMacOp.Nmsac => "vfwnmsac",
        _              => "vfw?",
    };

    private static string VFpWCvtStr(VFpWCvtOp op) => op switch {
        VFpWCvtOp.XuFromF    => "vfwcvt.xu.f.v", VFpWCvtOp.XFromF => "vfwcvt.x.f.v",
        VFpWCvtOp.FFromXu    => "vfwcvt.f.xu.v", VFpWCvtOp.FFromX => "vfwcvt.f.x.v",
        VFpWCvtOp.FFromF     => "vfwcvt.f.f.v",
        VFpWCvtOp.RtzXuFromF => "vfwcvt.rtz.xu.f.v", VFpWCvtOp.RtzXFromF => "vfwcvt.rtz.x.f.v",
        _                    => "vfwcvt.?",
    };

    private static string VFpNCvtStr(VFpNCvtOp op) => op switch {
        VFpNCvtOp.XuFromF    => "vfncvt.xu.f.w", VFpNCvtOp.XFromF        => "vfncvt.x.f.w",
        VFpNCvtOp.FFromXu    => "vfncvt.f.xu.w", VFpNCvtOp.FFromX        => "vfncvt.f.x.w",
        VFpNCvtOp.FFromF     => "vfncvt.f.f.w", VFpNCvtOp.RodFFromF      => "vfncvt.rod.f.f.w",
        VFpNCvtOp.RtzXuFromF => "vfncvt.rtz.xu.f.w", VFpNCvtOp.RtzXFromF => "vfncvt.rtz.x.f.w",
        _                    => "vfncvt.?",
    };

    private static string VtypeStr(int vtypei) {
        int sew = 8 << ((vtypei >> 3) & 0x7);
        string lmul = (vtypei & 0x7) switch {
            0 => "m1", 1  => "m2", 2  => "m4", 3  => "m8",
            5 => "mf8", 6 => "mf4", 7 => "mf2", _ => "m?",
        };
        return $"e{sew},{lmul}";
    }

    private static string CsrName(uint csr) => csr switch {
        0x001 => "fflags", 0x002   => "frm", 0x003  => "fcsr",
        0x100 => "sstatus", 0x104  => "sie", 0x105  => "stvec",
        0x140 => "sscratch", 0x141 => "sepc", 0x142 => "scause",
        0x143 => "stval", 0x144    => "sip", 0x180  => "satp",
        0x300 => "mstatus", 0x301  => "misa", 0x302 => "medeleg",
        0x303 => "mideleg", 0x304  => "mie", 0x305  => "mtvec",
        0x340 => "mscratch", 0x341 => "mepc", 0x342 => "mcause",
        0x343 => "mtval", 0x344    => "mip",
        0xC00 => "cycle", 0xC01    => "time", 0xC02 => "instret",
        _     => $"0x{csr:X3}",
    };
}