using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Unit tests for the Zbb (basic bit manipulation) extension.
///     Each test runs the target instruction in isolation on a SingleCycleTrain,
///     verifying the result register value.
/// </summary>
public class ZbbTests {
    // ── Encode helpers ────────────────────────────────────────────────────────

    // R-type, opcode=0x33
    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    // I-type ALU, opcode=0x13
    private static uint ITypeAlu(int imm12, int rs1, int funct3, int rd) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12)
             | ((rd & 0x1F) << 7) | 0x13u);

    // EBREAK
    private static uint EBreak() => 0x00100073u;

    // ── Zbb R-type encodings (from riscv32-none-elf-as -march=rv32i_zbb) ─────

    private static uint Andn(int rd, int rs1, int rs2) => RType(0x20, rs2, rs1, 7, rd);
    private static uint Orn(int rd, int rs1, int rs2) => RType(0x20, rs2, rs1, 6, rd);
    private static uint Xnor(int rd, int rs1, int rs2) => RType(0x20, rs2, rs1, 4, rd);
    private static uint Min(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 4, rd);
    private static uint Minu(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 5, rd);
    private static uint Max(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 6, rd);
    private static uint Maxu(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 7, rd);
    private static uint Rol(int rd, int rs1, int rs2) => RType(0x30, rs2, rs1, 1, rd);
    private static uint Ror(int rd, int rs1, int rs2) => RType(0x30, rs2, rs1, 5, rd);

    // zext.h: funct7=0x04, rs2=0, funct3=4, opcode=0x33
    private static uint ZextH(int rd, int rs1) => RType(0x04, 0, rs1, 4, rd);

    // ── Zbb I-type encodings ─────────────────────────────────────────────────

    // clz/ctz/cpop/sext.b/sext.h: funct7=0x30 in SLLI-space (funct3=1)
    private static uint Clz(int rd, int rs1) => ITypeAlu((0x30 << 5) | 0, rs1, 1, rd);
    private static uint Ctz(int rd, int rs1) => ITypeAlu((0x30 << 5) | 1, rs1, 1, rd);
    private static uint Cpop(int rd, int rs1) => ITypeAlu((0x30 << 5) | 2, rs1, 1, rd);
    private static uint SextB(int rd, int rs1) => ITypeAlu((0x30 << 5) | 4, rs1, 1, rd);
    private static uint SextH(int rd, int rs1) => ITypeAlu((0x30 << 5) | 5, rs1, 1, rd);

    // rori: funct7=0x30, funct3=5
    private static uint Rori(int rd, int rs1, int shamt) =>
        ITypeAlu((0x30 << 5) | (shamt & 0x1F), rs1, 5, rd);

    // orc.b: funct7=0x14, rs2_field=7, funct3=5
    private static uint OrcB(int rd, int rs1) => ITypeAlu((0x14 << 5) | 7, rs1, 5, rd);

    // rev8: funct7=0x34, rs2_field=24(0x18), funct3=5
    private static uint Rev8(int rd, int rs1) => ITypeAlu((0x34 << 5) | 24, rs1, 5, rd);

    // ── Test runner ──────────────────────────────────────────────────────────

    // Simpler runner: load x1 and x2 with given 32-bit values, run one instruction,
    // read back rd (encoded as x3) from memory via a SW at the end.
    private static uint RunInstr(uint x1Val, uint x2Val, uint instr) {
        const ulong codeBase = 0x1000u;
        const ulong outAddr = 0x0100u;

        var mem = new FlatMemory(0x4000);
        // lui x1, upper20 then addi x1, x1, lower12 — but values may not fit addi.
        // Use a simpler approach: store values to memory, then LW.
        mem.Load(0x200u, BitConverter.GetBytes(x1Val));
        mem.Load(0x204u, BitConverter.GetBytes(x2Val));

        // Program:
        //   lw  x1, 0x200(x0)   — opcode=0x03, funct3=2 (LW)
        //   lw  x2, 0x204(x0)
        //   <instr>              — uses x1,x2 → writes x3
        //   sw  x3, 0x100(x0)   — opcode=0x23, funct3=2
        //   ebreak
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

    // Run a unary instruction: x1 = val, instr uses x1 → x3, read result.
    private static uint RunUnary(uint x1Val, uint instr) => RunInstr(x1Val, 0, instr);

    // ── R-type tests ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0b1010u, 0b1100u, 0b0010u)] // 1010 & ~1100 = 1010 & 0011 = 0010
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, 0u)]
    [InlineData(0xFFFFFFFFu, 0u, 0xFFFFFFFFu)]
    public void Andn_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Andn(3, 1, 2)));

    [Theory]
    [InlineData(
        0b1010u, 0b1100u, 0xFFFFFFFBu
    )]                                // 1010 | ~1100 = 1010 | ...0011 = ...1011 → only bottom nibble: 1011 = 0xFFFFFFFB
    [InlineData(0u, 0xFFFFFFFFu, 0u)] // 0 | ~0xFFFFFFFF = 0 | 0 = 0
    [InlineData(0u, 0u, 0xFFFFFFFFu)] // 0 | ~0 = 0xFFFFFFFF
    public void Orn_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Orn(3, 1, 2)));

    [Theory]
    [InlineData(0b1010u, 0b1010u, 0xFFFFFFFFu)] // ~(a ^ a) = ~0 = all ones
    [InlineData(0b1010u, 0b0101u, 0xFFFFFFF0u)] // ~(1010 ^ 0101) = ~0b1111 = 0xFFFFFFF0
    [InlineData(0xAAAAAAAAu, 0x55555555u, 0u)]  // ~(AAAA ^ 5555) = ~0xFFFFFFFF = 0
    [InlineData(0u, 0u, 0xFFFFFFFFu)]
    public void Xnor_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Xnor(3, 1, 2)));

    [Theory]
    [InlineData(5u, 3u, 3u)]                   // min(5,3)=3
    [InlineData(0xFFFFFFFFu, 0u, 0xFFFFFFFFu)] // min(-1, 0) = -1 (signed)
    [InlineData(0u, 0xFFFFFFFFu, 0xFFFFFFFFu)] // min(0, -1) = -1
    public void Min_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Min(3, 1, 2)));

    [Theory]
    [InlineData(5u, 3u, 3u)]          // minu(5,3)=3
    [InlineData(0xFFFFFFFFu, 0u, 0u)] // minu(0xFFFF, 0) = 0 (unsigned)
    [InlineData(0u, 0xFFFFFFFFu, 0u)]
    public void Minu_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Minu(3, 1, 2)));

    [Theory]
    [InlineData(5u, 3u, 5u)]          // max(5,3)=5
    [InlineData(0xFFFFFFFFu, 0u, 0u)] // max(-1, 0) = 0 (signed)
    [InlineData(0u, 0xFFFFFFFFu, 0u)]
    public void Max_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Max(3, 1, 2)));

    [Theory]
    [InlineData(5u, 3u, 5u)]                   // maxu(5,3)=5
    [InlineData(0xFFFFFFFFu, 0u, 0xFFFFFFFFu)] // maxu(0xFFFF, 0) = 0xFFFF
    [InlineData(0u, 0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void Maxu_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Maxu(3, 1, 2)));

    [Theory]
    [InlineData(0x12345678u, 4u, 0x23456781u)] // ROL by 4
    [InlineData(0x12345678u, 0u, 0x12345678u)] // ROL by 0 = identity
    [InlineData(0x80000000u, 1u, 0x00000001u)] // MSB wraps to LSB
    public void Rol_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Rol(3, 1, 2)));

    [Theory]
    [InlineData(0x12345678u, 4u, 0x81234567u)] // ROR by 4
    [InlineData(0x12345678u, 0u, 0x12345678u)] // ROR by 0 = identity
    [InlineData(0x00000001u, 1u, 0x80000000u)] // LSB wraps to MSB
    public void Ror_CorrectResult(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Ror(3, 1, 2)));

    [Theory]
    [InlineData(0x12345678u, 0x5678u)]
    [InlineData(0xFFFF8000u, 0x8000u)]
    [InlineData(0u, 0u)]
    public void ZextH_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, ZextH(3, 1)));

    // ── I-type unary tests ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0u, 32u)]         // clz(0) = 32
    [InlineData(1u, 31u)]         // clz(1) = 31
    [InlineData(0x80000000u, 0u)] // clz(MSB) = 0
    [InlineData(0xFFFFFFFFu, 0u)]
    public void Clz_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, Clz(3, 1)));

    [Theory]
    [InlineData(0u, 32u)]          // ctz(0) = 32
    [InlineData(1u, 0u)]           // ctz(1) = 0
    [InlineData(0x80000000u, 31u)] // only MSB set
    [InlineData(0xFFFFFFFEu, 1u)]
    public void Ctz_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, Ctz(3, 1)));

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0xFFFFFFFFu, 32u)]
    [InlineData(0b10110110u, 5u)]
    [InlineData(1u, 1u)]
    public void Cpop_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, Cpop(3, 1)));

    [Theory]
    [InlineData(0x7Fu, 0x7Fu)]       // +127 stays +127
    [InlineData(0x80u, 0xFFFFFF80u)] // -128 sign-extended
    [InlineData(0xFFu, 0xFFFFFFFFu)] // -1 as byte → -1 as word
    [InlineData(0x100u, 0u)]         // upper bits stripped, byte=0
    public void SextB_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, SextB(3, 1)));

    [Theory]
    [InlineData(0x7FFFu, 0x7FFFu)]
    [InlineData(0x8000u, 0xFFFF8000u)] // -32768 sign-extended
    [InlineData(0xFFFFu, 0xFFFFFFFFu)]
    [InlineData(0x10000u, 0u)] // upper bits stripped
    public void SextH_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, SextH(3, 1)));

    // ── Rotate-right-immediate ────────────────────────────────────────────────

    [Theory]
    [InlineData(0x12345678u, 4, 0x81234567u)]
    [InlineData(0x12345678u, 0, 0x12345678u)]
    [InlineData(0x00000001u, 1, 0x80000000u)]
    [InlineData(0x00000001u, 31, 0x00000002u)]
    public void Rori_CorrectResult(uint a, int shamt, uint expected) =>
        Assert.Equal(expected, RunUnary(a, Rori(3, 1, shamt)));

    // ── orc.b ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x00000000u, 0x00000000u)]
    [InlineData(0xFF000000u, 0xFF000000u)]
    [InlineData(0x01020304u, 0xFFFFFFFFu)] // all bytes nonzero → all 0xFF
    [InlineData(0x01000001u, 0xFF0000FFu)] // bytes 1,2 are zero
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void OrcB_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, OrcB(3, 1)));

    // ── rev8 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x12345678u, 0x78563412u)]
    [InlineData(0x00000001u, 0x01000000u)]
    [InlineData(0xDEADBEEFu, 0xEFBEADDEu)]
    [InlineData(0u, 0u)]
    public void Rev8_CorrectResult(uint a, uint expected) =>
        Assert.Equal(expected, RunUnary(a, Rev8(3, 1)));
}