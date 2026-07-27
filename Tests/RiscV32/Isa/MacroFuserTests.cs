#region

using Mechanism;
using RiscV32.Decode;

#endregion

namespace Tests.RiscV32.Isa;

/// <summary>
///     Unit tests for <see cref="RvMacroFuser" />: the SLT(U)/SLTI(U) + BEQ/BNE-against-zero
///     pattern recognition. Pure opcode/operand matching — adjacency (the two instructions
///     were actually fetched back-to-back) is the caller's responsibility per
///     <see cref="IMacroFuser" />'s contract, so these tests decode standalone instructions
///     and feed them straight to <see cref="RvMacroFuser.TryFuse" /> without a real fetch
///     stream. Pipeline-level integration (adjacency, timing, retirement accounting) is
///     covered by <c>SuperscalarMacroFusionTests</c>.
/// </summary>
public class MacroFuserTests {
    private readonly Rv32Decoder _dec = new();
    private readonly RvMacroFuser _fuser = new();

    private ITooth D(uint raw, ulong pc = 0) => _dec.Decode(pc, raw);

    private static uint Slt(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0110011);

    private static uint Sltu(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b011 << 12) | (rd << 7) | 0b0110011);

    private static uint Slti(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0010011);

    private static uint Sltiu(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b011 << 12) | (rd << 7) | 0b0010011);

    private static uint Add(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint BranchB(uint funct3, int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (funct3 << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static uint Beq(int rs1, int rs2, int immOffset) => BranchB(0b000, rs1, rs2, immOffset);
    private static uint Bne(int rs1, int rs2, int immOffset) => BranchB(0b001, rs1, rs2, immOffset);
    private static uint Blt(int rs1, int rs2, int immOffset) => BranchB(0b100, rs1, rs2, immOffset);

    [Fact]
    public void Fuses_SltThenBne() {
        ITooth first = D(Slt(5, 2, 1), 0x100);
        ITooth second = D(Bne(5, 0, 16), 0x104);

        ITooth? fused = _fuser.TryFuse(first, second);

        Assert.NotNull(fused);
        Assert.Equal(0x100UL, fused!.Pc);
        Assert.Equal(8, fused.SizeBytes);
        Assert.Equal(5, fused.DestinationRegister);
        Assert.Equal(ToothClass.ConditionalBranch, fused.Class);
        Assert.Equal([2, 1], fused.SourceRegisters);
        var payload = Assert.IsType<RvFusedCompareBranch>(fused.Payload);
        Assert.True(payload.TakenWhenNonZero);
        Assert.Equal(0x104UL, payload.BranchPc);
        Assert.Equal(16, payload.BranchImm);
    }

    [Fact]
    public void Fuses_SltThenBeq_TakenWhenZero() {
        ITooth first = D(Slt(5, 2, 1), 0x100);
        ITooth second = D(Beq(5, 0, 16), 0x104);

        ITooth? fused = _fuser.TryFuse(first, second);

        Assert.NotNull(fused);
        var payload = Assert.IsType<RvFusedCompareBranch>(fused!.Payload);
        Assert.False(payload.TakenWhenNonZero);
    }

    [Fact]
    public void Fuses_SltuThenBne() {
        ITooth first = D(Sltu(3, 4, 0), 0x200);
        ITooth second = D(Bne(0, 3, -8), 0x204); // cmpRd may be on either operand side

        ITooth? fused = _fuser.TryFuse(first, second);

        Assert.NotNull(fused);
        Assert.Equal(3, fused!.DestinationRegister);
    }

    [Fact]
    public void Fuses_SltiThenBeq() {
        ITooth first = D(Slti(7, 6, 100), 0x300);
        ITooth second = D(Beq(7, 0, 12), 0x304);

        ITooth? fused = _fuser.TryFuse(first, second);

        Assert.NotNull(fused);
        Assert.Equal(7, fused!.DestinationRegister);
        Assert.Equal([6], fused.SourceRegisters);
    }

    [Fact]
    public void Fuses_SltiuThenBne() {
        ITooth first = D(Sltiu(9, 8, 5), 0x400);
        ITooth second = D(Bne(9, 0, 12), 0x404);

        ITooth? fused = _fuser.TryFuse(first, second);

        Assert.NotNull(fused);
        Assert.Equal(9, fused!.DestinationRegister);
    }

    [Fact]
    public void RejectsNonCompareFirstInstruction() {
        ITooth first = D(Add(5, 2, 1), 0x100);
        ITooth second = D(Bne(5, 0, 16), 0x104);

        Assert.Null(_fuser.TryFuse(first, second));
    }

    [Fact]
    public void RejectsNonEqualityBranch() {
        // BLT can't be fused: it isn't the "compare against zero" idiom SLT synthesizes,
        // and treating it as one would silently compute the wrong branch condition.
        ITooth first = D(Slt(5, 2, 1), 0x100);
        ITooth second = D(Blt(5, 0, 16), 0x104);

        Assert.Null(_fuser.TryFuse(first, second));
    }

    [Fact]
    public void RejectsBranchNotTestingCompareResult() {
        // The branch reads unrelated registers — nothing to fuse.
        ITooth first = D(Slt(5, 2, 1), 0x100);
        ITooth second = D(Bne(3, 4, 16), 0x104);

        Assert.Null(_fuser.TryFuse(first, second));
    }

    [Fact]
    public void RejectsBranchComparingAgainstNonZero() {
        // bne x5, x6, target — tests x5 against x6, not against x0. SLT's result alone
        // doesn't determine the branch outcome, so this isn't the fusible idiom.
        ITooth first = D(Slt(5, 2, 1), 0x100);
        ITooth second = D(Bne(5, 6, 16), 0x104);

        Assert.Null(_fuser.TryFuse(first, second));
    }

    [Fact]
    public void RejectsCompareWritingX0() {
        // slt x0, ... is a dead compare (its result is discarded by the ISA); nothing
        // meaningful for a following branch to have consumed.
        ITooth first = D(Slt(0, 2, 1), 0x100);
        ITooth second = D(Bne(0, 0, 16), 0x104);

        Assert.Null(_fuser.TryFuse(first, second));
    }
}
