using Mechanism;
using Orrery.Cache;
using Orrery.Scheduling;
using RiscV.Memory;

namespace Tests.Orrery;

public class StoreBufferTests {
    // Advance the escapement to the given tick by scheduling and running a no-op.
    private static void AdvanceTo(Escapement esc, long tick) {
        esc.Schedule(() => { }, tick, Phase.Fetch);
        esc.Run(tick);
    }

    private static (StoreBuffer sb, FlatMemory mem, Escapement esc) Make(int capacity = 8) {
        var mem = new FlatMemory(512);
        var esc = new Escapement();
        AdvanceTo(esc, 1);
        return (new StoreBuffer(mem, esc, capacity), mem, esc);
    }

    // ── Basic forwarding ──────────────────────────────────────────────────────

    [Fact]
    public void Write_ThenRead_SameAddress_Forwards() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 42, 4);
        ulong val = sb.Read(100, 4);
        Assert.Equal(42UL, val);
        Assert.Equal(1L, sb.Forwards);
    }

    [Fact]
    public void Read_NoMatch_GoesToBacking() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        mem.Write(200, 99, 4);
        ulong val = sb.Read(200, 4);
        Assert.Equal(99UL, val);
        Assert.Equal(0L, sb.Forwards);
    }

    [Fact]
    public void Write_ThenRead_DifferentAddress_GoesToBacking() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 42, 4);
        mem.Write(200, 77, 4);
        ulong val = sb.Read(200, 4);
        Assert.Equal(77UL, val);
        Assert.Equal(0L, sb.Forwards);
    }

    [Fact]
    public void MultipleWrites_SameAddress_ForwardsNewest() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 1, 4);
        sb.Write(100, 2, 4);
        sb.Write(100, 3, 4);
        ulong val = sb.Read(100, 4);
        Assert.Equal(3UL, val);
        Assert.Equal(1L, sb.Forwards);
    }

    // ── Partial overlap ───────────────────────────────────────────────────────

    [Fact]
    public void Read_PartialOverlap_DrainsThenReadsBacking() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        mem.Write(100, 0xDEADBEEF, 4); // backing has this
        sb.Write(100, 0x12345678, 4);  // buffer has a word write
        // Read a byte from the middle — partial overlap, not exact match
        ulong val = sb.Read(101, 1);   // byte at offset 1 within the word
        // After DrainAll, buffer flushed 0x12345678 to backing at addr 100.
        // Then read byte at 101 from backing. LE: byte[1] of 0x12345678 = 0x56.
        Assert.Equal(0x56UL, val);
        Assert.Equal(0L, sb.Forwards);
    }

    // ── Drain timing ──────────────────────────────────────────────────────────

    [Fact]
    public void DrainEligible_SameTick_DoesNotDrain() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 42, 4);  // tagged with tick 1
        sb.DrainEligible();    // still tick 1 — should NOT drain
        ulong val = sb.Read(100, 4);
        Assert.Equal(42UL, val); // still in buffer
    }

    [Fact]
    public void DrainEligible_NextTick_Drains() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 42, 4);  // tagged tick 1
        AdvanceTo(esc, 2);     // advance to tick 2
        sb.DrainEligible();    // tick 2 > 1 → should drain to backing
        // Buffer is now empty; read goes to backing
        ulong val = sb.Read(100, 4);
        Assert.Equal(42UL, val); // value now in backing
        Assert.Equal(0L, sb.Forwards); // was not a forward hit
    }

    [Fact]
    public void DrainAll_EmptiesBuffer() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make();
        sb.Write(100, 42, 4);
        sb.Write(200, 77, 4);
        sb.DrainAll();
        // Both entries drained: backing has them, forwards counter still 0
        Assert.Equal(42UL, sb.Read(100, 4));
        Assert.Equal(77UL, sb.Read(200, 4));
        Assert.Equal(0L, sb.Forwards);
    }

    // ── Capacity overflow ─────────────────────────────────────────────────────

    [Fact]
    public void Overflow_DrainsToBackingBeforeAddingNew() {
        (StoreBuffer sb, FlatMemory mem, Escapement esc) = Make(capacity: 2);
        sb.Write(100, 1, 4);
        sb.Write(200, 2, 4);
        sb.Write(300, 3, 4); // capacity exceeded → DrainAll then add
        // All three should be in backing now (first two drained, third added and readable)
        Assert.Equal(3UL, sb.Read(300, 4)); // third is in buffer → forward
        // 100 and 200 were drained to backing
        Assert.Equal(1UL, mem.Read(100, 4));
        Assert.Equal(2UL, mem.Read(200, 4));
    }
}
