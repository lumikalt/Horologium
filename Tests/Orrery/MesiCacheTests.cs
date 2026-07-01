using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Tests for <see cref="MesiCache"/> coherence correctness.
///
/// Cache configuration used throughout:
///   capacityBytes=256, ways=2, blockSize=64 → 2 sets
///   set = (address >> 6) &amp; 1
///   Addresses mapping to set 0: 0x000, 0x080, 0x100, 0x180, 0x200, …
///   Addresses mapping to set 1: 0x040, 0x0C0, 0x140, 0x1C0, …
///
/// IMPORTANT: assertions on Modified data must go through a cache read (or Flush()),
/// never directly against backing — the authoritative copy lives in the M-state cache.
/// </summary>
public class MesiCacheTests {
    private const int Capacity = 256;
    private const int Ways = 2;
    private const int Block = 64;

    private static (MesiBus bus, FlatMemory backing) MakeBus(int backingSize = 0x1000) {
        var backing = new FlatMemory(backingSize);
        var bus = new MesiBus(backing);
        return (bus, backing);
    }

    private static MesiCache MakeCache(MesiBus bus) => new(
        bus, MesiCacheTests.Capacity, MesiCacheTests.Ways, MesiCacheTests.Block
    );

    // ── Single-cache basic correctness ────────────────────────────────────────

    [Fact]
    public void ReadMiss_InstallsLine_Exclusive() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);

        Assert.Equal(MesiState.Exclusive, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void ReadHit_ReturnsData_ExclusiveUnchanged() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        var payload = new byte[MesiCacheTests.Block];
        payload[0] = 0xAB;
        payload[1] = 0xCD;
        payload[2] = 0xEF;
        payload[3] = 0x12;
        backing.Load(0x00, payload);
        MesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);         // cold miss
        ulong val = cache.Read(0x00, 4); // hit

        Assert.Equal(0x12EFCDABUL, val);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(MesiState.Exclusive, cache.StateOf(0x00));
    }

    [Fact]
    public void WriteHit_Exclusive_TransitionsToModified_NoWriteback() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);  // → E
        cache.Write(0x00, 42, 4); // E → M, no bus traffic

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        // Data must be readable from cache, not backing yet
        Assert.Equal(42UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteHit_Modified_StaysModified_NoWriteback() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);
        cache.Write(0x00, 1, 4);
        cache.Write(0x00, 2, 4);

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        Assert.Equal(2UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteMiss_InstallsLine_Modified_WriteAllocate() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xDEADBEEFUL, 4);

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x00, 4));
    }

    [Fact]
    public void Flush_WritesMLinesToBacking() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xCAFE1234UL, 4);
        Assert.NotEqual(0xCAFE1234UL, backing.Read(0x00, 4)); // not yet in backing

        cache.Flush();
        Assert.Equal(0xCAFE1234UL, backing.Read(0x00, 4));
        Assert.Equal(1, cache.Writebacks);
    }

    [Fact]
    public void StateOf_Invalid_WhenLineNotPresent() {
        (MesiBus bus, _) = MakeBus();
        MesiCache cache = MakeCache(bus);
        Assert.Equal(MesiState.Invalid, cache.StateOf(0x00));
    }

    [Fact]
    public void Evict_Modified_WritesBackToBacking() {
        // With 256B / 2-way / 64B: 2 sets. Set 0 = addr where bit6 == 0.
        // Addresses 0x000, 0x080, 0x100 all map to set 0 (3 > 2 ways → eviction).
        (MesiBus bus, FlatMemory backing) = MakeBus(0x400);
        backing.Load(0x000, new byte[MesiCacheTests.Block]);
        backing.Load(0x080, new byte[MesiCacheTests.Block]);
        backing.Load(0x100, new byte[MesiCacheTests.Block]);
        MesiCache cache = MakeCache(bus);

        cache.Write(0x000, 0xBEEFUL, 4); // set 0, way 0 → M, MRU
        cache.Write(0x080, 0xCAFEUL, 4); // set 0, way 1 → M (evicts invalid), now MRU
        // LRU is now way 0 (0x000). Writing 0x100 evicts it → writeback 0xBEEF.
        cache.Write(0x100, 0xDEADUL, 4); // set 0 → evict LRU (way 0) → write 0xBEEF to backing

        Assert.Equal(1, cache.Writebacks);
        Assert.Equal(0xBEEFUL, backing.Read(0x000, 4));
    }

    // ── Two-cache coherence ───────────────────────────────────────────────────

    [Fact]
    public void TwoCaches_BothRead_BothGetShared() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // BusRead: c0 E→S, c1→S

        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void TwoCaches_WriteInvalidatesSharedCopy() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4);  // c0: E
        _ = c1.Read(0x00, 4);  // both S
        c0.Write(0x00, 99, 4); // BusReadInvalidate: c1→I, c0→M

        Assert.Equal(MesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks); // S→M upgrade requires no writeback
    }

    [Fact]
    public void TwoCaches_ReadModified_CausesWriteback_BothShared() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0xABCDUL, 4); // c0: M

        ulong val = c1.Read(0x00, 4); // BusRead: c0 M→writeback+S, c1→S (reads from backing)

        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(0xABCDUL, val);    // c1 sees c0's written value
        Assert.Equal(1, c0.Writebacks); // c0 wrote back on snoop
    }

    [Fact]
    public void TwoCaches_ExclusiveUpgrade_ToModified_NoBusTraffic() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4);  // c0: E
        c0.Write(0x00, 77, 4); // E→M silently, no bus traffic

        Assert.Equal(MesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);
        Assert.Equal(0, c1.Writebacks);
    }

    [Fact]
    public void TwoCaches_FullCoherenceRoundTrip() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        // Step 1: c0 writes → M
        c0.Write(0x00, 0x1111UL, 4);
        Assert.Equal(MesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));

        // Step 2: c1 reads → c0 writeback+S, c1→S, c1 sees c0's value
        ulong v1 = c1.Read(0x00, 4);
        Assert.Equal(0x1111UL, v1);
        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));

        // Step 3: c1 writes → c0→I, c1→M
        c1.Write(0x00, 0x2222UL, 4);
        Assert.Equal(MesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MesiState.Modified, c1.StateOf(0x00));

        // Step 4: c0 reads → c1 writeback+S, c0→S, c0 sees c1's value
        ulong v0 = c0.Read(0x00, 4);
        Assert.Equal(0x2222UL, v0);
        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void TwoCaches_WriteAfterSharedWrite_SeesLatestValue() {
        // c0 writes X, c1 reads X (both S), c1 writes X, c0 reads X → c0 sees c1's write.
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0xAAAAUL, 4);  // c0: M
        _ = c1.Read(0x00, 4);         // c0→S, c1→S
        c1.Write(0x00, 0xBBBBUL, 4);  // c0→I, c1→M
        ulong val = c0.Read(0x00, 4); // c1→S, c0→S, sees 0xBBBB

        Assert.Equal(0xBBBBUL, val);
    }

    // ── LR/SC reservation cancellation ───────────────────────────────────────

    [Fact]
    public void SilentExclusiveUpgrade_CancelsReservation() {
        // Scenario: hart A has an active LR reservation at 0x200 but its cache line was
        // evicted (so cache0 is empty).  Hart B reads 0x200 → Exclusive (no peer holds it).
        // Hart B then writes 0x200 → E→M silent upgrade — no bus snoop is issued, but
        // BusSilentUpgrade must still cancel hart A's reservation.
        var backing = new FlatMemory(0x1000);
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MesiBus(backing, table);
        MesiCache cache0 = MakeCache(bus); // hart A's cache — deliberately left empty
        MesiCache cache1 = MakeCache(bus); // hart B's cache

        // Simulate an LR.W: hart A holds a reservation but its line is not cached.
        table.Set(0, 0x200);
        Assert.Equal(1, table.ActiveCount);

        // Hart B reads 0x200; no peer holds the line → installs as Exclusive.
        _ = cache1.Read(0x200, 4);
        Assert.Equal(MesiState.Exclusive, cache1.StateOf(0x200));

        // Hart B writes 0x200 → E→M silent upgrade.  Must cancel hart A's reservation.
        cache1.Write(0x200, 0xABCD, 4);

        Assert.Equal(MesiState.Modified, cache1.StateOf(0x200));
        Assert.Equal(0, table.ActiveCount); // reservation cancelled despite no bus snoop
    }

    [Fact]
    public void SharedUpgrade_CancelsReservation() {
        // Sanity-check the S→M path (BusReadInvalidate) also cancels the reservation.
        var backing = new FlatMemory(0x1000);
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MesiBus(backing, table);
        MesiCache cache0 = MakeCache(bus);
        MesiCache cache1 = MakeCache(bus);

        _ = cache0.Read(0x00, 4); // c0: E
        _ = cache1.Read(0x00, 4); // both S

        table.Set(0, 0x00);
        Assert.Equal(1, table.ActiveCount);

        cache1.Write(0x00, 42, 4); // S→M: BusReadInvalidate → cancels reservation

        Assert.Equal(0, table.ActiveCount);
    }

    [Fact]
    public void ModifiedWrite_DoesNotDoubleCancel() {
        // A second write to an already-Modified line (M→M) should not crash or over-cancel.
        var backing = new FlatMemory(0x1000);
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MesiBus(backing, table);
        MesiCache cache0 = MakeCache(bus);

        cache0.Write(0x00, 1, 4); // write-allocate → M
        table.Set(1, 0x40);       // unrelated reservation
        cache0.Write(0x00, 2, 4); // M→M: no bus call, reservation at 0x40 untouched

        Assert.Equal(1, table.ActiveCount); // reservation still intact
    }

    [Fact]
    public void Load_InvalidatesAllCaches() {
        (MesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MesiCacheTests.Block]);
        MesiCache c0 = MakeCache(bus);
        MesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0: S, c1: S

        // Load replaces backing data and invalidates all cached copies.
        var fresh = new byte[MesiCacheTests.Block];
        fresh[0] = 0xFF;
        c0.Load(0x00, fresh);

        Assert.Equal(MesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        // Next read should see the new data.
        Assert.Equal(0xFFUL, c0.Read(0x00, 1));
    }
}