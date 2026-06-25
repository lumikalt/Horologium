using Mechanism;
using RiscV.Decode;

namespace Tests.RiscV;

public class DecoderTests {
    private readonly RvDecoder _dec = new();

    // Helper: decode a raw word directly
    private ITooth D(uint raw, ulong pc = 0) => _dec.Decode(pc, raw);

    // ── R-type ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Add() {
        // add x1, x2, x3  →  0x003100B3
        ITooth i = D(0x003100B3);
        Assert.IsType<RvAdd>(i.Payload);
        var op = (RvAdd)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(ToothClass.IntegerAlu, i.Class);
    }

    [Fact]
    public void Decode_Sub() {
        // sub x1, x2, x3  →  0x403100B3
        ITooth i = D(0x403100B3);
        Assert.IsType<RvSub>(i.Payload);
    }

    [Fact]
    public void Decode_Sll() {
        // sll x5, x6, x7  →  0x007312B3
        ITooth i = D(0x007312B3);
        Assert.IsType<RvSll>(i.Payload);
        var op = (RvSll)i.Payload!;
        Assert.Equal(5, op.Rd);
        Assert.Equal(6, op.Rs1);
        Assert.Equal(7, op.Rs2);
    }

    [Fact]
    public void Decode_Sra() {
        // sra x1, x2, x3  →  0x403150B3
        ITooth i = D(0x403150B3);
        Assert.IsType<RvSra>(i.Payload);
    }

    // ── I-type ALU ────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Addi() {
        // addi x1, x2, 42  →  0x02A10093  (imm=42=0x02A, rs1=2, rd=1)
        ITooth i = D(0x02A10093);
        Assert.IsType<RvAddi>(i.Payload);
        var op = (RvAddi)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(42, op.Imm);
    }

    [Fact]
    public void Decode_Addi_NegativeImmediate() {
        // addi x1, x0, -1  →  0xFFF00093
        ITooth i = D(0xFFF00093);
        var op = (RvAddi)i.Payload!;
        Assert.Equal(-1, op.Imm);
    }

    [Fact]
    public void Decode_Srai() {
        // srai x1, x2, 5  →  funct7=0x20, shamt=5
        // 0x40515093
        ITooth i = D(0x40515093);
        Assert.IsType<RvSrai>(i.Payload);
        var op = (RvSrai)i.Payload!;
        Assert.Equal(5, op.Shamt);
    }

    // ── Loads ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Lw() {
        // lw x1, 8(x2)  →  0x00812083
        ITooth i = D(0x00812083);
        Assert.IsType<RvLw>(i.Payload);
        var op = (RvLw)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.Load, i.Class);
    }

    [Fact]
    public void Decode_Lb_NegativeOffset() {
        // lb x1, -4(x2)  →  imm=-4
        // 0xFFC10083
        ITooth i = D(0xFFC10083);
        var op = (RvLb)i.Payload!;
        Assert.Equal(-4, op.Imm);
    }

    // ── Stores ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Sw() {
        // sw x3, 8(x2)  →  0x00312423
        ITooth i = D(0x00312423);
        Assert.IsType<RvSw>(i.Payload);
        var op = (RvSw)i.Payload!;
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.Store, i.Class);
        Assert.Equal(-1, i.DestinationRegister);
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Beq() {
        // beq x1, x2, +8  →  0x00208463
        ITooth i = D(0x00208463);
        Assert.IsType<RvBeq>(i.Payload);
        var op = (RvBeq)i.Payload!;
        Assert.Equal(1, op.Rs1);
        Assert.Equal(2, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.ConditionalBranch, i.Class);
    }

    [Fact]
    public void Decode_Bne() {
        // bne x1, x2, +4  →  0x00209263
        ITooth i = D(0x00209263);
        Assert.IsType<RvBne>(i.Payload);
    }

    // ── JAL / JALR ────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Jal() {
        // jal x1, +8  →  0x008000EF
        ITooth i = D(0x008000EF);
        Assert.IsType<RvJal>(i.Payload);
        var op = (RvJal)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.Branch, i.Class);
    }

    [Fact]
    public void Decode_Jalr() {
        // jalr x1, 4 (x2) →  0x004100e7  (rd=1, imm=4, funct3=0)
        ITooth i = D(0x004100e7);
        Assert.IsType<RvJalr>(i.Payload);
        var op = (RvJalr)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(4, op.Imm);
    }

    // ── U-type ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Lui() {
        // lui x1, 0x12345  →  0x123450B7
        ITooth i = D(0x123450B7);
        Assert.IsType<RvLui>(i.Payload);
        var op = (RvLui)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(0x12345000, op.Imm);
    }

    [Fact]
    public void Decode_Auipc() {
        // auipc x1, 1  →  0x00001097
        ITooth i = D(0x00001097, 0x1000);
        Assert.IsType<RvAuipc>(i.Payload);
        var op = (RvAuipc)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(0x1000, op.Imm);
    }

    // ── System ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Ecall() {
        ITooth i = D(0x00000073);
        Assert.IsType<RvEcall>(i.Payload);
        Assert.Equal(ToothClass.System, i.Class);
    }

    [Fact]
    public void Decode_Ebreak() {
        ITooth i = D(0x00100073);
        Assert.IsType<RvEbreak>(i.Payload);
    }

    [Fact]
    public void Decode_Mret() {
        // mret  →  0x30200073
        ITooth i = D(0x30200073);
        Assert.IsType<RvMret>(i.Payload);
    }

    [Fact]
    public void Decode_Csrrw() {
        // csrrw x1, mstatus, x2  →  csr=0x300, rd=1, rs1=2
        // 0x300110F3
        ITooth i = D(0x300110F3);
        Assert.IsType<RvCsrrw>(i.Payload);
        var op = (RvCsrrw)i.Payload!;
        Assert.Equal(0x300u, op.Csr);
    }

    [Fact]
    public void Decode_Fence() {
        // fence  →  0x0000000F
        ITooth i = D(0x0000000F);
        Assert.IsType<RvFence>(i.Payload);
        Assert.Equal(ToothClass.Fence, i.Class);
    }

    // ── Source/destination register fields ────────────────────────────────────

    [Fact]
    public void Decode_SourceRegisters_RType() {
        ITooth i = D(0x003100B3); // add x1, x2, x3
        Assert.Equal([2, 3,], i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Store() {
        ITooth i = D(0x00312423); // sw x3, 8(x2)
        Assert.Equal([2, 3,], i.SourceRegisters);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Jal() {
        ITooth i = D(0x008000EF); // jal x1, +8
        Assert.Empty(i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Branch() {
        ITooth i = D(0x00208463); // beq x1, x2, +8
        Assert.Equal([1, 2,], i.SourceRegisters);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Load() {
        ITooth i = D(0x00812083); // lw x1, 8(x2)
        Assert.Equal([2,], i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    // ── R-type (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Decode_Srl() {
        // srl x5, x6, x7  →  0x007352B3
        ITooth i = D(0x007352B3);
        Assert.IsType<RvSrl>(i.Payload);
        var op = (RvSrl)i.Payload!;
        Assert.Equal(5, op.Rd);
        Assert.Equal(6, op.Rs1);
        Assert.Equal(7, op.Rs2);
    }

    [Fact]
    public void Decode_And() {
        // and x3, x1, x2  →  0x0020F1B3
        ITooth i = D(0x0020F1B3);
        Assert.IsType<RvAnd>(i.Payload);
        var op = (RvAnd)i.Payload!;
        Assert.Equal(3, op.Rd);
        Assert.Equal(1, op.Rs1);
        Assert.Equal(2, op.Rs2);
    }

    [Fact]
    public void Decode_Or() {
        // or x3, x1, x2  →  0x0020E1B3
        ITooth i = D(0x0020E1B3);
        Assert.IsType<RvOr>(i.Payload);
    }

    [Fact]
    public void Decode_Xor() {
        // xor x3, x1, x2  →  0x0020C1B3
        ITooth i = D(0x0020C1B3);
        Assert.IsType<RvXor>(i.Payload);
    }

    [Fact]
    public void Decode_Slt() {
        // slt x3, x1, x2  →  0x0020A1B3
        ITooth i = D(0x0020A1B3);
        Assert.IsType<RvSlt>(i.Payload);
    }

    [Fact]
    public void Decode_Sltu() {
        // sltu x3, x1, x2  →  0x0020B1B3
        ITooth i = D(0x0020B1B3);
        Assert.IsType<RvSltu>(i.Payload);
    }

    // ── I-type ALU (additional) ───────────────────────────────────────────────

    [Fact]
    public void Decode_Slli() {
        // slli x1, x2, 3  →  0x00311093
        ITooth i = D(0x00311093);
        Assert.IsType<RvSlli>(i.Payload);
        var op = (RvSlli)i.Payload!;
        Assert.Equal(3, op.Shamt);
    }

    [Fact]
    public void Decode_Srli() {
        // srli x1, x2, 3  →  0x00315093
        ITooth i = D(0x00315093);
        Assert.IsType<RvSrli>(i.Payload);
        var op = (RvSrli)i.Payload!;
        Assert.Equal(3, op.Shamt);
    }

    [Fact]
    public void Decode_Ori() {
        // ori x1, x2, 15  →  0x00F16093
        ITooth i = D(0x00F16093);
        Assert.IsType<RvOri>(i.Payload);
        var op = (RvOri)i.Payload!;
        Assert.Equal(15, op.Imm);
    }

    [Fact]
    public void Decode_Xori() {
        // xori x1, x2, 15  →  0x00F14093
        ITooth i = D(0x00F14093);
        Assert.IsType<RvXori>(i.Payload);
        var op = (RvXori)i.Payload!;
        Assert.Equal(15, op.Imm);
    }

    [Fact]
    public void Decode_Slti() {
        // slti x1, x2, 5  →  0x00512093
        ITooth i = D(0x00512093);
        Assert.IsType<RvSlti>(i.Payload);
        var op = (RvSlti)i.Payload!;
        Assert.Equal(5, op.Imm);
    }

    [Fact]
    public void Decode_Sltiu() {
        // sltiu x1, x2, 5  →  0x00513093
        ITooth i = D(0x00513093);
        Assert.IsType<RvSltiu>(i.Payload);
        var op = (RvSltiu)i.Payload!;
        Assert.Equal(5, op.Imm);
    }

    // ── Loads (additional) ────────────────────────────────────────────────────

    [Fact]
    public void Decode_Lh() {
        // lh x1, 4(x2)  →  0x00411083
        ITooth i = D(0x00411083);
        Assert.IsType<RvLh>(i.Payload);
        var op = (RvLh)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(4, op.Imm);
        Assert.Equal(ToothClass.Load, i.Class);
    }

    [Fact]
    public void Decode_Lhu() {
        // lhu x1, 4(x2)  →  0x00415083
        ITooth i = D(0x00415083);
        Assert.IsType<RvLhu>(i.Payload);
    }

    [Fact]
    public void Decode_Lbu() {
        // lbu x1, 4(x2)  →  0x00414083
        ITooth i = D(0x00414083);
        Assert.IsType<RvLbu>(i.Payload);
    }

    // ── Stores (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Decode_Sh() {
        // sh x3, 8(x2)  →  0x00311423
        ITooth i = D(0x00311423);
        Assert.IsType<RvSh>(i.Payload);
        var op = (RvSh)i.Payload!;
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.Store, i.Class);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_Sb() {
        // sb x3, 8(x2)  →  0x00310423
        ITooth i = D(0x00310423);
        Assert.IsType<RvSb>(i.Payload);
        var op = (RvSb)i.Payload!;
        Assert.Equal(8, op.Imm);
    }

    // ── Branches (additional) ─────────────────────────────────────────────────

    [Fact]
    public void Decode_Blt() {
        // blt x1, x2, +8  →  0x0020C463
        ITooth i = D(0x0020C463);
        Assert.IsType<RvBlt>(i.Payload);
        var op = (RvBlt)i.Payload!;
        Assert.Equal(8, op.Imm);
        Assert.Equal(ToothClass.ConditionalBranch, i.Class);
    }

    [Fact]
    public void Decode_Bge() {
        // bge x1, x2, +8  →  0x0020D463
        ITooth i = D(0x0020D463);
        Assert.IsType<RvBge>(i.Payload);
    }

    [Fact]
    public void Decode_Bltu() {
        // bltu x1, x2, +8  →  0x0020E463
        ITooth i = D(0x0020E463);
        Assert.IsType<RvBltu>(i.Payload);
    }

    [Fact]
    public void Decode_Bgeu() {
        // bgeu x1, x2, +8  →  0x0020F463
        ITooth i = D(0x0020F463);
        Assert.IsType<RvBgeu>(i.Payload);
    }

    // ── Error conditions ──────────────────────────────────────────────────────

    [Fact]
    public void Decode_UnknownOpcode_Throws() {
        // 0xFFFFFFFF has opcode 0x7F, which is not a valid RV32I opcode
        Assert.Throws<IllegalInstructionException>(() => D(0xFFFFFFFF));
    }

    // ── M extension ───────────────────────────────────────────────────────────

    // Encoding: funct7=0x01, rs2=x3, rs1=x2, funct3=N, rd=x1, opcode=0x33
    // mul  x1,x2,x3 = 0x023100B3
    // mulh x1,x2,x3 = 0x023110B3  (funct3=1)
    // etc.

    [Fact]
    public void Decode_Mul() {
        ITooth i = D(0x023100B3); // mul x1, x2, x3
        Assert.IsType<RvMul>(i.Payload);
        Assert.Equal(ToothClass.IntegerMulDiv, i.Class);
        var op = (RvMul)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
    }

    [Fact]
    public void Decode_Mulh() {
        ITooth i = D(0x023110B3); // mulh x1, x2, x3
        Assert.IsType<RvMulh>(i.Payload);
        Assert.Equal(ToothClass.IntegerMulDiv, i.Class);
    }

    [Fact]
    public void Decode_Mulhsu() {
        ITooth i = D(0x023120B3); // mulhsu x1, x2, x3
        Assert.IsType<RvMulhsu>(i.Payload);
    }

    [Fact]
    public void Decode_Mulhu() {
        ITooth i = D(0x023130B3); // mulhu x1, x2, x3
        Assert.IsType<RvMulhu>(i.Payload);
    }

    [Fact]
    public void Decode_Div() {
        ITooth i = D(0x023140B3); // div x1, x2, x3
        Assert.IsType<RvDiv>(i.Payload);
    }

    [Fact]
    public void Decode_Divu() {
        ITooth i = D(0x023150B3); // divu x1, x2, x3
        Assert.IsType<RvDivu>(i.Payload);
    }

    [Fact]
    public void Decode_Rem() {
        ITooth i = D(0x023160B3); // rem x1, x2, x3
        Assert.IsType<RvRem>(i.Payload);
    }

    [Fact]
    public void Decode_Remu() {
        ITooth i = D(0x023170B3); // remu x1, x2, x3
        Assert.IsType<RvRemu>(i.Payload);
    }

    // ── A extension ───────────────────────────────────────────────────────────

    // AMO encoding: [31:27]=funct5, [26]=aq, [25]=rl, [24:20]=rs2,
    //               [19:15]=rs1, [14:12]=010(word), [11:7]=rd, [6:0]=0x2F

    [Fact]
    public void Decode_LrW() {
        // lr.w x1, (x2)  →  funct5=0x02, rs2=0, rs1=2, rd=1
        // 0b 00010_0_0_00000_00010_010_00001_0101111 = 0x100120AF
        ITooth i = D(0x100120AF);
        Assert.IsType<RvLrW>(i.Payload);
        Assert.Equal(ToothClass.Atomic, i.Class);
        var op = (RvLrW)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Single(i.SourceRegisters); // LR.W only reads the address register
    }

    [Fact]
    public void Decode_ScW() {
        // sc.w x1, x3, (x2)  →  funct5=0x03, rs2=3, rs1=2, rd=1
        // 0b 00011_0_0_00011_00010_010_00001_0101111 = 0x183120AF
        ITooth i = D(0x183120AF);
        Assert.IsType<RvScW>(i.Payload);
        Assert.Equal(ToothClass.Atomic, i.Class);
    }

    [Fact]
    public void Decode_AmoswapW() {
        // amoswap.w x1, x3, (x2)  →  funct5=0x01
        // 0b 00001_0_0_00011_00010_010_00001_0101111 = 0x083120AF
        ITooth i = D(0x083120AF);
        Assert.IsType<RvAmoswapW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoaddW() {
        // amoadd.w x1, x3, (x2)  →  funct5=0x00
        // 0b 00000_0_0_00011_00010_010_00001_0101111 = 0x003120AF
        ITooth i = D(0x003120AF);
        Assert.IsType<RvAmoaddW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoorW() {
        // amoor.w x1, x3, (x2)  →  funct5=0x08
        // 0b 01000_0_0_00011_00010_010_00001_0101111 = 0x403120AF
        ITooth i = D(0x403120AF);
        Assert.IsType<RvAmoorW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoandW() {
        // amoand.w x1, x3, (x2)  →  funct5=0x0C
        // 0b 01100_0_0_00011_00010_010_00001_0101111 = 0x603120AF
        ITooth i = D(0x603120AF);
        Assert.IsType<RvAmoandW>(i.Payload);
    }

    // ── F extension ───────────────────────────────────────────────────────────
    // Register indices: FP registers are unified 32-63 (f0=32 … f31=63).
    // All encodings below use rd/rs1/rs2 = f1/f2/f3 (raw fields 1/2/3) unless noted.

    [Fact]
    public void Decode_Flw() {
        // flw f1, 4(x2)  →  imm=4, rs1=x2=2, rd=f1, opcode=0x07, funct3=2
        // 0x00412087
        ITooth i = D(0x00412087);
        Assert.IsType<RvFlw>(i.Payload);
        Assert.Equal(ToothClass.Load, i.Class);
        var op = (RvFlw)i.Payload!;
        Assert.Equal(33, op.Rd); // f1 in unified (1+32)
        Assert.Equal(2, op.Rs1); // x2 (integer base)
        Assert.Equal(4, op.Imm);
        Assert.Equal(33, i.DestinationRegister);
        Assert.Equal([2,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_Fsw() {
        // fsw f2, 4(x1)  →  imm=4, rs1=x1=1, rs2=f2=2 (raw), opcode=0x27, funct3=2
        // 0x0020A227
        ITooth i = D(0x0020A227);
        Assert.IsType<RvFsw>(i.Payload);
        Assert.Equal(ToothClass.Store, i.Class);
        var op = (RvFsw)i.Payload!;
        Assert.Equal(1, op.Rs1);  // x1 (integer base)
        Assert.Equal(34, op.Rs2); // f2 in unified (2+32)
        Assert.Equal(4, op.Imm);
        Assert.Equal(-1, i.DestinationRegister);
        Assert.Equal([1, 34,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FaddS() {
        // fadd.s f1, f2, f3  →  funct7=0x00
        // (0x00<<25)|(3<<20)|(2<<15)|(0<<12)|(1<<7)|0x53 = 0x003100D3
        ITooth i = D(0x003100D3);
        Assert.IsType<RvFaddS>(i.Payload);
        Assert.Equal(ToothClass.FloatingPoint, i.Class);
        var op = (RvFaddS)i.Payload!;
        Assert.Equal(33, op.Rd);
        Assert.Equal(34, op.Rs1);
        Assert.Equal(35, op.Rs2);
        Assert.Equal([34, 35,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FsubS() {
        // fsub.s f1, f2, f3  →  funct7=0x04 = 0x083100D3
        ITooth i = D(0x083100D3);
        Assert.IsType<RvFsubS>(i.Payload);
    }

    [Fact]
    public void Decode_FmulS() {
        // fmul.s f1, f2, f3  →  funct7=0x08 = 0x103100D3
        ITooth i = D(0x103100D3);
        Assert.IsType<RvFmulS>(i.Payload);
    }

    [Fact]
    public void Decode_FdivS() {
        // fdiv.s f1, f2, f3  →  funct7=0x0C = 0x183100D3
        ITooth i = D(0x183100D3);
        Assert.IsType<RvFdivS>(i.Payload);
    }

    [Fact]
    public void Decode_FsqrtS() {
        // fsqrt.s f1, f2  →  funct7=0x2C, rs2=0 = 0x580100D3
        ITooth i = D(0x580100D3);
        Assert.IsType<RvFsqrtS>(i.Payload);
        var op = (RvFsqrtS)i.Payload!;
        Assert.Equal(33, op.Rd);
        Assert.Equal(34, op.Rs1);
        Assert.Equal([34,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FsgnjS() {
        // fsgnj.s f1, f2, f3  →  funct7=0x10, funct3=0 = 0x203100D3
        ITooth i = D(0x203100D3);
        Assert.IsType<RvFsgnjS>(i.Payload);
    }

    [Fact]
    public void Decode_FsgnjnS() {
        // fsgnjn.s f1, f2, f3  →  funct7=0x10, funct3=1 = 0x203110D3
        ITooth i = D(0x203110D3);
        Assert.IsType<RvFsgnjnS>(i.Payload);
    }

    [Fact]
    public void Decode_FsgnjxS() {
        // fsgnjx.s f1, f2, f3  →  funct7=0x10, funct3=2 = 0x203120D3
        ITooth i = D(0x203120D3);
        Assert.IsType<RvFsgnjxS>(i.Payload);
    }

    [Fact]
    public void Decode_FminS() {
        // fmin.s f1, f2, f3  →  funct7=0x14, funct3=0 = 0x283100D3
        ITooth i = D(0x283100D3);
        Assert.IsType<RvFminS>(i.Payload);
    }

    [Fact]
    public void Decode_FmaxS() {
        // fmax.s f1, f2, f3  →  funct7=0x14, funct3=1 = 0x283110D3
        ITooth i = D(0x283110D3);
        Assert.IsType<RvFmaxS>(i.Payload);
    }

    [Fact]
    public void Decode_FeqS() {
        // feq.s x1, f2, f3  →  funct7=0x50, funct3=2; rd is integer = 0xA03120D3
        ITooth i = D(0xA03120D3);
        Assert.IsType<RvFeqS>(i.Payload);
        var op = (RvFeqS)i.Payload!;
        Assert.Equal(1, op.Rd);   // integer result register
        Assert.Equal(34, op.Rs1); // f2 unified
        Assert.Equal(35, op.Rs2); // f3 unified
        Assert.Equal(1, i.DestinationRegister);
        Assert.Equal([34, 35,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FltS() {
        // flt.s x1, f2, f3  →  funct7=0x50, funct3=1 = 0xA03110D3
        ITooth i = D(0xA03110D3);
        Assert.IsType<RvFltS>(i.Payload);
    }

    [Fact]
    public void Decode_FleS() {
        // fle.s x1, f2, f3  →  funct7=0x50, funct3=0 = 0xA03100D3
        ITooth i = D(0xA03100D3);
        Assert.IsType<RvFleS>(i.Payload);
    }

    [Fact]
    public void Decode_FclassS() {
        // fclass.s x1, f2  →  funct7=0x70, funct3=1, rs2=0 = 0xE00110D3
        ITooth i = D(0xE00110D3);
        Assert.IsType<RvFclassS>(i.Payload);
        var op = (RvFclassS)i.Payload!;
        Assert.Equal(1, op.Rd);   // integer result
        Assert.Equal(34, op.Rs1); // f2 unified
        Assert.Equal(1, i.DestinationRegister);
        Assert.Equal([34,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FcvtWS() {
        // fcvt.w.s x1, f2  →  funct7=0x60, rs2=0 = 0xC00100D3
        ITooth i = D(0xC00100D3);
        Assert.IsType<RvFcvtWs>(i.Payload);
        var op = (RvFcvtWs)i.Payload!;
        Assert.Equal(1, op.Rd);   // integer dest
        Assert.Equal(34, op.Rs1); // f2 unified FP source
        Assert.Equal(1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_FcvtWuS() {
        // fcvt.wu.s x1, f2  →  funct7=0x60, rs2=1 = 0xC01100D3
        ITooth i = D(0xC01100D3);
        Assert.IsType<RvFcvtWuS>(i.Payload);
    }

    [Fact]
    public void Decode_FcvtSW() {
        // fcvt.s.w f1, x2  →  funct7=0x68, rs2=0 = 0xD00100D3; rd is FP
        ITooth i = D(0xD00100D3);
        Assert.IsType<RvFcvtSw>(i.Payload);
        var op = (RvFcvtSw)i.Payload!;
        Assert.Equal(33, op.Rd); // f1 unified FP dest
        Assert.Equal(2, op.Rs1); // x2 integer source
        Assert.Equal(33, i.DestinationRegister);
    }

    [Fact]
    public void Decode_FcvtSWu() {
        // fcvt.s.wu f1, x2  →  funct7=0x68, rs2=1 = 0xD01100D3
        ITooth i = D(0xD01100D3);
        Assert.IsType<RvFcvtSWu>(i.Payload);
    }

    [Fact]
    public void Decode_FmvXW() {
        // fmv.x.w x1, f2  →  funct7=0x70, funct3=0, rs2=0 = 0xE00100D3
        ITooth i = D(0xE00100D3);
        Assert.IsType<RvFmvXw>(i.Payload);
        var op = (RvFmvXw)i.Payload!;
        Assert.Equal(1, op.Rd);   // integer dest
        Assert.Equal(34, op.Rs1); // f2 unified FP source
    }

    [Fact]
    public void Decode_FmvWX() {
        // fmv.w.x f1, x2  →  funct7=0x78, funct3=0, rs2=0 = 0xF00100D3
        ITooth i = D(0xF00100D3);
        Assert.IsType<RvFmvWx>(i.Payload);
        var op = (RvFmvWx)i.Payload!;
        Assert.Equal(33, op.Rd); // f1 unified FP dest
        Assert.Equal(2, op.Rs1); // x2 integer source
    }

    [Fact]
    public void Decode_FmaddS() {
        // fmadd.s f1, f2, f3, f4  →  opcode=0x43, rs3=f4=4
        // (4<<27)|(0<<25)|(3<<20)|(2<<15)|(0<<12)|(1<<7)|0x43 = 0x203100C3
        ITooth i = D(0x203100C3);
        Assert.IsType<RvFmaddS>(i.Payload);
        var op = (RvFmaddS)i.Payload!;
        Assert.Equal(33, op.Rd);
        Assert.Equal(34, op.Rs1);
        Assert.Equal(35, op.Rs2);
        Assert.Equal(36, op.Rs3);
        Assert.Equal([34, 35, 36,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_FmsubS() {
        // fmsub.s f1, f2, f3, f4  →  opcode=0x47 = 0x203100C7
        ITooth i = D(0x203100C7);
        Assert.IsType<RvFmsubS>(i.Payload);
    }

    [Fact]
    public void Decode_FnmsubS() {
        // fnmsub.s f1, f2, f3, f4  →  opcode=0x4B = 0x203100CB
        ITooth i = D(0x203100CB);
        Assert.IsType<RvFnmsubS>(i.Payload);
    }

    [Fact]
    public void Decode_FnmaddS() {
        // fnmadd.s f1, f2, f3, f4  →  opcode=0x4F = 0x203100CF
        ITooth i = D(0x203100CF);
        Assert.IsType<RvFnmaddS>(i.Payload);
    }
}