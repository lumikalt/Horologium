using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Tests for <see cref="MoesiCache"/> coherence correctness.
/// <para>
/// Cache configuration used throughout:
///   capacityBytes=256, ways=2, blockSize=64 → 2 sets
///   set = (address >> 6) &amp; 1
///   Addresses mapping to set 0: 0x000, 0x080, 0x100, 0x180, 0x200, …
///   Addresses mapping to set 1: 0x040, 0x0C0, 0x140, 0x1C0, …
/// </para>
/// <para>
/// IMPORTANT: assertions on dirty data must go through a cache read (or Flush()),
/// never directly against backing — the authoritative copy lives in the M- or O-state
/// cache, and MOESI cache-to-cache supply means backing stays stale while an Owned
/// copy exists.
/// </para>
/// </summary>
public class MoesiCacheTests {
    private const int Capacity = 256;
    private const int Ways = 2;
    private const int Block = 64;

    private static (MoesiBus bus, FlatMemory backing) MakeBus(int backingSize = 0x1000) {
        var backing = new FlatMemory(backingSize);
        var bus = new MoesiBus(backing);
        return (bus, backing);
    }

    private static MoesiCache MakeCache(MoesiBus bus) => new(
        bus, MoesiCacheTests.Capacity, MoesiCacheTests.Ways, MoesiCacheTests.Block
    );

    // ── Single-cache basic correctness ────────────────────────────────────────

    [Fact]
    public void ReadMiss_InstallsLine_Exclusive() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);

        Assert.Equal(MoesiState.Exclusive, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void ReadHit_ReturnsData_ExclusiveUnchanged() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        var payload = new byte[MoesiCacheTests.Block];
        payload[0] = 0xAB;
        payload[1] = 0xCD;
        payload[2] = 0xEF;
        payload[3] = 0x12;
        backing.Load(0x00, payload);
        MoesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);         // cold miss
        ulong val = cache.Read(0x00, 4); // hit

        Assert.Equal(0x12EFCDABUL, val);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(MoesiState.Exclusive, cache.StateOf(0x00));
    }

    [Fact]
    public void WriteHit_Exclusive_TransitionsToModified_NoWriteback() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);  // → E
        cache.Write(0x00, 42, 4); // E → M, no bus traffic

        Assert.Equal(MoesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        // Data must be readable from cache, not backing yet
        Assert.Equal(42UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteHit_Modified_StaysModified_NoWriteback() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);
        cache.Write(0x00, 1, 4);
        cache.Write(0x00, 2, 4);

        Assert.Equal(MoesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        Assert.Equal(2UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteMiss_InstallsLine_Modified_WriteAllocate() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xDEADBEEFUL, 4);

        Assert.Equal(MoesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x00, 4));
    }

    [Fact]
    public void Flush_WritesMLinesToBacking() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xCAFE1234UL, 4);
        Assert.NotEqual(0xCAFE1234UL, backing.Read(0x00, 4)); // not yet in backing

        cache.Flush();
        Assert.Equal(0xCAFE1234UL, backing.Read(0x00, 4));
        Assert.Equal(1, cache.Writebacks);
    }

    [Fact]
    public void StateOf_Invalid_WhenLineNotPresent() {
        (MoesiBus bus, _) = MakeBus();
        MoesiCache cache = MakeCache(bus);
        Assert.Equal(MoesiState.Invalid, cache.StateOf(0x00));
    }

    [Fact]
    public void Evict_Modified_WritesBackToBacking() {
        // With 256B / 2-way / 64B: 2 sets. Set 0 = addr where bit6 == 0.
        // Addresses 0x000, 0x080, 0x100 all map to set 0 (3 > 2 ways → eviction).
        (MoesiBus bus, FlatMemory backing) = MakeBus(0x400);
        backing.Load(0x000, new byte[MoesiCacheTests.Block]);
        backing.Load(0x080, new byte[MoesiCacheTests.Block]);
        backing.Load(0x100, new byte[MoesiCacheTests.Block]);
        MoesiCache cache = MakeCache(bus);

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
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // BusRead: c0 E→S, c1→S

        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void TwoCaches_WriteInvalidatesSharedCopy() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4);  // c0: E
        _ = c1.Read(0x00, 4);  // both S
        c0.Write(0x00, 99, 4); // BusReadInvalidate: c1→I, c0→M

        Assert.Equal(MoesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks); // S→M upgrade requires no writeback
    }

    [Fact]
    public void TwoCaches_ReadModified_SuppliedCacheToCache_OwnerKeepsDirty() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0xABCDUL, 4); // c0: M

        ulong val = c1.Read(0x00, 4); // BusRead: c0 M→O, supplies the block cache-to-cache

        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(0xABCDUL, val);              // c1 sees c0's written value
        Assert.Equal(0, c0.Writebacks);           // no writeback — data moved cache-to-cache
        Assert.Equal(1, c1.PeerSupplies);         // c1's fill came from c0
        Assert.Equal(0UL, backing.Read(0x00, 4)); // backing is stale while c0 owns the line
    }

    [Fact]
    public void TwoCaches_ExclusiveUpgrade_ToModified_NoBusTraffic() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4);  // c0: E
        c0.Write(0x00, 77, 4); // E→M silently, no bus traffic

        Assert.Equal(MoesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);
        Assert.Equal(0, c1.Writebacks);
    }

    [Fact]
    public void TwoCaches_FullCoherenceRoundTrip() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        // Step 1: c0 writes → M
        c0.Write(0x00, 0x1111UL, 4);
        Assert.Equal(MoesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));

        // Step 2: c1 reads → c0 supplies cache-to-cache, M→O; c1→S, c1 sees c0's value
        ulong v1 = c1.Read(0x00, 4);
        Assert.Equal(0x1111UL, v1);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));

        // Step 3: c1 writes → c0 O→writeback+I, c1→M
        c1.Write(0x00, 0x2222UL, 4);
        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c1.StateOf(0x00));
        Assert.Equal(1, c0.Writebacks); // dirty owner wrote back when invalidated

        // Step 4: c0 reads → c1 supplies cache-to-cache, M→O; c0→S, c0 sees c1's value
        ulong v0 = c0.Read(0x00, 4);
        Assert.Equal(0x2222UL, v0);
        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Owned, c1.StateOf(0x00));
    }

    [Fact]
    public void TwoCaches_WriteAfterSharedWrite_SeesLatestValue() {
        // c0 writes X, c1 reads X (both S), c1 writes X, c0 reads X → c0 sees c1's write.
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

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
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MoesiBus(backing, table);
        MakeCache(bus);
        MoesiCache cache1 = MakeCache(bus); // hart B's cache

        // Simulate an LR.W: hart A holds a reservation but its line is not cached.
        table.Set(0, 0x200);
        Assert.Equal(1, table.ActiveCount);

        // Hart B reads 0x200; no peer holds the line → installs as Exclusive.
        _ = cache1.Read(0x200, 4);
        Assert.Equal(MoesiState.Exclusive, cache1.StateOf(0x200));

        // Hart B writes 0x200 → E→M silent upgrade.  Must cancel hart A's reservation.
        cache1.Write(0x200, 0xABCD, 4);

        Assert.Equal(MoesiState.Modified, cache1.StateOf(0x200));
        Assert.Equal(0, table.ActiveCount); // reservation canceled despite no bus snoop
    }

    [Fact]
    public void SharedUpgrade_CancelsReservation() {
        // Sanity-check the S→M path (BusReadInvalidate) also cancels the reservation.
        var backing = new FlatMemory(0x1000);
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MoesiBus(backing, table);
        MoesiCache cache0 = MakeCache(bus);
        MoesiCache cache1 = MakeCache(bus);

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
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        var table = new ReservationTable();
        var bus = new MoesiBus(backing, table);
        MoesiCache cache0 = MakeCache(bus);

        cache0.Write(0x00, 1, 4); // write-allocate → M
        table.Set(1, 0x40);       // unrelated reservation
        cache0.Write(0x00, 2, 4); // M→M: no bus call, reservation at 0x40 untouched

        Assert.Equal(1, table.ActiveCount); // reservation still intact
    }

    [Fact]
    public void Load_InvalidatesAllCaches() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0: S, c1: S

        // Load replaces backing data and invalidates all cached copies.
        var fresh = new byte[MoesiCacheTests.Block];
        fresh[0] = 0xFF;
        c0.Load(0x00, fresh);

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        // Next read should see the new data.
        Assert.Equal(0xFFUL, c0.Read(0x00, 1));
    }

    // ── CBO cache-maintenance operations ────────────────────────────────────

    [Fact]
    public void FlushLine_ModifiedLine_WritesBackAndInvalidates() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xBEEF, 4); // line → M
        Assert.Equal(MoesiState.Modified, cache.StateOf(0x00));

        cache.FlushLine(0x00);

        Assert.Equal(MoesiState.Invalid, cache.StateOf(0x00));
        Assert.Equal(0xBEEFUL, backing.Read(0x00, 4)); // written back to backing
    }

    [Fact]
    public void FlushLine_InvalidLine_IsNoop() {
        (MoesiBus bus, _) = MakeBus();
        MoesiCache cache = MakeCache(bus);
        // No prior access — line is Invalid.
        cache.FlushLine(0x00); // should not throw
        Assert.Equal(MoesiState.Invalid, cache.StateOf(0x00));
    }

    [Fact]
    public void CleanLine_ModifiedLine_WritesBackAndKeepsExclusive() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xCAFE, 4); // line → M
        cache.CleanLine(0x00);

        Assert.Equal(MoesiState.Exclusive, cache.StateOf(0x00)); // still cached, now clean
        Assert.Equal(0xCAFEUL, backing.Read(0x00, 4));           // data in backing
        Assert.Equal(0xCAFEUL, cache.Read(0x00, 4));             // still readable from cache
    }

    [Fact]
    public void CleanLine_SharedLine_IsNoop() {
        (MoesiBus bus, _) = MakeBus();
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // E
        _ = c1.Read(0x00, 4); // c0→S, c1→S

        c0.CleanLine(0x00); // S is already clean; no state change
        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
    }

    [Fact]
    public void InvalidateLine_ModifiedLine_WritesBackAndInvalidates() {
        // Conservative: cbo.inval writes back dirty data before invalidating.
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x00, 0xDEAD, 4);
        cache.InvalidateLine(0x00);

        Assert.Equal(MoesiState.Invalid, cache.StateOf(0x00));
        Assert.Equal(0xDEADUL, backing.Read(0x00, 4));
    }

    [Fact]
    public void FlushLine_MidBlockAddress_FlushesContainingBlock() {
        // CBO operates on the block containing the address, not just the word.
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x04, 0x42, 4); // write at offset 4 within the 0x00 block
        cache.FlushLine(0x08);      // address in the same block

        Assert.Equal(MoesiState.Invalid, cache.StateOf(0x00));
        Assert.Equal(0x42UL, backing.Read(0x04, 4));
    }

    [Fact]
    public void CboFlush_MultiHart_MakesWriteVisibleToRemoteRead() {
        // H0 writes to cache0 (M state). H0 calls cbo.flush: data written to backing,
        // line invalidated. H1 then reads via cache1: gets the flushed data from backing.
        (MoesiBus bus, _) = MakeBus();
        MoesiCache cache0 = MakeCache(bus);
        MoesiCache cache1 = MakeCache(bus);

        cache0.Write(0x00, 0xF00D, 4); // cache0 line → M
        Assert.Equal(MoesiState.Modified, cache0.StateOf(0x00));

        cache0.FlushLine(0x00); // cbo.flush: writeback + invalidate

        Assert.Equal(MoesiState.Invalid, cache0.StateOf(0x00));
        // cache1 BusRead sees authoritative data from backing
        Assert.Equal(0xF00DUL, cache1.Read(0x00, 4));
        Assert.Equal(MoesiState.Exclusive, cache1.StateOf(0x00));
    }

    // ── MOESI ownership & cache-to-cache supply ──────────────────────────────

    [Fact]
    public void ExclusiveLine_SuppliedCacheToCache_OnPeerRead() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        var payload = new byte[MoesiCacheTests.Block];
        payload[0] = 0x77;
        backing.Load(0x00, payload);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 1);         // c0: E
        ulong val = c1.Read(0x00, 1); // c0 E→S, supplies clean block

        Assert.Equal(0x77UL, val);
        Assert.Equal(1, c1.PeerSupplies);
        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void OwnedLine_SuppliesEveryLaterReader_BackingStaysStale() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);
        MoesiCache c2 = MakeCache(bus);

        c0.Write(0x00, 0xFEED, 4);    // c0: M
        _ = c1.Read(0x00, 4);         // c0: M→O, supplies
        ulong val = c2.Read(0x00, 4); // c0: O→O, supplies again

        Assert.Equal(0xFEEDUL, val);
        Assert.Equal(1, c2.PeerSupplies);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c2.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);
        Assert.Equal(0UL, backing.Read(0x00, 4)); // still stale — c0 owns writeback
    }

    [Fact]
    public void OwnedLine_Eviction_WritesBackToBacking() {
        (MoesiBus bus, FlatMemory backing) = MakeBus(0x400);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x000, 0xBEEF, 4); // c0: M
        _ = c1.Read(0x000, 4);      // c0: O (dirty), c1: S
        // Fill set 0 of c0 with two more lines to evict the Owned line (2 ways).
        _ = c0.Read(0x080, 4);
        _ = c0.Read(0x100, 4);

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x000));
        Assert.Equal(1, c0.Writebacks);
        Assert.Equal(0xBEEFUL, backing.Read(0x000, 4)); // owner paid the writeback on evict
        Assert.Equal(0xBEEFUL, c1.Read(0x000, 4));      // sharer copy still valid
    }

    [Fact]
    public void OwnedLine_WriteUpgrade_InvalidatesSharers() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0x1111, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O, c1: S
        c0.Write(0x00, 0x2222, 4); // O→M: BusReadInvalidate snoops c1

        Assert.Equal(MoesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);           // upgrade needs no writeback
        Assert.Equal(0x2222UL, c1.Read(0x00, 4)); // supplied fresh by the new owner
    }

    [Fact]
    public void WriteBySharer_InvalidatedOwner_WritesBack() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0xAAAA, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O, c1: S
        c1.Write(0x00, 0xBBBB, 4); // S→M: c0 O→writeback+I

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c1.StateOf(0x00));
        Assert.Equal(1, c0.Writebacks);
        Assert.Equal(0xBBBBUL, c1.Read(0x00, 4));
    }

    [Fact]
    public void CleanLine_OwnedLine_WritesBackAndDropsToShared() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0xCAFE, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O, c1: S
        c0.CleanLine(0x00);        // cbo.clean: writeback, ownership returns to memory

        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00)); // peers still share — not E
        Assert.Equal(0xCAFEUL, backing.Read(0x00, 4));
    }

    [Fact]
    public void CrossBoundaryRead_SeesOwnDirtyLine() {
        // A read straddling two blocks goes to backing directly; dirty holders —
        // including the reading cache itself — must sync to backing first.
        (MoesiBus bus, _) = MakeBus();
        MoesiCache cache = MakeCache(bus);

        cache.Write(0x3C, 0x11223344, 4); // last word of block 0x00 → M
        ulong val = cache.Read(0x3E, 4);  // straddles 0x00/0x40

        Assert.Equal(0x1122UL, val & 0xFFFF);                   // low half from the dirty block
        Assert.Equal(MoesiState.Modified, cache.StateOf(0x00)); // state untouched
    }

    [Fact]
    public void CrossBoundaryRead_SeesPeerOwnedLine() {
        (MoesiBus bus, _) = MakeBus();
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x3C, 0x11223344, 4); // c0: M on block 0x00
        _ = c1.Read(0x00, 4);          // c0: O — backing still stale
        ulong val = c1.Read(0x3E, 4);  // straddles 0x00/0x40 → backing after sync

        Assert.Equal(0x1122UL, val & 0xFFFF);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00)); // sync does not change state
    }

    [Fact]
    public void PeerSupply_ChargesPeerSupplyLatency_NotMissLatency() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        var c0 = new MoesiCache(bus, MoesiCacheTests.Capacity, MoesiCacheTests.Ways, MoesiCacheTests.Block, 10, 3);
        var c1 = new MoesiCache(bus, MoesiCacheTests.Capacity, MoesiCacheTests.Ways, MoesiCacheTests.Block, 10, 3);

        c0.Write(0x00, 0xF00D, 4); // c0 miss → fills from backing
        Assert.Equal(10, c0.ConsumePendingStalls());

        _ = c1.Read(0x00, 4); // supplied cache-to-cache by c0
        Assert.Equal(3, c1.ConsumePendingStalls());
    }

    // ── Read-for-ownership: cache-to-cache supply on write misses ────────────

    [Fact]
    public void WriteMiss_ModifiedPeer_ForwardsBlock_NoWriteback() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        c0.Write(0x00, 0x1111, 4); // c0: M
        c0.Write(0x04, 0x2222, 4); // second word in the same M line
        c1.Write(0x00, 0x3333, 4); // write miss: c0 forwards the block and invalidates

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks); // forwarded, not written back
        Assert.Equal(1, c1.PeerSupplies);
        Assert.Equal(0UL, backing.Read(0x04, 4)); // backing never saw c0's data...
        Assert.Equal(0x2222UL, c1.Read(0x04, 4)); // ...but c1's line carries it
        Assert.Equal(0x3333UL, c1.Read(0x00, 4));
    }

    [Fact]
    public void WriteMiss_OwnedPeerWithSharer_OwnerForwards_SharerInvalidated() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);
        MoesiCache c2 = MakeCache(bus);

        c0.Write(0x04, 0xAAAA, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O, c1: S
        c2.Write(0x00, 0xCCCC, 4); // write miss: c0 (O) forwards+I, c1 (S) invalidated

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c2.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);
        Assert.Equal(1, c2.PeerSupplies);
        Assert.Equal(0xAAAAUL, c2.Read(0x04, 4)); // the O owner's dirty word travelled with the forward
    }

    [Fact]
    public void WriteMiss_ExclusivePeer_ForwardsClean() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        var payload = new byte[MoesiCacheTests.Block];
        payload[8] = 0x5A;
        backing.Load(0x00, payload);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4);      // c0: E
        c1.Write(0x00, 0x7777, 4); // write miss: c0 forwards clean block, → I

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c1.StateOf(0x00));
        Assert.Equal(1, c1.PeerSupplies);
        Assert.Equal(0x5AUL, c1.Read(0x08, 1)); // untouched byte came through the forward
    }

    [Fact]
    public void WriteMiss_SharedPeersOnly_FillsFromBacking() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        MoesiCache c0 = MakeCache(bus);
        MoesiCache c1 = MakeCache(bus);
        MoesiCache c2 = MakeCache(bus);

        _ = c0.Read(0x00, 4);      // c0: E
        _ = c1.Read(0x00, 4);      // c0: S, c1: S — memory clean, no owner
        c2.Write(0x00, 0x9999, 4); // write miss: S holders don't forward

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c2.StateOf(0x00));
        Assert.Equal(0, c2.PeerSupplies); // filled from (clean) backing
    }

    [Fact]
    public void WriteMissForward_ChargesPeerSupplyLatency() {
        (MoesiBus bus, FlatMemory backing) = MakeBus();
        backing.Load(0x00, new byte[MoesiCacheTests.Block]);
        var c0 = new MoesiCache(bus, MoesiCacheTests.Capacity, MoesiCacheTests.Ways, MoesiCacheTests.Block, 10, 3);
        var c1 = new MoesiCache(bus, MoesiCacheTests.Capacity, MoesiCacheTests.Ways, MoesiCacheTests.Block, 10, 3);

        c0.Write(0x00, 1, 4); // miss, no supplier → full miss latency
        Assert.Equal(10, c0.ConsumePendingStalls());

        c1.Write(0x00, 2, 4); // miss, forwarded by c0 → peer-supply latency
        Assert.Equal(3, c1.ConsumePendingStalls());
    }

    [Fact]
    public void WriteMissForward_CancelsReservation() {
        // The RFO transaction must cancel LR/SC reservations just like BusReadInvalidate.
        var backing = new FlatMemory(0x1000);
        var table = new ReservationTable();
        var bus = new MoesiBus(backing, table);
        MoesiCache cache0 = MakeCache(bus);
        MoesiCache cache1 = MakeCache(bus);

        _ = cache0.Read(0x00, 4); // cache0: E (will forward on the RFO)
        table.Set(0, 0x00);       // hart 0 reserves within the line
        Assert.Equal(1, table.ActiveCount);

        cache1.Write(0x00, 42, 4); // write miss → BusReadForOwnership

        Assert.Equal(1, cache1.PeerSupplies);
        Assert.Equal(0, table.ActiveCount); // reservation cancelled by the RFO
    }
}