using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionResultEqualsZero
// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
/// Tests for Zcmop (compressed may-be-operations): c.mop.N, N ∈ {1,3,5,...,15}.
/// Encoding: Q1 (bits[1:0]=01), funct3=011, nzimm=0, rd=even 0..14.
/// All 8 variants are hint NOPs with no architectural effect.
/// <para>
/// Encodings (hex):
///   c.mop.1  = 0x6081   c.mop.3  = 0x6181   c.mop.5  = 0x6281   c.mop.7  = 0x6381
///   c.mop.9  = 0x6481   c.mop.11 = 0x6581   c.mop.13 = 0x6681   c.mop.15 = 0x6781
/// </para>
/// </summary>
public class ZcmopTests {
    private const ulong CodeBase = 0x1000u;
    private const uint EBreak = 0x00100073u;

    // ── Decoder unit tests ────────────────────────────────────────────────────

    private static ITooth Decode(ushort c) => new Rv32Decoder().Decode(ZcmopTests.CodeBase, c);

    [Theory]
    [InlineData(0x6081, 1)]
    [InlineData(0x6181, 3)]
    [InlineData(0x6281, 5)]
    [InlineData(0x6381, 7)]
    [InlineData(0x6481, 9)]
    [InlineData(0x6581, 11)]
    [InlineData(0x6681, 13)]
    [InlineData(0x6781, 15)]
    public void CMopN_DecodesCorrectly(int encoding, int expectedN) {
        ITooth t = Decode((ushort)encoding);
        var op = Assert.IsType<RvCMopN>(t.Payload);
        Assert.Equal(expectedN, op.N);
        Assert.Equal(-1, t.DestinationRegister);
        Assert.Empty(t.SourceRegisters);
        Assert.Equal(ToothClass.IntegerAlu, t.Class);
    }

    // ── Pipeline (NOP) tests ──────────────────────────────────────────────────

    // Load a 16-bit compressed instruction followed by 32-bit EBREAK at CodeBase.
    private static bool RunCMop(ushort cmopEncoding) {
        var mem = new FlatMemory(0x4000);
        byte[] code = [
            (byte)(cmopEncoding & 0xFF),
            (byte)(cmopEncoding >> 8),
            (byte)(ZcmopTests.EBreak & 0xFF),
            (byte)((ZcmopTests.EBreak >> 8) & 0xFF),
            (byte)((ZcmopTests.EBreak >> 16) & 0xFF),
            (byte)(ZcmopTests.EBreak >> 24),
        ];
        mem.Load(ZcmopTests.CodeBase, code);
        new SingleCycleTrain(new Rv32Mechanism(), mem, ZcmopTests.CodeBase).Run(200);
        return true;
    }

    [Fact]
    public void CMop1_IsNop() => Assert.True(RunCMop(0x6081));

    [Fact]
    public void CMop15_IsNop() => Assert.True(RunCMop(0x6781));

    [Fact]
    public void CMop_DoesNotModifyRegister() {
        // addi x1, x0, 42 — set sentinel in x1
        const uint addi = (42 << 20) | (0 << 15) | (0 << 12) | (1 << 7) | 0x13u;
        // sw x0, x1, 0x100 — store x1 to 0x100
        const int imm = 0x100;
        const uint sw = (((imm >> 5) & 0x7F) << 25) | (1 << 20) | (0 << 15) | (2 << 12) | ((imm & 0x1F) << 7) | 0x23u;

        var mem = new FlatMemory(0x4000);
        // Layout: addi(32-bit), c.mop.5(16-bit), sw(32-bit), ebreak(32-bit)
        byte[] code = [
            (byte)(addi & 0xFF), (byte)((addi >> 8) & 0xFF),
            (byte)((addi >> 16) & 0xFF), (byte)(addi >> 24),
            0x81, 0x62, // c.mop.5 = 0x6281
            (byte)(sw & 0xFF), (byte)((sw >> 8) & 0xFF),
            (byte)((sw >> 16) & 0xFF), (byte)(sw >> 24),
            (byte)(ZcmopTests.EBreak & 0xFF), (byte)((ZcmopTests.EBreak >> 8) & 0xFF),
            (byte)((ZcmopTests.EBreak >> 16) & 0xFF), (byte)(ZcmopTests.EBreak >> 24),
        ];
        mem.Load(ZcmopTests.CodeBase, code);
        new SingleCycleTrain(new Rv32Mechanism(), mem, ZcmopTests.CodeBase).Run(200);

        Assert.Equal(42u, (uint)mem.Read(0x100, 4));
    }
}