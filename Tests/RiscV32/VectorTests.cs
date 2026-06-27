using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

// VectorRegisters is public on Rv32ArchState; CsrFile constants are public statics.
// CSR values are read via the public ISystemRegisters.Read() path.

namespace Tests.RiscV;

/// <summary>
/// Unit tests for the RISC-V V extension (decoder + executor).
/// All raw encodings are hand-assembled using the V spec 1.0 encoding tables.
/// </summary>
public class VectorTests {
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

    // vtypei for e32, m1, ta, ma = vsew=2, vlmul=0, vta=1, vma=1 → 0xC2
    private const int VtypeiE32M1Tama = (1 << 7) | (1 << 6) | 2;

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

    private ExecuteResult Exec(uint raw, Rv32ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // Helper: configure vl=4, sew=32 via vsetvli
    private void ConfigVl4E32(Rv32ArchState s) {
        uint raw = Vsetvli(10, 0, VectorTests.VtypeiE32M1Tama); // vsetvli a0, x0, e32,m1,ta,ma
        Exec(raw, s);                                           // side-effect: updates vl and vtype in state
    }

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
}