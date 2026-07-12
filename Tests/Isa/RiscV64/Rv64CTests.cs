using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for RV64C quadrant reassignments — C.LD/C.SD replace C.FLW/C.FSW (quadrant 0,
/// funct3 3/7), C.ADDIW replaces C.JAL (quadrant 1, funct3 1), C.LDSP/C.SDSP replace
/// C.FLWSP/C.FSWSP (quadrant 2, funct3 3/7). Exercised through both decoder entry points:
/// Decode(pc, uint) (DecodeRaw) and Decode(pc, IMemory) — the latter is the actual pipeline
/// fetch path and bypasses DecodeRaw for compressed instructions, so it needs its own coverage.
/// </summary>
public class Rv64CTests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(65536);

    private Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult ExecRaw(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // Decode via the (pc, IMemory) fetch path — the one the pipeline actually uses.
    private ExecuteResult ExecViaMemory(ushort raw, Rv64ArchState state, ulong pc) {
        _mem.Write(pc, raw, 2);
        ITooth instr = _dec.Decode(pc, _mem);
        return _exe.Execute(instr, state, _mem);
    }

    // C.LD / C.SD (CL/CS-format): uimm[5:3]=c[12:10], uimm[7:6]=c[6:5]. rd'/rs1'/rs2' are
    // 3-bit fields (+8 for x8-x15).
    private static ushort CLd(int rdP, int rs1P, int uimm) =>
        (ushort)(0x3u << 13 | ((uint)(uimm >> 6 & 0x3) << 5) | ((uint)(rs1P - 8) << 7)
                 | ((uint)(uimm >> 3 & 0x7) << 10) | ((uint)(rdP - 8) << 2) | 0x0u);

    private static ushort CSd(int rs1P, int rs2P, int uimm) =>
        (ushort)(0x7u << 13 | ((uint)(uimm >> 6 & 0x3) << 5) | ((uint)(rs1P - 8) << 7)
                 | ((uint)(uimm >> 3 & 0x7) << 10) | ((uint)(rs2P - 8) << 2) | 0x0u);

    // C.ADDIW (CI-format, quadrant 1, funct3=1): imm[5]=c[12], imm[4:0]=c[6:2].
    private static ushort CAddiw(int rd, int imm) =>
        (ushort)(0x1u << 13 | ((uint)(imm >> 5 & 0x1) << 12) | ((uint)rd << 7)
                 | ((uint)(imm & 0x1F) << 2) | 0x1u);

    // C.LDSP (CI-format, quadrant 2, funct3=3): uimm[5]=c[12], uimm[4:3]=c[6:5], uimm[8:6]=c[4:2].
    private static ushort CLdsp(int rd, int uimm) =>
        (ushort)(0x3u << 13 | ((uint)(uimm >> 5 & 0x1) << 12) | ((uint)rd << 7)
                 | ((uint)(uimm >> 3 & 0x3) << 5) | ((uint)(uimm >> 6 & 0x7) << 2) | 0x2u);

    // C.SDSP (CSS-format, quadrant 2, funct3=7): uimm[5:3]=c[12:10], uimm[8:6]=c[9:7].
    private static ushort CSdsp(int rs2, int uimm) =>
        (ushort)(0x7u << 13 | ((uint)(uimm >> 3 & 0x7) << 10) | ((uint)(uimm >> 6 & 0x7) << 7)
                 | ((uint)rs2 << 2) | 0x2u);

    // ── C.LD / C.SD ──────────────────────────────────────────────────────────────

    [Fact]
    public void CLd_LoadsDoublewordViaDecodeRaw() {
        _mem.Write(0x100, 0xABCDEF0123456789UL, 8);
        Rv64ArchState s = MakeState((9, 0x100)); // x9 = s1 (rs1' index 1 -> reg 9)
        ushort raw = CLd(10, 9, 0); // c.ld x10, 0(x9)
        ExecuteResult r = ExecRaw(raw, s);
        Assert.Equal(0xABCDEF0123456789UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CLd_LoadsDoublewordViaMemoryFetchPath() {
        // Regression test for the dual-entry-point bug: Decode(pc, IMemory) bypasses
        // DecodeRaw for compressed instructions, so this must go through TryDecodeRv64Compressed too.
        _mem.Write(0x100, 0xABCDEF0123456789UL, 8);
        Rv64ArchState s = MakeState((9, 0x100));
        ushort raw = CLd(10, 9, 0);
        ExecuteResult r = ExecViaMemory(raw, s, 0x400);
        Assert.Equal(0xABCDEF0123456789UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CLd_NonzeroOffset() {
        _mem.Write(0x118, 0x1122334455667788UL, 8);
        Rv64ArchState s = MakeState((8, 0x100)); // x8 = s0
        ushort raw = CLd(9, 8, 0x18);
        ExecuteResult r = ExecRaw(raw, s);
        Assert.Equal(0x1122334455667788UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CSd_StoresDoublewordViaMemoryFetchPath() {
        Rv64ArchState s = MakeState((8, 0x100), (9, 0xCAFEBABEDEADBEEFUL));
        ushort raw = CSd(8, 9, 0); // c.sd x9, 0(x8)
        ExecViaMemory(raw, s, 0x400);
        Assert.Equal(0xCAFEBABEDEADBEEFUL, _mem.Read(0x100, 8));
    }

    // ── C.ADDIW ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CAddiw_SignExtends32To64ViaMemoryFetchPath() {
        Rv64ArchState s = MakeState((5, 0x7FFFFFFEUL)); // x5 near INT32_MAX
        ushort raw = CAddiw(5, 3); // c.addiw x5, 3 -> lower32 overflow, sign-extend
        ExecuteResult r = ExecViaMemory(raw, s, 0x400);
        Assert.Equal(0xFFFFFFFF80000001UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CAddiw_NegativeImmediate() {
        Rv64ArchState s = MakeState((5, 10UL));
        ushort raw = CAddiw(5, -1);
        ExecuteResult r = ExecRaw(raw, s);
        Assert.Equal(9UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CAddiw_RdZero_IsReservedAndThrows() {
        ushort raw = CAddiw(0, 1);
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0UL, raw));
    }

    // ── C.LDSP / C.SDSP ──────────────────────────────────────────────────────────

    [Fact]
    public void CLdsp_LoadsFromStackPointerViaMemoryFetchPath() {
        _mem.Write(0x210, 0x9988776655443322UL, 8);
        Rv64ArchState s = MakeState((2, 0x200)); // sp = x2
        ushort raw = CLdsp(11, 0x10); // c.ldsp x11, 0x10(sp)
        ExecuteResult r = ExecViaMemory(raw, s, 0x400);
        Assert.Equal(0x9988776655443322UL, r.RegisterResult.Value);
    }

    [Fact]
    public void CLdsp_RdZero_IsReservedAndThrows() {
        ushort raw = CLdsp(0, 0x10);
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0UL, raw));
    }

    [Fact]
    public void CSdsp_StoresToStackPointerViaMemoryFetchPath() {
        Rv64ArchState s = MakeState((2, 0x200), (11, 0x1357913579135791UL));
        ushort raw = CSdsp(11, 0x18); // c.sdsp x11, 0x18(sp)
        ExecViaMemory(raw, s, 0x400);
        Assert.Equal(0x1357913579135791UL, _mem.Read(0x218, 8));
    }
}
