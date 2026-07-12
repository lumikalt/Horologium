using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for RV64 Zbb/Zbs immediate-form ops (CLZ/CTZ/CPOP/SEXT.B/SEXT.H/BSETI/BCLRI/
/// BINVI/RORI/REV8/ORC.B/BEXTI) — reachability (Rv64Decoder's 6-bit-shamt OP-IMM override
/// previously rejected every non-shift encoding as illegal) and 64-bit-width correctness
/// (the inherited RV32 implementations hardcode (uint) truncation, which is wrong once
/// regs.Read() returns a genuine 64-bit value under RV64).
/// </summary>
public class Rv64ZbbZbsTests {
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

    // OP-IMM encoding: opcode=0x13, 6-bit shamt (bits 25:20), top6 selector (bits 31:26).
    private static uint OpImm(uint funct3, uint top6, int rd, int rs1, uint shamt6) =>
        (top6 << 26) | (shamt6 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x13;

    // ── BSETI / BCLRI / BINVI / BEXTI ────────────────────────────────────────────

    [Fact]
    public void Bseti_SetsHighBit_ReachableAndFullWidth() {
        // bit 40 is above 32-bit width — would be silently lost if truncated to uint.
        Rv64ArchState s = MakeState((2, 0UL));
        ExecuteResult r = Exec(OpImm(0x1, 0x0A, 1, 2, 40), s);
        Assert.Equal(1UL << 40, r.RegisterResult.Value);
    }

    [Fact]
    public void Bclri_ClearsHighBit() {
        Rv64ArchState s = MakeState((2, 0xFFFFFFFFFFFFFFFFUL));
        ExecuteResult r = Exec(OpImm(0x1, 0x12, 1, 2, 40), s);
        Assert.Equal(~(1UL << 40), r.RegisterResult.Value);
    }

    [Fact]
    public void Binvi_TogglesHighBit() {
        Rv64ArchState s = MakeState((2, 0UL));
        ExecuteResult r = Exec(OpImm(0x1, 0x1A, 1, 2, 40), s);
        Assert.Equal(1UL << 40, r.RegisterResult.Value);
    }

    [Fact]
    public void Bexti_ExtractsHighBit() {
        Rv64ArchState s = MakeState((2, 1UL << 40));
        ExecuteResult r = Exec(OpImm(0x5, 0x12, 1, 2, 40), s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    // ── CLZ / CTZ / CPOP ──────────────────────────────────────────────────────────

    [Fact]
    public void Clz_CountsFullWidthLeadingZeros() {
        // Only bit 0 set: 63 leading zeros over 64 bits (would be 31 if computed over 32 bits).
        Rv64ArchState s = MakeState((2, 1UL));
        ExecuteResult r = Exec(OpImm(0x1, 0x18, 1, 2, 0), s);
        Assert.Equal(63UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Ctz_CountsTrailingZeros() {
        Rv64ArchState s = MakeState((2, 1UL << 40));
        ExecuteResult r = Exec(OpImm(0x1, 0x18, 1, 2, 1), s);
        Assert.Equal(40UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Cpop_CountsSetBitsAcrossFullWidth() {
        Rv64ArchState s = MakeState((2, 0xFFFFFFFFFFFFFFFFUL));
        ExecuteResult r = Exec(OpImm(0x1, 0x18, 1, 2, 2), s);
        Assert.Equal(64UL, r.RegisterResult.Value);
    }

    // ── SEXT.B / SEXT.H ───────────────────────────────────────────────────────────

    [Fact]
    public void SextB_SignExtendsByteTo64Bits() {
        Rv64ArchState s = MakeState((2, 0x80UL));
        ExecuteResult r = Exec(OpImm(0x1, 0x18, 1, 2, 4), s);
        Assert.Equal(0xFFFFFFFFFFFFFF80UL, r.RegisterResult.Value);
    }

    [Fact]
    public void SextH_SignExtendsHalfwordTo64Bits() {
        Rv64ArchState s = MakeState((2, 0x8000UL));
        ExecuteResult r = Exec(OpImm(0x1, 0x18, 1, 2, 5), s);
        Assert.Equal(0xFFFFFFFFFFFF8000UL, r.RegisterResult.Value);
    }

    // ── ORC.B ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrcB_BroadcastsNonzeroBytesAcrossAllEightBytes() {
        Rv64ArchState s = MakeState((2, 0x0001000000000100UL));
        ExecuteResult r = Exec(OpImm(0x5, 0x0A, 1, 2, 0x07), s);
        Assert.Equal(0x00FF000000000000UL | 0xFFUL << 8, r.RegisterResult.Value);
    }

    // ── RORI ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rori_RotatesRightAcrossFull64Bits() {
        // rotate by 1: bit 0 -> bit 63 — would be wrong if rotated within a 32-bit word.
        Rv64ArchState s = MakeState((2, 1UL));
        ExecuteResult r = Exec(OpImm(0x5, 0x18, 1, 2, 1), s);
        Assert.Equal(0x8000000000000000UL, r.RegisterResult.Value);
    }

    // ── REV8 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rev8_ReversesByteOrderAcrossAllEightBytes() {
        Rv64ArchState s = MakeState((2, 0x0102030405060708UL));
        ExecuteResult r = Exec(OpImm(0x5, 0x1A, 1, 2, 0x18), s);
        Assert.Equal(0x0807060504030201UL, r.RegisterResult.Value);
    }
}
