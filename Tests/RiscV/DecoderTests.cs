using Mechanism;
using RiscV.Decode;

namespace Tests.RiscV;

public class DecoderTests {
    private readonly RvDecoder _dec = new();

    // Helper: decode a raw word directly
    private IInstruction D(uint raw, ulong pc = 0) => _dec.Decode(pc, raw);

    // ── R-type ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Add() {
        // add x1, x2, x3  →  0x003100B3
        IInstruction i = D(0x003100B3);
        Assert.IsType<RvAdd>(i.Payload);
        var op = (RvAdd)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(InstructionClass.IntegerAlu, i.Class);
    }

    [Fact]
    public void Decode_Sub() {
        // sub x1, x2, x3  →  0x403100B3
        IInstruction i = D(0x403100B3);
        Assert.IsType<RvSub>(i.Payload);
    }

    [Fact]
    public void Decode_Sll() {
        // sll x5, x6, x7  →  0x007312B3
        IInstruction i = D(0x007312B3);
        Assert.IsType<RvSll>(i.Payload);
        var op = (RvSll)i.Payload!;
        Assert.Equal(5, op.Rd);
        Assert.Equal(6, op.Rs1);
        Assert.Equal(7, op.Rs2);
    }

    [Fact]
    public void Decode_Sra() {
        // sra x1, x2, x3  →  0x403150B3
        IInstruction i = D(0x403150B3);
        Assert.IsType<RvSra>(i.Payload);
    }

    // ── I-type ALU ────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Addi() {
        // addi x1, x2, 42  →  0x02A10093  (imm=42=0x02A, rs1=2, rd=1)
        IInstruction i = D(0x02A10093);
        Assert.IsType<RvAddi>(i.Payload);
        var op = (RvAddi)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(42, op.Imm);
    }

    [Fact]
    public void Decode_Addi_NegativeImmediate() {
        // addi x1, x0, -1  →  0xFFF00093
        IInstruction i = D(0xFFF00093);
        var op = (RvAddi)i.Payload!;
        Assert.Equal(-1, op.Imm);
    }

    [Fact]
    public void Decode_Srai() {
        // srai x1, x2, 5  →  funct7=0x20, shamt=5
        // 0x40515093
        IInstruction i = D(0x40515093);
        Assert.IsType<RvSrai>(i.Payload);
        var op = (RvSrai)i.Payload!;
        Assert.Equal(5, op.Shamt);
    }

    // ── Loads ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Lw() {
        // lw x1, 8(x2)  →  0x00812083
        IInstruction i = D(0x00812083);
        Assert.IsType<RvLw>(i.Payload);
        var op = (RvLw)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.Load, i.Class);
    }

    [Fact]
    public void Decode_Lb_NegativeOffset() {
        // lb x1, -4(x2)  →  imm=-4
        // 0xFFC10083
        IInstruction i = D(0xFFC10083);
        var op = (RvLb)i.Payload!;
        Assert.Equal(-4, op.Imm);
    }

    // ── Stores ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Sw() {
        // sw x3, 8(x2)  →  0x00312423
        IInstruction i = D(0x00312423);
        Assert.IsType<RvSw>(i.Payload);
        var op = (RvSw)i.Payload!;
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.Store, i.Class);
        Assert.Equal(-1, i.DestinationRegister);
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Beq() {
        // beq x1, x2, +8  →  0x00208463
        IInstruction i = D(0x00208463);
        Assert.IsType<RvBeq>(i.Payload);
        var op = (RvBeq)i.Payload!;
        Assert.Equal(1, op.Rs1);
        Assert.Equal(2, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.ConditionalBranch, i.Class);
    }

    [Fact]
    public void Decode_Bne() {
        // bne x1, x2, +4  →  0x00209263
        IInstruction i = D(0x00209263);
        Assert.IsType<RvBne>(i.Payload);
    }

    // ── JAL / JALR ────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Jal() {
        // jal x1, +8  →  0x008000EF
        IInstruction i = D(0x008000EF);
        Assert.IsType<RvJal>(i.Payload);
        var op = (RvJal)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.Branch, i.Class);
    }

    [Fact]
    public void Decode_Jalr() {
        // jalr x1, 4 (x2) →  0x004100e7  (rd=1, imm=4, funct3=0)
        IInstruction i = D(0x004100e7);
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
        IInstruction i = D(0x123450B7);
        Assert.IsType<RvLui>(i.Payload);
        var op = (RvLui)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(0x12345000, op.Imm);
    }

    [Fact]
    public void Decode_Auipc() {
        // auipc x1, 1  →  0x00001097
        IInstruction i = D(0x00001097, 0x1000);
        Assert.IsType<RvAuipc>(i.Payload);
        var op = (RvAuipc)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(0x1000, op.Imm);
    }

    // ── System ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Ecall() {
        IInstruction i = D(0x00000073);
        Assert.IsType<RvEcall>(i.Payload);
        Assert.Equal(InstructionClass.System, i.Class);
    }

    [Fact]
    public void Decode_Ebreak() {
        IInstruction i = D(0x00100073);
        Assert.IsType<RvEbreak>(i.Payload);
    }

    [Fact]
    public void Decode_Mret() {
        // mret  →  0x30200073
        IInstruction i = D(0x30200073);
        Assert.IsType<RvMret>(i.Payload);
    }

    [Fact]
    public void Decode_Csrrw() {
        // csrrw x1, mstatus, x2  →  csr=0x300, rd=1, rs1=2
        // 0x300110F3
        IInstruction i = D(0x300110F3);
        Assert.IsType<RvCsrrw>(i.Payload);
        var op = (RvCsrrw)i.Payload!;
        Assert.Equal(0x300u, op.Csr);
    }

    [Fact]
    public void Decode_Fence() {
        // fence  →  0x0000000F
        IInstruction i = D(0x0000000F);
        Assert.IsType<RvFence>(i.Payload);
        Assert.Equal(InstructionClass.Fence, i.Class);
    }

    // ── Source/destination register fields ────────────────────────────────────

    [Fact]
    public void Decode_SourceRegisters_RType() {
        IInstruction i = D(0x003100B3); // add x1, x2, x3
        Assert.Equal([2, 3,], i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Store() {
        IInstruction i = D(0x00312423); // sw x3, 8(x2)
        Assert.Equal([2, 3,], i.SourceRegisters);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Jal() {
        IInstruction i = D(0x008000EF); // jal x1, +8
        Assert.Empty(i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Branch() {
        IInstruction i = D(0x00208463); // beq x1, x2, +8
        Assert.Equal([1, 2,], i.SourceRegisters);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_SourceRegisters_Load() {
        IInstruction i = D(0x00812083); // lw x1, 8(x2)
        Assert.Equal([2,], i.SourceRegisters);
        Assert.Equal(1, i.DestinationRegister);
    }

    // ── R-type (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Decode_Srl() {
        // srl x5, x6, x7  →  0x007352B3
        IInstruction i = D(0x007352B3);
        Assert.IsType<RvSrl>(i.Payload);
        var op = (RvSrl)i.Payload!;
        Assert.Equal(5, op.Rd);
        Assert.Equal(6, op.Rs1);
        Assert.Equal(7, op.Rs2);
    }

    [Fact]
    public void Decode_And() {
        // and x3, x1, x2  →  0x0020F1B3
        IInstruction i = D(0x0020F1B3);
        Assert.IsType<RvAnd>(i.Payload);
        var op = (RvAnd)i.Payload!;
        Assert.Equal(3, op.Rd);
        Assert.Equal(1, op.Rs1);
        Assert.Equal(2, op.Rs2);
    }

    [Fact]
    public void Decode_Or() {
        // or x3, x1, x2  →  0x0020E1B3
        IInstruction i = D(0x0020E1B3);
        Assert.IsType<RvOr>(i.Payload);
    }

    [Fact]
    public void Decode_Xor() {
        // xor x3, x1, x2  →  0x0020C1B3
        IInstruction i = D(0x0020C1B3);
        Assert.IsType<RvXor>(i.Payload);
    }

    [Fact]
    public void Decode_Slt() {
        // slt x3, x1, x2  →  0x0020A1B3
        IInstruction i = D(0x0020A1B3);
        Assert.IsType<RvSlt>(i.Payload);
    }

    [Fact]
    public void Decode_Sltu() {
        // sltu x3, x1, x2  →  0x0020B1B3
        IInstruction i = D(0x0020B1B3);
        Assert.IsType<RvSltu>(i.Payload);
    }

    // ── I-type ALU (additional) ───────────────────────────────────────────────

    [Fact]
    public void Decode_Slli() {
        // slli x1, x2, 3  →  0x00311093
        IInstruction i = D(0x00311093);
        Assert.IsType<RvSlli>(i.Payload);
        var op = (RvSlli)i.Payload!;
        Assert.Equal(3, op.Shamt);
    }

    [Fact]
    public void Decode_Srli() {
        // srli x1, x2, 3  →  0x00315093
        IInstruction i = D(0x00315093);
        Assert.IsType<RvSrli>(i.Payload);
        var op = (RvSrli)i.Payload!;
        Assert.Equal(3, op.Shamt);
    }

    [Fact]
    public void Decode_Ori() {
        // ori x1, x2, 15  →  0x00F16093
        IInstruction i = D(0x00F16093);
        Assert.IsType<RvOri>(i.Payload);
        var op = (RvOri)i.Payload!;
        Assert.Equal(15, op.Imm);
    }

    [Fact]
    public void Decode_Xori() {
        // xori x1, x2, 15  →  0x00F14093
        IInstruction i = D(0x00F14093);
        Assert.IsType<RvXori>(i.Payload);
        var op = (RvXori)i.Payload!;
        Assert.Equal(15, op.Imm);
    }

    [Fact]
    public void Decode_Slti() {
        // slti x1, x2, 5  →  0x00512093
        IInstruction i = D(0x00512093);
        Assert.IsType<RvSlti>(i.Payload);
        var op = (RvSlti)i.Payload!;
        Assert.Equal(5, op.Imm);
    }

    [Fact]
    public void Decode_Sltiu() {
        // sltiu x1, x2, 5  →  0x00513093
        IInstruction i = D(0x00513093);
        Assert.IsType<RvSltiu>(i.Payload);
        var op = (RvSltiu)i.Payload!;
        Assert.Equal(5, op.Imm);
    }

    // ── Loads (additional) ────────────────────────────────────────────────────

    [Fact]
    public void Decode_Lh() {
        // lh x1, 4(x2)  →  0x00411083
        IInstruction i = D(0x00411083);
        Assert.IsType<RvLh>(i.Payload);
        var op = (RvLh)i.Payload!;
        Assert.Equal(1, op.Rd);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(4, op.Imm);
        Assert.Equal(InstructionClass.Load, i.Class);
    }

    [Fact]
    public void Decode_Lhu() {
        // lhu x1, 4(x2)  →  0x00415083
        IInstruction i = D(0x00415083);
        Assert.IsType<RvLhu>(i.Payload);
    }

    [Fact]
    public void Decode_Lbu() {
        // lbu x1, 4(x2)  →  0x00414083
        IInstruction i = D(0x00414083);
        Assert.IsType<RvLbu>(i.Payload);
    }

    // ── Stores (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Decode_Sh() {
        // sh x3, 8(x2)  →  0x00311423
        IInstruction i = D(0x00311423);
        Assert.IsType<RvSh>(i.Payload);
        var op = (RvSh)i.Payload!;
        Assert.Equal(2, op.Rs1);
        Assert.Equal(3, op.Rs2);
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.Store, i.Class);
        Assert.Equal(-1, i.DestinationRegister);
    }

    [Fact]
    public void Decode_Sb() {
        // sb x3, 8(x2)  →  0x00310423
        IInstruction i = D(0x00310423);
        Assert.IsType<RvSb>(i.Payload);
        var op = (RvSb)i.Payload!;
        Assert.Equal(8, op.Imm);
    }

    // ── Branches (additional) ─────────────────────────────────────────────────

    [Fact]
    public void Decode_Blt() {
        // blt x1, x2, +8  →  0x0020C463
        IInstruction i = D(0x0020C463);
        Assert.IsType<RvBlt>(i.Payload);
        var op = (RvBlt)i.Payload!;
        Assert.Equal(8, op.Imm);
        Assert.Equal(InstructionClass.ConditionalBranch, i.Class);
    }

    [Fact]
    public void Decode_Bge() {
        // bge x1, x2, +8  →  0x0020D463
        IInstruction i = D(0x0020D463);
        Assert.IsType<RvBge>(i.Payload);
    }

    [Fact]
    public void Decode_Bltu() {
        // bltu x1, x2, +8  →  0x0020E463
        IInstruction i = D(0x0020E463);
        Assert.IsType<RvBltu>(i.Payload);
    }

    [Fact]
    public void Decode_Bgeu() {
        // bgeu x1, x2, +8  →  0x0020F463
        IInstruction i = D(0x0020F463);
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
        IInstruction i = D(0x023100B3); // mul x1, x2, x3
        Assert.IsType<RvMul>(i.Payload);
        Assert.Equal(InstructionClass.IntegerMulDiv, i.Class);
        var op = (RvMul)i.Payload!;
        Assert.Equal(1, op.Rd); Assert.Equal(2, op.Rs1); Assert.Equal(3, op.Rs2);
    }

    [Fact]
    public void Decode_Mulh() {
        IInstruction i = D(0x023110B3); // mulh x1, x2, x3
        Assert.IsType<RvMulh>(i.Payload);
        Assert.Equal(InstructionClass.IntegerMulDiv, i.Class);
    }

    [Fact]
    public void Decode_Mulhsu() {
        IInstruction i = D(0x023120B3); // mulhsu x1, x2, x3
        Assert.IsType<RvMulhsu>(i.Payload);
    }

    [Fact]
    public void Decode_Mulhu() {
        IInstruction i = D(0x023130B3); // mulhu x1, x2, x3
        Assert.IsType<RvMulhu>(i.Payload);
    }

    [Fact]
    public void Decode_Div() {
        IInstruction i = D(0x023140B3); // div x1, x2, x3
        Assert.IsType<RvDiv>(i.Payload);
    }

    [Fact]
    public void Decode_Divu() {
        IInstruction i = D(0x023150B3); // divu x1, x2, x3
        Assert.IsType<RvDivu>(i.Payload);
    }

    [Fact]
    public void Decode_Rem() {
        IInstruction i = D(0x023160B3); // rem x1, x2, x3
        Assert.IsType<RvRem>(i.Payload);
    }

    [Fact]
    public void Decode_Remu() {
        IInstruction i = D(0x023170B3); // remu x1, x2, x3
        Assert.IsType<RvRemu>(i.Payload);
    }

    // ── A extension ───────────────────────────────────────────────────────────

    // AMO encoding: [31:27]=funct5, [26]=aq, [25]=rl, [24:20]=rs2,
    //               [19:15]=rs1, [14:12]=010(word), [11:7]=rd, [6:0]=0x2F

    [Fact]
    public void Decode_LrW() {
        // lr.w x1, (x2)  →  funct5=0x02, rs2=0, rs1=2, rd=1
        // 0b 00010_0_0_00000_00010_010_00001_0101111 = 0x100120AF
        IInstruction i = D(0x100120AF);
        Assert.IsType<RvLrW>(i.Payload);
        Assert.Equal(InstructionClass.Atomic, i.Class);
        var op = (RvLrW)i.Payload!;
        Assert.Equal(1, op.Rd); Assert.Equal(2, op.Rs1);
        Assert.Single(i.SourceRegisters); // LR.W only reads the address register
    }

    [Fact]
    public void Decode_ScW() {
        // sc.w x1, x3, (x2)  →  funct5=0x03, rs2=3, rs1=2, rd=1
        // 0b 00011_0_0_00011_00010_010_00001_0101111 = 0x183120AF
        IInstruction i = D(0x183120AF);
        Assert.IsType<RvScW>(i.Payload);
        Assert.Equal(InstructionClass.Atomic, i.Class);
    }

    [Fact]
    public void Decode_AmoswapW() {
        // amoswap.w x1, x3, (x2)  →  funct5=0x01
        // 0b 00001_0_0_00011_00010_010_00001_0101111 = 0x083120AF
        IInstruction i = D(0x083120AF);
        Assert.IsType<RvAmoswapW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoaddW() {
        // amoadd.w x1, x3, (x2)  →  funct5=0x00
        // 0b 00000_0_0_00011_00010_010_00001_0101111 = 0x003120AF
        IInstruction i = D(0x003120AF);
        Assert.IsType<RvAmoaddW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoorW() {
        // amoor.w x1, x3, (x2)  →  funct5=0x08
        // 0b 01000_0_0_00011_00010_010_00001_0101111 = 0x403120AF
        IInstruction i = D(0x403120AF);
        Assert.IsType<RvAmoorW>(i.Payload);
    }

    [Fact]
    public void Decode_AmoandW() {
        // amoand.w x1, x3, (x2)  →  funct5=0x0C
        // 0b 01100_0_0_00011_00010_010_00001_0101111 = 0x603120AF
        IInstruction i = D(0x603120AF);
        Assert.IsType<RvAmoandW>(i.Payload);
    }
}