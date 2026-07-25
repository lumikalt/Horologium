#region

using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Unit tests for the Zvbb (vector basic bit-manipulation), Zvkb (vector cryptography
///     bit-manipulation — a proper subset of Zvbb with no encodings of its own) and Zvbc (vector
///     carryless multiplication) extensions. Unlike the Zvk* crypto extensions (dedicated opcode
///     0x77, element-group semantics), these instructions reuse the standard OP-V opcode (0x57)
///     and are per-element (EEW=SEW), so they piggyback on the existing VIntOp/VWideOp record
///     shapes used by the base V-extension integer ALU/multiply ops.
/// </summary>
public class ZvbbTests {
    private const int VtypeiE8M1Tama = (1 << 7) | (1 << 6);
    private const int VtypeiE32M1Tama = (1 << 7) | (1 << 6) | (2 << 3);
    private const int VtypeiE64M1Tama = (1 << 7) | (1 << 6) | (3 << 3);

    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(0x10000);

    // ── Encoding helpers (mirrors VectorTests.cs's conventions) ──────────────────────────────

    private static uint Vsetivli(int rd, int zimm, int vtypei) =>
        0xC0000000u | (uint)((vtypei << 20) | (zimm << 15) | (7 << 12) | (rd << 7) | 0x57);

    private static uint VopVv(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | ((vd & 0x1F) << 7) | 0x57);

    private static uint VopVx(int funct6, int vd, int vs2, int rs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (4 << 12) | ((vd & 0x1F) << 7) | 0x57);

    private static uint VopVi(int funct6, int vd, int vs2, int imm5, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((imm5 & 0x1F) << 15) | (3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    private static uint VopMvv(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | (2 << 12) | ((vd & 0x1F) << 7) | 0x57);

    private static uint VopMvx(int funct6, int vd, int vs2, int rs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (6 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vror.vi: bits[31:27]=0x0A (fixed), bit26="zimm6hi" (immediate bit 5), bits[19:15]=imm[4:0].
    // The plain 5-bit VopVi helper can't reach immediates >= 32, needed to reach SEW=64 rotate
    // amounts (0-63) — this mirrors the decoder's own bit-stealing assembly in Rv32Decoder.V.cs.
    private static uint VrorVi(int vd, int vs2, int uimm6, bool masked = false) =>
        (uint)((0x0A << 27) | (((uimm6 >> 5) & 1) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((uimm6 & 0x1F) << 15) | (3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    private static Rv32ArchState MakeState() => new();

    private static void SetVReg(Rv32ArchState s, int vr, uint[] elements32) {
        var bytes = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elements32.Length && i * 4 + 3 < bytes.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), elements32[i]);
        s.VectorRegisters.Write(vr, bytes);
    }

    private static void SetVReg8(Rv32ArchState s, int vr, byte[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i < buf.Length; i++) buf[i] = elems[i];
        s.VectorRegisters.Write(vr, buf);
    }

    private static void SetVReg64(Rv32ArchState s, int vr, ulong[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i * 8 + 7 < buf.Length; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(i * 8), elems[i]);
        s.VectorRegisters.Write(vr, buf);
    }

    private ExecuteResult Exec(uint raw, Rv32ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    private void ConfigVl4E8(Rv32ArchState s) => Exec(Vsetivli(10, 4, VtypeiE8M1Tama), s);
    private void ConfigVl4E32(Rv32ArchState s) => Exec(Vsetivli(10, 4, VtypeiE32M1Tama), s);
    private void ConfigVl2E64(Rv32ArchState s) => Exec(Vsetivli(10, 2, VtypeiE64M1Tama), s);

    // ── Zvbb/Zvkb: vandn ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VandnVv_AndsWithComplement() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0xFFFFFFFFu, 0xF0F0F0F0u, 0u, 0xAAAAAAAAu,]);
        SetVReg(s, 3, [0x0000FFFFu, 0x0F0F0F0Fu, 0xFFFFFFFFu, 0x55555555u,]);

        Exec(VopVv(1, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0xFFFF0000u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0xF0F0F0F0u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(0xAAAAAAAAu, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VandnVx_AndsWithComplement() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0xFFFFFFFFu, 0x12345678u, 0u, 0u,]);
        s.IntegerRegisters.Write(5, 0x0000FFFFu);

        Exec(VopVx(1, 1, 2, 5), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0xFFFF0000u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0x12340000u, BitConverter.ToUInt32(r, 4));
    }

    // ── Zvbb/Zvkb: vrol/vror ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VrolVv_RotatesLeft() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x80000001u, 1u, 0u, 0u,]);
        SetVReg(s, 3, [1u, 31u, 0u, 0u,]); // rotate amounts

        Exec(VopVv(0x15, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0x00000003u, BitConverter.ToUInt32(r, 0)); // 0x80000001 rol 1 = 0x3
        Assert.Equal(0x80000000u, BitConverter.ToUInt32(r, 4)); // 1 rol 31 = 0x80000000
    }

    [Fact]
    public void Execute_VrorVv_RotatesRight() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x00000003u, 0x80000000u, 0u, 0u,]);
        SetVReg(s, 3, [1u, 31u, 0u, 0u,]);

        Exec(VopVv(0x14, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0x80000001u, BitConverter.ToUInt32(r, 0)); // 3 ror 1 = 0x80000001
        Assert.Equal(1u, BitConverter.ToUInt32(r, 4));          // 0x80000000 ror 31 = 1
    }

    [Fact]
    public void Execute_VrorVx_RotatesRight() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x00000003u, 0u, 0u, 0u,]);
        s.IntegerRegisters.Write(5, 1u);

        Exec(VopVx(0x14, 1, 2, 5), s).SideEffect!(s);

        Assert.Equal(0x80000001u, BitConverter.ToUInt32(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Decode_VrorVi_AssemblesSixBitImmediate() {
        // Rotate amount 45 (0b101101) requires bit 5 set — only reachable via the bit-stolen
        // encoding, not the plain 5-bit OPIVI immediate field every other .vi form uses.
        uint raw = VrorVi(1, 2, 45);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVi>(i.Payload);
        var op = (RvVIntAluVi)i.Payload!;
        Assert.Equal(VIntOp.Ror, op.Op);
        Assert.Equal(45, op.Imm);
    }

    [Fact]
    public void Execute_VrorVi_RotatesBySixBitImmediateAtSew64() {
        Rv32ArchState s = MakeState();
        ConfigVl2E64(s);
        SetVReg64(s, 2, [1UL, 0UL,]);

        // Rotate amount 45 is only reachable with the 6-bit immediate (SEW=64 needs 0-63).
        Exec(VrorVi(1, 2, 45), s).SideEffect!(s);

        Assert.Equal(1UL << (64 - 45), BitConverter.ToUInt64(s.VectorRegisters.Read(1), 0));
    }

    // ── Zvbb: vwsll ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VwsllVv_WideningShiftLeft() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s); // SEW=8 in, SEW=16 out
        SetVReg8(s, 2, [0x01, 0xFF, 0x80, 0x01,]);
        SetVReg8(s, 3, [1, 4, 1, 9,]); // shift amounts (mod 2*SEW=16)

        Exec(VopVv(0x35, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal((ushort)0x0002, BitConverter.ToUInt16(r, 0)); // 1 << 1
        Assert.Equal((ushort)0x0FF0, BitConverter.ToUInt16(r, 2)); // 0xFF << 4
        Assert.Equal((ushort)0x0100, BitConverter.ToUInt16(r, 4)); // 0x80 << 1
        Assert.Equal((ushort)0x0200, BitConverter.ToUInt16(r, 6)); // 1 << 9
    }

    [Fact]
    public void Execute_VwsllVi_WideningShiftLeft() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [0x01, 0x02, 0, 0,]);

        Exec(VopVi(0x35, 1, 2, 8), s).SideEffect!(s); // shift by immediate 8

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal((ushort)0x0100, BitConverter.ToUInt16(r, 0));
        Assert.Equal((ushort)0x0200, BitConverter.ToUInt16(r, 2));
    }

    [Fact]
    public void Execute_VwsllVx_WideningShiftLeft() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [0x01, 0, 0, 0,]);
        s.IntegerRegisters.Write(5, 8u);

        Exec(VopVx(0x35, 1, 2, 5), s).SideEffect!(s);

        Assert.Equal((ushort)0x0100, BitConverter.ToUInt16(s.VectorRegisters.Read(1), 0));
    }

    // ── Zvbb/Zvkb: vbrev8/vrev8 — Zvbb-only: vbrev/vclz/vctz/vcpop ───────────────────────────

    [Fact]
    public void Execute_Vbrev8V_ReversesBitsWithinEachByte() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x00000001u, 0x000000F0u, 0u, 0u,]);

        Exec(VopMvv(0x12, 1, 2, 0x8), s).SideEffect!(s); // vs1=8 -> Brev8

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0x00000080u, BitConverter.ToUInt32(r, 0)); // 0x01 -> 0x80 within its byte
        Assert.Equal(0x0000000Fu, BitConverter.ToUInt32(r, 4)); // 0xF0 -> 0x0F within its byte
    }

    [Fact]
    public void Execute_Vrev8V_ReversesByteOrder() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x01020304u, 0u, 0u, 0u,]);

        Exec(VopMvv(0x12, 1, 2, 0x9), s).SideEffect!(s); // vs1=9 -> Rev8

        Assert.Equal(0x04030201u, BitConverter.ToUInt32(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Execute_VbrevV_ReversesAllBits() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x00000001u, 0x80000000u, 0u, 0u,]);

        Exec(VopMvv(0x12, 1, 2, 0xA), s).SideEffect!(s); // vs1=0xA -> Brev

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0x80000000u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0x00000001u, BitConverter.ToUInt32(r, 4));
    }

    [Fact]
    public void Execute_VclzV_CountsLeadingZeros() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x00000001u, 0x80000000u, 0u, 0x0000FFFFu,]);

        Exec(VopMvv(0x12, 1, 2, 0xC), s).SideEffect!(s); // vs1=0xC -> Clz

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(31u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(32u, BitConverter.ToUInt32(r, 8)); // zero input -> SEW
        Assert.Equal(16u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VctzV_CountsTrailingZeros() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0x80000000u, 0x00000001u, 0u, 0x00010000u,]);

        Exec(VopMvv(0x12, 1, 2, 0xD), s).SideEffect!(s); // vs1=0xD -> Ctz

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(31u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(32u, BitConverter.ToUInt32(r, 8)); // zero input -> SEW
        Assert.Equal(16u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VcpopV_CountsSetBits() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0xFFFFFFFFu, 0u, 0x0F0F0F0Fu, 0x80000001u,]);

        Exec(VopMvv(0x12, 1, 2, 0xE), s).SideEffect!(s); // vs1=0xE -> Cpop

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(32u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(16u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(2u, BitConverter.ToUInt32(r, 12));
    }

    // ── Zvbc: vclmul/vclmulh ──────────────────────────────────────────────────────────────────
    // Expected values are derived independently from the GF(2)[x] polynomial identities, not
    // from the implementation under test: (x^2+1)(x+1) = x^3+x^2+x+1 = 0b1111, and
    // (x^63+1)^2 = x^126 + x^63 + x^63 + 1 = x^126 + 1 (the x^63 terms cancel over GF(2)), so the
    // low half keeps only bit 0 and the high half has only bit 126 (bit 62 of the high word) set.

    [Fact]
    public void Execute_VclmulVv_LowHalf_MatchesGf2PolynomialMultiply() {
        Rv32ArchState s = MakeState();
        ConfigVl2E64(s);
        SetVReg64(s, 2, [0b101UL, 0x8000000000000001UL,]);
        SetVReg64(s, 3, [0b011UL, 0x8000000000000001UL,]);

        Exec(VopMvv(0x0C, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0b1111UL, BitConverter.ToUInt64(r, 0));
        Assert.Equal(1UL, BitConverter.ToUInt64(r, 8));
    }

    [Fact]
    public void Execute_VclmulhVv_HighHalf_MatchesGf2PolynomialMultiply() {
        Rv32ArchState s = MakeState();
        ConfigVl2E64(s);
        SetVReg64(s, 2, [0b101UL, 0x8000000000000001UL,]);
        SetVReg64(s, 3, [0b011UL, 0x8000000000000001UL,]);

        Exec(VopMvv(0x0D, 1, 2, 3), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0UL, BitConverter.ToUInt64(r, 0)); // product fits entirely in the low half
        Assert.Equal(1UL << 62, BitConverter.ToUInt64(r, 8));
    }

    [Fact]
    public void Execute_VclmulVx_LowHalf() {
        Rv32ArchState s = MakeState();
        ConfigVl2E64(s);
        SetVReg64(s, 2, [0b101UL, 0UL,]);
        s.IntegerRegisters.Write(5, 0b011u);

        Exec(VopMvx(0x0C, 1, 2, 5), s).SideEffect!(s);

        Assert.Equal(0b1111UL, BitConverter.ToUInt64(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Execute_VclmulVx_And_VclmulhVx_SplitAcrossHalfBoundary() {
        Rv32ArchState s = MakeState();
        ConfigVl2E64(s);
        SetVReg64(s, 2, [0x8000000000000000UL, 0UL,]); // x = u^63
        s.IntegerRegisters.Write(5, 2u);                // y = u^1 (rs1 zero-extends to 64 bits)

        // x*y = u^63 * u^1 = u^64 over GF(2): entirely bit 64, i.e. bit 0 of the high word —
        // nothing lands in the low half, everything lands in the high half.
        Exec(VopMvx(0x0C, 1, 2, 5), s).SideEffect!(s);
        Assert.Equal(0UL, BitConverter.ToUInt64(s.VectorRegisters.Read(1), 0));

        Exec(VopMvx(0x0D, 1, 2, 5), s).SideEffect!(s);
        Assert.Equal(1UL, BitConverter.ToUInt64(s.VectorRegisters.Read(1), 0));
    }

    [Theory]
    [InlineData(VtypeiE8M1Tama)]
    [InlineData(VtypeiE32M1Tama)]
    public void Execute_VclmulVv_WrongSew_Traps(int vtypei) {
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 2, vtypei), s);

        ExecuteResult r = Exec(VopMvv(0x0C, 1, 2, 3), s);

        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }
}
