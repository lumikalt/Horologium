using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

public class ReservationTableTests {
    // ── Basic set / consume ───────────────────────────────────────────────────

    [Fact]
    public void TryConsume_AfterSet_Succeeds() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        Assert.True(t.TryConsume(0, 0x1000));
    }

    [Fact]
    public void TryConsume_WithoutSet_Fails() {
        var t = new ReservationTable();
        Assert.False(t.TryConsume(0, 0x1000));
    }

    [Fact]
    public void TryConsume_WrongAddress_Fails() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        Assert.False(t.TryConsume(0, 0x2000));
    }

    [Fact]
    public void TryConsume_AlwaysReleasesReservation() {
        // Even when SC fails (wrong address), the reservation is gone afterward.
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.TryConsume(0, 0x2000); // fail
        Assert.Equal(0, t.ActiveCount);
        Assert.False(t.TryConsume(0, 0x1000)); // second SC also fails
    }

    [Fact]
    public void TryConsume_OnSuccess_ReleasesReservation() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        Assert.True(t.TryConsume(0, 0x1000));
        Assert.False(t.TryConsume(0, 0x1000)); // gone
    }

    // ── Granule alignment ─────────────────────────────────────────────────────

    [Fact]
    public void Granule_IsAlignedToFourBytes() {
        // LR.W at 0x1002 → granule = 0x1000; SC.W at 0x1000 should match.
        var t = new ReservationTable();
        t.Set(0, 0x1002);
        Assert.True(t.TryConsume(0, 0x1000));
    }

    // ── Cross-hart invalidation ───────────────────────────────────────────────

    [Fact]
    public void InvalidateAt_CancelsOverlappingReservation() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.InvalidateAt(0x1000, 4); // hart 1 writes the same word
        Assert.False(t.TryConsume(0, 0x1000));
    }

    [Fact]
    public void InvalidateAt_DoesNotCancelNonOverlappingReservation() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.InvalidateAt(0x1004, 4); // adjacent word, no overlap
        Assert.True(t.TryConsume(0, 0x1000));
    }

    [Fact]
    public void InvalidateAt_CancelsAllOverlappingHarts() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.Set(1, 0x1000);
        t.Set(2, 0x2000); // different address
        t.InvalidateAt(0x1000, 4);
        Assert.False(t.TryConsume(0, 0x1000));
        Assert.False(t.TryConsume(1, 0x1000));
        Assert.True(t.TryConsume(2, 0x2000)); // unaffected
    }

    [Fact]
    public void InvalidateAt_WideWrite_CancelsMultipleGranules() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.Set(1, 0x1004);
        t.InvalidateAt(0x1000, 8); // 8-byte write spans both granules
        Assert.False(t.TryConsume(0, 0x1000));
        Assert.False(t.TryConsume(1, 0x1004));
    }

    [Fact]
    public void InvalidateAt_EmptyTable_IsNoop() {
        var t = new ReservationTable();
        t.InvalidateAt(0x1000, 4); // should not throw
        Assert.Equal(0, t.ActiveCount);
    }

    // ── Multi-hart independence ───────────────────────────────────────────────

    [Fact]
    public void DifferentHarts_HoldIndependentReservations() {
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.Set(1, 0x2000);
        Assert.True(t.TryConsume(0, 0x1000));
        Assert.True(t.TryConsume(1, 0x2000));
    }

    [Fact]
    public void Set_ReplacesExistingReservation_OldAddressFails() {
        // After a second LR, SC on the first address must fail.
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.Set(0, 0x2000);
        Assert.False(t.TryConsume(0, 0x1000));
    }

    [Fact]
    public void Set_ReplacesExistingReservation_NewAddressSucceeds() {
        // After a second LR, SC on the new address must succeed.
        var t = new ReservationTable();
        t.Set(0, 0x1000);
        t.Set(0, 0x2000);
        Assert.True(t.TryConsume(0, 0x2000));
    }

    // ── ReservationAwareMemory integration ────────────────────────────────────

    [Fact]
    public void ReservationAwareMemory_Write_InvalidatesReservation() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        table.Set(0, 0x10);
        guarded.Write(0x10, 42, 4);

        Assert.False(table.TryConsume(0, 0x10));
    }

    [Fact]
    public void ReservationAwareMemory_Read_DoesNotInvalidate() {
        var table = new ReservationTable();
        var flat = new FlatMemory(256);
        var guarded = new ReservationAwareMemory(flat, table);

        table.Set(0, 0x10);
        guarded.Read(0x10, 4); // reads must not cancel reservations

        Assert.True(table.TryConsume(0, 0x10));
    }
}