#region

using Orrery.Cache;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV64;

#endregion

namespace Tests.RiscV64.MultiHart;

/// <summary>
///     Integration tests for <see cref="MultiHartKernel" />: round-robin scheduling,
///     independent execution, and LR/SC cross-hart invalidation end-to-end.
///     <para>
///         Encoded instructions used below:
///         addi x1, x0, 42      = 0x02A00093
///         addi x1, x0, 99      = 0x06300093
///         lr.w  x1, (x2)       = 0x100120AF
///         sc.w  x4, x3, (x2)   = 0x1831222F
///         sw    x5, 0(x6)      = 0x00532023
///         ebreak                = 0x00100073
///     </para>
/// </summary>
public class MultiHartKernelTests {
    private const uint Ebreak = 0x00100073;

    // ── Independent execution ─────────────────────────────────────────────────

    [Fact]
    public void TwoHarts_RunIndependently_BothHalt() {
        // addi x1, x0, 42; ebreak  →  hart 0
        // addi x1, x0, 99; ebreak  →  hart 1
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        var mem = new FlatMemory(0x200);
        mem.Load(0x00, [..BitConverter.GetBytes(addi42), ..BitConverter.GetBytes(MultiHartKernelTests.Ebreak),]);
        mem.Load(0x40, [..BitConverter.GetBytes(addi99), ..BitConverter.GetBytes(MultiHartKernelTests.Ebreak),]);

        var mech0 = new Rv64Mechanism();
        var mech1 = new Rv64Mechanism();
        var kernel = new MultiHartKernel(mem, mech0, mech1);
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.Run(100);

        Assert.Equal(42UL, kernel.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(99UL, kernel.StateOf(1).IntegerRegisters.Read(1));
    }

    [Fact]
    public void HartCount_IsCorrect() {
        var mem = new FlatMemory(0x100);
        mem.Load(0x00, BitConverter.GetBytes(MultiHartKernelTests.Ebreak));
        mem.Load(0x10, BitConverter.GetBytes(MultiHartKernelTests.Ebreak));
        mem.Load(0x20, BitConverter.GetBytes(MultiHartKernelTests.Ebreak));

        var kernel = new MultiHartKernel(
            mem,
            new Rv64Mechanism(), new Rv64Mechanism(), new Rv64Mechanism()
        );
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x10);
        kernel.SetEntryPoint(2, 0x20);

        Assert.Equal(3, kernel.HartCount);
        kernel.Run();
        Assert.Equal(1L, kernel.Ticks); // all three harts halt on the same step
    }

    [Fact]
    public void Step_ReturnsZero_WhenAllHalted() {
        var mem = new FlatMemory(0x100);
        mem.Load(0x00, BitConverter.GetBytes(MultiHartKernelTests.Ebreak));

        var kernel = new MultiHartKernel(mem, new Rv64Mechanism());
        kernel.SetEntryPoint(0, 0x00);

        int first = kernel.Step();  // hart executes ebreak → halted
        int second = kernel.Step(); // all halted

        Assert.Equal(0, first); // ebreak returns IsHalt: halted before counting as active
        Assert.Equal(0, second);
    }

    // ── LR/SC cross-hart invalidation ─────────────────────────────────────────

    [Fact]
    public void CrossHart_InterleavedWrite_CausesScToFail() {
        // Hart 0 at 0x00: lr.w x1, (x2) → sc.w x4, x3, (x2) → ebreak
        // Hart 1 at 0x40: sw x5, 0(x6)  → ebreak
        //
        // Round-robin (Step 1): H0 runs lr.w → reserves 0x200; H1 runs sw → cancels reservation
        // Round-robin (Step 2): H0 runs sc.w → x4=1 (fail); H1 runs ebreak → halted
        // Round-robin (Step 3): H0 runs ebreak → halted
        //
        // x4 == 1 proves the reservation was invalidated by hart 1's store.

        const uint lrW = 0x100120AF;   // lr.w  x1, (x2)
        const uint scWx4 = 0x1831222F; // sc.w  x4, x3, (x2)
        const uint swX5 = 0x00532023;  // sw    x5, 0(x6)

        var flat = new FlatMemory(0x1000);

        // Program code
        flat.Load(0x00, ToBytes(lrW, scWx4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(swX5, MultiHartKernelTests.Ebreak));

        // Initial data at 0x200
        flat.Write(0x200, 0xBEEF, 4);

        var table = new ReservationTable();
        var guarded = new ReservationAwareMemory(flat, table);

        var mech0 = new Rv64Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv64Mechanism(reservationTable: table, hartId: 1);
        var kernel = new MultiHartKernel(guarded, mech0, mech1);

        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        // Hart 0: x2=addr, x3=desired write value
        kernel.StateOf(0).IntegerRegisters.Write(2, 0x200);
        kernel.StateOf(0).IntegerRegisters.Write(3, 0xCAFE);
        // Hart 1: x5=new value, x6=same addr
        kernel.StateOf(1).IntegerRegisters.Write(5, 0xDEAD);
        kernel.StateOf(1).IntegerRegisters.Write(6, 0x200);

        kernel.Run(100);

        // SC must have failed because hart 1 stored to the reserved granule first
        Assert.Equal(1UL, kernel.StateOf(0).IntegerRegisters.Read(4)); // 1 = failure
        // Hart 1's write stands
        Assert.Equal(0xDEADUL, flat.Read(0x200, 4));
    }

    [Fact]
    public void CrossHart_NoInterleavingWrite_ScSucceeds() {
        // Hart 1 writes to a different address → reservation is NOT canceled → SC succeeds.
        const uint lrW = 0x100120AF;   // lr.w  x1, (x2)
        const uint scWx4 = 0x1831222F; // sc.w  x4, x3, (x2)
        const uint swX5 = 0x00532023;  // sw    x5, 0(x6)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(lrW, scWx4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(swX5, MultiHartKernelTests.Ebreak));
        flat.Write(0x200, 0xBEEF, 4);
        flat.Write(0x300, 0, 4); // hart 1's target — different address

        var table = new ReservationTable();
        var guarded = new ReservationAwareMemory(flat, table);

        var mech0 = new Rv64Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv64Mechanism(reservationTable: table, hartId: 1);
        var kernel = new MultiHartKernel(guarded, mech0, mech1);

        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.StateOf(0).IntegerRegisters.Write(2, 0x200);
        kernel.StateOf(0).IntegerRegisters.Write(3, 0xCAFE);
        kernel.StateOf(1).IntegerRegisters.Write(5, 0xDEAD);
        kernel.StateOf(1).IntegerRegisters.Write(6, 0x300); // different address

        kernel.Run(100);

        Assert.Equal(0UL, kernel.StateOf(0).IntegerRegisters.Read(4)); // 0 = success
        Assert.Equal(0xCAFEUL, flat.Read(0x200, 4));                   // hart 0's write committed
    }

    // ── LR/SC over per-hart MOESIF caches ──────────────────────────────────────

    [Fact]
    public void MoesifCaches_InterleavedWrite_CausesScToFail() {
        // Same scenario as CrossHart_InterleavedWrite_CausesScToFail but using per-hart
        // MoesifCaches instead of a flat ReservationAwareMemory.
        //
        // Hart 0 at 0x00: lr.w x1,(x2)  →  sc.w x4,x3,(x2)  →  ebreak
        // Hart 1 at 0x40: sw x5, 0(x6)  →  ebreak
        //
        // Tick 1: H0 lr.w → reserve 0x200; H1 sw 0x200 → BusReadInvalidate → reservation canceled.
        // Tick 2: H0 sc.w → TryConsume fails → x4=1 (SC failure).

        const uint lrW = 0x100120AF;
        const uint scWx4 = 0x1831222F;
        const uint swX5 = 0x00532023;

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(lrW, scWx4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(swX5, MultiHartKernelTests.Ebreak));
        flat.Write(0x200, 0xBEEF, 4);

        var table = new ReservationTable();
        var bus = new MoesifBus(flat, table);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var mech0 = new Rv64Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv64Mechanism(reservationTable: table, hartId: 1);
        var kernel = new MultiHartKernel([cache0, cache1,], mech0, mech1);

        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.StateOf(0).IntegerRegisters.Write(2, 0x200);  // LR/SC address
        kernel.StateOf(0).IntegerRegisters.Write(3, 0xCAFE); // desired SC value
        kernel.StateOf(1).IntegerRegisters.Write(5, 0xDEAD); // SW value
        kernel.StateOf(1).IntegerRegisters.Write(6, 0x200);  // SW address (same line)

        kernel.Run(100);

        // SC must fail: hart 1's store invalidated the reservation via BusReadInvalidate
        Assert.Equal(1UL, kernel.StateOf(0).IntegerRegisters.Read(4)); // 1 = SC failure
        // Hart 1's write stands
        cache1.Flush();
        Assert.Equal(0xDEADUL, flat.Read(0x200, 4));
    }

    [Fact]
    public void MoesifCaches_NoInterleavingWrite_ScSucceeds() {
        // Hart 1 writes to a different cache line → BusReadInvalidate targets a different
        // lineBase → reservation at 0x200 is not canceled → SC succeeds.

        const uint lrW = 0x100120AF;
        const uint scWx4 = 0x1831222F;
        const uint swX5 = 0x00532023;

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(lrW, scWx4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(swX5, MultiHartKernelTests.Ebreak));
        flat.Write(0x200, 0xBEEF, 4);
        flat.Write(0x300, 0, 4); // different 64-byte line (0x2C0..0x2FF vs 0x200..0x23F)

        var table = new ReservationTable();
        var bus = new MoesifBus(flat, table);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var mech0 = new Rv64Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv64Mechanism(reservationTable: table, hartId: 1);
        var kernel = new MultiHartKernel([cache0, cache1,], mech0, mech1);

        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.StateOf(0).IntegerRegisters.Write(2, 0x200);
        kernel.StateOf(0).IntegerRegisters.Write(3, 0xCAFE);
        kernel.StateOf(1).IntegerRegisters.Write(5, 0xDEAD);
        kernel.StateOf(1).IntegerRegisters.Write(6, 0x300); // different line

        kernel.Run(100);

        Assert.Equal(0UL, kernel.StateOf(0).IntegerRegisters.Read(4)); // 0 = SC success
        cache0.Flush();
        Assert.Equal(0xCAFEUL, flat.Read(0x200, 4));
    }

    // ── Per-hart MOESIF cache coherence ─────────────────────────────────────────

    [Fact]
    public void PerHartMoesifCaches_WriteByHart0_ReadByHart1_SeesCoherentValue() {
        // Hart 0 at 0x00: sw x1, 0(x2)   →  stores 0xCAFE to address 0x200 via cache0 (→ M)
        //                 ebreak
        // Hart 1 at 0x40: lw x3, 0(x4)   →  loads from 0x200 via cache1 (BusRead snoops cache0)
        //                 ebreak
        //
        // Round-robin tick 1: H0 sw → cache0 line 0x200 is Modified;
        //                     H1 lw → BusRead: cache0 M→writeback+S, cache1 installs S, reads 0xCAFE.
        // After run: both caches hold the line in Shared state.
        //
        // sw x1, 0(x2) = 0x00112023   lw x3, 0(x4) = 0x00022183

        const uint swX1 = 0x00112023;
        const uint lwX3 = 0x00022183;

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(swX1, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(lwX3, MultiHartKernelTests.Ebreak));

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var mech0 = new Rv64Mechanism();
        var mech1 = new Rv64Mechanism();
        var kernel = new MultiHartKernel([cache0, cache1,], mech0, mech1);

        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        kernel.StateOf(0).IntegerRegisters.Write(1, 0xCAFE); // value to store
        kernel.StateOf(0).IntegerRegisters.Write(2, 0x200);  // store address
        kernel.StateOf(1).IntegerRegisters.Write(4, 0x200);  // load address

        kernel.Run(100);

        // Coherence: hart 1 must see the value hart 0 wrote
        Assert.Equal(0xCAFEUL, kernel.StateOf(1).IntegerRegisters.Read(3));

        // Writer supplies cache-to-cache and keeps the line as Owned; reader installs Shared
        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}