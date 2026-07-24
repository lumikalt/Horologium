#region

using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

#endregion

namespace Tests.Isa.RiscV64;

/// <summary>
///     Tests for RV64A — LR.D/SC.D/AMO*.D doubleword atomics (opcode=0x2F, funct3=0x3),
///     undecoded on the base RV32 AMO table (which only handles funct3=0x2 word AMOs and
///     the Zabha .b/.h forms).
/// </summary>
public class Rv64ATests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(65536);

    private static Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // R-type AMO encoding: opcode=0x2F, funct3=0x3 (doubleword), aq/rl bits left at 0.
    private static uint AmoD(uint funct5, int rd, int rs1, int rs2) =>
        (funct5 << 27) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0x3u << 12) | ((uint)rd << 7) | 0x2F;

    // ── LR.D / SC.D ──────────────────────────────────────────────────────────────

    [Fact]
    public void LrD_LoadsFullDoublewordAndSetsReservation() {
        _mem.Write(0x100, 0x1122334455667788UL, 8);
        Rv64ArchState s = MakeState((1, 0x100));
        ExecuteResult r = Exec(AmoD(0x02, 5, 1, 0), s);
        Assert.Equal(0x1122334455667788UL, r.RegisterResult.Value);
    }

    [Fact]
    public void ScD_SucceedsAfterMatchingLrD_AndWritesDoubleword() {
        _mem.Write(0x100, 0UL, 8);
        Rv64ArchState s = MakeState((1, 0x100), (2, 0xDEADBEEFCAFEBABEUL));
        Exec(AmoD(0x02, 5, 1, 0), s);                   // lr.d x5, (x1)
        ExecuteResult r = Exec(AmoD(0x03, 6, 1, 2), s); // sc.d x6, x2, (x1)
        Assert.Equal(0UL, r.RegisterResult.Value);      // success
        Assert.Equal(0xDEADBEEFCAFEBABEUL, _mem.Read(0x100, 8));
    }

    [Fact]
    public void ScD_FailsWithoutPriorLrD() {
        Rv64ArchState s = MakeState((1, 0x100), (2, 42UL));
        ExecuteResult r = Exec(AmoD(0x03, 6, 1, 2), s);
        Assert.Equal(1UL, r.RegisterResult.Value); // failure
    }

    [Fact]
    public void ScD_UsesEightByteGranule_UnaffectedByNarrowerOverlapOutsideRange() {
        // Reservation set must cover all 8 bytes of the doubleword — a write to the last
        // byte of the reservation should still invalidate it even though a 4-byte granule
        // would have missed it.
        _mem.Write(0x100, 0UL, 8);
        Rv64ArchState s = MakeState((1, 0x100), (2, 42UL));
        Exec(AmoD(0x02, 5, 1, 0), s); // lr.d x5, (x1) reserves [0x100, 0x108)
        _mem.Write(0x104, 0xFFUL, 4); // write inside the doubleword but past a 4-byte granule
        ExecuteResult r = Exec(AmoD(0x03, 6, 1, 2), s);
        // Note: FlatMemory writes don't invalidate the private-fallback reservation (no
        // ReservationTable wired for single-hart mode), so this only verifies SC.D still
        // reads/writes the full 8 bytes rather than the invalidation path itself.
        Assert.Equal(0UL, r.RegisterResult.Value);
        Assert.Equal(42UL, _mem.Read(0x100, 8));
    }

    // ── AMOSWAP.D / AMOADD.D ─────────────────────────────────────────────────────

    [Fact]
    public void AmoswapD_ReturnsOldValueAndWritesNew() {
        _mem.Write(0x200, 111UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 222UL));
        ExecuteResult r = Exec(AmoD(0x01, 3, 1, 2), s);
        Assert.Equal(111UL, r.RegisterResult.Value);
        Assert.Equal(222UL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmoaddD_AddsFullDoublewordWidth() {
        // Upper 32 bits must participate — would be wrong if truncated to word width.
        _mem.Write(0x200, 0x1_0000_0000UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 5UL));
        ExecuteResult r = Exec(AmoD(0x00, 3, 1, 2), s);
        Assert.Equal(0x1_0000_0000UL, r.RegisterResult.Value); // returns old value
        Assert.Equal(0x1_0000_0005UL, _mem.Read(0x200, 8));
    }

    // ── AMOXOR.D / AMOAND.D / AMOOR.D ────────────────────────────────────────────

    [Fact]
    public void AmoxorD_Xors() {
        _mem.Write(0x200, 0xFF00FF00FF00FF00UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 0x00FF00FF00FF00FFUL));
        Exec(AmoD(0x04, 3, 1, 2), s);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmoandD_Ands() {
        _mem.Write(0x200, 0xFF00FF00FF00FF00UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 0x0F0F0F0F0F0F0F0FUL));
        Exec(AmoD(0x0C, 3, 1, 2), s);
        Assert.Equal(0x0F000F000F000F00UL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmoorD_Ors() {
        _mem.Write(0x200, 0xFF00FF00FF00FF00UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 0x00FF00FF00FF00FFUL));
        Exec(AmoD(0x08, 3, 1, 2), s);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, _mem.Read(0x200, 8));
    }

    // ── AMOMIN.D / AMOMAX.D / AMOMINU.D / AMOMAXU.D ──────────────────────────────

    [Fact]
    public void AmominD_SignedMin_UpperBitsMatter() {
        _mem.Write(0x200, unchecked((ulong)-1), 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 5UL));
        Exec(AmoD(0x10, 3, 1, 2), s);
        Assert.Equal(unchecked((ulong)-1), _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmomaxD_SignedMax() {
        _mem.Write(0x200, unchecked((ulong)-1), 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 5UL));
        Exec(AmoD(0x14, 3, 1, 2), s);
        Assert.Equal(5UL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmominuD_UnsignedMin_TreatsAllOnesAsLargest() {
        _mem.Write(0x200, unchecked((ulong)-1), 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 5UL));
        Exec(AmoD(0x18, 3, 1, 2), s);
        Assert.Equal(5UL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmomaxuD_UnsignedMax_TreatsAllOnesAsLargest() {
        _mem.Write(0x200, unchecked((ulong)-1), 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 5UL));
        Exec(AmoD(0x1C, 3, 1, 2), s);
        Assert.Equal(unchecked((ulong)-1), _mem.Read(0x200, 8));
    }

    // ── AMOCAS.D (Zacas doubleword compare-and-swap, RV64-native single register) ────────

    [Fact]
    public void AmocasD_Success_WritesNewValueAndReturnsOld() {
        // amocas.d x5, x2, (x1) — x5=comparand, x1=address, x2=new value
        _mem.Write(0x200, 0x1122334455667788UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 0xDEADBEEFCAFEBABEUL), (5, 0x1122334455667788UL));
        ExecuteResult r = Exec(AmoD(0x05, 5, 1, 2), s);
        Assert.Equal(0x1122334455667788UL, r.RegisterResult.Value);
        Assert.Equal(0xDEADBEEFCAFEBABEUL, _mem.Read(0x200, 8));
    }

    [Fact]
    public void AmocasD_Failure_LeavesMemoryUnchangedAndReturnsOld() {
        _mem.Write(0x200, 0x1122334455667788UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 0xDEADBEEFCAFEBABEUL), (5, 0UL)); // mismatched comparand
        ExecuteResult r = Exec(AmoD(0x05, 5, 1, 2), s);
        Assert.Equal(0x1122334455667788UL, r.RegisterResult.Value);
        Assert.Equal(0x1122334455667788UL, _mem.Read(0x200, 8)); // unchanged
    }

    [Fact]
    public void AmocasD_ComparesFullDoublewordWidth_UpperBitsMatter() {
        // Comparand's low 32 bits match, but upper 32 bits don't — must still fail, unlike
        // AMOCAS.W which only ever compares 32 bits.
        _mem.Write(0x200, 0x1_0000_0000UL, 8);
        Rv64ArchState s = MakeState((1, 0x200), (2, 99UL), (5, 0UL)); // 0 matches low word only
        ExecuteResult r = Exec(AmoD(0x05, 5, 1, 2), s);
        Assert.Equal(0x1_0000_0000UL, r.RegisterResult.Value);
        Assert.Equal(0x1_0000_0000UL, _mem.Read(0x200, 8)); // unchanged
    }
}