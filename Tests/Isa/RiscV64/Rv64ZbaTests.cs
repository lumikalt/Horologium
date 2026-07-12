using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for RV64-only Zba ops — ADD.UW, SH1ADD.UW/SH2ADD.UW/SH3ADD.UW (OP-32), and
/// SLLI.UW (OP-IMM-32). These operate on the zero-extended low 32 bits of rs1 and have
/// no RV32 counterpart to inherit from.
/// </summary>
public class Rv64ZbaTests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(65536);

    private Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // OP-32 encoding: opcode=0x3B.
    private static uint Op32(uint funct3, uint funct7, int rd, int rs1, int rs2) =>
        (funct7 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x3B;

    // OP-IMM-32 encoding: opcode=0x1B, 6-bit shamt (bits 25:20).
    private static uint OpImm32(uint funct3, uint top6, int rd, int rs1, uint shamt6) =>
        (top6 << 26) | (shamt6 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x1B;

    // ── ADD.UW ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AdduW_ZeroExtendsRs1LowerHalf_ThenAddsRs2() {
        // Upper 32 bits of rs1 must be dropped before the add.
        Rv64ArchState s = MakeState((1, 0xFFFFFFFF_00000001UL), (2, 10UL));
        ExecuteResult r = Exec(Op32(0x0, 0x04, 3, 1, 2), s);
        Assert.Equal(11UL, r.RegisterResult.Value);
    }

    [Fact]
    public void AdduW_RegularOperands() {
        Rv64ArchState s = MakeState((1, 5UL), (2, 100UL));
        ExecuteResult r = Exec(Op32(0x0, 0x04, 3, 1, 2), s);
        Assert.Equal(105UL, r.RegisterResult.Value);
    }

    // ── SH1ADD.UW / SH2ADD.UW / SH3ADD.UW ────────────────────────────────────────

    [Fact]
    public void Sh1AdduW_ZeroExtendsThenShiftsByOneAndAdds() {
        Rv64ArchState s = MakeState((1, 0xFFFFFFFF_00000004UL), (2, 1UL));
        ExecuteResult r = Exec(Op32(0x2, 0x10, 3, 1, 2), s);
        Assert.Equal((4UL << 1) + 1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Sh2AdduW_ZeroExtendsThenShiftsByTwoAndAdds() {
        Rv64ArchState s = MakeState((1, 0xFFFFFFFF_00000004UL), (2, 1UL));
        ExecuteResult r = Exec(Op32(0x4, 0x10, 3, 1, 2), s);
        Assert.Equal((4UL << 2) + 1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Sh3AdduW_ZeroExtendsThenShiftsByThreeAndAdds() {
        Rv64ArchState s = MakeState((1, 0xFFFFFFFF_00000004UL), (2, 1UL));
        ExecuteResult r = Exec(Op32(0x6, 0x10, 3, 1, 2), s);
        Assert.Equal((4UL << 3) + 1UL, r.RegisterResult.Value);
    }

    // ── SLLI.UW ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SlliUw_ZeroExtendsThenShiftsLeft() {
        // Upper 32 bits of rs1 must be dropped before the shift.
        Rv64ArchState s = MakeState((1, 0xFFFFFFFF_00000001UL));
        ExecuteResult r = Exec(OpImm32(0x1, 0x02, 3, 1, 4), s);
        Assert.Equal(0x10UL, r.RegisterResult.Value);
    }

    [Fact]
    public void SlliUw_ShiftCanCrossInto64BitRange() {
        // shamt=40 with zext32 operand: result must not wrap at 32 bits.
        Rv64ArchState s = MakeState((1, 1UL));
        ExecuteResult r = Exec(OpImm32(0x1, 0x02, 3, 1, 40), s);
        Assert.Equal(1UL << 40, r.RegisterResult.Value);
    }
}
