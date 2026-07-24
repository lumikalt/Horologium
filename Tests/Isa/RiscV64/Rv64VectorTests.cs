#region

using Mechanism;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

#endregion

namespace Tests.Isa.RiscV64;

/// <summary>
///     V-extension coverage on RV64. Decode and most execution fall straight through to the
///     inherited RV32 implementation (VectorRegisterFile and the vtype/vl CSRs are XLEN-agnostic —
///     see Rv32Executor.V.cs), so this focuses on the one path that genuinely differs: vlse/vsse
///     strided load/store, whose stride register must be read as a full 64-bit signed value on
///     RV64 instead of the RV32 32-bit-sign-extend path (Rv32Executor.ReadStride /
///     Rv64Executor.ReadStride).
/// </summary>
/// <summary>Byte-dictionary-backed IMemory for tests that need addresses spanning &gt; 32 bits.</summary>
internal sealed class SparseMemory : IMemory {
    private readonly Dictionary<ulong, byte> _bytes = new();

    public ulong Read(ulong address, int bytes) {
        ulong v = 0;
        for (var i = 0; i < bytes; i++) v |= (ulong)_bytes.GetValueOrDefault(address + (ulong)i) << (i * 8);
        return v;
    }

    public void Write(ulong address, ulong value, int bytes) {
        for (var i = 0; i < bytes; i++) _bytes[address + (ulong)i] = (byte)(value >> (i * 8));
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        for (var i = 0; i < data.Length; i++) _bytes[address + (ulong)i] = data[i];
    }
}

public class Rv64VectorTests {
    // vtypei for e32,m1,ta,ma
    private const int VtypeiE32M1Tama = (1 << 7) | (1 << 6) | (2 << 3);
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(0x100000);
    private readonly SparseMemory _sparseMem = new();

    // vsetivli rd, zimm, vtypei
    private static uint Vsetivli(int rd, int zimm, int vtypei) =>
        0xC0000000u | (uint)((vtypei << 20) | (zimm << 15) | (7 << 12) | (rd << 7) | 0x57);

    // vadd.vv vd, vs2, vs1  (funct6=0, vm=1, funct3=0)
    private static uint VaddVv(int vd, int vs2, int vs1) =>
        (uint)((0 << 26) | (1 << 25) | ((vs2 & 0x1F) << 20) | ((vs1 & 0x1F) << 15) | ((vd & 0x1F) << 7) | 0x57);

    // vlse{sew}.v vd,(rs1),rs2  (mop=2, opcode=0x07)
    private static uint Vlse(int vd, int rs1, int rs2, int funct3Width) =>
        (uint)((2 << 26) | (1 << 25) | (rs2 << 20) | (rs1 << 15) | (funct3Width << 12) | (vd << 7) | 0x07);

    // vsse{sew}.v vs3,(rs1),rs2  (mop=2, opcode=0x27)
    private static uint Vsse(int vs3, int rs1, int rs2, int funct3Width) =>
        (uint)((2 << 26) | (1 << 25) | (rs2 << 20) | (rs1 << 15) | (funct3Width << 12) | (vs3 << 7) | 0x27);

    // vlsseg{nf}e{sew}.v vd,(rs1),rs2  (mop=2, opcode=0x07, nf-1 in bits[31:29])
    private static uint Vlsseg(int nf, int vd, int rs1, int rs2, int funct3Width) =>
        (uint)(((nf - 1) << 29) | (2 << 26) | (1 << 25) | (rs2 << 20) | (rs1 << 15) | (funct3Width << 12) |
               (vd << 7) | 0x07);

    // vssseg{nf}e{sew}.v vs3,(rs1),rs2  (mop=2, opcode=0x27, nf-1 in bits[31:29])
    private static uint Vssseg(int nf, int vs3, int rs1, int rs2, int funct3Width) =>
        (uint)(((nf - 1) << 29) | (2 << 26) | (1 << 25) | (rs2 << 20) | (rs1 << 15) | (funct3Width << 12) |
               (vs3 << 7) | 0x27);

    private static Rv64ArchState MakeState() => new();

    private ExecuteResult Exec(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    private ExecuteResult ExecSparse(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _sparseMem);
    }

    private void ConfigVl4E32(Rv64ArchState s) => Exec(Vsetivli(10, 4, Rv64VectorTests.VtypeiE32M1Tama), s);

    private static void SetVReg(Rv64ArchState s, int vr, uint[] elements32) {
        var bytes = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < elements32.Length && i < 4; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), elements32[i]);
        s.VectorRegisters.Write(vr, bytes);
    }

    [Fact]
    public void VaddVv_WorksViaInheritedRv32Path() {
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 2, [1u, 2u, 3u, 4u,]);
        SetVReg(s, 3, [10u, 20u, 30u, 40u,]);

        ExecuteResult r = Exec(VaddVv(1, 2, 3), s);
        r.SideEffect!(s);

        byte[] result = s.VectorRegisters.Read(1);
        Assert.Equal(11u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(44u, BitConverter.ToUInt32(result, 12));
    }

    [Fact]
    public void Vlse32_PositiveStrideWithinLow32Bits_StillWorks() {
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);
        _mem.Write(0x1000, 10, 4);
        _mem.Write(0x1008, 20, 4);
        _mem.Write(0x1010, 30, 4);
        _mem.Write(0x1018, 40, 4);

        s.IntegerRegisters.Write(10, 0x1000); // base
        s.IntegerRegisters.Write(11, 8);      // stride

        Exec(Vlse(1, 10, 11, 6), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Vlse32_StrideAbove32Bits_UsesFull64BitValue() {
        // Regression test: a naive (int)(uint) truncation of the stride register drops this
        // to a stride of 8, which would silently produce the wrong element sequence.
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);

        const ulong stride = 0x1_0000_0008UL; // > uint.MaxValue
        const ulong baseAddr = 0x2000;
        _sparseMem.Write(baseAddr, 10, 4);
        _sparseMem.Write(baseAddr + stride, 20, 4);
        _sparseMem.Write(baseAddr + stride * 2, 30, 4);
        _sparseMem.Write(baseAddr + stride * 3, 40, 4);

        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        ExecSparse(Vlse(1, 10, 11, 6), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Vsse32_StrideAbove32Bits_UsesFull64BitValue() {
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [1u, 2u, 3u, 4u,]);

        const ulong stride = 0x1_0000_0008UL;
        const ulong baseAddr = 0x3000;
        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        ExecSparse(Vsse(1, 10, 11, 6), s);

        Assert.Equal(1UL, _sparseMem.Read(baseAddr, 4));
        Assert.Equal(2UL, _sparseMem.Read(baseAddr + stride, 4));
        Assert.Equal(3UL, _sparseMem.Read(baseAddr + stride * 2, 4));
        Assert.Equal(4UL, _sparseMem.Read(baseAddr + stride * 3, 4));
    }

    [Fact]
    public void Vlse32_NegativeStride_SignExtendsFullWidth() {
        // Walk backward from a high base address; a truncating stride read would misread
        // this negative 64-bit value entirely.
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);

        const ulong baseAddr = 0x5000;
        const ulong stride = unchecked((ulong)-8L); // -8 as a full 64-bit two's-complement value
        _mem.Write(baseAddr, 10, 4);
        _mem.Write(baseAddr - 8, 20, 4);
        _mem.Write(baseAddr - 16, 30, 4);
        _mem.Write(baseAddr - 24, 40, 4);

        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        Exec(Vlse(1, 10, 11, 6), s).SideEffect!(s);

        byte[] r = s.VectorRegisters.Read(1);
        Assert.Equal(10u, BitConverter.ToUInt32(r, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(r, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(r, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(r, 12));
    }

    [Fact]
    public void Vlsseg2e32_StrideAbove32Bits_UsesFull64BitValue() {
        // Regression test: ExecuteVlsseg used to inline the same truncating stride read as
        // ExecuteVlse before ReadStride existed as a virtual hook it could share.
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);

        const ulong stride = 0x1_0000_0010UL; // > uint.MaxValue
        const ulong baseAddr = 0x2000;
        for (var i = 0; i < 4; i++) {
            ulong addr = baseAddr + stride * (ulong)i;
            _sparseMem.Write(addr, (ulong)(10 + i * 10), 4);
            _sparseMem.Write(addr + 4, (ulong)(11 + i * 10), 4);
        }

        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        ExecuteResult r = ExecSparse(Vlsseg(2, 1, 10, 11, 6), s);
        r.SideEffect!(s);

        byte[] field0 = s.VectorRegisters.Read(1);
        byte[] field1 = s.VectorRegisters.Read(2);
        Assert.Equal(10u, BitConverter.ToUInt32(field0, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(field0, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(field0, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(field0, 12));
        Assert.Equal(11u, BitConverter.ToUInt32(field1, 0));
        Assert.Equal(21u, BitConverter.ToUInt32(field1, 4));
        Assert.Equal(31u, BitConverter.ToUInt32(field1, 8));
        Assert.Equal(41u, BitConverter.ToUInt32(field1, 12));
    }

    [Fact]
    public void Vssseg2e32_StrideAbove32Bits_UsesFull64BitValue() {
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);
        SetVReg(s, 1, [10u, 20u, 30u, 40u,]); // field 0
        SetVReg(s, 2, [11u, 21u, 31u, 41u,]); // field 1

        const ulong stride = 0x1_0000_0010UL; // > uint.MaxValue
        const ulong baseAddr = 0x3000;
        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        ExecSparse(Vssseg(2, 1, 10, 11, 6), s);

        for (var i = 0; i < 4; i++) {
            ulong addr = baseAddr + stride * (ulong)i;
            Assert.Equal((ulong)(10 + i * 10), _sparseMem.Read(addr, 4));
            Assert.Equal((ulong)(11 + i * 10), _sparseMem.Read(addr + 4, 4));
        }
    }

    [Fact]
    public void Vlsseg2e32_NegativeStride_SignExtendsFullWidth() {
        Rv64ArchState s = MakeState();
        ConfigVl4E32(s);

        const ulong baseAddr = 0x5000;
        const ulong stride = unchecked((ulong)-16L);
        for (var i = 0; i < 4; i++) {
            ulong addr = baseAddr - (ulong)(16 * i);
            _mem.Write(addr, (ulong)(10 + i * 10), 4);
            _mem.Write(addr + 4, (ulong)(11 + i * 10), 4);
        }

        s.IntegerRegisters.Write(10, baseAddr);
        s.IntegerRegisters.Write(11, stride);

        ExecuteResult r = Exec(Vlsseg(2, 1, 10, 11, 6), s);
        r.SideEffect!(s);

        byte[] field0 = s.VectorRegisters.Read(1);
        byte[] field1 = s.VectorRegisters.Read(2);
        Assert.Equal(10u, BitConverter.ToUInt32(field0, 0));
        Assert.Equal(20u, BitConverter.ToUInt32(field0, 4));
        Assert.Equal(30u, BitConverter.ToUInt32(field0, 8));
        Assert.Equal(40u, BitConverter.ToUInt32(field0, 12));
        Assert.Equal(11u, BitConverter.ToUInt32(field1, 0));
        Assert.Equal(21u, BitConverter.ToUInt32(field1, 4));
        Assert.Equal(31u, BitConverter.ToUInt32(field1, 8));
        Assert.Equal(41u, BitConverter.ToUInt32(field1, 12));
    }
}