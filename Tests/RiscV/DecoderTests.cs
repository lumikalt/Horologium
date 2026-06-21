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
}