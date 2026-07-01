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

    private static MesiCache MakeCache(MesiBus bus) => new(bus, Capacity, Ways, Block);

    // ── Single-cache basic correctness ────────────────────────────────────────

    [Fact]
    public void ReadMiss_InstallsLine_Exclusive() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);

        Assert.Equal(MesiState.Exclusive, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void ReadHit_ReturnsData_ExclusiveUnchanged() {
        var (bus, backing) = MakeBus();
        var payload = new byte[Block];
        payload[0] = 0xAB; payload[1] = 0xCD; payload[2] = 0xEF; payload[3] = 0x12;
        backing.Load(0x00, payload);
        var cache = MakeCache(bus);

        _ = cache.Read(0x00, 4); // cold miss
        ulong val = cache.Read(0x00, 4); // hit

        Assert.Equal(0x12EFCDABUL, val);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(MesiState.Exclusive, cache.StateOf(0x00));
    }

    [Fact]
    public void WriteHit_Exclusive_TransitionsToModified_NoWriteback() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var cache = MakeCache(bus);

        _ = cache.Read(0x00, 4); // → E
        cache.Write(0x00, 42, 4); // E → M, no bus traffic

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        // Data must be readable from cache, not backing yet
        Assert.Equal(42UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteHit_Modified_StaysModified_NoWriteback() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var cache = MakeCache(bus);

        _ = cache.Read(0x00, 4);
        cache.Write(0x00, 1, 4);
        cache.Write(0x00, 2, 4);

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(0, cache.Writebacks);
        Assert.Equal(2UL, cache.Read(0x00, 4));
    }

    [Fact]
    public void WriteMiss_InstallsLine_Modified_WriteAllocate() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var cache = MakeCache(bus);

        cache.Write(0x00, 0xDEADBEEFUL, 4);

        Assert.Equal(MesiState.Modified, cache.StateOf(0x00));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x00, 4));
    }

    [Fact]
    public void Flush_WritesMLinesToBacking() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var cache = MakeCache(bus);

        cache.Write(0x00, 0xCAFE1234UL, 4);
        Assert.NotEqual(0xCAFE1234UL, backing.Read(0x00, 4)); // not yet in backing

        cache.Flush();
        Assert.Equal(0xCAFE1234UL, backing.Read(0x00, 4));
        Assert.Equal(1, cache.Writebacks);
    }

    [Fact]
    public void StateOf_Invalid_WhenLineNotPresent() {
        var (bus, _) = MakeBus();
        var cache = MakeCache(bus);
        Assert.Equal(MesiState.Invalid, cache.StateOf(0x00));
    }

    [Fact]
    public void Evict_Modified_WritesBackToBacking() {
        // With 256B / 2-way / 64B: 2 sets. Set 0 = addr where bit6 == 0.
        // Addresses 0x000, 0x080, 0x100 all map to set 0 (3 > 2 ways → eviction).
        var (bus, backing) = MakeBus(0x400);
        backing.Load(0x000, new byte[Block]);
        backing.Load(0x080, new byte[Block]);
        backing.Load(0x100, new byte[Block]);
        var cache = MakeCache(bus);

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
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // BusRead: c0 E→S, c1→S

        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void TwoCaches_WriteInvalidatesSharedCopy() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // both S
        c0.Write(0x00, 99, 4); // BusReadInvalidate: c1→I, c0→M

        Assert.Equal(MesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks); // S→M upgrade requires no writeback
    }

    [Fact]
    public void TwoCaches_ReadModified_CausesWriteback_BothShared() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        c0.Write(0x00, 0xABCDUL, 4); // c0: M

        ulong val = c1.Read(0x00, 4); // BusRead: c0 M→writeback+S, c1→S (reads from backing)

        Assert.Equal(MesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(0xABCDUL, val);      // c1 sees c0's written value
        Assert.Equal(1, c0.Writebacks);   // c0 wrote back on snoop
    }

    [Fact]
    public void TwoCaches_ExclusiveUpgrade_ToModified_NoBusTraffic() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        c0.Write(0x00, 77, 4); // E→M silently, no bus traffic

        Assert.Equal(MesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(0, c0.Writebacks);
        Assert.Equal(0, c1.Writebacks);
    }

    [Fact]
    public void TwoCaches_FullCoherenceRoundTrip() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

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
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        c0.Write(0x00, 0xAAAAUL, 4); // c0: M
        _ = c1.Read(0x00, 4);        // c0→S, c1→S
        c1.Write(0x00, 0xBBBBUL, 4); // c0→I, c1→M
        ulong val = c0.Read(0x00, 4); // c1→S, c0→S, sees 0xBBBB

        Assert.Equal(0xBBBBUL, val);
    }

    [Fact]
    public void Load_InvalidatesAllCaches() {
        var (bus, backing) = MakeBus();
        backing.Load(0x00, new byte[Block]);
        var c0 = MakeCache(bus);
        var c1 = MakeCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0: S, c1: S

        // Load replaces backing data and invalidates all cached copies.
        var fresh = new byte[Block];
        fresh[0] = 0xFF;
        c0.Load(0x00, fresh);

        Assert.Equal(MesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MesiState.Invalid, c1.StateOf(0x00));
        // Next read should see the new data.
        Assert.Equal(0xFFUL, c0.Read(0x00, 1));
    }
}
