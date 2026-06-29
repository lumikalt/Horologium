namespace RiscV32.Decode;

public static class RvDisassembler {
    private static string Xi(int r) => $"x{r & 31}";
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
        RvFence     => "fence",
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
        RvVsetvli op    => $"vsetvli {Xi(op.Rd)}, {Xi(op.Rs1)}, {VtypeStr(op.Vtypei)}",
        RvVsetivli op   => $"vsetivli {Xi(op.Rd)}, {op.Zimm}, {VtypeStr(op.Vtypei)}",
        RvVsetvl op     => $"vsetvl {Xi(op.Rd)}, {Xi(op.Rs1)}, {Xi(op.Rs2)}",
        RvVleVv op      => $"vle{op.Sew}.v v{op.Vd}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVlm op        => $"vlm.v v{op.Vd}, ({Xi(op.Rs1)})",
        RvVseVv op      => $"vse{op.Sew}.v v{op.Vs3}, ({Xi(op.Rs1)}){MaskSuffix(op.Masked)}",
        RvVsm op        => $"vsm.v v{op.Vs3}, ({Xi(op.Rs1)})",
        RvVIntAluVv op  => $"{VIntStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVIntAluVx op  => $"{VIntStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVIntAluVi op  => $"{VIntStr(op.Op)}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVv op => $"vms{VMaskStr(op.Op)}.vv v{op.Vd}, v{op.Vs2}, v{op.Vs1}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVx op => $"vms{VMaskStr(op.Op)}.vx v{op.Vd}, v{op.Vs2}, {Xi(op.Rs1)}{MaskSuffix(op.Masked)}",
        RvVMaskCmpVi op => $"vms{VMaskStr(op.Op)}.vi v{op.Vd}, v{op.Vs2}, {op.Imm}{MaskSuffix(op.Masked)}",

        null => "???",
        _    => payload.GetType().Name,
    };

    private static string MaskSuffix(bool masked) => masked ? ", v0.t" : "";

    private static string VIntStr(VIntOp op) => op switch {
        VIntOp.Add => "vadd", VIntOp.Sub => "vsub",
        VIntOp.And => "vand", VIntOp.Or  => "vor", VIntOp.Xor  => "vxor",
        VIntOp.Sll => "vsll", VIntOp.Srl => "vsrl", VIntOp.Sra => "vsra",
        _          => "v?",
    };

    private static string VMaskStr(VMaskCmpOp op) => op switch {
        VMaskCmpOp.Eq  => "eq", VMaskCmpOp.Ne  => "ne",
        VMaskCmpOp.Ltu => "ltu", VMaskCmpOp.Lt => "lt",
        VMaskCmpOp.Gtu => "gtu", VMaskCmpOp.Gt => "gt",
        _              => "?",
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