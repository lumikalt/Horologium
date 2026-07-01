using Mechanism;
using Orrery.Cache;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;

namespace Tests.RiscV32;

/// <summary>
/// Integration tests for <see cref="MultiHartKernel"/>: round-robin scheduling,
/// independent execution, and LR/SC cross-hart invalidation end-to-end.
///
/// Encoded instructions used below:
///   addi x1, x0, 42      = 0x02A00093
///   addi x1, x0, 99      = 0x06300093
///   lr.w  x1, (x2)       = 0x100120AF
///   sc.w  x4, x3, (x2)   = 0x1831222F
///   sw    x5, 0(x6)      = 0x00532023
///   ebreak                = 0x00100073
/// </summary>
public class MultiHartKernelTests {
    private const uint Ebreak = 0x00100073;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FlatMemory LoadProgram(uint startAddress, uint[] words, int memSize = 0x2000) {
        var mem = new FlatMemory(memSize);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(startAddress, bytes);
        return mem;
    }

    // ── Independent execution ─────────────────────────────────────────────────

    [Fact]
    public void TwoHarts_RunIndependently_BothHalt() {
        // addi x1, x0, 42; ebreak  →  hart 0
        // addi x1, x0, 99; ebreak  →  hart 1
        const uint Addi42 = 0x02A00093;
        const uint Addi99 = 0x06300093;

        var mem = new FlatMemory(0x200);
        mem.Load(0x00, [..BitConverter.GetBytes(Addi42), ..BitConverter.GetBytes(MultiHartKernelTests.Ebreak),]);
        mem.Load(0x40, [..BitConverter.GetBytes(Addi99), ..BitConverter.GetBytes(MultiHartKernelTests.Ebreak),]);

        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
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
            new Rv32Mechanism(), new Rv32Mechanism(), new Rv32Mechanism()
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

        var kernel = new MultiHartKernel(mem, new Rv32Mechanism());
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

        const uint LrW = 0x100120AF;   // lr.w  x1, (x2)
        const uint ScWX4 = 0x1831222F; // sc.w  x4, x3, (x2)
        const uint SwX5 = 0x00532023;  // sw    x5, 0(x6)

        var flat = new FlatMemory(0x1000);

        // Program code
        flat.Load(0x00, ToBytes(LrW, ScWX4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(SwX5, MultiHartKernelTests.Ebreak));

        // Initial data at 0x200
        flat.Write(0x200, 0xBEEF, 4);

        var table = new ReservationTable();
        var guarded = new ReservationAwareMemory(flat, table);

        var mech0 = new Rv32Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv32Mechanism(reservationTable: table, hartId: 1);
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
        // Hart 1 writes to a different address → reservation is NOT cancelled → SC succeeds.
        const uint LrW = 0x100120AF;   // lr.w  x1, (x2)
        const uint ScWX4 = 0x1831222F; // sc.w  x4, x3, (x2)
        const uint SwX5 = 0x00532023;  // sw    x5, 0(x6)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(LrW, ScWX4, MultiHartKernelTests.Ebreak));
        flat.Load(0x40, ToBytes(SwX5, MultiHartKernelTests.Ebreak));
        flat.Write(0x200, 0xBEEF, 4);
        flat.Write(0x300, 0, 4); // hart 1's target — different address

        var table = new ReservationTable();
        var guarded = new ReservationAwareMemory(flat, table);

        var mech0 = new Rv32Mechanism(reservationTable: table, hartId: 0);
        var mech1 = new Rv32Mechanism(reservationTable: table, hartId: 1);
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

    // ── Helper ────────────────────────────────────────────────────────────────

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}