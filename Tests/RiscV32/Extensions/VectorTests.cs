using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

// ReSharper disable ShiftExpressionZeroLeftOperand

// VectorRegisters is public on Rv32ArchState; CsrFile constants are public statics.
// CSR values are read via the public ISystemRegisters.Read() path.

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Unit tests for the RISC-V V extension (decoder + executor).
///     All raw encodings are hand-assembled using the V spec 1.0 encoding tables.
/// </summary>
public class VectorTests {
    // vtypei for e32, m1, ta, ma: vma=bit7, vta=bit6, vsew[5:3]=010, vlmul[2:0]=000 → 0xD0
    private const int VtypeiE32M1Tama = (1 << 7) | (1 << 6) | (2 << 3);

    // vtypei for e8,m1,ta,ma: vma=bit7, vta=bit6, vsew[5:3]=000, vlmul[2:0]=000 → 0xC0
    private const int VtypeiE8M1Tama = (1 << 7) | (1 << 6);

    // vtypei for e16,m1,ta,ma: vsew[2:0]=001
    private const int VtypeiE16M1Tama = (1 << 7) | (1 << 6) | (1 << 3);
    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(0x10000);

    // ── Encoding helpers ──────────────────────────────────────────────────────

    // vsetvli rd, rs1, vtypei  (bit31=0, bits[30:20]=vtypei, funct3=7, opcode=0x57)
    private static uint Vsetvli(int rd, int rs1, int vtypei) =>
        (uint)((vtypei << 20) | (rs1 << 15) | (7 << 12) | (rd << 7) | 0x57);

    // vsetivli rd, zimm, vtypei  (bits[31:30]=11, bits[29:20]=vtypei, bits[19:15]=zimm)
    private static uint Vsetivli(int rd, int zimm, int vtypei) =>
        0xC0000000u | (uint)((vtypei << 20) | (zimm << 15) | (7 << 12) | (rd << 7) | 0x57);

    // vadd/vsub/etc.vv vd, vs2, vs1  (funct6, vm=1, funct3=0, opcode=0x57)
    private static uint VopVv(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | ((vd & 0x1F) << 7) | 0x57);

    // vadd/etc.vx vd, vs2, rs1  (funct3=4)
    private static uint VopVx(int funct6, int vd, int vs2, int rs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (4 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vadd/etc.vi vd, vs2, simm5  (funct3=3)
    private static uint VopVi(int funct6, int vd, int vs2, int imm5, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((imm5 & 0x1F) << 15) | (3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vle{sew}.v vd, (rs1)  (opcode=0x07, funct3=width, lumop=0, vm=1)
    private static uint Vle(int vd, int rs1, int funct3Width) =>
        (uint)((1 << 25) | (rs1 << 15) | (funct3Width << 12) | (vd << 7) | 0x07);

    // vse{sew}.v vs3, (rs1)  (opcode=0x27, funct3=width, sumop=0, vm=1)
    private static uint Vse(int vs3, int rs1, int funct3Width) =>
        (uint)((1 << 25) | (rs1 << 15) | (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vlse{sew}.v vd,(rs1),rs2  (mop=2, opcode=0x07)
    private static uint Vlse(int vd, int rs1, int rs2, int funct3Width, bool masked = false) =>
        (uint)((2 << 26) | ((masked ? 0 : 1) << 25) | (rs2 << 20) | (rs1 << 15) |
               (funct3Width << 12) | (vd << 7) | 0x07);

    // vsse{sew}.v vs3,(rs1),rs2  (mop=2, opcode=0x27)
    private static uint Vsse(int vs3, int rs1, int rs2, int funct3Width, bool masked = false) =>
        (uint)((2 << 26) | ((masked ? 0 : 1) << 25) | (rs2 << 20) | (rs1 << 15) |
               (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vluxei{sew}.v vd,(rs1),vs2  (mop=1, opcode=0x07)
    private static uint Vluxei(int vd, int rs1, int vs2, int funct3Width, bool masked = false) =>
        (uint)((1 << 26) | ((masked ? 0 : 1) << 25) | (vs2 << 20) | (rs1 << 15) |
               (funct3Width << 12) | (vd << 7) | 0x07);

    // vloxei{sew}.v vd,(rs1),vs2  (mop=3, opcode=0x07)
    private static uint Vloxei(int vd, int rs1, int vs2, int funct3Width, bool masked = false) =>
        (uint)((3 << 26) | ((masked ? 0 : 1) << 25) | (vs2 << 20) | (rs1 << 15) |
               (funct3Width << 12) | (vd << 7) | 0x07);

    // vsuxei{sew}.v vs3,(rs1),vs2  (mop=1, opcode=0x27)
    private static uint Vsuxei(int vs3, int rs1, int vs2, int funct3Width, bool masked = false) =>
        (uint)((1 << 26) | ((masked ? 0 : 1) << 25) | (vs2 << 20) | (rs1 << 15) |
               (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vlseg{nf}e{sew}.v vd,(rs1)  (unit-stride, mop=0, lumop=0, nf=numFields-1)
    private static uint Vlseg(int numFields, int vd, int rs1, int funct3Width, bool masked = false) =>
        (uint)(((numFields - 1) << 29) | ((masked ? 0 : 1) << 25) |
               (rs1 << 15) | (funct3Width << 12) | (vd << 7) | 0x07);

    // vsseg{nf}e{sew}.v vs3,(rs1)  (unit-stride, mop=0, sumop=0, nf=numFields-1)
    private static uint Vsseg(int numFields, int vs3, int rs1, int funct3Width, bool masked = false) =>
        (uint)(((numFields - 1) << 29) | ((masked ? 0 : 1) << 25) |
               (rs1 << 15) | (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vslideup/vslidedown.vx  (funct3=4=OPIVX) and vslide1up/down.vx (funct3=6=OPMVX)
    private static uint VslideVx(int funct6, int vd, int vs2, int rs1, int funct3, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (funct3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vslideup/vslidedown.vi  (funct3=3=OPIVI, uimm5 in bits[19:15])
    private static uint VslideVi(int funct6, int vd, int vs2, int uimm5, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((uimm5 & 0x1F) << 15) | (3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vmul/vdiv/etc.vv vd, vs2, vs1  (OPMVV, funct3=2)
    private static uint VopMvv(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | (2 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vmul/vdiv/etc.vx vd, vs2, rs1  (OPMVX, funct3=6)
    private static uint VopMvx(int funct6, int vd, int vs2, int rs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (6 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vfadd/vfmul/etc.vv vd, vs2, vs1  (OPFVV, funct3=1)
    private static uint VopFvv(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | (1 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vfadd/vfmul/etc.vf vd, vs2, rs1  (OPFVF, funct3=5, rs1=float reg 0-31)
    private static uint VopFvf(int funct6, int vd, int vs2, int frs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((frs1 & 0x1F) << 15) | (5 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vfsqrt.v / vfcvt.* / vfclass.v — unary, vs1 field selects sub-op (OPFVV, funct3=1)
    private static uint VopFvUnary(int funct6, int vd, int vs2, int vs1Sel, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1Sel & 0x1F) << 15) | (1 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // ── Decoder tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Vsetvli() {
        // vsetvli a0(x10), zero(x0), e32,m1,ta,ma
        uint raw = Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVsetvli>(i.Payload);
        var op = (RvVsetvli)i.Payload!;
        Assert.Equal(10, op.Rd);
        Assert.Equal(0, op.Rs1);
        Assert.Equal(VectorTests.VtypeiE32M1Tama, op.Vtypei);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal(10, i.DestinationRegister); // writes integer rd
    }

    [Fact]
    public void Decode_Vsetvli_HasIntegerDest() {
        // vsetvli writes rd (integer), so DestinationRegister = rd
        uint raw = Vsetvli(10, 1, VectorTests.VtypeiE32M1Tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.Equal(10, i.DestinationRegister);
    }

    [Fact]
    public void Decode_Vsetivli() {
        // vsetivli a1(x11), 4, e32,m1,ta,ma
        uint raw = Vsetivli(11, 4, VectorTests.VtypeiE32M1Tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVsetivli>(i.Payload);
        var op = (RvVsetivli)i.Payload!;
        Assert.Equal(11, op.Rd);
        Assert.Equal(4, op.Zimm);
        Assert.Equal(VectorTests.VtypeiE32M1Tama, op.Vtypei);
    }

    [Fact]
    public void Decode_Vle32() {
        // vle32.v v1, (a0)  — funct3=6 for 32-bit
        uint raw = Vle(1, 10, 6);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVv>(i.Payload);
        var op = (RvVleVv)i.Payload!;
        Assert.Equal(1, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(32, op.Sew);
        Assert.False(op.Masked);
        Assert.Equal(-1, i.DestinationRegister); // vector dest, not integer
    }

    [Fact]
    public void Decode_Vse32() {
        // vse32.v v2, (a1)
        uint raw = Vse(2, 11, 6);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVseVv>(i.Payload);
        var op = (RvVseVv)i.Payload!;
        Assert.Equal(2, op.Vs3);
        Assert.Equal(11, op.Rs1);
        Assert.Equal(32, op.Sew);
    }

    [Fact]
    public void Decode_VaddVV() {
        // vadd.vv v1, v2, v3  (funct6=0, funct3=0)
        uint raw = VopVv(0, 1, 2, 3);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVv>(i.Payload);
        var op = (RvVIntAluVv)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VsubVV() {
        // vsub.vv v1, v2, v3  (funct6=2)
        uint raw = VopVv(2, 1, 2, 3);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVv>(i.Payload);
        Assert.Equal(VIntOp.Sub, ((RvVIntAluVv)i.Payload!).Op);
    }

    [Fact]
    public void Decode_VaddVX() {
        // vadd.vx v4, v5, a0(x10)
        uint raw = VopVx(0, 4, 5, 10);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVx>(i.Payload);
        var op = (RvVIntAluVx)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(4, op.Vd);
        Assert.Equal(5, op.Vs2);
        Assert.Equal(10, op.Rs1);
    }

    [Fact]
    public void Decode_VaddVI() {
        // vadd.vi v1, v2, 7
        uint raw = VopVi(0, 1, 2, 7);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVi>(i.Payload);
        var op = (RvVIntAluVi)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(7, op.Imm);
    }

    [Fact]
    public void Decode_VmseqVV() {
        // vmseq.vv v0, v1, v2  (funct6=24)
        uint raw = VopVv(24, 0, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVMaskCmpVv>(i.Payload);
        var op = (RvVMaskCmpVv)i.Payload!;
        Assert.Equal(VMaskCmpOp.Eq, op.Op);
        Assert.Equal(0, op.Vd);
    }

    [Fact]
    public void Decode_VmsneVX() {
        // vmsne.vx v0, v2, a1  (funct6=25, funct3=4)
        uint raw = VopVx(25, 0, 2, 11);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVMaskCmpVx>(i.Payload);
        Assert.Equal(VMaskCmpOp.Ne, ((RvVMaskCmpVx)i.Payload!).Op);
    }

    [Fact]
    public void Decode_Vle8() {
        // vle8.v v3, (a2)  — funct3=0 for 8-bit
        uint raw = Vle(3, 12, 0);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVv>(i.Payload);
        Assert.Equal(8, ((RvVleVv)i.Payload!).Sew);
    }

    [Fact]
    public void Decode_Vle16() {
        // vle16.v v3, (a2)  — funct3=5 for 16-bit
        uint raw = Vle(3, 12, 5);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVv>(i.Payload);
        Assert.Equal(16, ((RvVleVv)i.Payload!).Sew);
    }

    // ── Executor tests ────────────────────────────────────────────────────────

    private static Rv32ArchState MakeState() => new();

    private static void SetGpr(Rv32ArchState s, int r, uint v) => s.IntegerRegisters.Write(r, v);

    private static void SetVReg(Rv32ArchState s, int vr, uint[] elements32) {
        var bytes = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elements32.Length && i < 4; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), elements32[i]);
        s.VectorRegisters.Write(vr, bytes);
    }

    private static ulong ReadCsr(Rv32ArchState s, uint addr) =>
        s.SystemRegisters.Read(addr, RvPrivilege.Machine);

    private static void SetVReg8(Rv32ArchState s, int vr, byte[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i < buf.Length; i++) buf[i] = elems[i];
        s.VectorRegisters.Write(vr, buf);
    }

    private static void SetVReg16(Rv32ArchState s, int vr, ushort[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i * 2 + 1 < buf.Length; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(i * 2), elems[i]);
        s.VectorRegisters.Write(vr, buf);
    }

    private static ushort R16(byte[] data, int i) => BitConverter.ToUInt16(data, i * 2);

    private ExecuteResult Exec(uint raw, Rv32ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // Helper: configure vl=4, sew=32 via vsetvli
    private void ConfigVl4E32(Rv32ArchState s) {
        uint raw = Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama); // vsetvli a0, x0, e32,m1,ta,ma
        Exec(raw, s);                                           // side-effect: updates vl and vtype in state
    }

    private void ConfigVl4E8(Rv32ArchState s) => Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
    private void ConfigVl4E16(Rv32ArchState s) => Exec(Vsetivli(10, 4, VectorTests.VtypeiE16M1Tama), s);

    [Fact]
    public void Execute_Vsetvli_SetsVlAndVtype() {
        Rv32ArchState s = MakeState();
        // vsetvli a0(10), zero, e32,m1,ta,ma → should set vl=4, return 4 in a0
        uint raw = Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama);
        ExecuteResult r = Exec(raw, s);

        Assert.Equal(4UL, r.RegisterResult.Value); // new vl
        Assert.Equal(4u, ReadCsr(s, CsrFile.Vl));
        Assert.Equal((uint)VectorTests.VtypeiE32M1Tama, ReadCsr(s, CsrFile.Vtype));
    }

    [Fact]
    public void Execute_Vsetvli_AvlCapsAtVlmax() {
        Rv32ArchState s = MakeState();
        SetGpr(s, 1, 100);                                      // AVL=100, much larger than VLMAX=4
        uint raw = Vsetvli(10, 1, VectorTests.VtypeiE32M1Tama); // vsetvli a0, x1, e32,m1,ta,ma
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(4UL, r.RegisterResult.Value);
        Assert.Equal(4u, ReadCsr(s, CsrFile.Vl));
    }

    [Fact]
    public void Execute_Vsetivli_SetsVl() {
        Rv32ArchState s = MakeState();
        uint raw = Vsetivli(10, 3, VectorTests.VtypeiE32M1Tama); // AVL=3
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(3UL, r.RegisterResult.Value);
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));
    }

    [Fact]
    public void Execute_Vsetvli_PreservesVlWhenRd0Rs10() {
        Rv32ArchState s = MakeState();
        // Set vl=3 by using a non-zero rs1 (AVL=3 in x1)
        SetGpr(s, 1, 3);
        Exec(Vsetvli(10, 1, VectorTests.VtypeiE32M1Tama), s); // vl = min(3, 4) = 3
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));

        // Now: vsetvli x0, x0, vtypei → vl unchanged, vtype updated
        uint raw = Vsetvli(0, 0, 0x42); // different vtypei (e8m1)
        Exec(raw, s);
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));       // preserved
        Assert.Equal(0x42u, ReadCsr(s, CsrFile.Vtype)); // updated to new vtypei
    }

    [Fact]
    public void Execute_Vle32_LoadsElements() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);

        // Store [10, 20, 30, 40] at address 0x100
        _mem.Write(0x100, 10, 4);
        _mem.Write(0x104, 20, 4);
        _mem.Write(0x108, 30, 4);
        _mem.Write(0x10C, 40, 4);
        SetGpr(s, 10, 0x100); // a0 = 0x100

        uint raw = Vle(1, 10, 6); // vle32.v v1, (a0)
        ExecuteResult r = Exec(raw, s);

        Assert.NotNull(r.SideEffect);
        r.SideEffect!(s);

        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(result, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_Vse32_StoresElements() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [100u, 200u, 300u, 400u,]);
        SetGpr(s, 11, 0x200); // a1 = 0x200

        uint raw = Vse(2, 11, 6); // vse32.v v2, (a1)
        ExecuteResult r = Exec(raw, s);
        Assert.Null(r.SideEffect);

        Assert.Equal(100UL, _mem.Read(0x200, 4));
        Assert.Equal(200UL, _mem.Read(0x204, 4));
        Assert.Equal(300UL, _mem.Read(0x208, 4));
        Assert.Equal(400UL, _mem.Read(0x20C, 4));
    }

    [Fact]
    public void Execute_VaddVV_AddsElements() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 3, [10u, 20u, 30u, 40u,]);

        uint raw = VopVv(0, 1, 2, 3); // vadd.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);

        Assert.NotNull(r.SideEffect);
        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(11u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(22u, BitConverter.ToUInt32(result, 4));
        Assert.Equal(33u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(44u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_VsubVV_SubtractsElements() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [10u, 20u, 30u, 40u,]);
        SetVReg(s, 3, [1u, 2u, 3u, 4u,]);

        uint raw = VopVv(2, 1, 2, 3); // vsub.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(9u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(18u, BitConverter.ToUInt32(result, 4));
    }

    [Fact]
    public void Execute_VaddVX_AddsScalar() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetGpr(s, 5, 100);

        uint raw = VopVx(0, 1, 2, 5); // vadd.vx v1, v2, x5
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(101u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(102u, BitConverter.ToUInt32(result, 4));
        Assert.Equal(103u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(104u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_VaddVI_AddsImmediate() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [5u, 10u, 15u, 20u,]);

        uint raw = VopVi(0, 1, 2, 3); // vadd.vi v1, v2, 3
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(8u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(13u, BitConverter.ToUInt32(result, 4));
    }

    [Fact]
    public void Execute_VmseqVV_SetsMatchBits() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // v1 = [5, 5, 7, 7], v2 = [5, 6, 7, 8]
        SetVReg(s, 1, [5u, 5u, 7u, 7u,]);
        SetVReg(s, 2, [5u, 6u, 7u, 8u,]);

        uint raw = VopVv(24, 0, 1, 2); // vmseq.vv v0, v1, v2
        ExecuteResult r = Exec(raw, s);

        Assert.NotNull(r.SideEffect);
        r.SideEffect!(s);
        // elements 0 and 2 match: bits [0] and [2] set → byte 0 = 0b0101 = 5
        Assert.Equal(0b0101, s.VectorRegisters.Read(0)[0]);
    }

    [Fact]
    public void Execute_VmsltuVV_UnsignedLessThan() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // v1 = [1, 5, 3, 10], v2 = [2, 4, 3, 11]  → v1[i] < v2[i]? [T, F, F, T]
        SetVReg(s, 1, [1u, 5u, 3u, 10u,]);
        SetVReg(s, 2, [2u, 4u, 3u, 11u,]);

        uint raw = VopVv(26, 0, 1, 2); // vmsltu.vv v0, v1, v2
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        // bits [0]=1, [1]=0, [2]=0, [3]=1 → byte0 = 0b1001 = 9
        Assert.Equal(0b1001, s.VectorRegisters.Read(0)[0]);
    }

    [Fact]
    public void Execute_VandVV_BitwiseAnd() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0xFFu, 0xF0u, 0x0Fu, 0xAAu,]);
        SetVReg(s, 2, [0x55u, 0xF0u, 0xF0u, 0x55u,]);

        uint raw = VopVv(9, 3, 1, 2); // vand.vv v3, v1, v2  (funct6=9)
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(3);
        Assert.Equal(0x55u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(0xF0u, BitConverter.ToUInt32(result, 4));
        Assert.Equal(0x00u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(0x00u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_VsllVV_ShiftLeft() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [1u, 2u, 4u, 8u,]);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]); // shift amounts

        uint raw = VopVv(37, 3, 1, 2); // vsll.vv v3, v1, v2  (funct6=37)
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(3);
        Assert.Equal(2u, BitConverter.ToUInt32(result, 0));    // 1 << 1
        Assert.Equal(8u, BitConverter.ToUInt32(result, 4));    // 2 << 2
        Assert.Equal(32u, BitConverter.ToUInt32(result, 8));   // 4 << 3
        Assert.Equal(128u, BitConverter.ToUInt32(result, 12)); // 8 << 4
    }

    [Fact]
    public void Execute_VaddVV_MaskedOperation() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // mask v0: elements 0 and 2 active (bits [0] and [2] = byte0 = 0b0101 = 5)
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101;
        s.VectorRegisters.Write(0, maskReg);

        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 3, [10u, 20u, 30u, 40u,]);

        uint raw = VopVv(0, 1, 2, 3, true); // vadd.vv v1, v2, v3, v0.t
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        // element 0: active → 1+10=11
        Assert.Equal(11u, BitConverter.ToUInt32(result, 0));
        // element 1: masked out → stays 0 (result buf starts zeroed)
        Assert.Equal(0u, BitConverter.ToUInt32(result, 4));
        // element 2: active → 3+30=33
        Assert.Equal(33u, BitConverter.ToUInt32(result, 8));
        // element 3: masked out → 0
        Assert.Equal(0u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_Vlenb_ReadsCorrectly() {
        // vlenb CSR (0xC22) should always read as 16
        Rv32ArchState s = MakeState();
        Assert.Equal(16UL, ReadCsr(s, CsrFile.Vlenb));
    }

    [Fact]
    public void Execute_VsrlVV_ShiftRightLogical() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0x80000000u, 16u, 0u, 0xFFFFFFFFu,]);
        SetVReg(s, 2, [1u, 1u, 1u, 4u,]);

        uint raw = VopVv(40, 3, 1, 2); // vsrl.vv v3, v1, v2  (funct6=40)
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(3);
        Assert.Equal(0x40000000u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(8u, BitConverter.ToUInt32(result, 4));
        Assert.Equal(0u, BitConverter.ToUInt32(result, 8));
        Assert.Equal(0x0FFFFFFFu, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Execute_VsraVV_ShiftRightArithmetic() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0x80000000u, 0x7FFFFFFFu, 0u, 0u,]);
        SetVReg(s, 2, [1u, 1u, 0u, 0u,]);

        uint raw = VopVv(41, 3, 1, 2); // vsra.vv v3, v1, v2  (funct6=41)
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(3);
        Assert.Equal(0xC0000000u, BitConverter.ToUInt32(result, 0)); // sign extended
        Assert.Equal(0x3FFFFFFFu, BitConverter.ToUInt32(result, 4));
    }

    // ── Strided load/store tests ──────────────────────────────────────────────

    [Fact]
    public void Decode_Vlse32_Payload() {
        // vlse32.v v1, (a0), a1  (funct3=6 for 32-bit, mop=2, rs2=a1=11)
        uint raw = Vlse(1, 10, 11, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVlseVv>(i.Payload);
        Assert.Equal(1, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(11, op.Rs2);
        Assert.Equal(32, op.Sew);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal(1, i.VectorDestinationRegister);
        Assert.Equal([10, 11,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_Vsse32_Payload() {
        uint raw = Vsse(2, 10, 11, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVsseVv>(i.Payload);
        Assert.Equal(2, op.Vs3);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(11, op.Rs2);
        Assert.Equal(32, op.Sew);
        Assert.Equal(-1, i.VectorDestinationRegister);
        Assert.Equal([2,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_Vlse32_StridedLoad() {
        // Memory layout: [10, 99, 20, 99, 30, 99, 40, 99] × 4 bytes at 0x100
        // stride=8: loads words at 0x100, 0x108, 0x110, 0x118 → [10,20,30,40]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        _mem.Write(0x100, 10, 4);
        _mem.Write(0x108, 20, 4);
        _mem.Write(0x110, 30, 4);
        _mem.Write(0x118, 40, 4);

        s.IntegerRegisters.Write(10, 0x100); // rs1=a0=base
        s.IntegerRegisters.Write(11, 8);     // rs2=a1=stride

        uint raw = Vlse(1, 10, 11, 6); // vlse32.v v1, (a0), a1
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_Vsse32_StridedStore() {
        // Write [1,2,3,4] to every other 32-bit slot starting at 0x200
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [1u, 2u, 3u, 4u,]);

        s.IntegerRegisters.Write(10, 0x200); // base
        s.IntegerRegisters.Write(11, 8);     // stride=8 bytes

        uint raw = Vsse(1, 10, 11, 6); // vsse32.v v1, (a0), a1
        Exec(raw, s);                  // stores directly (no SideEffect)

        Assert.Equal(1UL, _mem.Read(0x200, 4));
        Assert.Equal(2UL, _mem.Read(0x208, 4));
        Assert.Equal(3UL, _mem.Read(0x210, 4));
        Assert.Equal(4UL, _mem.Read(0x218, 4));
    }

    [Fact]
    public void Execute_Vlse8_ByteStride() {
        // Load 4 bytes with stride=2 (every other byte)
        Rv32ArchState s = MakeState();
        // vsetivli a0, 4, e8,m1,ta,ma — direct CSR update, no SideEffect needed
        const int vtypeiE8M1 = (1 << 7) | (1 << 6) | (0 << 3); // e8,m1,ta,ma
        Exec(Vsetivli(10, 4, vtypeiE8M1), s);

        _mem.Write(0x300, 0xAA, 1);
        _mem.Write(0x302, 0xBB, 1);
        _mem.Write(0x304, 0xCC, 1);
        _mem.Write(0x306, 0xDD, 1);

        s.IntegerRegisters.Write(10, 0x300); // base
        s.IntegerRegisters.Write(11, 2);     // stride=2

        uint raw = Vlse(1, 10, 11, 0); // vlse8.v v1, (a0), a1  (funct3=0 for e8)
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(0xAAu, r[0]);
        Assert.Equal(0xBBu, r[1]);
        Assert.Equal(0xCCu, r[2]);
        Assert.Equal(0xDDu, r[3]);
    }

    // ── Integer multiply/divide tests ────────────────────────────────────────

    [Fact]
    public void Decode_VmulVv_Payload() {
        // vmul.vv v3, v1, v2 — funct6=0x25, funct3=2 (OPMVV)
        uint raw = VopMvv(0x25, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVMulVv>(i.Payload);
        Assert.Equal(VMulOp.Mul, op.Op);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(2, op.Vs1);
        Assert.False(op.Masked);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal(-1, i.DestinationRegister);
        Assert.Equal(3, i.VectorDestinationRegister);
        Assert.Equal([1, 2,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Decode_VmulVx_Payload() {
        // vmul.vx v3, v1, a0(x10) — funct6=0x25, funct3=6 (OPMVX)
        uint raw = VopMvx(0x25, 3, 1, 10);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVMulVx>(i.Payload);
        Assert.Equal(VMulOp.Mul, op.Op);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(10, op.Rs1);
        Assert.Equal([10,], i.SourceRegisters);
        Assert.Equal([1,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_VmulVV_LowHalf() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [3u, 0x80000000u, 7u, 0xFFFFFFFFu,]); // vs2
        SetVReg(s, 2, [4u, 2u, 6u, 0xFFFFFFFFu,]);          // vs1

        uint raw = VopMvv(0x25, 3, 1, 2); // vmul.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(12u, BitConverter.ToUInt32(r, 0)); // 3*4
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));  // 0x80000000*2 low 32 = 0
        Assert.Equal(42u, BitConverter.ToUInt32(r, 8)); // 7*6
        Assert.Equal(1u, BitConverter.ToUInt32(r, 12)); // (-1)*(-1) = +1
    }

    [Fact]
    public void Execute_VmulVX_ScalarBroadcast() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [2u, 3u, 4u, 5u,]); // vs2
        s.IntegerRegisters.Write(10, 10);

        uint raw = VopMvx(0x25, 2, 1, 10); // vmul.vx v2, v1, a0
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(2);
        Assert.Equal(20u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(50u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VmulhVV_SignedHighHalf() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // (-1) * (-1) high 32 = 0  (64-bit product = +1, high 32 = 0)
        // INT_MIN * 2 high 32 = -1  (product = INT64_MIN, high 32 = 0xFFFFFFFF)
        // 2 * 3 high 32 = 0
        // -2 * 3 high 32 = -1  (product = -6, fits in 32 bits, high = -1 if negative)
        SetVReg(s, 1, [0xFFFFFFFFu, 0x80000000u, 2u, 0xFFFFFFFEu,]); // vs2: -1, INT_MIN, 2, -2
        SetVReg(s, 2, [0xFFFFFFFFu, 2u, 3u, 3u,]);                   // vs1: -1, 2, 3, 3

        uint raw = VopMvv(0x27, 3, 1, 2); // vmulh.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0u, BitConverter.ToUInt32(r, 0));           // (-1)*(-1)=+1 → high=0
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 4));  // INT_MIN*2 → high=-1
        Assert.Equal(0u, BitConverter.ToUInt32(r, 8));           // 2*3=6 → high=0
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 12)); // (-2)*3=-6 → high=-1
    }

    [Fact]
    public void Execute_VmulhuVV_UnsignedHighHalf() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // UINT_MAX * UINT_MAX = (2^32-1)^2 high 32 = 2^32-2 = 0xFFFFFFFE
        // 2 * 3 = 6, high 32 = 0
        SetVReg(s, 1, [0xFFFFFFFFu, 2u, 0u, 0u,]); // vs2
        SetVReg(s, 2, [0xFFFFFFFFu, 3u, 1u, 0u,]); // vs1

        uint raw = VopMvv(0x24, 3, 1, 2); // vmulhu.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0xFFFFFFFEu, BitConverter.ToUInt32(r, 0)); // (2^32-1)^2 high
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));          // 2*3 high
        Assert.Equal(0u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VmulhsuVV_SignedVsUnsigned() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vs2=-1 (signed), vs1=UINT_MAX (unsigned): (-1) * (2^32-1) = -(2^32-1)
        // high 32: (-(2^32-1)) >> 32 = -1
        // vs2=1 (signed), vs1=UINT_MAX (unsigned): 1 * (2^32-1) = 2^32-1 ≤ 64-bit
        // high 32 = 0
        SetVReg(s, 1, [0xFFFFFFFFu, 1u, 2u, 0u,]);          // vs2: -1, 1, 2, 0 (signed)
        SetVReg(s, 2, [0xFFFFFFFFu, 0xFFFFFFFFu, 3u, 0u,]); // vs1 (unsigned)

        uint raw = VopMvv(0x26, 3, 1, 2); // vmulhsu.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 0)); // (-1)*UINT_MAX high = -1
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));          // 1*UINT_MAX high = 0
        Assert.Equal(0u, BitConverter.ToUInt32(r, 8));          // 2*3=6 high = 0
    }

    [Fact]
    public void Execute_VdivVV_SignedDivision() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // 12 / 3 = 4;  -12 / 3 = -4;  div-by-zero = -1;  INT_MIN / -1 = INT_MIN
        SetVReg(s, 1, [12u, 0xFFFFFFF4u, 5u, 0x80000000u,]); // vs2: 12, -12, 5, INT_MIN
        SetVReg(s, 2, [3u, 3u, 0u, 0xFFFFFFFFu,]);           // vs1: 3, 3, 0, -1

        uint raw = VopMvv(0x21, 3, 1, 2); // vdiv.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(4u, BitConverter.ToUInt32(r, 0));           // 12/3=4
        Assert.Equal(0xFFFFFFFCu, BitConverter.ToUInt32(r, 4));  // -12/3=-4
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 8));  // 5/0=-1
        Assert.Equal(0x80000000u, BitConverter.ToUInt32(r, 12)); // INT_MIN/-1=INT_MIN
    }

    [Fact]
    public void Execute_VdivuVV_UnsignedDivision() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // 12 / 4 = 3;  UINT_MAX / 2;  div-by-zero = UINT_MAX
        SetVReg(s, 1, [12u, 0xFFFFFFFFu, 7u, 0u,]); // vs2
        SetVReg(s, 2, [4u, 2u, 0u, 1u,]);           // vs1

        uint raw = VopMvv(0x20, 3, 1, 2); // vdivu.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(3u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0x7FFFFFFFu, BitConverter.ToUInt32(r, 4)); // UINT_MAX/2
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 8)); // 7/0=UINT_MAX
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12));         // 0/1=0
    }

    [Fact]
    public void Execute_VremVV_SignedRemainder() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // 13 % 5 = 3;  -13 % 5 = -3;  rem-by-zero = dividend;  INT_MIN % -1 = 0
        SetVReg(s, 1, [13u, 0xFFFFFFF3u, 99u, 0x80000000u,]); // vs2: 13, -13, 99, INT_MIN
        SetVReg(s, 2, [5u, 5u, 0u, 0xFFFFFFFFu,]);            // vs1: 5, 5, 0, -1

        uint raw = VopMvv(0x23, 3, 1, 2); // vrem.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(3u, BitConverter.ToUInt32(r, 0));          // 13%5=3
        Assert.Equal(0xFFFFFFFDu, BitConverter.ToUInt32(r, 4)); // -13%5=-3
        Assert.Equal(99u, BitConverter.ToUInt32(r, 8));         // 99%0=99 (dividend)
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12));         // INT_MIN%-1=0
    }

    [Fact]
    public void Execute_VremuVV_UnsignedRemainder() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [13u, 0xFFFFFFFFu, 7u, 0u,]); // vs2
        SetVReg(s, 2, [5u, 3u, 0u, 1u,]);           // vs1

        uint raw = VopMvv(0x22, 3, 1, 2); // vremu.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(3u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));  // UINT_MAX%3=0
        Assert.Equal(7u, BitConverter.ToUInt32(r, 8));  // 7%0=7 (dividend)
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12)); // 0%1=0
    }

    // ── Reduction tests ───────────────────────────────────────────────────────

    [Fact]
    public void Decode_VredsumVs_Payload() {
        // vredsum.vs v3, v1, v2 — funct6=0, funct3=2 (OPMVV)
        uint raw = VopMvv(0, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVRedVs>(i.Payload);
        Assert.Equal(VRedOp.Sum, op.Op);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(2, op.Vs1);
        Assert.Equal(3, i.VectorDestinationRegister);
        Assert.Equal([1, 2,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_VredsumVs_SumsAllElements() {
        // vs2 = [1, 2, 3, 4], vs1[0] = 10 (seed) → sum = 10+1+2+3+4 = 20
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [1u, 2u, 3u, 4u,]);  // vs2
        SetVReg(s, 2, [10u, 0u, 0u, 0u,]); // vs1 (seed in element 0)

        uint raw = VopMvv(0, 3, 1, 2); // vredsum.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(20u, BitConverter.ToUInt32(r, 0)); // result in element 0
    }

    [Fact]
    public void Execute_VredandVs_AndAllElements() {
        // vs2 = [0xFF, 0x0F, 0xF0, 0xFF], vs1[0] = 0xFF → and = 0x00
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0xFFu, 0x0Fu, 0xF0u, 0xFFu,]);
        SetVReg(s, 2, [0xFFFFFFFFu, 0u, 0u, 0u,]);

        uint raw = VopMvv(1, 3, 1, 2); // vredand.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0u, BitConverter.ToUInt32(r, 0));
    }

    [Fact]
    public void Execute_VredorVs_OrAllElements() {
        // vs2 = [0x01, 0x02, 0x04, 0x08], vs1[0] = 0 → or = 0x0F
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0x01u, 0x02u, 0x04u, 0x08u,]);
        SetVReg(s, 2, [0u, 0u, 0u, 0u,]);

        uint raw = VopMvv(2, 3, 1, 2); // vredor.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0x0Fu, BitConverter.ToUInt32(r, 0));
    }

    [Fact]
    public void Execute_VredmaxuVs_UnsignedMax() {
        // vs2 = [5, 3, UINT_MAX, 2], vs1[0] = 0 → max = UINT_MAX
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [5u, 3u, 0xFFFFFFFFu, 2u,]);
        SetVReg(s, 2, [0u, 0u, 0u, 0u,]);

        uint raw = VopMvv(6, 3, 1, 2); // vredmaxu.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(r, 0));
    }

    [Fact]
    public void Execute_VredminVs_SignedMin() {
        // vs2 = [5, -3, INT_MIN, 2] (signed), vs1[0] = 100 → min = INT_MIN
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [5u, 0xFFFFFFFDu, 0x80000000u, 2u,]); // -3, INT_MIN
        SetVReg(s, 2, [100u, 0u, 0u, 0u,]);

        uint raw = VopMvv(5, 3, 1, 2); // vredmin.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0x80000000u, BitConverter.ToUInt32(r, 0)); // INT_MIN
    }

    [Fact]
    public void Execute_VredsumVs_MaskedSkipsElements() {
        // vs2=[1,2,3,4], mask: elements 0 and 2 active, vs1[0]=0 → 0+1+3=4
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101; // elements 0, 2
        s.VectorRegisters.Write(0, maskReg);
        SetVReg(s, 1, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 2, [0u, 0u, 0u, 0u,]); // seed = 0

        uint raw = VopMvv(0, 3, 1, 2, true); // vredsum.vs v3, v1, v2, v0.t
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(4u, BitConverter.ToUInt32(r, 0)); // 0+1+3=4
    }

    // ── Widening integer reduction tests ──────────────────────────────────────

    [Fact]
    public void Decode_VwredsumUVs_Payload() {
        // vwredsumu.vs v3, v1, v2 — funct6=0x30, funct3=0 (OPIVV)
        uint raw = VopVv(0x30, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVWideRedVs>(i.Payload);
        Assert.False(op.Signed);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(2, op.Vs1);
        Assert.Equal(3, i.VectorDestinationRegister);
        Assert.Equal([1, 2,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Decode_VwredsumVs_Payload() {
        // vwredsum.vs v3, v1, v2 — funct6=0x31, funct3=0 (OPIVV)
        uint raw = VopVv(0x31, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVWideRedVs>(i.Payload);
        Assert.True(op.Signed);
    }

    [Fact]
    public void Execute_VwredsumUVs_ZeroExtendsSew8() {
        // SEW=8, vl=4: vs2=[100, 200, 50, 5], seed=10 → 10+100+200+50+5=365 (u16)
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 1, [100, 200, 50, 5,]); // vs2 (u8 elements)
        // seed in vs1[0] as u16: write 10 as little-endian into a u32 SetVReg slot
        SetVReg(s, 2, [10u, 0u, 0u, 0u,]); // vs1[0] as u16 = 10 (bytes 0-1 = 0x000A)

        uint raw = VopVv(0x30, 3, 1, 2); // vwredsumu.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)365, BitConverter.ToUInt16(r, 0));
    }

    [Fact]
    public void Execute_VwredsumVs_SignExtendsSew8() {
        // SEW=8, vl=3: vs2=[0xFF(-1), 0x80(-128), 100], seed=0
        // vwredsum (signed): 0 + (-1) + (-128) + 100 = -29 → as u16 = 0xFFE3
        // vwredsumu (unsigned) would give: 0 + 255 + 128 + 100 = 483, but this tests signed
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 3, VectorTests.VtypeiE8M1Tama), s); // vl=3, SEW=8
        SetVReg8(s, 1, [0xFF, 0x80, 100,]);                   // -1, -128, 100
        SetVReg(s, 2, [0u, 0u, 0u, 0u,]);                     // seed = 0

        uint raw = VopVv(0x31, 3, 1, 2); // vwredsum.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(unchecked((ushort)-29), BitConverter.ToUInt16(r, 0)); // 0xFFE3
    }

    [Fact]
    public void Execute_VwredsumUVs_Sew16_32BitResult() {
        // SEW=16, vl=4: vs2=[1000, 2000, 3000, 4000], seed=500 → 10500 as u32
        Rv32ArchState s = MakeState();
        ConfigVl4E16(s);
        SetVReg16(s, 1, [1000, 2000, 3000, 4000,]); // vs2 (u16 elements)
        SetVReg(s, 2, [500u, 0u, 0u, 0u,]);         // vs1[0] as u32 seed = 500

        uint raw = VopVv(0x30, 3, 1, 2); // vwredsumu.vs v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(10500u, BitConverter.ToUInt32(r, 0));
    }

    [Fact]
    public void Execute_VwredsumUVs_MaskedSkipsElements() {
        // SEW=8, vl=4, mask: elements 1 and 3 active; vs2=[10,20,30,40], seed=0 → 0+20+40=60
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b1010; // elements 1 and 3
        s.VectorRegisters.Write(0, maskReg);
        SetVReg8(s, 1, [10, 20, 30, 40,]);
        SetVReg(s, 2, [0u, 0u, 0u, 0u,]); // seed = 0

        uint raw = VopVv(0x30, 3, 1, 2, true); // vwredsumu.vs v3, v1, v2, v0.t
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)60, BitConverter.ToUInt16(r, 0));
    }

    // ── Indexed load/store tests ──────────────────────────────────────────────

    [Fact]
    public void Decode_Vluxei32_Payload() {
        // vluxei32.v v1, (a0), v3  (mop=1, funct3=6, unmasked)
        uint raw = Vluxei(1, 10, 3, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVlxeiVv>(i.Payload);
        Assert.Equal(1, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(3, op.Vs2);
        Assert.Equal(32, op.IndexSew);
        Assert.False(op.Masked);
        Assert.False(op.Ordered);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal(1, i.VectorDestinationRegister);
        Assert.Equal([10,], i.SourceRegisters);
        Assert.Equal([3,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Decode_Vloxei8_Payload() {
        // vloxei8.v v1, (a0), v3  (mop=3 = ordered, funct3=0 for 8-bit index)
        uint raw = Vloxei(1, 10, 3, 0);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVlxeiVv>(i.Payload);
        Assert.Equal(8, op.IndexSew);
        Assert.True(op.Ordered);
        Assert.Equal(1, i.VectorDestinationRegister);
    }

    [Fact]
    public void Decode_Vsuxei32_Payload() {
        // vsuxei32.v v2, (a0), v3  (store: vs3=2, vs2=3, rs1=10)
        uint raw = Vsuxei(2, 10, 3, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVsxeiVv>(i.Payload);
        Assert.Equal(2, op.Vs3);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(3, op.Vs2);
        Assert.Equal(32, op.IndexSew);
        Assert.False(op.Ordered);
        Assert.Equal(-1, i.VectorDestinationRegister);
        Assert.Equal([10,], i.SourceRegisters);
        Assert.Equal([2, 3,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_Vluxei32_IndexedLoad() {
        // Index vector v2 = [0, 8, 4, 12] (byte offsets), base=0x400
        // Memory: 0x400=10, 0x404=30, 0x408=20, 0x40C=40
        // Expected v1 = [10, 20, 30, 40]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        _mem.Write(0x400, 10, 4);
        _mem.Write(0x404, 30, 4);
        _mem.Write(0x408, 20, 4);
        _mem.Write(0x40C, 40, 4);
        SetVReg(s, 2, [0u, 8u, 4u, 12u,]); // index offsets
        s.IntegerRegisters.Write(10, 0x400);

        uint raw = Vluxei(1, 10, 2, 6); // vluxei32.v v1, (a0), v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_Vsuxei32_IndexedStore() {
        // Store v1=[10,20,30,40] to scattered addresses via index vector v2=[0,8,4,12]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // data
        SetVReg(s, 2, [0u, 8u, 4u, 12u,]);    // byte offsets
        s.IntegerRegisters.Write(10, 0x400);

        uint raw = Vsuxei(1, 10, 2, 6); // vsuxei32.v v1, (a0), v2
        Exec(raw, s);                   // stores bypass SideEffect

        Assert.Equal(10UL, _mem.Read(0x400, 4));
        Assert.Equal(30UL, _mem.Read(0x404, 4)); // offset 4 → element 2 value
        Assert.Equal(20UL, _mem.Read(0x408, 4)); // offset 8 → element 1 value
        Assert.Equal(40UL, _mem.Read(0x40C, 4));
    }

    [Fact]
    public void Execute_Vluxei32_MaskedIndexedLoad() {
        // Only elements 0 and 2 are active (mask byte0 = 0b0101)
        // indices=[0,4,8,12], base=0x500; only loads at 0x500 (elem0) and 0x508 (elem2)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101;
        s.VectorRegisters.Write(0, maskReg);
        _mem.Write(0x500, 11, 4);
        _mem.Write(0x508, 33, 4);
        SetVReg(s, 2, [0u, 4u, 8u, 12u,]); // index offsets
        s.IntegerRegisters.Write(10, 0x500);

        uint raw = Vluxei(1, 10, 2, 6, true); // vluxei32.v v1, (a0), v2, v0.t
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(11u, BitConverter.ToUInt32(r, 0)); // active
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));  // inactive
        Assert.Equal(33u, BitConverter.ToUInt32(r, 8)); // active
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12)); // inactive
    }

    // ── Slide and gather tests ────────────────────────────────────────────────

    [Fact]
    public void Decode_VslideupVx_Payload() {
        // vslideup.vx v2, v4, a0  (funct6=0x0E, funct3=4, Is1=false)
        uint raw = VslideVx(0x0E, 2, 4, 10, 4);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVSlideVx>(i.Payload);
        Assert.Equal(VSlideDir.Up, op.Dir);
        Assert.False(op.Is1);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal([4,], i.VectorSourceRegisters);
        Assert.Equal([10,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_Vslide1upVx_Payload() {
        // vslide1up.vx v2, v4, a0  (funct6=0x0E, funct3=6=OPMVX, Is1=true)
        uint raw = VslideVx(0x0E, 2, 4, 10, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVSlideVx>(i.Payload);
        Assert.True(op.Is1);
        Assert.Equal(VSlideDir.Up, op.Dir);
    }

    [Fact]
    public void Decode_VslidedownVi_Payload() {
        // vslidedown.vi v2, v4, 3  (funct6=0x0F, funct3=3=OPIVI)
        uint raw = VslideVi(0x0F, 2, 4, 3);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVSlideVi>(i.Payload);
        Assert.Equal(VSlideDir.Down, op.Dir);
        Assert.Equal(3, op.Imm);
    }

    [Fact]
    public void Decode_VrgatherVv_Payload() {
        // vrgather.vv v2, v4, v6  (funct6=0x0C, funct3=0=OPIVV)
        uint raw = VopVv(0x0C, 2, 4, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVRgatherVv>(i.Payload);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(6, op.Vs1);
        Assert.Equal([4, 6,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_VslideupVx_ShiftsElementsUp() {
        // vs2=[10,20,30,40], offset=2 → vd=[old0,old1,10,20]; elements at i<2 are undisturbed
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2
        SetVReg(s, 3, [99u, 88u, 77u, 66u,]); // initial vd content (should be preserved for i<offset)
        s.IntegerRegisters.Write(10, 2);      // offset=2

        uint raw = VslideVx(0x0E, 3, 1, 10, 4); // vslideup.vx v3, v1, a0
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(99u, BitConverter.ToUInt32(r, 0));  // undisturbed (i=0 < offset=2)
        Assert.Equal(88u, BitConverter.ToUInt32(r, 4));  // undisturbed (i=1 < offset=2)
        Assert.Equal(10u, BitConverter.ToUInt32(r, 8));  // vs2[0]
        Assert.Equal(20u, BitConverter.ToUInt32(r, 12)); // vs2[1]
    }

    [Fact]
    public void Execute_VslidedownVi_ShiftsElementsDown() {
        // vslidedown.vi v3, v1, 2 → vd[i] = vs2[i+2]; out-of-bounds → 0
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]);

        uint raw = VslideVi(0x0F, 3, 1, 2); // vslidedown.vi v3, v1, 2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(30u, BitConverter.ToUInt32(r, 0)); // vs2[2]
        Assert.Equal(40u, BitConverter.ToUInt32(r, 4)); // vs2[3]
        Assert.Equal(0u, BitConverter.ToUInt32(r, 8));  // out of bounds → 0
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12)); // out of bounds → 0
    }

    [Fact]
    public void Execute_Vslide1upVx_InsertScalarAtElement0() {
        // vslide1up.vx v3, v1, a0: vd[0]=rs1, vd[i]=vs2[i-1] for i>0
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2
        s.IntegerRegisters.Write(10, 99);     // scalar to insert at position 0

        uint raw = VslideVx(0x0E, 3, 1, 10, 6); // vslide1up.vx v3, v1, a0  (funct3=6)
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(99u, BitConverter.ToUInt32(r, 0));  // rs1 inserted
        Assert.Equal(10u, BitConverter.ToUInt32(r, 4));  // vs2[0]
        Assert.Equal(20u, BitConverter.ToUInt32(r, 8));  // vs2[1]
        Assert.Equal(30u, BitConverter.ToUInt32(r, 12)); // vs2[2]
    }

    [Fact]
    public void Execute_Vslide1downVx_InsertScalarAtLastElement() {
        // vslide1down.vx v3, v1, a0: vd[i]=vs2[i+1] for i<vl-1, vd[vl-1]=rs1
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2
        s.IntegerRegisters.Write(10, 99);

        uint raw = VslideVx(0x0F, 3, 1, 10, 6); // vslide1down.vx v3, v1, a0  (funct3=6)
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(20u, BitConverter.ToUInt32(r, 0));  // vs2[1]
        Assert.Equal(30u, BitConverter.ToUInt32(r, 4));  // vs2[2]
        Assert.Equal(40u, BitConverter.ToUInt32(r, 8));  // vs2[3]
        Assert.Equal(99u, BitConverter.ToUInt32(r, 12)); // rs1 inserted at last
    }

    [Fact]
    public void Execute_VrgatherVv_GathersByIndex() {
        // vrgather.vv v3, v1, v2: vd[i] = vs2[vs1[i]]
        // vs2=[10,20,30,40], vs1 indices=[3,0,2,1] → vd=[40,10,30,20]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2 (data)
        SetVReg(s, 2, [3u, 0u, 2u, 1u,]);     // vs1 (indices)

        uint raw = VopVv(0x0C, 3, 1, 2); // vrgather.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(40u, BitConverter.ToUInt32(r, 0));  // vs2[3]
        Assert.Equal(10u, BitConverter.ToUInt32(r, 4));  // vs2[0]
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));  // vs2[2]
        Assert.Equal(20u, BitConverter.ToUInt32(r, 12)); // vs2[1]
    }

    [Fact]
    public void Execute_VrgatherVi_BroadcastElement() {
        // vrgather.vi v3, v1, 2: vd[i] = vs2[2] for all i (broadcast element 2)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2

        uint raw = VopVi(0x0C, 3, 1, 2); // vrgather.vi v3, v1, 2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(30u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Execute_VrgatherVv_OutOfBoundsYieldsZero() {
        // vrgather with index >= vl → element should be 0
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2 (vl=4, so valid indices 0-3)
        SetVReg(s, 2, [1u, 5u, 2u, 99u,]);    // vs1: indices 5 and 99 are out-of-bounds

        uint raw = VopVv(0x0C, 3, 1, 2);
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(20u, BitConverter.ToUInt32(r, 0)); // vs2[1]
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));  // index 5 >= vl=4 → 0
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8)); // vs2[2]
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12)); // index 99 >= vl=4 → 0
    }

    [Fact]
    public void Decode_VrgathereI16Vv_Payload() {
        // vrgatherei16.vv v3, v1, v2 — funct6=0x0E, funct3=0 (OPIVV)
        uint raw = VopVv(0x0E, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVRgatherEi16Vv>(i.Payload);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(2, op.Vs1);
        Assert.Equal(3, i.VectorDestinationRegister);
        Assert.Equal([1, 2,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_VrgathereI16Vv_UsesU16Indices() {
        // SEW=32, vl=4: vs1 (index) = [3,0,2,1] as u16, vs2=[10,20,30,40]
        // Result: [vs2[3], vs2[0], vs2[2], vs2[1]] = [40, 10, 30, 20]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // vs2
        SetVReg16(s, 2, [3, 0, 2, 1,]);       // vs1: u16 indices
        uint raw = VopVv(0x0E, 3, 1, 2);
        Exec(raw, s).SideEffect!(s);
        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(40u, BitConverter.ToUInt32(r, 0));  // vs2[3]
        Assert.Equal(10u, BitConverter.ToUInt32(r, 4));  // vs2[0]
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));  // vs2[2]
        Assert.Equal(20u, BitConverter.ToUInt32(r, 12)); // vs2[1]
    }

    [Fact]
    public void Execute_VrgathereI16Vv_OutOfBoundsYieldsZero() {
        // Index >= vl → element should be 0
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]);
        SetVReg16(s, 2, [0, 99, 2, 1000,]); // indices 99 and 1000 are out-of-bounds (vl=4)
        uint raw = VopVv(0x0E, 3, 1, 2);
        Exec(raw, s).SideEffect!(s);
        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0)); // vs2[0]
        Assert.Equal(0u, BitConverter.ToUInt32(r, 4));  // index 99 >= vl → 0
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8)); // vs2[2]
        Assert.Equal(0u, BitConverter.ToUInt32(r, 12)); // index 1000 >= vl → 0
    }

    // ── Widening/narrowing integer op tests ──────────────────────────────────

    [Fact]
    public void Decode_VwadduVv_Payload() {
        // vwaddu.vv v2, v4, v6  (funct6=0x30, funct3=2=OPMVV)
        uint raw = VopMvv(0x30, 2, 4, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVWideVv>(i.Payload);
        Assert.Equal(VWideOp.AddU, op.Op);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(6, op.Vs1);
        Assert.False(op.Vs2IsWide);
        Assert.Equal(2, i.VectorDestinationRegister);
        Assert.Equal([4, 6,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Decode_VwaddWvVv_Payload() {
        // vwadd.wv v2, v4, v6  (funct6=0x35, Vs2IsWide=true)
        uint raw = VopMvv(0x35, 2, 4, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVWideVv>(i.Payload);
        Assert.Equal(VWideOp.Add, op.Op);
        Assert.True(op.Vs2IsWide);
    }

    [Fact]
    public void Decode_VwmulVx_Payload() {
        // vwmul.vx v2, v4, a0  (funct6=0x3B, funct3=6=OPMVX)
        uint raw = VopMvx(0x3B, 2, 4, 10);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVWideVx>(i.Payload);
        Assert.Equal(VWideOp.Mul, op.Op);
        Assert.Equal(10, op.Rs1);
        Assert.False(op.Vs2IsWide);
        Assert.Equal([10,], i.SourceRegisters);
    }

    [Fact]
    public void Decode_VnsrlWv_Payload() {
        // vnsrl.wv v2, v4, v6  (funct6=0x2C, funct3=0=OPIVV)
        uint raw = VopVv(0x2C, 2, 4, 6);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVNarrVv>(i.Payload);
        Assert.Equal(VNarrOp.Srl, op.Op);
        Assert.Equal(2, op.Vd);
        Assert.Equal([4, 6,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Decode_VnsraWi_Payload() {
        // vnsra.wi v2, v4, 3  (funct6=0x2D, funct3=3=OPIVI)
        uint raw = VopVi(0x2D, 2, 4, 3);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVNarrVi>(i.Payload);
        Assert.Equal(VNarrOp.Sra, op.Op);
        Assert.Equal(3, op.Imm);
    }

    [Fact]
    public void Execute_VwadduVv_E8_ZeroExtend() {
        // SEW=8 → output SEW=16; zero-extends both inputs
        // vs2=[0xFF,0x80,0x01,0x00], vs1=[0x01,0x80,0xFF,0x00]
        // vd=[0x0100, 0x0100, 0x0100, 0x0000] as u16
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 1, [0xFF, 0x80, 0x01, 0x00,]);
        SetVReg8(s, 2, [0x01, 0x80, 0xFF, 0x00,]);

        uint raw = VopMvv(0x30, 3, 1, 2); // vwaddu.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)0x0100, R16(r, 0));
        Assert.Equal((ushort)0x0100, R16(r, 1));
        Assert.Equal((ushort)0x0100, R16(r, 2));
        Assert.Equal((ushort)0x0000, R16(r, 3));
    }

    [Fact]
    public void Execute_VwaddVv_E8_SignExtend() {
        // SEW=8 → output SEW=16; sign-extends both inputs
        // vs2=[-1,-128,1,127]→[0xFF,0x80,0x01,0x7F], vs1=[-1,-128,127,1]→[0xFF,0x80,0x7F,0x01]
        // vd=[-2,-256,128,128] as i16 = [0xFFFE, 0xFF00, 0x0080, 0x0080]
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 1, [0xFF, 0x80, 0x01, 0x7F,]);
        SetVReg8(s, 2, [0xFF, 0x80, 0x7F, 0x01,]);

        uint raw = VopMvv(0x31, 3, 1, 2); // vwadd.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)0xFFFE, R16(r, 0)); // -1 + -1 = -2
        Assert.Equal((ushort)0xFF00, R16(r, 1)); // -128 + -128 = -256
        Assert.Equal((ushort)0x0080, R16(r, 2)); // 1 + 127 = 128
        Assert.Equal((ushort)0x0080, R16(r, 3)); // 127 + 1 = 128
    }

    [Fact]
    public void Execute_VwmulVv_E8_SignedProduct() {
        // SEW=8 signed × signed → 16-bit
        // vs2=[10,-10,100,-100], vs1=[3,-3,-3,3]
        // vd=[30,30,-300,-300] as i16
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 1, [10, unchecked((byte)-10), 100, unchecked((byte)-100),]);
        SetVReg8(s, 2, [3, unchecked((byte)-3), unchecked((byte)-3), 3,]);

        uint raw = VopMvv(0x3B, 3, 1, 2); // vwmul.vv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)30, R16(r, 0));
        Assert.Equal((ushort)30, R16(r, 1));
        Assert.Equal(unchecked((ushort)-300), R16(r, 2));
        Assert.Equal(unchecked((ushort)-300), R16(r, 3));
    }

    [Fact]
    public void Execute_VwadduWv_E8_Vs2IsWide() {
        // vwaddu.wv: vs2 is already 2*SEW=16-bit; vs1 is SEW=8-bit (zero-extended)
        // vs2=[300,400,500,600] as u16, vs1=[1,2,3,4] as u8
        // vd=[301,402,503,604] as u16
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg16(s, 1, [300, 400, 500, 600,]); // vs2 (2*SEW = 16-bit elements)
        SetVReg8(s, 2, [1, 2, 3, 4,]);          // vs1 (SEW = 8-bit elements)

        uint raw = VopMvv(0x34, 3, 1, 2); // vwaddu.wv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal((ushort)301, R16(r, 0));
        Assert.Equal((ushort)402, R16(r, 1));
        Assert.Equal((ushort)503, R16(r, 2));
        Assert.Equal((ushort)604, R16(r, 3));
    }

    [Fact]
    public void Execute_VnsrlWv_E8_LogicalShift() {
        // vnsrl.wv: SEW=8 output, vs2 is 16-bit (2*SEW)
        // vs2=[0x0280, 0xFFFF, 0x0001, 0x00FF] as u16
        // vs1=[1,4,0,3] as u8 (shift amounts)
        // vd=[0x40, 0xFF, 0x01, 0x1F] as u8
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg16(s, 1, [0x0280, 0xFFFF, 0x0001, 0x00FF,]); // vs2 (source, 2*SEW)
        SetVReg8(s, 2, [1, 4, 0, 3,]);                      // vs1 (shifts)

        uint raw = VopVv(0x2C, 3, 1, 2); // vnsrl.wv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0x40, r[0]); // 0x0280 >> 1 = 0x0140 → low byte 0x40
        Assert.Equal(0xFF, r[1]); // 0xFFFF >> 4 = 0x0FFF → low byte 0xFF
        Assert.Equal(0x01, r[2]); // 0x0001 >> 0 = 0x0001 → low byte 0x01
        Assert.Equal(0x1F, r[3]); // 0x00FF >> 3 = 0x001F → low byte 0x1F
    }

    [Fact]
    public void Execute_VnsraWv_E8_ArithmeticShift() {
        // vnsra.wv: SEW=8 output, vs2 is 16-bit (2*SEW), arithmetic (sign-extended) shift
        // vs2=[0xFF00(=-256), 0x0080(=128), 0x8000(=-32768), 0x7FFF(=32767)] as i16
        // vs1=[4,1,8,1] as u8 (shift amounts)
        // vd=[0xF0(-16), 0x40(64), 0x80(-128), 0xFF(127)] as i8 (truncated to byte)
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg16(s, 1, [0xFF00, 0x0080, 0x8000, 0x7FFF,]);
        SetVReg8(s, 2, [4, 1, 8, 1,]);

        uint raw = VopVv(0x2D, 3, 1, 2); // vnsra.wv v3, v1, v2
        Exec(raw, s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(3);
        Assert.Equal(0xF0, r[0]); // -256 >> 4 = -16 → 0xF0
        Assert.Equal(0x40, r[1]); // 128 >> 1 = 64 → 0x40
        Assert.Equal(0x80, r[2]); // -32768 >> 8 = -128 → 0x80
        Assert.Equal(0xFF, r[3]); // 32767 >> 1 = 16383 → low byte = 0xFF
    }

    // ── Pipeline-level vector tests ───────────────────────────────────────────

    private static void LoadProg(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static byte[] VRegs(OooeTrain t, int vr) =>
        ((Rv32ArchState)t.ArchState).VectorRegisters.Read(vr);

    private static byte[] VRegs(FiveStageTrain t, int vr) =>
        ((Rv32ArchState)t.ArchState).VectorRegisters.Read(vr);

    private static uint Elem32(byte[] v, int i) => BitConverter.ToUInt32(v, i * 4);

    [Fact]
    public void OoOE_VectorStoreLoad_MultiElement() {
        // Configure vl=4/e32, load [10,20,30,40] from 0x100, store to 0x200, ebreak.
        // Validates that all four 32-bit elements survive the OoOE pipeline (not just
        // the last one — bug: CapturingMemory captured only one element write).
        var mem = new FlatMemory(0x400);
        var train = new OooeTrain(new Rv32Mechanism(), mem);
        mem.Write(0x100, 10, 4);
        mem.Write(0x104, 20, 4);
        mem.Write(0x108, 30, 4);
        mem.Write(0x10C, 40, 4);

        LoadProg(
            mem,
            0x10000513,                                  // addi a0, x0, 0x100
            0x20000593,                                  // addi a1, x0, 0x200
            Vsetvli(12, 0, VectorTests.VtypeiE32M1Tama), // vsetvli a2, x0, e32m1ta
            Vle(1, 10, 6),                               // vle32.v v1, (a0)
            Vse(1, 11, 6),                               // vse32.v v1, (a1)
            0x00100073                                   // ebreak
        );
        train.Run();

        Assert.Equal(10UL, mem.Read(0x200, 4));
        Assert.Equal(20UL, mem.Read(0x204, 4));
        Assert.Equal(30UL, mem.Read(0x208, 4));
        Assert.Equal(40UL, mem.Read(0x20C, 4));
    }

    [Fact]
    public void OoOE_ChainedVectorAdd_Correct() {
        // vadd.vi v1, v1, 3 then vadd.vi v1, v1, 5 → each element should be 8.
        // Head-serialization in OoOE ensures the second vadd sees v1 already updated.
        var mem = new FlatMemory(0x200);
        var train = new OooeTrain(new Rv32Mechanism(), mem);

        LoadProg(
            mem,
            Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama), // vsetvli a0, x0, e32m1ta
            VopVi(0, 1, 1, 3),                           // vadd.vi v1, v1, 3
            VopVi(0, 1, 1, 5),                           // vadd.vi v1, v1, 5  (reads v1)
            0x00100073                                   // ebreak
        );
        train.Run();

        byte[] v1 = VRegs(train, 1);
        Assert.Equal(8u, Elem32(v1, 0));
        Assert.Equal(8u, Elem32(v1, 1));
        Assert.Equal(8u, Elem32(v1, 2));
        Assert.Equal(8u, Elem32(v1, 3));
    }

    [Fact]
    public void FiveStage_VectorRawHazard_Stalls() {
        // vadd.vi v1, v1, 3 followed immediately by vadd.vi v2, v1, 5.
        // The hazard unit stalls the second vadd until v1 is written in WB.
        // Without the stall, the second vadd reads the unwritten v1 (all-zeros).
        var mem = new FlatMemory(0x200);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem);

        LoadProg(
            mem,
            Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama), // vsetvli a0, x0, e32m1ta
            VopVi(0, 1, 1, 3),                           // vadd.vi v1, v1, 3   (writes v1)
            VopVi(0, 2, 1, 5),                           // vadd.vi v2, v1, 5   (reads v1 → RAW)
            0x00100073                                   // ebreak
        );
        train.Run();

        // v1 starts all-zeros → +3 = 3; v2 = v1 + 5 = 8.
        byte[] v2 = VRegs(train, 2);
        Assert.Equal(8u, Elem32(v2, 0));
        Assert.Equal(8u, Elem32(v2, 1));
        Assert.Equal(8u, Elem32(v2, 2));
        Assert.Equal(8u, Elem32(v2, 3));
    }

    // ── FP vector helpers ─────────────────────────────────────────────────────

    // Write float bits to a unified-register-file float register (index = 32 + fpr).
    private static void SetFReg(Rv32ArchState s, int fpr, float value) =>
        s.IntegerRegisters.Write(32 + fpr, 0xFFFFFFFF00000000UL | (uint)BitConverter.SingleToInt32Bits(value));

    // Read element i of a vector register as a float32.
    private static float FElemF(byte[] v, int i) =>
        BitConverter.Int32BitsToSingle((int)BitConverter.ToUInt32(v, i * 4));

    // ── FP vector decode tests ────────────────────────────────────────────────

    [Fact]
    public void Decode_VfaddVv_IsRvVFpBinVv() {
        uint raw = VopFvv(0x00, 1, 2, 3); // vfadd.vv v1, v2, v3
        var op = Assert.IsType<RvVFpBinVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpBinOp.Add, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VfaddVf_IsRvVFpBinVf() {
        // fa0 = float reg 10; stored as rs1=10+32=42 in the unified register file
        uint raw = VopFvf(0x00, 1, 2, 10); // vfadd.vf v1, v2, fa0
        var op = Assert.IsType<RvVFpBinVf>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpBinOp.Add, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(42, op.Rs1);
    }

    [Fact]
    public void Decode_VfsqrtV_IsRvVFpSqrt() {
        uint raw = VopFvUnary(0x13, 1, 2, 0); // vfsqrt.v v1, v2
        var op = Assert.IsType<RvVFpSqrt>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VfmaccVv_IsRvVFpFmaVv() {
        uint raw = VopFvv(0x2C, 1, 2, 3); // vfmacc.vv v1, v2, v3
        var op = Assert.IsType<RvVFpFmaVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpFmaOp.Macc, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
    }

    [Fact]
    public void Decode_VmfeqVv_IsRvVMFpCmpVv() {
        uint raw = VopFvv(0x18, 1, 2, 3); // vmfeq.vv v1, v2, v3
        var op = Assert.IsType<RvVmFpCmpVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpCmpOp.Eq, op.Op);
    }

    // ── FP vector execute tests ───────────────────────────────────────────────

    [Fact]
    public void Execute_VfaddVv_AddsElementWise() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f),]);
        SetVReg(s, 3, [FBitsU(10.0f), FBitsU(20.0f), FBitsU(30.0f), FBitsU(40.0f),]);
        uint raw = VopFvv(0x00, 1, 2, 3); // vfadd.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(11.0f, FElemF(v1, 0));
        Assert.Equal(22.0f, FElemF(v1, 1));
        Assert.Equal(33.0f, FElemF(v1, 2));
        Assert.Equal(44.0f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfmulVf_BroadcastsScalar() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f),]);
        SetFReg(s, 10, 5.0f);              // fa0 = 5.0
        uint raw = VopFvf(0x24, 1, 2, 10); // vfmul.vf v1, v2, fa0
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(5.0f, FElemF(v1, 0));
        Assert.Equal(10.0f, FElemF(v1, 1));
        Assert.Equal(15.0f, FElemF(v1, 2));
        Assert.Equal(20.0f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfmaccVv_Accumulates() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vfmacc: vd = vd + vs2 * vs1
        SetVReg(s, 1, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f),]);     // vd (accumulator)
        SetVReg(s, 2, [FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f), FBitsU(5.0f),]);     // vs2
        SetVReg(s, 3, [FBitsU(10.0f), FBitsU(10.0f), FBitsU(10.0f), FBitsU(10.0f),]); // vs1
        uint raw = VopFvv(0x2C, 1, 2, 3);                                             // vfmacc.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(21.0f, FElemF(v1, 0)); // 1 + 2*10
        Assert.Equal(32.0f, FElemF(v1, 1)); // 2 + 3*10
        Assert.Equal(43.0f, FElemF(v1, 2)); // 3 + 4*10
        Assert.Equal(54.0f, FElemF(v1, 3)); // 4 + 5*10
    }

    [Fact]
    public void Execute_VmfeqVv_ProduceMaskBits() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f),]);
        SetVReg(s, 3, [FBitsU(1.0f), FBitsU(99.0f), FBitsU(3.0f), FBitsU(99.0f),]);
        uint raw = VopFvv(0x18, 0, 2, 3); // vmfeq.vv v0, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        // elements 0 and 2 match → bits 0 and 2 set → byte 0 = 0b0101 = 5
        Assert.Equal(0b0101, s.VectorRegisters.Read(0)[0] & 0xF);
    }

    [Fact]
    public void Execute_VfsqrtV_TakesSquareRoot() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(4.0f), FBitsU(9.0f), FBitsU(16.0f), FBitsU(25.0f),]);
        uint raw = VopFvUnary(0x13, 1, 2, 0); // vfsqrt.v v1, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(2.0f, FElemF(v1, 0));
        Assert.Equal(3.0f, FElemF(v1, 1));
        Assert.Equal(4.0f, FElemF(v1, 2));
        Assert.Equal(5.0f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfcvtFxV_Int32ToFloat32() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 0xFFFFFFFFu, 100u, unchecked((uint)-42),]); // -1 as twos-complement
        // vfcvt.f.x.v v1, v2  (vs1 selector = 3)
        uint raw = VopFvUnary(0x12, 1, 2, 3);
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(1.0f, FElemF(v1, 0));
        Assert.Equal(-1.0f, FElemF(v1, 1));
        Assert.Equal(100.0f, FElemF(v1, 2));
        Assert.Equal(-42.0f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfmvVf_BroadcastsScalarToAll() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetFReg(s, 10, 3.14f);
        uint raw = VopFvf(0x17, 1, 0, 10); // vfmv.v.f v1, fa0
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(3.14f, FElemF(v1, 0));
        Assert.Equal(3.14f, FElemF(v1, 1));
        Assert.Equal(3.14f, FElemF(v1, 2));
        Assert.Equal(3.14f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfmvFs_ReadsElement0ToScalar() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(7.5f), FBitsU(0.0f), FBitsU(0.0f), FBitsU(0.0f),]);
        // vfmv.f.s vd=1 (fa1=f1), vs2=2  →  fa1 = vs2[0] = 7.5
        // In OPFVV encoding: bits[11:7] = vd = 1, that means rd = fa1 (float reg 1, unified 33)
        uint raw = VopFvv(0x10, 1, 2, 0); // vfmv.f.s fa1, v2
        ExecuteResult r = Exec(raw, s);
        Assert.True(r.RegisterResult.HasValue);
        var bits = (uint)r.RegisterResult.Value;
        Assert.Equal(7.5f, BitConverter.Int32BitsToSingle((int)bits));
    }

    [Fact]
    public void Execute_VfminVv_ElementWiseMin() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(3.0f), FBitsU(1.0f), FBitsU(5.0f), FBitsU(2.0f),]);
        SetVReg(s, 3, [FBitsU(2.0f), FBitsU(4.0f), FBitsU(1.0f), FBitsU(2.0f),]);
        uint raw = VopFvv(0x04, 1, 2, 3); // vfmin.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(2.0f, FElemF(v1, 0));
        Assert.Equal(1.0f, FElemF(v1, 1));
        Assert.Equal(1.0f, FElemF(v1, 2));
        Assert.Equal(2.0f, FElemF(v1, 3));
    }

    [Fact]
    public void Execute_VfsgnjVv_SignInject() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [FBitsU(-3.0f), FBitsU(2.0f), FBitsU(-1.0f), FBitsU(4.0f),]); // magnitude source
        SetVReg(s, 3, [FBitsU(-1.0f), FBitsU(-2.0f), FBitsU(3.0f), FBitsU(5.0f),]); // sign source
        uint raw = VopFvv(0x08, 1, 2, 3);                                           // vfsgnj.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(-3.0f, FElemF(v1, 0)); // -|3| from vs3
        Assert.Equal(-2.0f, FElemF(v1, 1)); // -|2| from vs3
        Assert.Equal(1.0f, FElemF(v1, 2));  // +|1| from vs3
        Assert.Equal(4.0f, FElemF(v1, 3));  // +|4| from vs3
    }

    // Convert float to uint bits (for SetVReg)
    private static uint FBitsU(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    // ── Integer min/max ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_VminuVv_UnsignedMinimum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [10u, 0xFFFFFFFFu, 5u, 100u,]); // vs2
        SetVReg(s, 3, [20u, 1u, 5u, 50u,]);           // vs1
        uint raw = VopVv(4, 1, 2, 3);                 // vminu.vv v1, v2, v3  (funct6=4)
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(v1, 0));  // min(10, 20) = 10
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 4));   // min(0xFFFFFFFF, 1) = 1 (unsigned)
        Assert.Equal(5u, BitConverter.ToUInt32(v1, 8));   // min(5, 5) = 5
        Assert.Equal(50u, BitConverter.ToUInt32(v1, 12)); // min(100, 50) = 50
    }

    [Fact]
    public void Execute_VminVv_SignedMinimum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vs2: -1(0xFFFFFFFF), -128(0x80000000), 5, -3
        // vs1: 1, -2(0xFFFFFFFE), 3, 0
        SetVReg(s, 2, [0xFFFFFFFFu, 0x80000000u, 5u, 0xFFFFFFFDu,]);
        SetVReg(s, 3, [1u, 0xFFFFFFFEu, 3u, 0u,]);
        uint raw = VopVv(5, 1, 2, 3); // vmin.vv v1, v2, v3  (funct6=5)
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(v1, 0));  // min(-1, 1) = -1
        Assert.Equal(0x80000000u, BitConverter.ToUInt32(v1, 4));  // min(INT_MIN, -2) = INT_MIN
        Assert.Equal(3u, BitConverter.ToUInt32(v1, 8));           // min(5, 3) = 3
        Assert.Equal(0xFFFFFFFDu, BitConverter.ToUInt32(v1, 12)); // min(-3, 0) = -3
    }

    [Fact]
    public void Execute_VmaxuVv_UnsignedMaximum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [10u, 0xFFFFFFFFu, 5u, 100u,]);
        SetVReg(s, 3, [20u, 1u, 5u, 50u,]);
        uint raw = VopVv(6, 1, 2, 3); // vmaxu.vv v1, v2, v3  (funct6=6)
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(20u, BitConverter.ToUInt32(v1, 0));         // max(10, 20) = 20
        Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(v1, 4)); // max(0xFFFFFFFF, 1) = 0xFFFFFFFF
        Assert.Equal(5u, BitConverter.ToUInt32(v1, 8));          // max(5, 5) = 5
        Assert.Equal(100u, BitConverter.ToUInt32(v1, 12));       // max(100, 50) = 100
    }

    [Fact]
    public void Execute_VmaxVv_SignedMaximum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0xFFFFFFFFu, 0x80000000u, 5u, 0xFFFFFFFDu,]); // -1, INT_MIN, 5, -3
        SetVReg(s, 3, [1u, 0xFFFFFFFEu, 3u, 0u,]);                   //  1,      -2, 3,  0
        uint raw = VopVv(7, 1, 2, 3);                                // vmax.vv v1, v2, v3  (funct6=7)
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 0));          // max(-1, 1) = 1
        Assert.Equal(0xFFFFFFFEu, BitConverter.ToUInt32(v1, 4)); // max(INT_MIN, -2) = -2
        Assert.Equal(5u, BitConverter.ToUInt32(v1, 8));          // max(5, 3) = 5
        Assert.Equal(0u, BitConverter.ToUInt32(v1, 12));         // max(-3, 0) = 0
    }

    // ── Integer MAC ──────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VmaccVv_MultiplyAccumulate() {
        // vmacc.vv vd, vs1, vs2: vd[i] = vd[i] + vs2[i]*vs1[i]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [100u, 200u, 0u, 10u,]); // vd (accumulator)
        SetVReg(s, 2, [3u, 4u, 5u, 6u,]);      // vs2
        SetVReg(s, 3, [2u, 5u, 3u, 7u,]);      // vs1 (multiplier in assembly: vmacc vd, vs1, vs2)
        // vmacc.vv v1, v3, v2 (funct6=0x2D, OPMVV): v1[i] += v2[i]*v3[i]
        uint raw = VopMvv(0x2D, 1, 2, 3); // vd=1, vs2=2, vs1=3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(106u, BitConverter.ToUInt32(v1, 0)); // 100 + 3*2 = 106
        Assert.Equal(220u, BitConverter.ToUInt32(v1, 4)); // 200 + 4*5 = 220
        Assert.Equal(15u, BitConverter.ToUInt32(v1, 8));  // 0 + 5*3 = 15
        Assert.Equal(52u, BitConverter.ToUInt32(v1, 12)); // 10 + 6*7 = 52
    }

    [Fact]
    public void Execute_VnmsacVv_NegateMulSubtract() {
        // vnmsac.vv vd, vs1, vs2: vd[i] = vd[i] - vs2[i]*vs1[i]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [100u, 200u, 0u, 50u,]); // vd (accumulator)
        SetVReg(s, 2, [3u, 4u, 5u, 6u,]);      // vs2
        SetVReg(s, 3, [2u, 5u, 3u, 7u,]);      // vs1
        uint raw = VopMvv(0x2F, 1, 2, 3);      // vnmsac.vv v1, v3, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(94u, BitConverter.ToUInt32(v1, 0));                       // 100 - 3*2 = 94
        Assert.Equal(180u, BitConverter.ToUInt32(v1, 4));                      // 200 - 4*5 = 180
        Assert.Equal(unchecked((uint)(0 - 15)), BitConverter.ToUInt32(v1, 8)); // 0 - 5*3 = -15 wraps
        Assert.Equal(8u, BitConverter.ToUInt32(v1, 12));                       // 50 - 6*7 = 8
    }

    [Fact]
    public void Execute_VmaccVv_MaskedLeavesInactiveLanesUndisturbed() {
        // vmacc.vv v1, v3, v2, v0.t  — mask 0b0101: elements 0,2 active; elements 1,3 undisturbed
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101; // bits 0 and 2 active
        s.VectorRegisters.Write(0, maskReg);
        SetVReg(s, 1, [100u, 200u, 0u, 50u,]);  // vd (accumulator)
        SetVReg(s, 2, [3u, 4u, 5u, 6u,]);       // vs2
        SetVReg(s, 3, [2u, 5u, 3u, 7u,]);       // vs1
        uint raw = VopMvv(0x2D, 1, 2, 3, true); // vmacc.vv v1, v3, v2, v0.t
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(106u, BitConverter.ToUInt32(v1, 0)); // active:   100 + 3*2 = 106
        Assert.Equal(200u, BitConverter.ToUInt32(v1, 4)); // inactive: vd[1]=200 undisturbed
        Assert.Equal(15u, BitConverter.ToUInt32(v1, 8));  // active:   0 + 5*3 = 15
        Assert.Equal(50u, BitConverter.ToUInt32(v1, 12)); // inactive: vd[3]=50 undisturbed
    }

    [Fact]
    public void Decode_VmaddVv_Payload() {
        // vmadd.vv v3, v2, v1 — funct6=0x29, OPMVV; vd=3, vs2_field=1, vs1_field=2
        uint raw = VopMvv(0x29, 3, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVIntMacVv>(i.Payload);
        Assert.Equal(VIntMacOp.Madd, op.Op);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(2, op.Vs1);
        Assert.Equal(3, i.VectorDestinationRegister);
        Assert.Equal([3, 1, 2,], i.VectorSourceRegisters); // vd is also a source
    }

    [Fact]
    public void Decode_VnmsubVx_Payload() {
        // vnmsub.vx v3, a0, v1 — funct6=0x2B, OPMVX
        uint raw = VopMvx(0x2B, 3, 1, 10);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVIntMacVx>(i.Payload);
        Assert.Equal(VIntMacOp.Nmsub, op.Op);
        Assert.Equal(3, op.Vd);
        Assert.Equal(1, op.Vs2);
        Assert.Equal(10, op.Rs1);
    }

    [Fact]
    public void Execute_VmaddVv_AddsVs2ToVdTimesVs1() {
        // vmadd.vv vd, vs1, vs2: vd[i] = vs2[i] + vd[i]*vs1[i]
        // vd=v1=[3,4,5,6], vs1=v3=[2,5,3,7], vs2=v2=[100,200,0,10]
        // VopMvv(0x29, vd=1, vs2_field=2, vs1_field=3)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [3u, 4u, 5u, 6u,]);      // vd (multiplicand, also destination)
        SetVReg(s, 2, [100u, 200u, 0u, 10u,]); // vs2 (addend)
        SetVReg(s, 3, [2u, 5u, 3u, 7u,]);      // vs1 (multiplier)
        uint raw = VopMvv(0x29, 1, 2, 3);
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(106u, BitConverter.ToUInt32(v1, 0)); // 100 + 3*2 = 106
        Assert.Equal(220u, BitConverter.ToUInt32(v1, 4)); // 200 + 4*5 = 220
        Assert.Equal(15u, BitConverter.ToUInt32(v1, 8));  // 0 + 5*3 = 15
        Assert.Equal(52u, BitConverter.ToUInt32(v1, 12)); // 10 + 6*7 = 52
    }

    [Fact]
    public void Execute_VnmsubVv_SubtractsVdTimesVs1FromVs2() {
        // vnmsub.vv vd, vs1, vs2: vd[i] = vs2[i] - vd[i]*vs1[i]
        // vd=v1=[10,20,0,5], vs1=v3=[2,3,4,6], vs2=v2=[100,200,50,80]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 0u, 5u,]);     // vd
        SetVReg(s, 2, [100u, 200u, 50u, 80u,]); // vs2 (addend)
        SetVReg(s, 3, [2u, 3u, 4u, 6u,]);       // vs1 (multiplier)
        uint raw = VopMvv(0x2B, 1, 2, 3);
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(80u, BitConverter.ToUInt32(v1, 0));  // 100 - 10*2 = 80
        Assert.Equal(140u, BitConverter.ToUInt32(v1, 4)); // 200 - 20*3 = 140
        Assert.Equal(50u, BitConverter.ToUInt32(v1, 8));  // 50 - 0*4 = 50
        Assert.Equal(50u, BitConverter.ToUInt32(v1, 12)); // 80 - 5*6 = 50
    }

    [Fact]
    public void Execute_VmaddVx_ScalarMultiplier() {
        // vmadd.vx vd, rs1, vs2: vd[i] = vs2[i] + vd[i]*rs1
        // VopMvx(0x29, vd=1, vs2_field=2, rs1=10)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [3u, 4u, 5u, 6u,]);      // vd (multiplicand)
        SetVReg(s, 2, [100u, 200u, 0u, 50u,]); // vs2 (addend)
        s.IntegerRegisters.Write(10, 10);      // a0 = 10 (multiplier)
        uint raw = VopMvx(0x29, 1, 2, 10);
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(130u, BitConverter.ToUInt32(v1, 0));  // 100 + 3*10 = 130
        Assert.Equal(240u, BitConverter.ToUInt32(v1, 4));  // 200 + 4*10 = 240
        Assert.Equal(50u, BitConverter.ToUInt32(v1, 8));   // 0 + 5*10 = 50
        Assert.Equal(110u, BitConverter.ToUInt32(v1, 12)); // 50 + 6*10 = 110
    }

    // ── vmv.s.x ──────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VmvSx_WritesScalarToElement0() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [0xDEADBEEFu, 0xCAFEBABEu, 0x12345678u, 0xABCDEF00u,]);
        s.IntegerRegisters.Write(10, 42); // a0 = 42
        // vmv.s.x v1, a0  (funct6=0x10, OPMVX, vs2 field=0)
        uint raw = VopMvx(0x10, 1, 0, 10); // vd=1, vs2=0, rs1=10
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(42u, BitConverter.ToUInt32(v1, 0));          // element 0 written
        Assert.Equal(0xCAFEBABEu, BitConverter.ToUInt32(v1, 4));  // element 1 undisturbed
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(v1, 8));  // element 2 undisturbed
        Assert.Equal(0xABCDEF00u, BitConverter.ToUInt32(v1, 12)); // element 3 undisturbed
    }

    // ── vmerge ───────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VmergeVvm_SelectsBasedOnMask() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // v0 mask: bits 0,2 set → elements 0,2 take vs1; elements 1,3 take vs2
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101; // bits 0 and 2
        s.VectorRegisters.Write(0, maskReg);
        SetVReg(s, 2, [10u, 20u, 30u, 40u,]);     // vs2 (mask=0 source)
        SetVReg(s, 3, [100u, 200u, 300u, 400u,]); // vs1 (mask=1 source)
        // vmerge.vvm v1, v2, v3, v0  (funct6=0x17, OPIVV, masked=true)
        uint raw = VopVv(0x17, 1, 2, 3, true);
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(100u, BitConverter.ToUInt32(v1, 0)); // mask=1 → vs1[0] = 100
        Assert.Equal(20u, BitConverter.ToUInt32(v1, 4));  // mask=0 → vs2[1] = 20
        Assert.Equal(300u, BitConverter.ToUInt32(v1, 8)); // mask=1 → vs1[2] = 300
        Assert.Equal(40u, BitConverter.ToUInt32(v1, 12)); // mask=0 → vs2[3] = 40
    }

    // ── vfmerge / vfslide1 ───────────────────────────────────────────────────

    [Fact]
    public void Decode_VfmergeVfm_Payload() {
        // vfmerge.vfm v1, v2, fa0, v0 — funct6=0x17, OPFVF, vm=0 (masked)
        uint raw = VopFvf(0x17, 1, 2, 10, true);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVFpMergeVf>(i.Payload);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(42, op.FpRs1); // fa0 = f10 → unified index 42
        Assert.Equal(1, i.VectorDestinationRegister);
        Assert.Equal([2, 42, 0,], i.VectorSourceRegisters); // vs2, fpRs1, v0 mask
    }

    [Fact]
    public void Execute_VfmergeVfm_SelectsBasedOnMask() {
        // mask bits 0,2 set → elements 0,2 take scalar fa0; elements 1,3 take vs2[i]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101; // bits 0 and 2
        s.VectorRegisters.Write(0, maskReg);
        SetVReg(
            s, 2, [
                (uint)BitConverter.SingleToInt32Bits(10.0f),
                (uint)BitConverter.SingleToInt32Bits(20.0f),
                (uint)BitConverter.SingleToInt32Bits(30.0f),
                (uint)BitConverter.SingleToInt32Bits(40.0f),
            ]
        );                                       // vs2
        SetFReg(s, 10, 99.0f);                   // fa0 = 99.0
        uint raw = VopFvf(0x17, 1, 2, 10, true); // vfmerge.vfm v1, v2, fa0, v0
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(99.0f, FElemF(v1, 0)); // mask=1 → scalar
        Assert.Equal(20.0f, FElemF(v1, 1)); // mask=0 → vs2[1]
        Assert.Equal(99.0f, FElemF(v1, 2)); // mask=1 → scalar
        Assert.Equal(40.0f, FElemF(v1, 3)); // mask=0 → vs2[3]
    }

    [Fact]
    public void Decode_VfslideUp1Vf_Payload() {
        // vfslide1up.vf v1, v2, fa0 — funct6=0x0E, OPFVF
        uint raw = VopFvf(0x0E, 1, 2, 10);
        ITooth i = _dec.Decode(0, raw);
        var op = Assert.IsType<RvVFpSlide1Vf>(i.Payload);
        Assert.Equal(VSlideDir.Up, op.Dir);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(42, op.FpRs1);
        Assert.Equal([2, 42,], i.VectorSourceRegisters);
    }

    [Fact]
    public void Execute_VfslideUp1Vf_InsertsScalarAtBottom() {
        // vfslide1up.vf: vd[0]=scalar, vd[i]=vs2[i-1]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(
            s, 2, [
                (uint)BitConverter.SingleToInt32Bits(1.0f),
                (uint)BitConverter.SingleToInt32Bits(2.0f),
                (uint)BitConverter.SingleToInt32Bits(3.0f),
                (uint)BitConverter.SingleToInt32Bits(4.0f),
            ]
        );                                 // vs2
        SetFReg(s, 10, 0.0f);              // fa0 = 0.0 (inserted at vd[0])
        uint raw = VopFvf(0x0E, 3, 2, 10); // vfslide1up.vf v3, v2, fa0
        Exec(raw, s).SideEffect!(s);
        byte[] v3 = s.VectorRegisters.Read(3);
        Assert.Equal(0.0f, FElemF(v3, 0)); // scalar inserted at [0]
        Assert.Equal(1.0f, FElemF(v3, 1)); // vs2[0]
        Assert.Equal(2.0f, FElemF(v3, 2)); // vs2[1]
        Assert.Equal(3.0f, FElemF(v3, 3)); // vs2[2]
    }

    [Fact]
    public void Execute_VfslideDown1Vf_InsertsScalarAtTop() {
        // vfslide1down.vf: vd[i]=vs2[i+1], vd[vl-1]=scalar
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(
            s, 2, [
                (uint)BitConverter.SingleToInt32Bits(1.0f),
                (uint)BitConverter.SingleToInt32Bits(2.0f),
                (uint)BitConverter.SingleToInt32Bits(3.0f),
                (uint)BitConverter.SingleToInt32Bits(4.0f),
            ]
        );                                 // vs2
        SetFReg(s, 10, 5.0f);              // fa0 = 5.0 (inserted at vd[vl-1])
        uint raw = VopFvf(0x0F, 3, 2, 10); // vfslide1down.vf v3, v2, fa0
        Exec(raw, s).SideEffect!(s);
        byte[] v3 = s.VectorRegisters.Read(3);
        Assert.Equal(2.0f, FElemF(v3, 0)); // vs2[1]
        Assert.Equal(3.0f, FElemF(v3, 1)); // vs2[2]
        Assert.Equal(4.0f, FElemF(v3, 2)); // vs2[3]
        Assert.Equal(5.0f, FElemF(v3, 3)); // scalar inserted at last position
    }

    // ── mask logical ops ─────────────────────────────────────────────────────
    // vs2=0xCC, vs1=0xAA fills all VLENB bytes; results checked on byte[0].
    // These ops ignore vl and always write all VLENB bytes.

    private static void SetMaskReg(Rv32ArchState s, int reg, byte pattern) {
        var buf = new byte[VectorRegisterFile.VLenB];
        Array.Fill(buf, pattern);
        s.VectorRegisters.Write(reg, buf);
    }

    private static byte MaskByte0(Rv32ArchState s, int reg) => s.VectorRegisters.Read(reg)[0];

    // vs2=0xCC, vs1=0xAA: expected results pre-computed to avoid C# constant overflow
    // vmand=0x88, vmandn=0x44, vmor=0xEE, vmorn=0xDD, vmxor=0x66, vmnand=0x77, vmnor=0x11, vmxnor=0x99

    [Fact]
    public void Execute_VmandMm_BitwiseAnd() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x19, 1, 2, 3), s).SideEffect!(s); // vmand.mm v1, v2, v3
        Assert.Equal((byte)0x88, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmandnMm_AndNot() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x18, 1, 2, 3), s).SideEffect!(s); // vmandn.mm v1, v2, v3
        Assert.Equal((byte)0x44, MaskByte0(s, 1));     // 0xCC & ~0xAA = 0x44
    }

    [Fact]
    public void Execute_VmorMm_BitwiseOr() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1A, 1, 2, 3), s).SideEffect!(s); // vmor.mm v1, v2, v3
        Assert.Equal((byte)0xEE, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmornMm_OrNot() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1C, 1, 2, 3), s).SideEffect!(s); // vmorn.mm v1, v2, v3
        Assert.Equal((byte)0xDD, MaskByte0(s, 1));     // 0xCC | ~0xAA = 0xDD
    }

    [Fact]
    public void Execute_VmxorMm_BitwiseXor() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1B, 1, 2, 3), s).SideEffect!(s); // vmxor.mm v1, v2, v3
        Assert.Equal((byte)0x66, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmnandMm_NegatedAnd() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1D, 1, 2, 3), s).SideEffect!(s); // vmnand.mm v1, v2, v3
        Assert.Equal((byte)0x77, MaskByte0(s, 1));     // ~(0xCC & 0xAA) = 0x77
    }

    [Fact]
    public void Execute_VmnorMm_NegatedOr() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1E, 1, 2, 3), s).SideEffect!(s); // vmnor.mm v1, v2, v3
        Assert.Equal((byte)0x11, MaskByte0(s, 1));     // ~(0xCC | 0xAA) = 0x11
    }

    [Fact]
    public void Execute_VmxnorMm_NegatedXor() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0xCC);
        SetMaskReg(s, 3, 0xAA);
        Exec(VopMvv(0x1F, 1, 2, 3), s).SideEffect!(s); // vmxnor.mm v1, v2, v3
        Assert.Equal((byte)0x99, MaskByte0(s, 1));     // ~(0xCC ^ 0xAA) = 0x99
    }

    [Fact]
    public void Execute_VmandMm_WritesAllVlenbBytes() {
        // mask logical ops write all VLENB bytes regardless of vl
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s); // vl=4 elements, VLENB=16 bytes
        SetMaskReg(s, 2, 0xFF);
        SetMaskReg(s, 3, 0x0F);
        Exec(VopMvv(0x19, 1, 2, 3), s).SideEffect!(s); // vmand.mm v1, v2, v3
        byte[] result = s.VectorRegisters.Read(1);
        Assert.All(result, b => Assert.Equal((byte)0x0F, b));
    }

    // ── vcpop.m ──────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VcpopM_CountsSetBits() {
        // vs2 byte0 = 0b1010 (bits 1,3 set), vl=4 → count = 2
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b1010);
        // vcpop.m a0, v2  (funct6=0x10, vs1=16, unmasked)
        ExecuteResult r = Exec(VopMvv(0x10, 10, 2, 16), s);
        Assert.Equal(2UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_VcpopM_MaskedCountsOnlyActiveBits() {
        // vs2 = 0b1111 (all 4 set), mask v0 = 0b0101 (bits 0,2 active) → count = 2
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 0, 0b0101);
        SetMaskReg(s, 2, 0b1111);
        ExecuteResult r = Exec(VopMvv(0x10, 10, 2, 16, true), s);
        Assert.Equal(2UL, r.RegisterResult.Value);
    }

    // ── vfirst.m ─────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VfirstM_FindsFirstSetBit() {
        // vs2 = 0b1010 → first set bit is at index 1
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b1010);
        // vfirst.m a0, v2  (funct6=0x10, vs1=17, unmasked)
        ExecuteResult r = Exec(VopMvv(0x10, 10, 2, 17), s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_VfirstM_ReturnsMinusOneWhenNoneSet() {
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b0000);
        ExecuteResult r = Exec(VopMvv(0x10, 10, 2, 17), s);
        Assert.Equal(ulong.MaxValue, r.RegisterResult.Value); // -1 sign-extended to 64 bits
    }

    // ── vmsbf.m / vmsof.m / vmsif.m ──────────────────────────────────────────

    [Fact]
    public void Execute_VmsbfM_SetsBeforeFirstSetBit() {
        // vs2 = 0b1010 → first set at bit 1; bits before it: bit 0 → result = 0b0001
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b1010);
        Exec(VopMvv(0x14, 1, 2, 1), s).SideEffect!(s); // vmsbf.m v1, v2
        Assert.Equal((byte)0b0001, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmsofM_SetsOnlyFirstSetBit() {
        // vs2 = 0b1010 → first set at bit 1; result = 0b0010
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b1010);
        Exec(VopMvv(0x14, 1, 2, 2), s).SideEffect!(s); // vmsof.m v1, v2
        Assert.Equal((byte)0b0010, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmsifM_SetsUpToAndIncludingFirstSetBit() {
        // vs2 = 0b1010 → first set at bit 1; result = 0b0011
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b1010);
        Exec(VopMvv(0x14, 1, 2, 3), s).SideEffect!(s); // vmsif.m v1, v2
        Assert.Equal((byte)0b0011, MaskByte0(s, 1));
    }

    [Fact]
    public void Execute_VmsbfM_NoSetBit_SetsAllActive() {
        // vs2 = 0b0000, no first set bit → all active elements get 1 → result = 0b1111
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 4, VectorTests.VtypeiE8M1Tama), s);
        SetMaskReg(s, 2, 0b0000);
        Exec(VopMvv(0x14, 1, 2, 1), s).SideEffect!(s); // vmsbf.m v1, v2
        Assert.Equal((byte)0b1111, MaskByte0(s, 1));
    }

    // ── viota.m ───────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_ViotaM_WritesExclusivePrefixSum() {
        // vs2 = 0b1010: bit0=0, bit1=1, bit2=0, bit3=1, vl=4, SEW=32
        // vd[0]=0, vd[1]=0, vd[2]=1, vd[3]=1  (exclusive prefix sum)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetMaskReg(s, 2, 0b1010);
        Exec(VopMvv(0x14, 1, 2, 16), s).SideEffect!(s); // viota.m v1, v2
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(0u, BitConverter.ToUInt32(v1, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(v1, 4));
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 8));
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 12));
    }

    // ── vid.v ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VidV_WritesElementIndex() {
        // vl=4, SEW=32; vd[i] = i
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vid.v v1  (funct6=0x14, vs1=17, vs2=0 fixed, unmasked)
        Exec(VopMvv(0x14, 1, 0, 17), s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(0u, BitConverter.ToUInt32(v1, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 4));
        Assert.Equal(2u, BitConverter.ToUInt32(v1, 8));
        Assert.Equal(3u, BitConverter.ToUInt32(v1, 12));
    }

    // ── vcompress.vm ─────────────────────────────────────────────────────────

    [Fact]
    public void Execute_VcompressVm_PacksActiveElements() {
        // vs2 = [10, 20, 30, 40], vs1 mask = 0b1010 (bits 1,3 active)
        // expected: vd = [20, 40, <undisturbed>, <undisturbed>]
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [10u, 20u, 30u, 40u,]);
        SetMaskReg(s, 3, 0b1010);             // vs1 explicit mask
        SetVReg(s, 1, [99u, 99u, 77u, 77u,]); // initial vd (tail undisturbed)
        // vcompress.vm v1, v2, v3  (funct6=0x17, OPMVV, vm=1)
        Exec(VopMvv(0x17, 1, 2, 3), s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(20u, BitConverter.ToUInt32(v1, 0));  // first active element
        Assert.Equal(40u, BitConverter.ToUInt32(v1, 4));  // second active element
        Assert.Equal(77u, BitConverter.ToUInt32(v1, 8));  // tail undisturbed
        Assert.Equal(77u, BitConverter.ToUInt32(v1, 12)); // tail undisturbed
    }

    // ── vmv{N}r.v ─────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Vmv1rV_CopiesOneRegister() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        // vmv1r.v v1, v2  (funct6=0x27, OPIVI, imm=0 → N=1)
        Exec(VopVi(0x27, 1, 2, 0), s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(v1, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(v1, 8));
        Assert.Equal(4u, BitConverter.ToUInt32(v1, 12));
    }

    [Fact]
    public void Execute_Vmv4rV_CopiesFourRegisters() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [10u, 11u, 12u, 13u,]);
        SetVReg(s, 5, [20u, 21u, 22u, 23u,]);
        SetVReg(s, 6, [30u, 31u, 32u, 33u,]);
        SetVReg(s, 7, [40u, 41u, 42u, 43u,]);
        // vmv4r.v v8, v4  (funct6=0x27, OPIVI, imm=3 → N=4)
        Exec(VopVi(0x27, 8, 4, 3), s).SideEffect!(s);
        Assert.Equal(10u, BitConverter.ToUInt32(s.VectorRegisters.Read(8), 0));
        Assert.Equal(20u, BitConverter.ToUInt32(s.VectorRegisters.Read(9), 0));
        Assert.Equal(30u, BitConverter.ToUInt32(s.VectorRegisters.Read(10), 0));
        Assert.Equal(40u, BitConverter.ToUInt32(s.VectorRegisters.Read(11), 0));
    }

    // ── FP reductions ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_VfredusumVs_IsRvVFpRedVs() {
        uint raw = VopFvv(0x01, 1, 2, 3); // vfredusum.vs v1, v2, v3
        var op = Assert.IsType<RvVFpRedVs>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpRedOp.Usum, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VfredosumVs_IsRvVFpRedVs() {
        uint raw = VopFvv(0x03, 1, 2, 3); // vfredosum.vs v1, v2, v3
        var op = Assert.IsType<RvVFpRedVs>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpRedOp.Osum, op.Op);
    }

    [Fact]
    public void Decode_VfredminVs_IsRvVFpRedVs() {
        uint raw = VopFvv(0x05, 1, 2, 3); // vfredmin.vs v1, v2, v3
        var op = Assert.IsType<RvVFpRedVs>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpRedOp.Min, op.Op);
    }

    [Fact]
    public void Decode_VfredmaxVs_IsRvVFpRedVs() {
        uint raw = VopFvv(0x07, 1, 2, 3); // vfredmax.vs v1, v2, v3
        var op = Assert.IsType<RvVFpRedVs>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpRedOp.Max, op.Op);
    }

    [Fact]
    public void Execute_VfredusumVs_SumsWithInitialAccumulator() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vs2 = [1.0, 2.0, 3.0, 4.0]; vs1[0] = 10.0 (initial acc); expected = 10+1+2+3+4 = 20
        SetVReg(s, 2, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(3.0f), FBitsU(4.0f),]);
        SetVReg(s, 3, [FBitsU(10.0f), 0u, 0u, 0u,]);
        uint raw = VopFvv(0x01, 1, 2, 3); // vfredusum.vs v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        Assert.Equal(20.0f, FElemF(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Execute_VfredminVs_FindsMinimum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vs2 = [5.0, 1.0, 3.0, 7.0]; vs1[0] = 99.0 (initial acc); expected min = 1.0
        SetVReg(s, 2, [FBitsU(5.0f), FBitsU(1.0f), FBitsU(3.0f), FBitsU(7.0f),]);
        SetVReg(s, 3, [FBitsU(99.0f), 0u, 0u, 0u,]);
        uint raw = VopFvv(0x05, 1, 2, 3); // vfredmin.vs v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        Assert.Equal(1.0f, FElemF(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Execute_VfredmaxVs_FindsMaximum() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // vs2 = [5.0, 1.0, 9.0, 3.0]; vs1[0] = -99.0 (initial acc); expected max = 9.0
        SetVReg(s, 2, [FBitsU(5.0f), FBitsU(1.0f), FBitsU(9.0f), FBitsU(3.0f),]);
        SetVReg(s, 3, [FBitsU(-99.0f), 0u, 0u, 0u,]);
        uint raw = VopFvv(0x07, 1, 2, 3); // vfredmax.vs v1, v2, v3
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        Assert.Equal(9.0f, FElemF(s.VectorRegisters.Read(1), 0));
    }

    [Fact]
    public void Execute_VfredusumVs_MaskedSkipsInactiveElements() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        // mask byte 0 = 0b0101 → elements 0 and 2 active
        SetVReg(s, 0, [0b0101,]);                                                 // mask v0
        SetVReg(s, 2, [FBitsU(1.0f), FBitsU(2.0f), FBitsU(4.0f), FBitsU(8.0f),]); // vs2
        SetVReg(s, 3, [FBitsU(0.0f), 0u, 0u, 0u,]);                               // vs1[0] = 0
        uint raw = VopFvv(0x01, 1, 2, 3, true);                                   // vfredusum.vs v1, v2, v3, v0.t
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        // only elements 0 (1.0) and 2 (4.0) contribute → 0 + 1 + 4 = 5
        Assert.Equal(5.0f, FElemF(s.VectorRegisters.Read(1), 0));
    }

    // ── V integer widening MAC ─────────────────────────────────────────────────

    [Fact]
    public void Decode_VwmaccVv_IsRvVWMacVv() {
        uint raw = VopMvv(0x3D, 1, 2, 3); // vwmacc.vv v1, v3, v2 (vd=1, vs2=2, vs1=3)
        var op = Assert.IsType<RvVwMacVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VwMacOp.Macc, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VwmaccuVv_IsRvVWMacVv() {
        uint raw = VopMvv(0x3C, 1, 2, 3); // vwmaccu.vv
        var op = Assert.IsType<RvVwMacVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VwMacOp.Maccu, op.Op);
    }

    [Fact]
    public void Decode_VwmaccsuVv_IsRvVWMacVv() {
        uint raw = VopMvv(0x3F, 1, 2, 3); // vwmaccsu.vv
        var op = Assert.IsType<RvVwMacVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VwMacOp.Maccsu, op.Op);
    }

    [Fact]
    public void Decode_VwmaccusVx_IsRvVWMacVx() {
        uint raw = VopMvx(0x3E, 1, 2, 10); // vwmaccus.vx v1, a0, v2
        var op = Assert.IsType<RvVwMacVx>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VwMacOp.Maccus, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(10, op.Rs1);
    }

    // Use SEW=8 for widening MAC tests — 1-byte sources, 2-byte (uint16) accumulator.
    // With vl=4, outEwBytes=2, effectiveVl=min(4, 16/2)=4.
    [Fact]
    public void Execute_VwmaccVv_SignedAccumulatesIntoWideElement() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s); // vl=4, SEW=8
        // vd (16-bit acc): [100, 200, 300, 400]
        SetVReg16(s, 1, [100, 200, 300, 400,]);
        // vs2 (8-bit signed): [-1, 2, -3, 4]
        SetVReg8(s, 2, [0xFF, 0x02, 0xFD, 0x04,]);
        // vs1 (8-bit signed): [2, 3, 4, 5]
        SetVReg8(s, 3, [2, 3, 4, 5,]);
        uint raw = VopMvv(0x3D, 1, 2, 3); // vwmacc.vv v1, v3, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((ushort)98, BitConverter.ToUInt16(v1, 0));  // 100 + (-1)*2 = 98
        Assert.Equal((ushort)206, BitConverter.ToUInt16(v1, 2)); // 200 + 2*3
        Assert.Equal((ushort)288, BitConverter.ToUInt16(v1, 4)); // 300 + (-3)*4
        Assert.Equal((ushort)420, BitConverter.ToUInt16(v1, 6)); // 400 + 4*5
    }

    [Fact]
    public void Execute_VwmaccuVv_UnsignedAccumulates() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        // vd (16-bit): [100, 100, 100, 100]; vs2=[5,10,200,255]; vs1=[2,3,4,1]
        SetVReg16(s, 1, [100, 100, 100, 100,]);
        SetVReg8(s, 2, [5, 10, 200, 255,]);
        SetVReg8(s, 3, [2, 3, 4, 1,]);
        uint raw = VopMvv(0x3C, 1, 2, 3); // vwmaccu.vv v1, v3, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((ushort)110, BitConverter.ToUInt16(v1, 0)); // 100 + 5*2
        Assert.Equal((ushort)130, BitConverter.ToUInt16(v1, 2)); // 100 + 10*3
        Assert.Equal((ushort)900, BitConverter.ToUInt16(v1, 4)); // 100 + 200*4
        Assert.Equal((ushort)355, BitConverter.ToUInt16(v1, 6)); // 100 + 255*1
    }

    [Fact]
    public void Execute_VwmaccsuVv_SignedVs2UnsignedVs1() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        // vwmaccsu: vd[i] += signed(vs2[i]) * unsigned(vs1[i])
        SetVReg16(s, 1, [0, 0, 0, 0,]);
        SetVReg8(s, 2, [0xFF, 2, 0xFD, 4,]); // signed: -1, 2, -3, 4
        SetVReg8(s, 3, [2, 3, 4, 5,]);       // unsigned: 2, 3, 4, 5
        uint raw = VopMvv(0x3F, 1, 2, 3);    // vwmaccsu.vv v1, v3, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(unchecked((ushort)-2), BitConverter.ToUInt16(v1, 0));  // 0 + (-1)*2
        Assert.Equal((ushort)6, BitConverter.ToUInt16(v1, 2));              // 0 + 2*3
        Assert.Equal(unchecked((ushort)-12), BitConverter.ToUInt16(v1, 4)); // 0 + (-3)*4
        Assert.Equal((ushort)20, BitConverter.ToUInt16(v1, 6));             // 0 + 4*5
    }

    [Fact]
    public void Execute_VwmaccusVx_UnsignedVs2SignedScalar() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        // vwmaccus: vd[i] += unsigned(vs2[i]) * signed(rs1)
        SetVReg16(s, 1, [100, 100, 100, 100,]);
        SetVReg8(s, 2, [5, 10, 15, 20,]);                   // unsigned sources
        s.IntegerRegisters.Write(10, unchecked((ulong)-3)); // rs1 = a0 = -3
        uint raw = VopMvx(0x3E, 1, 2, 10);                  // vwmaccus.vx v1, a0, v2
        ExecuteResult r = Exec(raw, s);
        r.SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((ushort)85, BitConverter.ToUInt16(v1, 0)); // 100 + 5*(-3)
        Assert.Equal((ushort)70, BitConverter.ToUInt16(v1, 2)); // 100 + 10*(-3)
        Assert.Equal((ushort)55, BitConverter.ToUInt16(v1, 4)); // 100 + 15*(-3)
        Assert.Equal((ushort)40, BitConverter.ToUInt16(v1, 6)); // 100 + 20*(-3)
    }

    // ── V saturating integer arithmetic ───────────────────────────────────────

    [Fact]
    public void Decode_VsadduVv_IsRvVSatIntVv() {
        uint raw = VopVv(0x20, 1, 2, 3); // vsaddu.vv v1, v2, v3
        var op = Assert.IsType<RvVSatIntVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Saddu, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
    }

    [Fact]
    public void Decode_VsaddVx_IsRvVSatIntVx() {
        uint raw = VopVx(0x21, 1, 2, 10); // vsadd.vx v1, v2, a0
        var op = Assert.IsType<RvVSatIntVx>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Sadd, op.Op);
    }

    [Fact]
    public void Decode_VssubuVv_IsRvVSatIntVv() {
        uint raw = VopVv(0x22, 1, 2, 3); // vssubu.vv
        var op = Assert.IsType<RvVSatIntVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Ssubu, op.Op);
    }

    [Fact]
    public void Decode_VssubVi_IsRvVSatIntVi() {
        // vssub has no VI form; funct6=0x23, funct3=3 should throw
        uint raw = VopVi(0x23, 1, 2, 3); // invalid VI form
        Assert.ThrowsAny<Exception>(() => _dec.Decode(0, raw));
    }

    [Fact]
    public void Decode_VsmulVv_IsRvVSatIntVv() {
        uint raw = VopVv(0x27, 1, 2, 3); // vsmul.vv
        var op = Assert.IsType<RvVSatIntVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Smul, op.Op);
    }

    [Fact]
    public void Decode_VssrlVi_IsRvVSatIntVi() {
        uint raw = VopVi(0x2A, 1, 2, 3); // vssrl.vi v1, v2, 3
        var op = Assert.IsType<RvVSatIntVi>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Ssrl, op.Op);
        Assert.Equal(3, op.Imm);
    }

    [Fact]
    public void Decode_VssraVv_IsRvVSatIntVv() {
        uint raw = VopVv(0x2B, 1, 2, 3); // vssra.vv
        var op = Assert.IsType<RvVSatIntVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VSatIntOp.Ssra, op.Op);
    }

    [Fact]
    public void Decode_VnclipuWv_IsRvVNClipVv() {
        uint raw = VopVv(0x2E, 1, 2, 3); // vnclipu.wv
        var op = Assert.IsType<RvVnClipVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VnClipOp.Clipu, op.Op);
    }

    [Fact]
    public void Decode_VnclipWi_IsRvVNClipVi() {
        uint raw = VopVi(0x2F, 1, 2, 2); // vnclip.wi v1, v2, 2
        var op = Assert.IsType<RvVnClipVi>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VnClipOp.Clip, op.Op);
        Assert.Equal(2, op.Imm);
    }

    [Fact]
    public void Execute_Vsadd_SaturatesToIntMax() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [100, unchecked((byte)-1), 127, 0,]);
        SetVReg8(s, 3, [50, unchecked((byte)-1), 1, unchecked((byte)-1),]);
        uint raw = VopVv(0x21, 1, 2, 3); // vsadd.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((sbyte)127, (sbyte)v1[0]); // 100+50=150 → sat 127
        Assert.Equal((sbyte)-2, (sbyte)v1[1]);  // -1+-1=-2
        Assert.Equal((sbyte)127, (sbyte)v1[2]); // 127+1=128 → sat 127
        Assert.Equal((sbyte)-1, (sbyte)v1[3]);  // 0+-1=-1
    }

    [Fact]
    public void Execute_Vsaddu_SaturatesToUintMax() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [200, 100, 0, 255,]);
        SetVReg8(s, 3, [100, 50, 0, 1,]);
        uint raw = VopVv(0x20, 1, 2, 3); // vsaddu.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(255, v1[0]); // 200+100=300 → sat 255
        Assert.Equal(150, v1[1]); // 100+50=150
        Assert.Equal(0, v1[2]);
        Assert.Equal(255, v1[3]); // 255+1=256 → sat 255
    }

    [Fact]
    public void Execute_Vssub_SaturatesToIntMin() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [unchecked((byte)-100), 127, 0, 10,]);
        SetVReg8(s, 3, [50, unchecked((byte)-1), unchecked((byte)-128), 5,]);
        uint raw = VopVv(0x23, 1, 2, 3); // vssub.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((sbyte)-128, (sbyte)v1[0]); // -100-50=-150 → sat -128
        Assert.Equal((sbyte)127, (sbyte)v1[1]);  // 127-(-1)=128 → sat 127
        Assert.Equal((sbyte)127, (sbyte)v1[2]);  // 0-(-128)=128 → sat 127
        Assert.Equal((sbyte)5, (sbyte)v1[3]);
    }

    [Fact]
    public void Execute_Vssubu_ClampsToZero() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetVReg8(s, 2, [50, 200, 0, 100,]);
        SetVReg8(s, 3, [100, 50, 1, 100,]);
        uint raw = VopVv(0x22, 1, 2, 3); // vssubu.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(0, v1[0]);   // 50-100 → clamp 0
        Assert.Equal(150, v1[1]); // 200-50=150
        Assert.Equal(0, v1[2]);   // 0-1 → clamp 0
        Assert.Equal(0, v1[3]);   // 100-100=0
    }

    [Fact]
    public void Execute_Vsmul_SaturatesAndRoundsDown() {
        // vxrm=rdn (2), SEW=e8; product shifted right by 7 bits, truncated
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        s.SystemRegisters.Write(CsrFile.Vxrm, 2, RvPrivilege.Machine); // rdn
        SetVReg8(s, 2, [2, 127, unchecked((byte)-128), unchecked((byte)-128),]);
        SetVReg8(s, 3, [3, 127, unchecked((byte)-1), unchecked((byte)-128),]);
        uint raw = VopVv(0x27, 1, 2, 3); // vsmul.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((sbyte)0, (sbyte)v1[0]);   // 6>>7 = 0
        Assert.Equal((sbyte)126, (sbyte)v1[1]); // 16129>>7 = 126 (truncate)
        Assert.Equal((sbyte)1, (sbyte)v1[2]);   // -128*-1=128>>7=1
        Assert.Equal((sbyte)127, (sbyte)v1[3]); // -128*-128=16384>>7=128 → sat 127
    }

    [Fact]
    public void Execute_Vssrl_RoundsNearestUp() {
        // vxrm=rnu (0), SEW=e8; shift right 1, round up if lsb=1
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        s.SystemRegisters.Write(CsrFile.Vxrm, 0, RvPrivilege.Machine); // rnu
        SetVReg8(s, 2, [3, 5, 7, 1,]);
        SetGpr(s, 10, 1);                 // shamt = 1
        uint raw = VopVx(0x2A, 1, 2, 10); // vssrl.vx
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        // shifted>>1 + round(lsb): 3→2, 5→3, 7→4, 1→1
        Assert.Equal(2, v1[0]);
        Assert.Equal(3, v1[1]);
        Assert.Equal(4, v1[2]);
        Assert.Equal(1, v1[3]);
    }

    [Fact]
    public void Execute_Vssra_RoundsTruncate() {
        // vxrm=rdn (2), SEW=e8; arithmetic shift right 1, truncate
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        s.SystemRegisters.Write(CsrFile.Vxrm, 2, RvPrivilege.Machine); // rdn
        SetVReg8(
            s, 2, [
                unchecked((byte)-4), unchecked((byte)-1), 8, unchecked((byte)-8),
            ]
        );
        SetGpr(s, 10, 1);                 // shamt = 1
        uint raw = VopVx(0x2B, 1, 2, 10); // vssra.vx
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((sbyte)-2, (sbyte)v1[0]);
        Assert.Equal((sbyte)-1, (sbyte)v1[1]);
        Assert.Equal((sbyte)4, (sbyte)v1[2]);
        Assert.Equal((sbyte)-4, (sbyte)v1[3]);
    }

    // ── V narrowing saturating clip ────────────────────────────────────────────

    [Fact]
    public void Execute_Vnclipu_ClampsToUintMax() {
        // vxrm=rdn, output SEW=e8, input SEW=e16; shift=1
        Rv32ArchState s = MakeState();
        ConfigVl4E16(s); // output e16 → wait, clip output = SEW, input = 2*SEW
        // We want output e8, so configure e8
        ConfigVl4E8(s);
        s.SystemRegisters.Write(CsrFile.Vxrm, 2, RvPrivilege.Machine); // rdn
        // Input is 2*SEW=16-bit values stored in vs2
        SetVReg16(s, 2, [510, 256, 100, 600,]);
        SetGpr(s, 10, 1);                 // shamt = 1
        uint raw = VopVx(0x2E, 1, 2, 10); // vnclipu.wx
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal(255, v1[0]); // 510>>1=255 → no clamp
        Assert.Equal(128, v1[1]); // 256>>1=128
        Assert.Equal(50, v1[2]);  // 100>>1=50
        Assert.Equal(255, v1[3]); // 600>>1=300 → clamp 255
    }

    [Fact]
    public void Execute_Vnclip_SaturatesSignedRange() {
        // vxrm=rdn, output SEW=e8, input SEW=e16; shift=1
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        s.SystemRegisters.Write(CsrFile.Vxrm, 2, RvPrivilege.Machine); // rdn
        // 16-bit signed inputs
        SetVReg16(s, 2, [256, unchecked((ushort)-256), 127, unchecked((ushort)-129),]);
        SetGpr(s, 10, 1);                 // shamt = 1
        uint raw = VopVx(0x2F, 1, 2, 10); // vnclip.wx
        Exec(raw, s).SideEffect!(s);
        byte[] v1 = s.VectorRegisters.Read(1);
        Assert.Equal((sbyte)127, (sbyte)v1[0]);  // 256>>1=128 → sat 127
        Assert.Equal((sbyte)-128, (sbyte)v1[1]); // -256>>1=-128
        Assert.Equal((sbyte)63, (sbyte)v1[2]);   // 127>>1=63
        Assert.Equal((sbyte)-65, (sbyte)v1[3]);  // -129>>1=-65
    }

    [Fact]
    public void OoOE_VectorStore_BlocksYoungerScalarLoad() {
        // vse32.v v1, (a1) followed by lw x5, 0(a1). The scalar load must not
        // issue until the vector store has executed and committed its write.
        // Without the fix, HasPrecedingPendingStore misses vector stores (IsStore=false)
        // and the load can race past, reading stale memory (0 instead of 42).
        var mem = new FlatMemory(0x400);
        var train = new OooeTrain(new Rv32Mechanism(), mem);
        mem.Write(0x100, 42, 4);

        LoadProg(
            mem,
            0x10000513,                                  // addi a0, x0, 0x100
            0x20000593,                                  // addi a1, x0, 0x200
            Vsetvli(12, 0, VectorTests.VtypeiE32M1Tama), // vsetvli a2, x0, e32m1ta
            Vle(1, 10, 6),                               // vle32.v v1, (a0)   → v1[0]=42
            Vse(1, 11, 6),                               // vse32.v v1, (a1)   → mem[0x200]=42
            0x0005A283,                                  // lw x5, 0(a1)       → x5 must = 42
            0x00100073                                   // ebreak
        );
        train.Run();

        ulong x5 = ((Rv32ArchState)train.ArchState).IntegerRegisters.Read(5);
        Assert.Equal(42UL, x5);
    }

    // ── V widening FP helpers ─────────────────────────────────────────────────

    // Read element i of a vector register as a float64.
    private static double DElemF(byte[] v, int i) =>
        BitConverter.Int64BitsToDouble((long)BitConverter.ToUInt64(v, i * 8));

    // Write float64 elements into a vector register (for WV form tests where vs2 is 2×SEW).
    private static void SetVRegF(Rv32ArchState s, int vr, float[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i * 4 + 3 < buf.Length; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(i * 4), elems[i]);
        s.VectorRegisters.Write(vr, buf);
    }

    private static void SetVRegD(Rv32ArchState s, int vr, double[] elems) {
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elems.Length && i * 8 + 7 < buf.Length; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(i * 8), elems[i]);
        s.VectorRegisters.Write(vr, buf);
    }

    // ── V widening FP decode tests ────────────────────────────────────────────

    [Fact]
    public void Decode_VfwaddVv_IsRvVFpWArithVv() {
        uint raw = VopFvv(0x30, 2, 4, 6); // vfwadd.vv v2, v4, v6
        var op = Assert.IsType<RvVFpWArithVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWideArithOp.Add, op.Op);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(6, op.Vs1);
        Assert.False(op.Vs2Wide);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VfwsubWv_IsRvVFpWArithVv_Vs2Wide() {
        uint raw = VopFvv(0x36, 2, 4, 6); // vfwsub.wv v2, v4, v6
        var op = Assert.IsType<RvVFpWArithVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWideArithOp.Sub, op.Op);
        Assert.True(op.Vs2Wide);
    }

    [Fact]
    public void Decode_VfwmulVf_IsRvVFpWArithVf() {
        uint raw = VopFvf(0x38, 2, 4, 10); // vfwmul.vf v2, v4, fa0
        var op = Assert.IsType<RvVFpWArithVf>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWideArithOp.Mul, op.Op);
        Assert.Equal(42, op.Rs1); // 10 + 32 = 42
        Assert.False(op.Vs2Wide);
    }

    [Fact]
    public void Decode_VfwmaccVv_IsRvVFpWMacVv() {
        uint raw = VopFvv(0x3C, 2, 4, 6); // vfwmacc.vv v2, v6, v4 (vs1=6, vs2=4 in encoding)
        var op = Assert.IsType<RvVFpWMacVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWMacOp.Macc, op.Op);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(6, op.Vs1);
    }

    [Fact]
    public void Decode_VfwnmsacVf_IsRvVFpWMacVf() {
        uint raw = VopFvf(0x3F, 2, 4, 10); // vfwnmsac.vf v2, fa0, v4
        var op = Assert.IsType<RvVFpWMacVf>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWMacOp.Nmsac, op.Op);
    }

    [Fact]
    public void Decode_VfwcvtFFromF_IsRvVFpWCvt() {
        uint raw = VopFvUnary(0x12, 2, 4, 12); // vfwcvt.f.f.v v2, v4
        var op = Assert.IsType<RvVFpWCvt>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWCvtOp.FFromF, op.Op);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
    }

    [Fact]
    public void Decode_VfncvtRodFFromF_IsRvVFpNCvt() {
        uint raw = VopFvUnary(0x12, 2, 4, 21); // vfncvt.rod.f.f.w v2, v4
        var op = Assert.IsType<RvVFpNCvt>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpNCvtOp.RodFFromF, op.Op);
    }

    [Fact]
    public void Decode_VfwcvtRtzXuFromF_IsRvVFpWCvt() {
        uint raw = VopFvUnary(0x12, 2, 4, 14); // vfwcvt.rtz.xu.f.v
        var op = Assert.IsType<RvVFpWCvt>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(VFpWCvtOp.RtzXuFromF, op.Op);
    }

    // ── V widening FP execute tests ───────────────────────────────────────────

    [Fact]
    public void Execute_VfwaddVv_WidensAndAdds() {
        // vfwadd.vv: f32 + f32 → f64; at VLEN=128, SEW=32, effectiveVl=2
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [FBitsU(1.0f), FBitsU(2.0f), 0u, 0u,]);   // vs2
        SetVReg(s, 6, [FBitsU(10.0f), FBitsU(20.0f), 0u, 0u,]); // vs1
        uint raw = VopFvv(0x30, 2, 4, 6);                       // vfwadd.vv v2, v4, v6
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(11.0, DElemF(v2, 0));
        Assert.Equal(22.0, DElemF(v2, 1));
    }

    [Fact]
    public void Execute_VfwsubWv_Vs2WideSub() {
        // vfwsub.wv: f64 - f32 → f64
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegD(s, 4, [100.0, 200.0,]);                      // vs2 already f64
        SetVReg(s, 6, [FBitsU(1.0f), FBitsU(2.0f), 0u, 0u,]); // vs1 f32
        uint raw = VopFvv(0x36, 2, 4, 6);                     // vfwsub.wv v2, v4, v6
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(99.0, DElemF(v2, 0));
        Assert.Equal(198.0, DElemF(v2, 1));
    }

    [Fact]
    public void Execute_VfwmulVv_WidensAndMultiplies() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [FBitsU(3.0f), FBitsU(4.0f), 0u, 0u,]);
        SetVReg(s, 6, [FBitsU(5.0f), FBitsU(6.0f), 0u, 0u,]);
        uint raw = VopFvv(0x38, 2, 4, 6); // vfwmul.vv v2, v4, v6
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(15.0, DElemF(v2, 0));
        Assert.Equal(24.0, DElemF(v2, 1));
    }

    [Fact]
    public void Execute_VfwmaccVv_Accumulates() {
        // vfwmacc: vd += vs2*vs1 (f32 inputs, f64 accumulator)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegD(s, 2, [1000.0, 2000.0,]);                    // vd accumulator
        SetVReg(s, 4, [FBitsU(3.0f), FBitsU(4.0f), 0u, 0u,]); // vs2
        SetVReg(s, 6, [FBitsU(5.0f), FBitsU(6.0f), 0u, 0u,]); // vs1
        uint raw = VopFvv(0x3C, 2, 4, 6);                     // vfwmacc.vv v2, v6, v4 (vs2=4, vs1=6 in encoding)
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(1015.0, DElemF(v2, 0)); // 1000 + 3*5
        Assert.Equal(2024.0, DElemF(v2, 1)); // 2000 + 4*6
    }

    [Fact]
    public void Execute_VfwnmaccVv_NegProductNegAcc() {
        // vfwnmacc: vd = -(vs2*vs1) - vd
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegD(s, 2, [10.0, 20.0,]);
        SetVReg(s, 4, [FBitsU(3.0f), FBitsU(4.0f), 0u, 0u,]);
        SetVReg(s, 6, [FBitsU(2.0f), FBitsU(5.0f), 0u, 0u,]);
        uint raw = VopFvv(0x3D, 2, 4, 6); // vfwnmacc.vv
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(-16.0, DElemF(v2, 0)); // -(3*2) - 10 = -16
        Assert.Equal(-40.0, DElemF(v2, 1)); // -(4*5) - 20 = -40
    }

    [Fact]
    public void Execute_VfwcvtFFromF_F32ToF64() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [FBitsU(1.5f), FBitsU(-3.14f), 0u, 0u,]);
        uint raw = VopFvUnary(0x12, 2, 4, 12); // vfwcvt.f.f.v v2, v4
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(1.5, DElemF(v2, 0), 10);
        Assert.Equal(-3.14f, DElemF(v2, 1), 10);
    }

    [Fact]
    public void Execute_VfwcvtFFromX_I32ToF64() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [unchecked((uint)-42), 1000, 0u, 0u,]);
        uint raw = VopFvUnary(0x12, 2, 4, 11); // vfwcvt.f.x.v v2, v4
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(-42.0, DElemF(v2, 0));
        Assert.Equal(1000.0, DElemF(v2, 1));
    }

    [Fact]
    public void Execute_VfwcvtXuFromF_SaturatesOnOverflow() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 4, [FBitsU(-1.0f), FBitsU(float.PositiveInfinity), 0u, 0u,]);
        uint raw = VopFvUnary(0x12, 2, 4, 8); // vfwcvt.xu.f.v
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(0UL, BitConverter.ToUInt64(v2, 0));            // negative → 0
        Assert.Equal(ulong.MaxValue, BitConverter.ToUInt64(v2, 8)); // +Inf → MaxValue
    }

    [Fact]
    public void Execute_VfncvtFFromF_F64ToF32() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegD(s, 4, [1.5, -2.5,]);
        uint raw = VopFvUnary(0x12, 2, 4, 20); // vfncvt.f.f.w v2, v4
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(1.5f, FElemF(v2, 0));
        Assert.Equal(-2.5f, FElemF(v2, 1));
    }

    [Fact]
    public void Execute_VfncvtXFromF_F64ToI32_Saturates() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegD(s, 4, [double.PositiveInfinity, -99.9,]);
        uint raw = VopFvUnary(0x12, 2, 4, 17); // vfncvt.x.f.w v2, v4
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal((uint)int.MaxValue, BitConverter.ToUInt32(v2, 0));   // +Inf → MaxValue
        Assert.Equal(unchecked((uint)-99), BitConverter.ToUInt32(v2, 4)); // truncate
    }

    [Fact]
    public void Execute_VfncvtRodFFromF_RoundToOdd() {
        // round-to-odd: f64 → f32, inexact → force mantissa LSB=1
        // 1.0 + 2^-24 is exactly representable as f64 but rounds to 1.0 in f32 (even).
        // rod rounds it to the next odd mantissa instead.
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        const double exact = 1.0;                      // exact in f32 → no rounding
        double inexact = 1.0 + Math.Pow(2, -24) * 0.5; // mid-point, rounds toward odd
        SetVRegD(s, 4, [exact, inexact,]);
        uint raw = VopFvUnary(0x12, 2, 4, 21); // vfncvt.rod.f.f.w v2, v4
        Exec(raw, s).SideEffect!(s);
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(1.0f, FElemF(v2, 0)); // exact → unchanged
        // inexact → mantissa LSB forced to 1
        int resultBits = BitConverter.SingleToInt32Bits(FElemF(v2, 1));
        Assert.True((resultBits & 1) == 1, "round-to-odd: mantissa LSB must be 1 for inexact result");
    }

    // ── Whole-register load/store (vl{N}r.v / vs{N}r.v) ─────────────────────

    [Fact]
    public void Decode_Vl1rV_IsRvVlrV() {
        const uint raw = 0x02850107; // vl1r.v v2, (a0)
        var op = Assert.IsType<RvVlrV>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(1, op.NumRegs);
        Assert.Equal(2, op.Vd);
        Assert.Equal(10, op.Rs1);
    }

    [Fact]
    public void Decode_Vl4rV_IsRvVlrV_NumRegs4() {
        const uint raw = 0x62850207; // vl4r.v v4, (a0)
        var op = Assert.IsType<RvVlrV>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(4, op.NumRegs);
        Assert.Equal(4, op.Vd);
    }

    [Fact]
    public void Decode_Vs1rV_IsRvVsrV() {
        const uint raw = 0x02850127; // vs1r.v v2, (a0)
        var op = Assert.IsType<RvVsrV>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(1, op.NumRegs);
        Assert.Equal(2, op.Vs3);
        Assert.Equal(10, op.Rs1);
    }

    [Fact]
    public void Decode_Vs2rV_IsRvVsrV_NumRegs2() {
        const uint raw = 0x22850127; // vs2r.v v2, (a0)
        var op = Assert.IsType<RvVsrV>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.NumRegs);
    }

    [Fact]
    public void Execute_Vl1rV_LoadsEntireRegister() {
        Rv32ArchState s = MakeState();
        SetGpr(s, 10, 0x1000);
        // Write 16 bytes of distinctive data to memory
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) _mem.Write(0x1000 + (ulong)b, (ulong)(b + 1), 1);

        Exec(0x02850107, s).SideEffect!(s); // vl1r.v v2, (a0)

        byte[] v2 = s.VectorRegisters.Read(2);
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) Assert.Equal(b + 1, v2[b]);
    }

    [Fact]
    public void Execute_Vl2rV_LoadsTwoRegisters() {
        Rv32ArchState s = MakeState();
        SetGpr(s, 10, 0x2000);
        const int total = 2 * VectorRegisterFile.VLenB;
        for (var b = 0; b < total; b++) _mem.Write(0x2000 + (ulong)b, (ulong)(b + 10), 1);

        Exec(0x22850107, s).SideEffect!(s); // vl2r.v v2, (a0)

        byte[] v2 = s.VectorRegisters.Read(2);
        byte[] v3 = s.VectorRegisters.Read(3);
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) Assert.Equal(b + 10, v2[b]);
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) Assert.Equal(VectorRegisterFile.VLenB + b + 10, v3[b]);
    }

    [Fact]
    public void Execute_Vs1rV_StoresEntireRegister() {
        Rv32ArchState s = MakeState();
        SetGpr(s, 10, 0x3000);
        var buf = new byte[VectorRegisterFile.VLenB];
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) buf[b] = (byte)(b + 0xA0);
        s.VectorRegisters.Write(2, buf);

        Exec(0x02850127, s); // vs1r.v v2, (a0) — no SideEffect (store is immediate)

        for (var b = 0; b < VectorRegisterFile.VLenB; b++)
            Assert.Equal((ulong)(b + 0xA0), _mem.Read(0x3000 + (ulong)b, 1));
    }

    [Fact]
    public void Execute_Vs2rV_StoresTwoRegisters() {
        Rv32ArchState s = MakeState();
        SetGpr(s, 10, 0x4000);
        var buf2 = new byte[VectorRegisterFile.VLenB];
        var buf3 = new byte[VectorRegisterFile.VLenB];
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) {
            buf2[b] = (byte)(b + 1);
            buf3[b] = (byte)(b + 17);
        }

        s.VectorRegisters.Write(2, buf2);
        s.VectorRegisters.Write(3, buf3);

        Exec(0x22850127, s); // vs2r.v v2, (a0)

        for (var b = 0; b < VectorRegisterFile.VLenB; b++)
            Assert.Equal((ulong)(b + 1), _mem.Read(0x4000 + (ulong)b, 1));
        for (var b = 0; b < VectorRegisterFile.VLenB; b++)
            Assert.Equal((ulong)(b + 17), _mem.Read(0x4000 + (ulong)(VectorRegisterFile.VLenB + b), 1));
    }

    [Fact]
    public void Execute_Vl1rVs1r_RoundTrip() {
        // Store a register, load it back into a different register — bytes must match.
        Rv32ArchState s = MakeState();
        SetGpr(s, 10, 0x5000);
        SetGpr(s, 11, 0x5000);
        var original = new byte[VectorRegisterFile.VLenB];
        for (var b = 0; b < VectorRegisterFile.VLenB; b++) original[b] = (byte)(0xFF - b);
        s.VectorRegisters.Write(4, original);

        // vs1r.v v4, (a0) — store v4 to 0x5000
        const uint vs1R = 0x02850227; // vs1r.v v4, (a0): vd=vs3=4
        Exec(vs1R, s);

        // vl1r.v v6, (a1) — load 0x5000 into v6
        const uint vl1R = 0x02858307; // vl1r.v v6, (a1=x11): vd=6, rs1=11
        Exec(vl1R, s).SideEffect!(s);

        byte[] v6 = s.VectorRegisters.Read(6);
        Assert.Equal(original, v6);
    }

    // ── Segment loads / stores ────────────────────────────────────────────────

    [Fact]
    public void Decode_Vlseg3e32() {
        // vlseg3e32.v v2, (a0)  — nf=2 (3 fields), sew=32, vd=2, rs1=10, unmasked
        uint raw = Vlseg(3, 2, 10, 6);
        Assert.Equal(0x42056107u, raw);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVlsegVv>(i.Payload);
        var op = (RvVlsegVv)i.Payload!;
        Assert.Equal(3, op.NumFields);
        Assert.Equal(2, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(32, op.Sew);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_Vsseg3e32() {
        // vsseg3e32.v v2, (a0)  — nf=2 (3 fields), sew=32, vs3=2, rs1=10, unmasked
        uint raw = Vsseg(3, 2, 10, 6);
        Assert.Equal(0x42056127u, raw);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVssegVv>(i.Payload);
        var op = (RvVssegVv)i.Payload!;
        Assert.Equal(3, op.NumFields);
        Assert.Equal(2, op.Vs3);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(32, op.Sew);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Execute_Vlseg2e8_LoadsFields() {
        // vlseg2e8.v v4, (a0): 2 fields, e8, vl=4
        // Memory: [f0e0, f1e0, f0e1, f1e1, f0e2, f1e2, f0e3, f1e3]
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x1000);
        _mem.Write(0x1000, 10, 1);
        _mem.Write(0x1001, 11, 1);
        _mem.Write(0x1002, 20, 1);
        _mem.Write(0x1003, 21, 1);
        _mem.Write(0x1004, 30, 1);
        _mem.Write(0x1005, 31, 1);
        _mem.Write(0x1006, 40, 1);
        _mem.Write(0x1007, 41, 1);

        Exec(Vlseg(2, 4, 10, 0), s).SideEffect!(s); // vlseg2e8.v v4, (a0)

        byte[] v4 = s.VectorRegisters.Read(4);
        byte[] v5 = s.VectorRegisters.Read(5);
        Assert.Equal(10, v4[0]);
        Assert.Equal(20, v4[1]);
        Assert.Equal(30, v4[2]);
        Assert.Equal(40, v4[3]);
        Assert.Equal(11, v5[0]);
        Assert.Equal(21, v5[1]);
        Assert.Equal(31, v5[2]);
        Assert.Equal(41, v5[3]);
    }

    [Fact]
    public void Execute_Vsseg3e32_StoresFields() {
        // vsseg3e32.v v2, (a0): 3 fields, e32, vl=2
        // v2=field0, v3=field1, v4=field2; memory layout: [f0e0,f1e0,f2e0, f0e1,f1e1,f2e1]
        Rv32ArchState s = MakeState();
        Exec(Vsetivli(10, 2, VectorTests.VtypeiE32M1Tama), s); // vl=2, e32
        SetVReg(s, 2, [100u, 200u, 0u, 0u,]);
        SetVReg(s, 3, [300u, 400u, 0u, 0u,]);
        SetVReg(s, 4, [500u, 600u, 0u, 0u,]);
        SetGpr(s, 10, 0x2000);

        Exec(Vsseg(3, 2, 10, 6), s); // vsseg3e32.v v2, (a0)

        Assert.Equal(100UL, _mem.Read(0x2000, 4)); // elem 0, field 0
        Assert.Equal(300UL, _mem.Read(0x2004, 4)); // elem 0, field 1
        Assert.Equal(500UL, _mem.Read(0x2008, 4)); // elem 0, field 2
        Assert.Equal(200UL, _mem.Read(0x200C, 4)); // elem 1, field 0
        Assert.Equal(400UL, _mem.Read(0x2010, 4)); // elem 1, field 1
        Assert.Equal(600UL, _mem.Read(0x2014, 4)); // elem 1, field 2
    }

    [Fact]
    public void Execute_Vlseg2e8_MaskedSkipsInactiveElements() {
        // vlseg2e8.v v4, (a0), v0.t — mask disables elements 1 and 3
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x3000);
        // Mask: bits 0 and 2 set → elements 0 and 2 active
        var mask = new byte[VectorRegisterFile.VLenB];
        mask[0] = 0b0101; // bits 0,2
        s.VectorRegisters.Write(0, mask);

        _mem.Write(0x3000, 0xAA, 1); // elem 0, field 0
        _mem.Write(0x3001, 0xBB, 1); // elem 0, field 1
        // elem 1 inactive — write sentinel values that must not appear
        _mem.Write(0x3002, 0xFF, 1);
        _mem.Write(0x3003, 0xFF, 1);
        _mem.Write(0x3004, 0xCC, 1); // elem 2, field 0
        _mem.Write(0x3005, 0xDD, 1); // elem 2, field 1
        // elem 3 inactive
        _mem.Write(0x3006, 0xFF, 1);
        _mem.Write(0x3007, 0xFF, 1);

        uint raw = Vlseg(2, 4, 10, 0, true); // vlseg2e8.v v4, (a0), v0.t
        Exec(raw, s).SideEffect!(s);

        byte[] v4 = s.VectorRegisters.Read(4);
        byte[] v5 = s.VectorRegisters.Read(5);
        Assert.Equal(0xAA, v4[0]); // elem 0 active
        Assert.Equal(0x00, v4[1]); // elem 1 inactive → zero-init
        Assert.Equal(0xCC, v4[2]); // elem 2 active
        Assert.Equal(0x00, v4[3]); // elem 3 inactive → zero-init
        Assert.Equal(0xBB, v5[0]);
        Assert.Equal(0x00, v5[1]);
        Assert.Equal(0xDD, v5[2]);
        Assert.Equal(0x00, v5[3]);
    }

    // ── vleFF (fault-only-first) ───────────────────────────────────────────────

    [Fact]
    public void Decode_Vle32ff_IsRvVleFF() {
        // vle32ff.v v2, (a0): lumop=0x10, funct3=6(e32), mop=0
        const uint raw = (0 << 29) | (1 << 25) | (0x10 << 20) | (10 << 15) | (6 << 12) | (2 << 7) | 0x07;
        var op = Assert.IsType<RvVleFf>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(32, op.Sew);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Execute_Vle8ff_LoadsLikeVle() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x1000);
        _mem.Write(0x1000, 0xAA, 1);
        _mem.Write(0x1001, 0xBB, 1);
        _mem.Write(0x1002, 0xCC, 1);
        _mem.Write(0x1003, 0xDD, 1);
        const uint raw = (1 << 25) | (0x10 << 20) | (10 << 15) | (0 << 12) | (4 << 7) | 0x07; // vle8ff.v v4,(a0)
        Exec(raw, s).SideEffect!(s);
        byte[] v4 = s.VectorRegisters.Read(4);
        Assert.Equal(0xAA, v4[0]);
        Assert.Equal(0xBB, v4[1]);
        Assert.Equal(0xCC, v4[2]);
        Assert.Equal(0xDD, v4[3]);
    }

    // ── vzext / vsext ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Vzext_vf2_IsRvVExt() {
        // vzext.vf2: OPMVV, funct6=0x12, vs1=6 → factor=2, unsigned
        var op = Assert.IsType<RvVExt>(((RvInstruction)_dec.Decode(0, VopMvv(0x12, 2, 4, 6))).Payload);
        Assert.False(op.Signed);
        Assert.Equal(2, op.Factor);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
    }

    [Fact]
    public void Decode_Vsext_vf4_IsRvVExt() {
        // vsext.vf4: vs1=5 → factor=4, signed
        var op = Assert.IsType<RvVExt>(((RvInstruction)_dec.Decode(0, VopMvv(0x12, 2, 4, 5))).Payload);
        Assert.True(op.Signed);
        Assert.Equal(4, op.Factor);
    }

    [Fact]
    public void Execute_Vzext_vf2_E32_ZeroExtends16to32() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s); // SEW=32
        SetVReg16(s, 4, [0xFFFE, 0x0001, 0x8000, 0x7FFF,]);
        Exec(VopMvv(0x12, 2, 4, 6), s).SideEffect!(s); // vzext.vf2 v2, v4
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(0xFFFEu, BitConverter.ToUInt32(v2, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(v2, 4));
        Assert.Equal(0x8000u, BitConverter.ToUInt32(v2, 8));
        Assert.Equal(0x7FFFu, BitConverter.ToUInt32(v2, 12));
    }

    [Fact]
    public void Execute_Vsext_vf2_E32_SignExtends16to32() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg16(s, 4, [0x8000, 0x7FFF, 0xFFFF, 0x0001,]);
        Exec(VopMvv(0x12, 2, 4, 7), s).SideEffect!(s); // vsext.vf2 v2, v4
        byte[] v2 = s.VectorRegisters.Read(2);
        Assert.Equal(unchecked((uint)-32768), BitConverter.ToUInt32(v2, 0));
        Assert.Equal(0x7FFFu, BitConverter.ToUInt32(v2, 4));
        Assert.Equal(unchecked((uint)-1), BitConverter.ToUInt32(v2, 8));
        Assert.Equal(1u, BitConverter.ToUInt32(v2, 12));
    }

    // ── vaaddu / vaadd / vasubu / vasub ──────────────────────────────────────

    [Fact]
    public void Decode_Vaaddu_IsRvVAvgVv() {
        // vaaddu.vv: OPMVV funct6=0x08
        var op = Assert.IsType<RvVAvgVv>(((RvInstruction)_dec.Decode(0, VopMvv(0x08, 2, 4, 6))).Payload);
        Assert.Equal(VAvgOp.Addu, op.Op);
        Assert.Equal(2, op.Vd);
    }

    [Fact]
    public void Decode_Vasub_Vx_IsRvVAvgVx() {
        // vasub.vx: OPMVX funct6=0x0B
        var op = Assert.IsType<RvVAvgVx>(((RvInstruction)_dec.Decode(0, VopMvx(0x0B, 2, 4, 10))).Payload);
        Assert.Equal(VAvgOp.Sub, op.Op);
    }

    [Fact]
    public void Execute_Vaaddu_E32_RoundsTruncate() {
        // vaaddu.vv with vxrm=2 (truncate): (5 + 2) >> 1 = 3 (no round)
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [5u, 10u, 3u, 7u,]);
        SetVReg(s, 4, [2u, 1u, 2u, 2u,]);
        // vxrm=2 (truncate) — already the default (CSR=0 is RNU, but easiest is to just test RNU)
        Exec(VopMvv(0x08, 6, 2, 4), s).SideEffect!(s); // vaaddu.vv v6, v2, v4
        byte[] v6 = s.VectorRegisters.Read(6);
        // RNU (vxrm=0): round up when LSB of sum is 1
        // (5+2)=7→LSB=1→(7>>1)+1=4; (10+1)=11→LSB=1→6; (3+2)=5→LSB=1→3; (7+2)=9→LSB=1→5
        Assert.Equal(4u, BitConverter.ToUInt32(v6, 0));
        Assert.Equal(6u, BitConverter.ToUInt32(v6, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(v6, 8));
        Assert.Equal(5u, BitConverter.ToUInt32(v6, 12));
    }

    [Fact]
    public void Execute_Vasubu_E32_UnsignedAverageSub() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [10u, 4u, 0u, 6u,]);
        SetVReg(s, 4, [2u, 2u, 0u, 0u,]);
        Exec(VopMvv(0x0A, 6, 2, 4), s).SideEffect!(s); // vasubu.vv v6, v2, v4 (RNU)
        byte[] v6 = s.VectorRegisters.Read(6);
        // (10-2)=8→8>>1=4 (even, no round); (4-2)=2→1; (0-0)=0; (6-0)=6→3
        Assert.Equal(4u, BitConverter.ToUInt32(v6, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(v6, 4));
        Assert.Equal(0u, BitConverter.ToUInt32(v6, 8));
        Assert.Equal(3u, BitConverter.ToUInt32(v6, 12));
    }

    // ── vrsub ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_VrsubVx_IsRsub() {
        // vrsub.vx: funct6=3, funct3=4(OPIVX)
        var op = Assert.IsType<RvVIntAluVx>(((RvInstruction)_dec.Decode(0, VopVx(3, 2, 4, 10))).Payload);
        Assert.Equal(VIntOp.Rsub, op.Op);
    }

    [Fact]
    public void Decode_VrsubVi_IsRsub() {
        // vrsub.vi: funct6=3, funct3=3(OPIVI)
        var op = Assert.IsType<RvVIntAluVi>(((RvInstruction)_dec.Decode(0, VopVi(3, 2, 4, 5))).Payload);
        Assert.Equal(VIntOp.Rsub, op.Op);
        Assert.Equal(5, op.Imm);
    }

    [Fact]
    public void Execute_VrsubVx_ComputesScalarMinusVector() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetGpr(s, 5, 10);
        Exec(VopVx(3, 4, 2, 5), s).SideEffect!(s); // vrsub.vx v4, v2, x5  → v4[i] = 10 - v2[i]
        byte[] v4 = s.VectorRegisters.Read(4);
        Assert.Equal(9u, BitConverter.ToUInt32(v4, 0));
        Assert.Equal(8u, BitConverter.ToUInt32(v4, 4));
        Assert.Equal(7u, BitConverter.ToUInt32(v4, 8));
        Assert.Equal(6u, BitConverter.ToUInt32(v4, 12));
    }

    [Fact]
    public void Execute_VrsubVi_ComputesImmMinusVector() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [0u, 1u, 2u, 3u,]);
        Exec(VopVi(3, 4, 2, 5), s).SideEffect!(s); // vrsub.vi v4, v2, 5 → v4[i] = 5 - v2[i]
        byte[] v4 = s.VectorRegisters.Read(4);
        Assert.Equal(5u, BitConverter.ToUInt32(v4, 0));
        Assert.Equal(4u, BitConverter.ToUInt32(v4, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(v4, 8));
        Assert.Equal(2u, BitConverter.ToUInt32(v4, 12));
    }

    // ── vfcvt.rtz.* ──────────────────────────────────────────────────────────

    [Fact]
    public void Decode_VfcvtRtzXuFV_IsRvVFpCvt() {
        // vfcvt.rtz.xu.f.v: OPFVV funct6=0x12, vs1=6
        var op = Assert.IsType<RvVFpCvt>(((RvInstruction)_dec.Decode(0, VopFvUnary(0x12, 2, 4, 6))).Payload);
        Assert.Equal(VFpCvtOp.RtzXuFromF, op.Op);
    }

    [Fact]
    public void Decode_VfcvtRtzXFV_IsRvVFpCvt() {
        // vfcvt.rtz.x.f.v: vs1=7
        var op = Assert.IsType<RvVFpCvt>(((RvInstruction)_dec.Decode(0, VopFvUnary(0x12, 2, 4, 7))).Payload);
        Assert.Equal(VFpCvtOp.RtzXFromF, op.Op);
    }

    [Fact]
    public void Execute_VfcvtRtzXuFV_TruncatesFloat() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegF(s, 2, [3.9f, 0.1f, 100.9f, -1.0f,]);
        Exec(VopFvUnary(0x12, 4, 2, 6), s).SideEffect!(s); // vfcvt.rtz.xu.f.v v4, v2
        byte[] v4 = s.VectorRegisters.Read(4);
        Assert.Equal(3u, BitConverter.ToUInt32(v4, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(v4, 4));
        Assert.Equal(100u, BitConverter.ToUInt32(v4, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(v4, 12)); // saturate negative to 0
    }

    // ── vfwredusum / vfwredosum ───────────────────────────────────────────────

    [Fact]
    public void Decode_Vfwredusum_IsRvVFpWideRedVs() {
        // vfwredusum.vs: OPFVV funct6=0x31
        var op = Assert.IsType<RvVFpWideRedVs>(((RvInstruction)_dec.Decode(0, VopFvv(0x31, 2, 4, 6))).Payload);
        Assert.False(op.Ordered);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs2);
        Assert.Equal(6, op.Vs1);
    }

    [Fact]
    public void Decode_Vfwredosum_IsRvVFpWideRedVs_Ordered() {
        var op = Assert.IsType<RvVFpWideRedVs>(((RvInstruction)_dec.Decode(0, VopFvv(0x33, 2, 4, 6))).Payload);
        Assert.True(op.Ordered);
    }

    [Fact]
    public void Execute_Vfwredusum_SumsF32IntoF64() {
        Rv32ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVRegF(s, 2, [1.0f, 2.0f, 3.0f, 4.0f,]);
        SetVRegD(s, 6, [0.0,]);                        // seed = 0.0 in f64
        Exec(VopFvv(0x31, 4, 2, 6), s).SideEffect!(s); // vfwredusum.vs v4, v2, v6
        byte[] v4 = s.VectorRegisters.Read(4);
        var result = BitConverter.ToDouble(v4, 0);
        Assert.Equal(10.0, result, 1e-9);
    }

    // ── strided segment load / store ──────────────────────────────────────────

    [Fact]
    public void Decode_Vlsseg2e32_IsRvVlssegVv() {
        // vlsseg2e32.v v2, (a0), a1: mop=2(bits[27:26]=10), nf=1(2 fields), funct3=6(e32), rs2=11
        const uint raw = (1 << 29) | (2 << 26) | (1 << 25) | (11 << 20) | (10 << 15) | (6 << 12) | (2 << 7) | 0x07;
        var op = Assert.IsType<RvVlssegVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.NumFields);
        Assert.Equal(2, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(11, op.Rs2);
        Assert.Equal(32, op.Sew);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_Vssseg2e32_IsRvVsssegVv() {
        // vssseg2e32.v v2, (a0), a1: mop=2(bits[27:26]=10), nf=1, funct3=6, rs2=11
        const uint raw = (1 << 29) | (2 << 26) | (1 << 25) | (11 << 20) | (10 << 15) | (6 << 12) | (2 << 7) | 0x27;
        var op = Assert.IsType<RvVsssegVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.NumFields);
        Assert.Equal(2, op.Vs3);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(11, op.Rs2);
        Assert.Equal(32, op.Sew);
    }

    [Fact]
    public void Execute_Vlsseg2e8_StrideLoadsFields() {
        // vlsseg2e8.v v4, (a0), a1 — stride=4, 2 fields, 4 elements
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x1000);
        SetGpr(s, 11, 4); // stride=4
        // Group 0 at 0x1000: f0=0xAA, f1=0xBB
        _mem.Write(0x1000, 0xAA, 1);
        _mem.Write(0x1001, 0xBB, 1);
        // Group 1 at 0x1004: f0=0xCC, f1=0xDD
        _mem.Write(0x1004, 0xCC, 1);
        _mem.Write(0x1005, 0xDD, 1);
        // Group 2 at 0x1008: f0=0xEE, f1=0xFF
        _mem.Write(0x1008, 0xEE, 1);
        _mem.Write(0x1009, 0xFF, 1);
        // Group 3 at 0x100C: f0=0x11, f1=0x22
        _mem.Write(0x100C, 0x11, 1);
        _mem.Write(0x100D, 0x22, 1);

        // vlsseg2e8.v v4, (a0), a1: nf=1→2 fields, mop=2(bits[27:26]=10), funct3=0(e8), rs2=11
        const uint raw = (1 << 29) | (2 << 26) | (1 << 25) | (11 << 20) | (10 << 15) | (0 << 12) | (4 << 7) | 0x07;
        Exec(raw, s).SideEffect!(s);

        byte[] v4 = s.VectorRegisters.Read(4);
        byte[] v5 = s.VectorRegisters.Read(5);
        Assert.Equal(0xAA, v4[0]);
        Assert.Equal(0xCC, v4[1]);
        Assert.Equal(0xEE, v4[2]);
        Assert.Equal(0x11, v4[3]);
        Assert.Equal(0xBB, v5[0]);
        Assert.Equal(0xDD, v5[1]);
        Assert.Equal(0xFF, v5[2]);
        Assert.Equal(0x22, v5[3]);
    }

    [Fact]
    public void Execute_Vssseg2e8_StrideStoresFields() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x2000);
        SetGpr(s, 11, 4);                          // stride=4
        SetVReg8(s, 4, [0xAA, 0xCC, 0xEE, 0x11,]); // field 0
        SetVReg8(s, 5, [0xBB, 0xDD, 0xFF, 0x22,]); // field 1

        const uint raw = (1 << 29) | (2 << 26) | (1 << 25) | (11 << 20) | (10 << 15) | (0 << 12) | (4 << 7) | 0x27;
        Exec(raw, s);

        Assert.Equal(0xAAUL, _mem.Read(0x2000, 1));
        Assert.Equal(0xBBUL, _mem.Read(0x2001, 1));
        Assert.Equal(0xCCUL, _mem.Read(0x2004, 1));
        Assert.Equal(0xDDUL, _mem.Read(0x2005, 1));
        Assert.Equal(0xEEUL, _mem.Read(0x2008, 1));
        Assert.Equal(0xFFUL, _mem.Read(0x2009, 1));
    }

    // ── indexed segment load / store ──────────────────────────────────────────

    [Fact]
    public void Decode_Vluxseg2ei8_IsRvVlxsegVv() {
        // vluxseg2ei8.v v2, (a0), v6: mop=1, nf=1(2 fields), funct3=0(e8-idx), rs2=vs2=6
        const uint raw = (1 << 29) | (1 << 26) | (1 << 25) | (6 << 20) | (10 << 15) | (0 << 12) | (2 << 7) | 0x07;
        var op = Assert.IsType<RvVlxsegVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.NumFields);
        Assert.Equal(2, op.Vd);
        Assert.Equal(10, op.Rs1);
        Assert.Equal(6, op.Vs2);
        Assert.Equal(8, op.IndexSew);
        Assert.False(op.Ordered);
    }

    [Fact]
    public void Decode_Vsoxseg2ei8_IsRvVsxsegVv_Ordered() {
        const uint raw = (1 << 29) | (3 << 26) | (1 << 25) | (6 << 20) | (10 << 15) | (0 << 12) | (2 << 7) | 0x27;
        var op = Assert.IsType<RvVsxsegVv>(((RvInstruction)_dec.Decode(0, raw)).Payload);
        Assert.Equal(2, op.NumFields);
        Assert.True(op.Ordered);
    }

    [Fact]
    public void Execute_Vluxseg2ei8_IndexedLoadsFields() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x1000);
        // Index vector v8: [0x00, 0x04, 0x08, 0x0C]
        SetVReg8(s, 8, [0x00, 0x04, 0x08, 0x0C,]);
        _mem.Write(0x1000, 0xA0, 1);
        _mem.Write(0x1001, 0xA1, 1); // idx=0: f0,f1
        _mem.Write(0x1004, 0xB0, 1);
        _mem.Write(0x1005, 0xB1, 1); // idx=4: f0,f1
        _mem.Write(0x1008, 0xC0, 1);
        _mem.Write(0x1009, 0xC1, 1); // idx=8: f0,f1
        _mem.Write(0x100C, 0xD0, 1);
        _mem.Write(0x100D, 0xD1, 1); // idx=0xC: f0,f1

        const uint raw = (1 << 29) | (1 << 26) | (1 << 25) | (8 << 20) | (10 << 15) | (0 << 12) | (4 << 7) | 0x07;
        Exec(raw, s).SideEffect!(s);

        byte[] v4 = s.VectorRegisters.Read(4);
        byte[] v5 = s.VectorRegisters.Read(5);
        Assert.Equal(0xA0, v4[0]);
        Assert.Equal(0xB0, v4[1]);
        Assert.Equal(0xC0, v4[2]);
        Assert.Equal(0xD0, v4[3]);
        Assert.Equal(0xA1, v5[0]);
        Assert.Equal(0xB1, v5[1]);
        Assert.Equal(0xC1, v5[2]);
        Assert.Equal(0xD1, v5[3]);
    }

    [Fact]
    public void Execute_Vsuxseg2ei8_IndexedStoresFields() {
        Rv32ArchState s = MakeState();
        ConfigVl4E8(s);
        SetGpr(s, 10, 0x2000);
        SetVReg8(s, 8, [0x00, 0x04, 0x08, 0x0C,]); // index vector
        SetVReg8(s, 4, [0xA0, 0xB0, 0xC0, 0xD0,]); // field 0 data
        SetVReg8(s, 5, [0xA1, 0xB1, 0xC1, 0xD1,]); // field 1 data

        const uint raw = (1 << 29) | (1 << 26) | (1 << 25) | (8 << 20) | (10 << 15) | (0 << 12) | (4 << 7) | 0x27;
        Exec(raw, s);

        Assert.Equal(0xA0UL, _mem.Read(0x2000, 1));
        Assert.Equal(0xA1UL, _mem.Read(0x2001, 1));
        Assert.Equal(0xB0UL, _mem.Read(0x2004, 1));
        Assert.Equal(0xB1UL, _mem.Read(0x2005, 1));
        Assert.Equal(0xC0UL, _mem.Read(0x2008, 1));
        Assert.Equal(0xD0UL, _mem.Read(0x200C, 1));
    }
}