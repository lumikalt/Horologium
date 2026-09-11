#region

using System.Text;
using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

#endregion

namespace Tests.Orrery;

/// <summary>Unit tests for ScatterCache — Werner et al., USENIX Security 2019.</summary>
public sealed class ScatterCacheTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2CapacityBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new ScatterCache(new FlatMemory(4096), 1000, 4, 32, 10));
    }

    [Fact]
    public void Constructor_NonPow2Ways_Throws() {
        Assert.Throws<ArgumentException>(() => new ScatterCache(new FlatMemory(4096), 1024, 3, 32, 10));
    }

    // ── Basic correctness ─────────────────────────────────────────────────────

    [Fact]
    public void WriteThenRead_ReturnsWrittenValue() {
        // WriteThrough + no-write-allocate default (matching CeaserCache/BdiCache): a write to a
        // never-touched line always writes to backing but never installs it, so the write itself
        // is a miss and the following read is a fresh (separate) miss-and-fill.
        var backing = new FlatMemory(4096);
        var cache = new ScatterCache(backing, 1024, 4, 32, 10);
        cache.Write(0x100, 0xDEADBEEF, 4);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);

        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void ReadMiss_FetchesFromBacking() {
        var backing = new FlatMemory(4096);
        backing.Write(0x200, 0x12345678, 4);
        var cache = new ScatterCache(backing, 1024, 4, 32, 10);
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void CrossBoundaryAccess_BypassesCache() {
        var backing = new FlatMemory(4096);
        var cache = new ScatterCache(backing, 1024, 4, 32, 10);
        ulong lastByteOfLine = 32 - 4;
        cache.Write(lastByteOfLine + 2, 0xAABBCCDD, 4);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
        Assert.Equal(0xAABBCCDDUL, backing.Read(lastByteOfLine + 2, 4));
    }

    [Fact]
    public void FillLine_PrefersEmptyCandidateSlot_NoEvictionWhileFreeSlotsExist() {
        // Regression guard: fill must scan for an empty candidate slot among the nways ways before
        // evicting. A real bug here picked one random way and evicted unconditionally, even when
        // other candidate ways had a free slot for the same address — inflating misses on a warming
        // cache. 8 distinct lines into a 4-way x 8-rows/way (32-slot) cache should never need to
        // evict a valid line to make room.
        var backing = new FlatMemory(4096);
        var cache = new ScatterCache(backing, 1024, 4, 32, 10);
        for (var i = 0; i < 8; i++) cache.Read((ulong)(i * 32), 4);
        Assert.Equal(8, cache.Misses);
        Assert.Equal(0, cache.Evictions);
    }

    // ── Per-way independence (the paper's whole point) ────────────────────────

    [Fact]
    public void WaysAreIndependentlyIndexed() {
        // 4 ways, 4 rows/way. Each way's IDF is derived from a per-way tweak, so for a given
        // address, way 0's row and way 1's row should mostly disagree — proving each way is
        // genuinely independently indexed, not a shared set the way a conventional (or CEASER)
        // cache would compute.
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);

        const int n = 200;
        var sameRow = 0;
        for (var i = 0; i < n; i++) {
            ulong addr = (ulong)i * 32;
            if (cache.IdfRow(0, addr) == cache.IdfRow(1, addr)) sameRow++;
        }

        // 4 rows/way -> ~1/4 chance of agreement if the two ways are genuinely independent. A bug
        // that used the same index for every way would make every address agree.
        Assert.True(sameRow < n / 2, $"{sameRow}/{n} addresses landed in the same row across ways.");
    }

    // ── SDID isolation ─────────────────────────────────────────────────────────

    [Fact]
    public void DifferentSdids_MostlyRedistributeRowAssignment() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);

        const int n = 200;
        var sameRow = 0;
        for (var i = 0; i < n; i++) {
            ulong addr = (ulong)i * 32;
            cache.SetRequestSdid(1);
            int row1 = cache.IdfRow(0, addr);
            cache.SetRequestSdid(2);
            int row2 = cache.IdfRow(0, addr);
            if (row1 == row2) sameRow++;
        }

        Assert.True(sameRow < n / 2, $"{sameRow}/{n} addresses landed in the same row across SDIDs.");
    }

    [Fact]
    public void SetRequestSdid_DefaultsToZero() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);
        cache.SetRequestSdid(7);
        int rowUnderSdid7 = cache.IdfRow(0, 0x1000);
        cache.SetRequestSdid(0);
        int rowUnderSdid0 = cache.IdfRow(0, 0x1000);

        var fresh = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);
        Assert.Equal(rowUnderSdid0, fresh.IdfRow(0, 0x1000));
        Assert.NotEqual(rowUnderSdid7, rowUnderSdid0); // sanity: SDID actually changed something above
    }

    // ── Rekey correctness ──────────────────────────────────────────────────────

    [Fact]
    public void Rekey_FlushesDirtyLinesBeforeInvalidating() {
        var backing = new FlatMemory(4096);
        var cache = new ScatterCache(backing, 1024, 4, 32, 10, writePolicy: WritePolicyKind.WriteBack);

        cache.Write(0x40, 0xCAFEF00DUL, 4);       // dirty, resident, not yet in backing
        Assert.Equal(0UL, backing.Read(0x40, 4)); // confirms it's genuinely only in the cache

        cache.Rekey();

        Assert.Equal(0xCAFEF00DUL, backing.Read(0x40, 4)); // flushed before invalidation
        Assert.Equal(1, cache.RekeyCount);
        Assert.All(cache.GetSnapshot(), l => Assert.False(l.Valid)); // full invalidation
    }

    [Fact]
    public void Rekey_ChangesRowAssignment() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);

        const int n = 200;
        var sameRow = 0;
        for (var i = 0; i < n; i++) {
            ulong addr = (ulong)i * 32;
            int before = cache.IdfRow(0, addr);
            cache.Rekey();
            int after = cache.IdfRow(0, addr);
            if (before == after) sameRow++;
            // Rekey every iteration so each comparison is independent, matching the "key genuinely
            // enters the mix" statistical argument used elsewhere.
        }

        Assert.True(sameRow < n / 2, $"{sameRow}/{n} addresses kept the same row across a rekey.");
    }

    // ── Auto-rekey cadence ─────────────────────────────────────────────────────

    [Fact]
    public void AutoRekey_FiresAtExpectedCadence() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10, 20);

        for (var i = 0; i < 45; i++) cache.Read(0x100, 4); // repeated access to one line
        Assert.Equal(2, cache.RekeyCount);                 // floor(45 / 20)

        for (var i = 0; i < 20; i++) cache.Read(0x100, 4); // 65 total -> floor(65/20)=3
        Assert.Equal(3, cache.RekeyCount);
    }

    [Fact]
    public void RekeyInterval_Zero_NeverAutoRekeys() {
        var cache = new ScatterCache(new FlatMemory(65536), 1024, 4, 32, 10); // rekeyInterval defaults to 0
        for (var i = 0; i < 500; i++) cache.Read((ulong)(i * 32), 4);
        Assert.Equal(0, cache.RekeyCount);
    }

    [Fact]
    public void PeekRead_DoesNotAdvanceRekeyState() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10, 1);
        for (var i = 0; i < 100; i++) cache.PeekRead(0x100, 4);
        Assert.Equal(0, cache.RekeyCount);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    // ── Checkpoint round-trip ─────────────────────────────────────────────────

    [Fact]
    public void Checkpoint_RestoresAcrossMidIntervalProgress() {
        // rekeyInterval=8: prime 5 accesses (short of the threshold), checkpoint, then drive 3 more
        // — if _accessesSinceRekey round-tripped correctly, this crosses the threshold (5+3=8) and
        // fires exactly one rekey; if it silently reset to 0, it would need 8 more, not 3.
        // Generously sized (8 ways * 16 rows/way = 128 slots) so the priming reads to unrelated
        // addresses are very unlikely to randomly evict the dirty line before it's checkpointed —
        // `restored` gets an independent empty backing, so only the checkpoint (not backing) can
        // supply the dirty bytes back.
        var original = new ScatterCache(
            new FlatMemory(65536), 4096, 8, 32, 10, 8, writePolicy: WritePolicyKind.WriteBack
        );

        original.Write(0x40, 0xABCDEF01UL, 4);                                  // 1 access, dirty line resident
        for (var i = 0; i < 4; i++) original.Read((ulong)(0x1000 + i * 32), 4); // 4 more -> counter=5
        Assert.Equal(0, original.RekeyCount);
        Assert.Contains(
            original.GetSnapshot(), l => l.Valid && l.Address == 0x40 && l.Dirty
        ); // still resident going into the checkpoint

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { original.WriteState(w); }

        var restored = new ScatterCache(
            new FlatMemory(65536), 4096, 8, 32, 10, 8, writePolicy: WritePolicyKind.WriteBack
        );
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { restored.ReadState(r); }

        Assert.Equal(0xABCDEF01UL, restored.PeekRead(0x40, 4)); // non-mutating: doesn't touch the counter

        for (var i = 0; i < 3; i++) restored.Read((ulong)(0x2000 + i * 32), 4);
        Assert.Equal(1, restored.RekeyCount);
    }

    [Fact]
    public void Checkpoint_PreservesRngStateAcrossPostRestoreRekey() {
        // Every miss draws from the RNG (random-way victim selection), so even light priming
        // advances state well beyond construction — unlike a scheme where only rekeys draw.
        // Priming through one auto-rekey additionally exercises key regeneration specifically.
        const int capacityBytes = 512, ways = 4, blockBytes = 32, rekeyInterval = 10, seed = 99;

        var original = new ScatterCache(
            new FlatMemory(65536), capacityBytes, ways, blockBytes, 10, rekeyInterval, seed
        );

        for (var i = 0; i < 15; i++) original.Read((ulong)(0x1000 + i * 32), 4); // >= 1 rekey (interval=10)
        Assert.True(original.RekeyCount >= 1, "priming should have crossed at least one auto-rekey");

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { original.WriteState(w); }

        long rekeyCountAtCheckpoint = original.RekeyCount;

        // A small working set revisited over many rounds so eviction/placement (and therefore the
        // live key) actually influences which accesses hit vs. miss.
        const int workingSetSize = 8, rounds = 10;
        var postCheckpointAddresses = new ulong[workingSetSize * rounds];
        for (var r = 0; r < rounds; r++)
        for (var i = 0; i < workingSetSize; i++)
            postCheckpointAddresses[r * workingSetSize + i] = (ulong)(0x2000 + i * 32);

        foreach (ulong a in postCheckpointAddresses) original.Read(a, 4);
        long originalRekeyDelta = original.RekeyCount - rekeyCountAtCheckpoint;
        Assert.Equal(8, originalRekeyDelta); // 80/10, deterministic regardless of RNG state

        var restored = new ScatterCache(
            new FlatMemory(65536), capacityBytes, ways, blockBytes, 10, rekeyInterval, seed
        );
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { restored.ReadState(r); }

        foreach (ulong a in postCheckpointAddresses) restored.Read(a, 4);
        Assert.Equal(originalRekeyDelta, restored.RekeyCount);

        // Decisive check: compare the LIVE key's effect directly via IdfRow, not aggregate hit/miss
        // counts (proven insufficient during CeaserCache's own test-design iteration — a thrash-heavy
        // workload's miss count can coincidentally match across different keys). With a correct
        // restore, `original` and `restored` share a byte-for-byte identical key after driving the
        // identical post-restore sequence, so IdfRow must agree for every probe address. If
        // _rngState weren't restored, the key regenerated at the first post-restore auto-rekey would
        // differ from `original`'s true (already-advanced) one — with only 4 rows/way, independently
        // drawn keys agree by chance only ~1/4 of the time.
        const int probeCount = 200;
        var sameRow = 0;
        for (var i = 0; i < probeCount; i++) {
            var addr = (ulong)(0x5000 + i * 32);
            if (original.IdfRow(0, addr) == restored.IdfRow(0, addr)) sameRow++;
        }

        Assert.Equal(probeCount, sameRow);
    }

    [Fact]
    public void Checkpoint_GeometryMismatch_Throws() {
        var original = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { original.WriteState(w); }

        var differentGeometry = new ScatterCache(new FlatMemory(4096), 1024, 8, 32, 10);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<CheckpointException>(() => differentGeometry.ReadState(r));
    }

    // ── Snapshot introspection ────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReflectsResidentLine() {
        var cache = new ScatterCache(new FlatMemory(4096), 1024, 4, 32, 10);
        cache.Read(0x300, 4);

        ScatterCacheLine[] snapshot = cache.GetSnapshot();
        ScatterCacheLine? resident = snapshot.FirstOrDefault(l => l.Valid && l.Address == 0x300);
        Assert.NotNull(resident);
    }
}