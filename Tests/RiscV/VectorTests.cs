using Mechanism;
using RiscV.Decode;
using RiscV.Execute;
using RiscV.Memory;
using RiscV.Registers;
using RiscV.State;

// VectorRegisters is public on RvArchState; CsrFile constants are public statics.
// CSR values are read via the public ISystemRegisters.Read() path.

namespace Tests.RiscV;

/// <summary>
/// Unit tests for the RISC-V V extension (decoder + executor).
/// All raw encodings are hand-assembled using the V spec 1.0 encoding tables.
/// </summary>
public class VectorTests {
    private readonly RvDecoder _dec = new();
    private readonly RvExecutor _exe = new();
    private readonly FlatMemory _mem = new(0x10000);

    // ── Encoding helpers ──────────────────────────────────────────────────────

    // vsetvli rd, rs1, vtypei  (bit31=0, bits[30:20]=vtypei, funct3=7, opcode=0x57)
    private static uint Vsetvli(int rd, int rs1, int vtypei) =>
        (uint)((vtypei << 20) | (rs1 << 15) | (7 << 12) | (rd << 7) | 0x57);

    // vsetivli rd, zimm, vtypei  (bits[31:30]=11, bits[29:20]=vtypei, bits[19:15]=zimm)
    private static uint Vsetivli(int rd, int zimm, int vtypei) =>
        0xC0000000u | (uint)((vtypei << 20) | (zimm << 15) | (7 << 12) | (rd << 7) | 0x57);

    // vadd/vsub/etc.vv vd, vs2, vs1  (funct6, vm=1, funct3=0, opcode=0x57)
    private static uint VopVV(int funct6, int vd, int vs2, int vs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | (0 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vadd/etc.vx vd, vs2, rs1  (funct3=4)
    private static uint VopVX(int funct6, int vd, int vs2, int rs1, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (4 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vadd/etc.vi vd, vs2, simm5  (funct3=3)
    private static uint VopVI(int funct6, int vd, int vs2, int imm5, bool masked = false) =>
        (uint)(((funct6 & 0x3F) << 26) | ((masked ? 0 : 1) << 25) |
               ((vs2 & 0x1F) << 20) | ((imm5 & 0x1F) << 15) | (3 << 12) | ((vd & 0x1F) << 7) | 0x57);

    // vle{sew}.v vd, (rs1)  (opcode=0x07, funct3=width, lumop=0, vm=1)
    private static uint Vle(int vd, int rs1, int funct3Width) =>
        (uint)((0 << 26) | (1 << 25) | (0 << 20) | (rs1 << 15) | (funct3Width << 12) | (vd << 7) | 0x07);

    // vse{sew}.v vs3, (rs1)  (opcode=0x27, funct3=width, sumop=0, vm=1)
    private static uint Vse(int vs3, int rs1, int funct3Width) =>
        (uint)((0 << 26) | (1 << 25) | (0 << 20) | (rs1 << 15) | (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vtypei for e32, m1, ta, ma = vsew=2, vlmul=0, vta=1, vma=1 → 0xC2
    private const int Vtypei_e32m1_tama = (1 << 7) | (1 << 6) | (0 << 3) | 2;

    // ── Decoder tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_Vsetvli() {
        // vsetvli a0(x10), zero(x0), e32,m1,ta,ma
        uint raw = Vsetvli(10, 0, VectorTests.Vtypei_e32m1_tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVsetvli>(i.Payload);
        var op = (RvVsetvli)i.Payload!;
        Assert.Equal(10, op.Rd);
        Assert.Equal(0, op.Rs1);
        Assert.Equal(VectorTests.Vtypei_e32m1_tama, op.Vtypei);
        Assert.Equal(ToothClass.Vector, i.Class);
        Assert.Equal(10, i.DestinationRegister); // writes integer rd
    }

    [Fact]
    public void Decode_Vsetvli_HasIntegerDest() {
        // vsetvli writes rd (integer), so DestinationRegister = rd
        uint raw = Vsetvli(10, 1, VectorTests.Vtypei_e32m1_tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.Equal(10, i.DestinationRegister);
    }

    [Fact]
    public void Decode_Vsetivli() {
        // vsetivli a1(x11), 4, e32,m1,ta,ma
        uint raw = Vsetivli(11, 4, VectorTests.Vtypei_e32m1_tama);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVsetivli>(i.Payload);
        var op = (RvVsetivli)i.Payload!;
        Assert.Equal(11, op.Rd);
        Assert.Equal(4, op.Zimm);
        Assert.Equal(VectorTests.Vtypei_e32m1_tama, op.Vtypei);
    }

    [Fact]
    public void Decode_Vle32() {
        // vle32.v v1, (a0)  — funct3=6 for 32-bit
        uint raw = Vle(1, 10, 6);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVV>(i.Payload);
        var op = (RvVleVV)i.Payload!;
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
        Assert.IsType<RvVseVV>(i.Payload);
        var op = (RvVseVV)i.Payload!;
        Assert.Equal(2, op.Vs3);
        Assert.Equal(11, op.Rs1);
        Assert.Equal(32, op.Sew);
    }

    [Fact]
    public void Decode_VaddVV() {
        // vadd.vv v1, v2, v3  (funct6=0, funct3=0)
        uint raw = VopVV(0, 1, 2, 3);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVV>(i.Payload);
        var op = (RvVIntAluVV)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(1, op.Vd);
        Assert.Equal(2, op.Vs2);
        Assert.Equal(3, op.Vs1);
        Assert.False(op.Masked);
    }

    [Fact]
    public void Decode_VsubVV() {
        // vsub.vv v1, v2, v3  (funct6=2)
        uint raw = VopVV(2, 1, 2, 3);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVV>(i.Payload);
        Assert.Equal(VIntOp.Sub, ((RvVIntAluVV)i.Payload!).Op);
    }

    [Fact]
    public void Decode_VaddVX() {
        // vadd.vx v4, v5, a0(x10)
        uint raw = VopVX(0, 4, 5, 10);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVX>(i.Payload);
        var op = (RvVIntAluVX)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(4, op.Vd);
        Assert.Equal(5, op.Vs2);
        Assert.Equal(10, op.Rs1);
    }

    [Fact]
    public void Decode_VaddVI() {
        // vadd.vi v1, v2, 7
        uint raw = VopVI(0, 1, 2, 7);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVIntAluVI>(i.Payload);
        var op = (RvVIntAluVI)i.Payload!;
        Assert.Equal(VIntOp.Add, op.Op);
        Assert.Equal(7, op.Imm);
    }

    [Fact]
    public void Decode_VmseqVV() {
        // vmseq.vv v0, v1, v2  (funct6=24)
        uint raw = VopVV(24, 0, 1, 2);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVMaskCmpVV>(i.Payload);
        var op = (RvVMaskCmpVV)i.Payload!;
        Assert.Equal(VMaskCmpOp.Eq, op.Op);
        Assert.Equal(0, op.Vd);
    }

    [Fact]
    public void Decode_VmsneVX() {
        // vmsne.vx v0, v2, a1  (funct6=25, funct3=4)
        uint raw = VopVX(25, 0, 2, 11);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVMaskCmpVX>(i.Payload);
        Assert.Equal(VMaskCmpOp.Ne, ((RvVMaskCmpVX)i.Payload!).Op);
    }

    [Fact]
    public void Decode_Vle8() {
        // vle8.v v3, (a2)  — funct3=0 for 8-bit
        uint raw = Vle(3, 12, 0);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVV>(i.Payload);
        Assert.Equal(8, ((RvVleVV)i.Payload!).Sew);
    }

    [Fact]
    public void Decode_Vle16() {
        // vle16.v v3, (a2)  — funct3=5 for 16-bit
        uint raw = Vle(3, 12, 5);
        ITooth i = _dec.Decode(0, raw);
        Assert.IsType<RvVleVV>(i.Payload);
        Assert.Equal(16, ((RvVleVV)i.Payload!).Sew);
    }

    // ── Executor tests ────────────────────────────────────────────────────────

    private RvArchState MakeState() => new();

    private void SetGpr(RvArchState s, int r, uint v) => s.IntegerRegisters.Write(r, v);

    private static void SetVReg(RvArchState s, int vr, uint[] elements32) {
        var bytes = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elements32.Length && i < 4; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), elements32[i]);
        s.VectorRegisters.Write(vr, bytes);
    }

    private static ulong ReadCsr(RvArchState s, uint addr) =>
        s.SystemRegisters.Read(addr, RvPrivilege.Machine);

    private ExecuteResult Exec(uint raw, RvArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // Helper: configure vl=4, sew=32 via vsetvli
    private void ConfigVl4e32(RvArchState s) {
        uint raw = Vsetvli(10, 0, VectorTests.Vtypei_e32m1_tama); // vsetvli a0, x0, e32,m1,ta,ma
        Exec(raw, s);                                             // side-effect: updates vl and vtype in state
    }

    [Fact]
    public void Execute_Vsetvli_SetsVlAndVtype() {
        RvArchState s = MakeState();
        // vsetvli a0(10), zero, e32,m1,ta,ma → should set vl=4, return 4 in a0
        uint raw = Vsetvli(10, 0, VectorTests.Vtypei_e32m1_tama);
        ExecuteResult r = Exec(raw, s);

        Assert.Equal(4UL, r.RegisterResult); // new vl
        Assert.Equal(4u, ReadCsr(s, CsrFile.Vl));
        Assert.Equal((uint)VectorTests.Vtypei_e32m1_tama, ReadCsr(s, CsrFile.Vtype));
    }

    [Fact]
    public void Execute_Vsetvli_AvlCapsAtVlmax() {
        RvArchState s = MakeState();
        SetGpr(s, 1, 100);                                        // AVL=100, much larger than VLMAX=4
        uint raw = Vsetvli(10, 1, VectorTests.Vtypei_e32m1_tama); // vsetvli a0, x1, e32,m1,ta,ma
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(4UL, r.RegisterResult);
        Assert.Equal(4u, ReadCsr(s, CsrFile.Vl));
    }

    [Fact]
    public void Execute_Vsetivli_SetsVl() {
        RvArchState s = MakeState();
        uint raw = Vsetivli(10, 3, VectorTests.Vtypei_e32m1_tama); // AVL=3
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(3UL, r.RegisterResult);
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));
    }

    [Fact]
    public void Execute_Vsetvli_PreservesVlWhenRd0Rs10() {
        RvArchState s = MakeState();
        // Set vl=3 by using a non-zero rs1 (AVL=3 in x1)
        SetGpr(s, 1, 3);
        Exec(Vsetvli(10, 1, VectorTests.Vtypei_e32m1_tama), s); // vl = min(3, 4) = 3
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));

        // Now: vsetvli x0, x0, vtypei → vl unchanged, vtype updated
        uint raw = Vsetvli(0, 0, 0x42); // different vtypei (e8m1)
        Exec(raw, s);
        Assert.Equal(3u, ReadCsr(s, CsrFile.Vl));       // preserved
        Assert.Equal(0x42u, ReadCsr(s, CsrFile.Vtype)); // updated to new vtypei
    }

    [Fact]
    public void Execute_Vle32_LoadsElements() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);

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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 3, [10u, 20u, 30u, 40u,]);

        uint raw = VopVV(0, 1, 2, 3); // vadd.vv v1, v2, v3
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 2, [10u, 20u, 30u, 40u,]);
        SetVReg(s, 3, [1u, 2u, 3u, 4u,]);

        uint raw = VopVV(2, 1, 2, 3); // vsub.vv v1, v2, v3
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(9u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(18u, BitConverter.ToUInt32(result, 4));
    }

    [Fact]
    public void Execute_VaddVX_AddsScalar() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetGpr(s, 5, 100);

        uint raw = VopVX(0, 1, 2, 5); // vadd.vx v1, v2, x5
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 2, [5u, 10u, 15u, 20u,]);

        uint raw = VopVI(0, 1, 2, 3); // vadd.vi v1, v2, 3
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(8u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(13u, BitConverter.ToUInt32(result, 4));
    }

    [Fact]
    public void Execute_VmseqVV_SetsMatchBits() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        // v1 = [5, 5, 7, 7], v2 = [5, 6, 7, 8]
        SetVReg(s, 1, [5u, 5u, 7u, 7u,]);
        SetVReg(s, 2, [5u, 6u, 7u, 8u,]);

        uint raw = VopVV(24, 0, 1, 2); // vmseq.vv v0, v1, v2
        ExecuteResult r = Exec(raw, s);

        Assert.NotNull(r.SideEffect);
        r.SideEffect!(s);
        // elements 0 and 2 match: bits [0] and [2] set → byte 0 = 0b0101 = 5
        Assert.Equal(0b0101, s.VectorRegisters.Read(0)[0]);
    }

    [Fact]
    public void Execute_VmsltuVV_UnsignedLessThan() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        // v1 = [1, 5, 3, 10], v2 = [2, 4, 3, 11]  → v1[i] < v2[i]? [T, F, F, T]
        SetVReg(s, 1, [1u, 5u, 3u, 10u,]);
        SetVReg(s, 2, [2u, 4u, 3u, 11u,]);

        uint raw = VopVV(26, 0, 1, 2); // vmsltu.vv v0, v1, v2
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        // bits [0]=1, [1]=0, [2]=0, [3]=1 → byte0 = 0b1001 = 9
        Assert.Equal(0b1001, s.VectorRegisters.Read(0)[0]);
    }

    [Fact]
    public void Execute_VandVV_BitwiseAnd() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 1, [0xFFu, 0xF0u, 0x0Fu, 0xAAu,]);
        SetVReg(s, 2, [0x55u, 0xF0u, 0xF0u, 0x55u,]);

        uint raw = VopVV(9, 3, 1, 2); // vand.vv v3, v1, v2  (funct6=9)
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 1, [1u, 2u, 4u, 8u,]);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]); // shift amounts

        uint raw = VopVV(37, 3, 1, 2); // vsll.vv v3, v1, v2  (funct6=37)
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        // mask v0: elements 0 and 2 active (bits [0] and [2] = byte0 = 0b0101 = 5)
        var maskReg = new byte[VectorRegisterFile.VLenB];
        maskReg[0] = 0b0101;
        s.VectorRegisters.Write(0, maskReg);

        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 3, [10u, 20u, 30u, 40u,]);

        uint raw = VopVV(0, 1, 2, 3, true); // vadd.vv v1, v2, v3, v0.t
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
        RvArchState s = MakeState();
        Assert.Equal(16UL, ReadCsr(s, CsrFile.Vlenb));
    }

    [Fact]
    public void Execute_VsrlVV_ShiftRightLogical() {
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 1, [0x80000000u, 16u, 0u, 0xFFFFFFFFu,]);
        SetVReg(s, 2, [1u, 1u, 1u, 4u,]);

        uint raw = VopVV(40, 3, 1, 2); // vsrl.vv v3, v1, v2  (funct6=40)
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
        RvArchState s = MakeState();
        ConfigVl4e32(s);
        SetVReg(s, 1, [0x80000000u, 0x7FFFFFFFu, 0u, 0u,]);
        SetVReg(s, 2, [1u, 1u, 0u, 0u,]);

        uint raw = VopVV(41, 3, 1, 2); // vsra.vv v3, v1, v2  (funct6=41)
        ExecuteResult r = Exec(raw, s);

        r.SideEffect!(s);
        byte[] result = s.VectorRegisters.Read(3);
        Assert.Equal(0xC0000000u, BitConverter.ToUInt32(result, 0)); // sign extended
        Assert.Equal(0x3FFFFFFFu, BitConverter.ToUInt32(result, 4));
    }
}