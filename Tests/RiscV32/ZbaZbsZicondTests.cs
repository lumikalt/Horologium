using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32;

/// <summary>
/// Tests for Zba (address generation), Zbs (single-bit ops), and Zicond
/// (integer conditional ops). Each test loads x1/x2, runs one instruction
/// that writes x3, then reads the result via a SW to a known address.
/// </summary>
public class ZbaZbsZicondTests {
    // ── Encode helpers ────────────────────────────────────────────────────────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12)
             | ((rd & 0x1F) << 7) | 0x13u);

    private static uint EBreak() => 0x00100073u;

    // ── Zba (funct7=0x10, opcode=0x33) ───────────────────────────────────────
    private static uint Sh1Add(int rd, int rs1, int rs2) => RType(0x10, rs2, rs1, 2, rd);
    private static uint Sh2Add(int rd, int rs1, int rs2) => RType(0x10, rs2, rs1, 4, rd);
    private static uint Sh3Add(int rd, int rs1, int rs2) => RType(0x10, rs2, rs1, 6, rd);

    // ── Zbs R-type ────────────────────────────────────────────────────────────
    private static uint Bclr(int rd, int rs1, int rs2) => RType(0x24, rs2, rs1, 1, rd);
    private static uint Bext(int rd, int rs1, int rs2) => RType(0x24, rs2, rs1, 5, rd);
    private static uint Binv(int rd, int rs1, int rs2) => RType(0x34, rs2, rs1, 1, rd);
    private static uint Bset(int rd, int rs1, int rs2) => RType(0x14, rs2, rs1, 1, rd);

    // ── Zbs I-type ────────────────────────────────────────────────────────────
    private static uint Bclri(int rd, int rs1, int shamt) => ITypeAlu((0x24 << 5) | (shamt & 31), rs1, 1, rd);
    private static uint Bexti(int rd, int rs1, int shamt) => ITypeAlu((0x24 << 5) | (shamt & 31), rs1, 5, rd);
    private static uint Binvi(int rd, int rs1, int shamt) => ITypeAlu((0x34 << 5) | (shamt & 31), rs1, 1, rd);
    private static uint Bseti(int rd, int rs1, int shamt) => ITypeAlu((0x14 << 5) | (shamt & 31), rs1, 1, rd);

    // ── Zicond (funct7=0x07, opcode=0x33) ────────────────────────────────────
    private static uint CzeroEqz(int rd, int rs1, int rs2) => RType(0x07, rs2, rs1, 5, rd);
    private static uint CzeroNez(int rd, int rs1, int rs2) => RType(0x07, rs2, rs1, 7, rd);

    // ── Test runner ──────────────────────────────────────────────────────────

    private static uint RunInstr(uint x1Val, uint x2Val, uint instr) {
        const ulong codeBase = 0x1000u;
        const ulong outAddr = 0x0100u;

        var mem = new FlatMemory(0x4000);
        mem.Load(0x200u, BitConverter.GetBytes(x1Val));
        mem.Load(0x204u, BitConverter.GetBytes(x2Val));

        const uint lw1 = (0x200 << 20) | (0 << 15) | (2 << 12) | (1 << 7) | 0x03u;
        const uint lw2 = (0x204 << 20) | (0 << 15) | (2 << 12) | (2 << 7) | 0x03u;
        const uint sw3 = (((0x100 >> 5) & 0x7F) << 25) | (3 << 20) | (0 << 15)
                       | (2 << 12) | ((0x100 & 0x1F) << 7) | 0x23u;

        uint[] words = [lw1, lw2, instr, sw3, EBreak(),];
        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, codeBase);
        train.Run(200);
        return (uint)mem.Read(outAddr, 4);
    }

    private static uint RunUnary(uint x1Val, uint instr) => RunInstr(x1Val, 0, instr);

    // ── Zba tests ─────────────────────────────────────────────────────────────
    // sh1add rd, rs1, rs2 → rd = rs2 + (rs1 << 1)

    [Theory]
    [InlineData(3u, 10u, 16u)]                 // 10 + (3 << 1) = 16
    [InlineData(0u, 100u, 100u)]               // 100 + 0 = 100
    [InlineData(0xFFFFFFFFu, 0u, 0xFFFFFFFEu)] // 0 + (0xFFFFFFFF << 1) wraps to 0xFFFFFFFE
    public void Sh1Add_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Sh1Add(3, 1, 2)));

    [Theory]
    [InlineData(3u, 10u, 22u)] // 10 + (3 << 2) = 22
    [InlineData(0u, 100u, 100u)]
    [InlineData(1u, 0u, 4u)] // 0 + (1 << 2) = 4
    public void Sh2Add_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Sh2Add(3, 1, 2)));

    [Theory]
    [InlineData(3u, 10u, 34u)] // 10 + (3 << 3) = 34
    [InlineData(0u, 100u, 100u)]
    [InlineData(1u, 0u, 8u)] // 0 + (1 << 3) = 8
    public void Sh3Add_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Sh3Add(3, 1, 2)));

    // ── Zbs R-type tests ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0b1111u, 0u, 0b1110u)] // clear bit 0
    [InlineData(0b1111u, 3u, 0b0111u)] // clear bit 3
    [InlineData(0u, 5u, 0u)]           // clearing already-clear bit
    public void Bclr_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Bclr(3, 1, 2)));

    [Theory]
    [InlineData(0b1010u, 1u, 1u)] // bit 1 of 0b1010 = 1
    [InlineData(0b1010u, 0u, 0u)] // bit 0 of 0b1010 = 0
    [InlineData(0b1010u, 3u, 1u)] // bit 3 = 1
    [InlineData(0u, 7u, 0u)]      // bit 7 of 0 = 0
    public void Bext_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Bext(3, 1, 2)));

    [Theory]
    [InlineData(0b1010u, 1u, 0b1000u)] // flip bit 1 → 0b1000
    [InlineData(0b1010u, 0u, 0b1011u)] // flip bit 0 → 0b1011
    [InlineData(0u, 5u, 0b100000u)]
    public void Binv_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Binv(3, 1, 2)));

    [Theory]
    [InlineData(0b1010u, 0u, 0b1011u)] // set bit 0
    [InlineData(0b1010u, 1u, 0b1010u)] // bit 1 already set → no change
    [InlineData(0u, 5u, 0b100000u)]
    public void Bset_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, Bset(3, 1, 2)));

    // ── Zbs I-type tests ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0xFFFFFFFFu, 0, 0xFFFFFFFEu)]
    [InlineData(0xFFFFFFFFu, 31, 0x7FFFFFFFu)]
    [InlineData(0b1111u, 2, 0b1011u)]
    public void Bclri_CorrectResult(uint rs1, int shamt, uint expected) =>
        Assert.Equal(expected, RunUnary(rs1, Bclri(3, 1, shamt)));

    [Theory]
    [InlineData(0b1010u, 1, 1u)]
    [InlineData(0b1010u, 0, 0u)]
    [InlineData(0xFFFFFFFFu, 31, 1u)]
    [InlineData(0u, 15, 0u)]
    public void Bexti_CorrectResult(uint rs1, int shamt, uint expected) =>
        Assert.Equal(expected, RunUnary(rs1, Bexti(3, 1, shamt)));

    [Theory]
    [InlineData(0u, 5, 0b100000u)]
    [InlineData(0xFFFFFFFFu, 0, 0xFFFFFFFEu)]
    [InlineData(0b1010u, 3, 0b0010u)] // flip bit 3 of 0b1010 → 0b0010
    public void Binvi_CorrectResult(uint rs1, int shamt, uint expected) =>
        Assert.Equal(expected, RunUnary(rs1, Binvi(3, 1, shamt)));

    [Theory]
    [InlineData(0u, 5, 0b100000u)]
    [InlineData(0b1010u, 1, 0b1010u)] // already set
    [InlineData(0b1010u, 0, 0b1011u)]
    public void Bseti_CorrectResult(uint rs1, int shamt, uint expected) =>
        Assert.Equal(expected, RunUnary(rs1, Bseti(3, 1, shamt)));

    // ── Zicond tests ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(42u, 0u, 0u)]  // rs2==0 → result is 0 (zeroed)
    [InlineData(42u, 1u, 42u)] // rs2!=0 → result is rs1
    [InlineData(42u, 0xFFFFFFFFu, 42u)]
    [InlineData(0u, 0u, 0u)]
    public void CzeroEqz_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, CzeroEqz(3, 1, 2)));

    [Theory]
    [InlineData(42u, 1u, 0u)]  // rs2!=0 → result is 0 (zeroed)
    [InlineData(42u, 0u, 42u)] // rs2==0 → result is rs1
    [InlineData(42u, 0xFFFFFFFFu, 0u)]
    [InlineData(0u, 0u, 0u)]
    public void CzeroNez_CorrectResult(uint rs1, uint rs2, uint expected) =>
        Assert.Equal(expected, RunInstr(rs1, rs2, CzeroNez(3, 1, 2)));
}