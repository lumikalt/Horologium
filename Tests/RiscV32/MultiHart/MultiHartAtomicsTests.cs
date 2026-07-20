#region

using Mechanism;
using Orrery.Cache;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     Multi-hart LR/SC correctness: reservation cancellation when another hart
///     stores to the same granule between a hart's paired LR.W and SC.W.
///     <para>
///         Encoded instructions (RV32A):
///         lr.w  x1, (x2)       = 0x100120AF
///         sc.w  x1, x3, (x2)   = 0x183120AF
///         sw    x3, 0(x2)       = 0x00312023
///     </para>
/// </summary>
public class MultiHartAtomicsTests {
    private const uint LrW = 0x100120AF; // lr.w  x1, (x2)
    private const uint ScW = 0x183120AF; // sc.w  x1, x3, (x2)
    private const uint Sw = 0x00312023;  // sw    x3, 0(x2)

    private readonly Rv32Decoder _dec = new();

    private ExecuteResult Exec(Rv32Executor exe, uint raw, Rv32ArchState state, IMemory mem, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return exe.Execute(instr, state, mem);
    }

    private static Rv32ArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, uint v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    // ── Backward-compat: single-hart private reservation ─────────────────────

    [Fact]
    public void SingleHart_LrThenSc_Succeeds() {
        var mem = new FlatMemory(256);
        mem.Write(0x10, 0xDEADBEEF, 4);
        var exe = new Rv32Executor();
        Rv32ArchState s = MakeState((2, 0x10), (3, 0xCAFE));

        Exec(exe, MultiHartAtomicsTests.LrW, s, mem);
        ExecuteResult sc = Exec(exe, MultiHartAtomicsTests.ScW, s, mem);

        Assert.Equal(0UL, sc.RegisterResult.Value); // 0 = success
        Assert.Equal(0xCAFEUL, mem.Read(0x10, 4));
    }

    [Fact]
    public void SingleHart_ScWithoutLr_Fails() {
        var mem = new FlatMemory(256);
        var exe = new Rv32Executor();
        Rv32ArchState s = MakeState((2, 0x10), (3, 99));

        ExecuteResult sc = Exec(exe, MultiHartAtomicsTests.ScW, s, mem);

        Assert.Equal(1UL, sc.RegisterResult.Value); // 1 = failure
        Assert.Equal(0UL, mem.Read(0x10, 4));       // nothing written
    }

    [Fact]
    public void SingleHart_ScAlwaysReleasesReservation() {
        // SC at the wrong address must still clear the reservation.
        var mem = new FlatMemory(256);
        var exe = new Rv32Executor();
        Rv32ArchState s0 = MakeState((2, 0x10));          // LR address
        Rv32ArchState s1 = MakeState((2, 0x20), (3, 99)); // SC at different address

        Exec(exe, MultiHartAtomicsTests.LrW, s0, mem);
        ExecuteResult sc1 = Exec(exe, MultiHartAtomicsTests.ScW, s1, mem); // fail: wrong address
        Assert.Equal(1UL, sc1.RegisterResult.Value);

        // Second SC on the original address must also fail (reservation was released)
        Rv32ArchState s2 = MakeState((2, 0x10), (3, 77));
        ExecuteResult sc2 = Exec(exe, MultiHartAtomicsTests.ScW, s2, mem);
        Assert.Equal(1UL, sc2.RegisterResult.Value);
    }

    // ── Multi-hart: cross-hart reservation invalidation ───────────────────────

    [Fact]
    public void MultiHart_OtherHartWritesBetweenLrAndSc_ScFails() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        var exe0 = new Rv32Executor { ReservationTable = table, HartId = 0, };
        var exe1 = new Rv32Executor { ReservationTable = table, HartId = 1, };

        Rv32ArchState s0 = MakeState((2, 0x10), (3, 0xCAFE)); // hart 0
        Rv32ArchState s1 = MakeState((2, 0x10), (3, 0xDEAD)); // hart 1, same addr

        flat.Write(0x10, 0, 4);

        // Hart 0: LR.W → acquires reservation
        Exec(exe0, MultiHartAtomicsTests.LrW, s0, guarded);
        Assert.Equal(1, table.ActiveCount);

        // Hart 1: SW → cancels hart 0's reservation via ReservationAwareMemory
        Exec(exe1, MultiHartAtomicsTests.Sw, s1, guarded);
        Assert.Equal(0, table.ActiveCount);

        // Hart 0: SC.W → must fail
        ExecuteResult sc = Exec(exe0, MultiHartAtomicsTests.ScW, s0, guarded);
        Assert.Equal(1UL, sc.RegisterResult.Value);    // 1 = failure
        Assert.Equal(0xDEADUL, guarded.Read(0x10, 4)); // hart 1's write stands
    }

    [Fact]
    public void MultiHart_NoInterveningWrite_ScSucceeds() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        var exe0 = new Rv32Executor { ReservationTable = table, HartId = 0, };
        Rv32ArchState s0 = MakeState((2, 0x10), (3, 0x4242));

        flat.Write(0x10, 0, 4);
        Exec(exe0, MultiHartAtomicsTests.LrW, s0, guarded);
        ExecuteResult sc = Exec(exe0, MultiHartAtomicsTests.ScW, s0, guarded);

        Assert.Equal(0UL, sc.RegisterResult.Value); // 0 = success
        Assert.Equal(0x4242UL, guarded.Read(0x10, 4));
    }

    [Fact]
    public void MultiHart_WriteToDifferentAddress_DoesNotCancelReservation() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        var exe0 = new Rv32Executor { ReservationTable = table, HartId = 0, };
        var exe1 = new Rv32Executor { ReservationTable = table, HartId = 1, };

        Rv32ArchState s0 = MakeState((2, 0x10), (3, 0x77)); // hart 0 at 0x10
        Rv32ArchState s1 = MakeState((2, 0x20), (3, 0x88)); // hart 1 writes at 0x20

        flat.Write(0x10, 0, 4);
        flat.Write(0x20, 0, 4);

        Exec(exe0, MultiHartAtomicsTests.LrW, s0, guarded);
        Exec(exe1, MultiHartAtomicsTests.Sw, s1, guarded); // different address → must not cancel

        ExecuteResult sc = Exec(exe0, MultiHartAtomicsTests.ScW, s0, guarded);
        Assert.Equal(0UL, sc.RegisterResult.Value); // 0 = success
    }

    [Fact]
    public void MultiHart_HartsHoldSeparateReservations() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        var exe0 = new Rv32Executor { ReservationTable = table, HartId = 0, };
        var exe1 = new Rv32Executor { ReservationTable = table, HartId = 1, };

        Rv32ArchState s0 = MakeState((2, 0x10), (3, 0x11));
        Rv32ArchState s1 = MakeState((2, 0x20), (3, 0x22));

        // Both harts acquire reservations on distinct addresses
        Exec(exe0, MultiHartAtomicsTests.LrW, s0, guarded);
        Exec(exe1, MultiHartAtomicsTests.LrW, s1, guarded);
        Assert.Equal(2, table.ActiveCount);

        // Both SCs succeed independently
        ExecuteResult r0 = Exec(exe0, MultiHartAtomicsTests.ScW, s0, guarded);
        ExecuteResult r1 = Exec(exe1, MultiHartAtomicsTests.ScW, s1, guarded);
        Assert.Equal(0UL, r0.RegisterResult.Value);
        Assert.Equal(0UL, r1.RegisterResult.Value);
    }
}