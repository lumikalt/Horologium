using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
///     Unit tests for SetAssociativeCache.
///     All configurations use blockSize=16, sets computed from capacity/ways/block.
/// </summary>
public class CacheTests {
    // 4-way, 64-byte capacity → 64/(4*16) = 1 set (fully-associative in effect).
    // Useful for testing LRU without set-index complexity.
    private static SetAssociativeCache MakeFullyAssoc(IMemory backing, int missLatency = 10) =>
        new(backing, 64, 4, 16, missLatency);

    // 2-way, 64-byte capacity → 64/(2*16) = 2 sets.
    private static SetAssociativeCache Make2Way2Set(IMemory backing, int missLatency = 10) =>
        new(backing, 64, 2, 16, missLatency);

    // ── Basic miss / hit ──────────────────────────────────────────────────────

    [Fact]
    public void ColdMiss_RecordsMissAndStalls() {
        var mem = new FlatMemory(256);
        mem.Load(0, [42,]);

        SetAssociativeCache cache = MakeFullyAssoc(mem);
        ulong val = cache.Read(0, 1);

        Assert.Equal(42UL, val);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(10, cache.ConsumePendingStalls());
    }

    [Fact]
    public void SecondRead_SameAddress_IsHit() {
        var mem = new FlatMemory(256);
        mem.Load(0, [7,]);

        SetAssociativeCache cache = MakeFullyAssoc(mem);
        cache.Read(0, 1); // cold miss
        cache.ConsumePendingStalls();

        ulong val = cache.Read(0, 1); // hit

        Assert.Equal(7UL, val);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.ConsumePendingStalls()); // no new stalls
    }

    [Fact]
    public void HitOnDifferentByteInSameBlock() {
        var mem = new FlatMemory(256);
        mem.Load(0, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,]);

        SetAssociativeCache cache = MakeFullyAssoc(mem);
        cache.Read(0, 1); // fills block [0..15]
        cache.ConsumePendingStalls();

        ulong val = cache.Read(8, 1); // byte 8 — should be in the cached block

        Assert.Equal(9UL, val);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    // ── LRU eviction ─────────────────────────────────────────────────────────

    [Fact]
    public void LruEviction_2Way_EvictsLeastRecentlyUsed() {
        // 2-way, 1 set → only 2 lines can be resident at once.
        // After filling both ways, a third miss should evict the LRU way.
        var backing = new FlatMemory(1024);

        // Write distinguishable values at the start of three different cache lines.
        // With blockSize=16 and 2 sets, set = (address >> 4) & 1.
        // To force all three into the same set (set 0), use addresses whose
        // set-index bits are 0: 0x00, 0x20, 0x40 (step = 2 * blockSize = 32).
        backing.Load(0x00, [0xAA,]);
        backing.Load(0x20, [0xBB,]);
        backing.Load(0x40, [0xCC,]);

        SetAssociativeCache cache = Make2Way2Set(backing);

        cache.Read(0x00, 1); // miss — fills way 0
        cache.Read(0x20, 1); // miss — fills way 1  (both ways now resident)
        cache.Read(0x00, 1); // hit  — promotes 0x00 to MRU, making 0x20 LRU
        cache.Read(0x40, 1); // miss — evicts 0x20 (LRU), installs 0x40

        // 0x40 should now be resident
        cache.ConsumePendingStalls();
        ulong val = cache.Read(0x40, 1);
        Assert.Equal(0xCCUL, val);
        Assert.Equal(2, cache.Hits); // 0x00 re-read + 0x40 second read
    }

    [Fact]
    public void Evictions_CountIncrementsOnEvict() {
        var backing = new FlatMemory(1024);
        SetAssociativeCache cache = Make2Way2Set(backing);

        cache.Read(0x00, 1); // fills set 0, way 0
        cache.Read(0x20, 1); // fills set 0, way 1
        cache.Read(0x40, 1); // evicts from set 0

        Assert.Equal(1, cache.Evictions);
    }

    // ── Write-through / no-write-allocate ────────────────────────────────────

    [Fact]
    public void WriteThrough_WritesAlwaysReachBacking() {
        var backing = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(backing);

        cache.Write(0, 99, 1); // write-through — goes to backing unconditionally

        Assert.Equal(99UL, backing.Read(0, 1));
    }

    [Fact]
    public void WriteHit_UpdatesBothCacheAndBacking() {
        var backing = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(backing);

        cache.Read(0, 1); // fill line (miss)
        cache.ConsumePendingStalls();
        cache.Write(0, 55, 1); // write hit — counts as a hit

        Assert.Equal(55UL, backing.Read(0, 1)); // write-through reached backing
        Assert.Equal(1, cache.Hits);            // the write was a hit
        Assert.Equal(1, cache.Misses);          // only the initial read was a miss
    }

    [Fact]
    public void WriteMiss_NoWriteAllocate_DoesNotInstallLine() {
        var backing = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(backing);

        cache.Write(0, 77, 1); // write miss — no-write-allocate

        Assert.Equal(1, cache.Misses);
        // Reading back should trigger another miss (line was not installed).
        cache.Read(0, 1);
        Assert.Equal(2, cache.Misses);
    }

    // ── Stall accounting ─────────────────────────────────────────────────────

    [Fact]
    public void ConsumePendingStalls_ClearsAfterRead() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256), 5);
        cache.Read(0, 1);
        cache.Read(0x20, 1); // second miss (different line)

        long stalls = cache.ConsumePendingStalls();
        Assert.Equal(10, stalls); // 2 misses × 5 cycles

        Assert.Equal(0, cache.ConsumePendingStalls()); // cleared
    }

    // ── Load invalidation ─────────────────────────────────────────────────────

    [Fact]
    public void Load_InvalidatesOverlappingCacheLines() {
        var backing = new FlatMemory(256);
        backing.Load(0, [10,]);

        SetAssociativeCache cache = MakeFullyAssoc(backing);
        cache.Read(0, 1); // fills line covering [0..15]
        cache.ConsumePendingStalls();

        // Overwrite byte 0 via Load — should invalidate the cached line.
        backing.Load(0, [99,]);
        cache.Load(0, [99,]);

        // Next read should be a miss (and return updated value).
        ulong val = cache.Read(0, 1);
        Assert.Equal(99UL, val);
        Assert.Equal(2, cache.Misses); // original miss + post-invalidation miss
    }

    // ── Cross-boundary bypass ─────────────────────────────────────────────────

    [Fact]
    public void CrossBoundaryRead_BypassesCache() {
        // blockSize=16: byte at offset 15 + 2 bytes spans [0..15]/[16..31].
        var backing = new FlatMemory(256);
        backing.Load(15, [0xAB, 0xCD,]);

        SetAssociativeCache cache = MakeFullyAssoc(backing);
        ulong val = cache.Read(15, 2);

        // Bypass: no hit or miss recorded.
        Assert.Equal(0L, cache.Hits);
        Assert.Equal(0L, cache.Misses);
        _ = val; // value correctness is backing's responsibility
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPowerOfTwo_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() =>
                                             new SetAssociativeCache(mem, 100, 4, 16, 5)
        );
    }

    // ── Realistic prefetch latency ────────────────────────────────────────────
    // With prefetchLatency > 0, a prefetched line is in flight for that many
    // TickPrefetch() calls; a demand hit arriving earlier pays the remainder.

    private static SetAssociativeCache MakeWithPrefetchLatency(IMemory backing, int latency) =>
        new(backing, 64, 4, 16, 10, latency);

    [Fact]
    public void Prefetch_ZeroLatency_DemandHitIsFree() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(mem);

        cache.Prefetch(0);
        cache.Read(0, 1);

        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.InFlightPrefetchCount);
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_ImmediateDemandHitPaysFullLatency() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        Assert.Equal(1, cache.InFlightPrefetchCount);

        cache.Read(0, 1); // same cycle: fill has not started arriving

        Assert.Equal(1, cache.Hits); // still counted as a hit (line is resident)
        Assert.Equal(1, cache.LatePrefetchHits);
        Assert.Equal(10, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.InFlightPrefetchCount); // demand access claimed the fill
    }

    [Fact]
    public void Prefetch_WithLatency_DemandHitPaysRemainingCountdown() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        for (var i = 0; i < 4; i++) cache.TickPrefetch();

        cache.Read(0, 1);

        Assert.Equal(1, cache.LatePrefetchHits);
        Assert.Equal(6, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_ArrivedLineIsFree() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        for (var i = 0; i < 10; i++) cache.TickPrefetch();

        Assert.Equal(0, cache.InFlightPrefetchCount);
        cache.Read(0, 1);

        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.LatePrefetchHits);
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_SecondHitAfterClaimIsFree() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        cache.Read(0, 1); // pays 10, claims the fill
        cache.ConsumePendingStalls();

        cache.Read(4, 1); // same line, fill already claimed

        Assert.Equal(2, cache.Hits);
        Assert.Equal(1, cache.LatePrefetchHits);
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_WriteHitAlsoPaysRemaining() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        for (var i = 0; i < 3; i++) cache.TickPrefetch();

        cache.Write(0, 0xAB, 1);

        Assert.Equal(1, cache.LatePrefetchHits);
        Assert.Equal(7, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_EvictedInFlightLineIsForgotten() {
        // 1-way (direct-mapped): 64/(1*16) = 4 sets; addresses 0 and 64 share set 0.
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(mem, 64, 1, 16, 10, 10);

        cache.Prefetch(0);
        Assert.Equal(1, cache.InFlightPrefetchCount);

        cache.Read(64, 1); // demand miss on the same set evicts the in-flight line
        cache.ConsumePendingStalls();
        Assert.Equal(0, cache.InFlightPrefetchCount);

        cache.Read(0, 1); // back to line 0: a plain full miss, not a late-prefetch hit

        Assert.Equal(0, cache.LatePrefetchHits);
        Assert.Equal(2, cache.Misses);
        Assert.Equal(10, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Prefetch_WithLatency_InvalidatedInFlightLineIsForgotten() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        cache.Load(0, [1, 2, 3, 4,]); // Load() invalidates overlapping lines

        Assert.Equal(0, cache.InFlightPrefetchCount);
    }

    [Fact]
    public void Prefetch_WithLatency_AlreadyResidentLineDoesNotRearm() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeWithPrefetchLatency(mem, 10);

        cache.Prefetch(0);
        for (var i = 0; i < 8; i++) cache.TickPrefetch();

        cache.Prefetch(0); // line resident, countdown must not reset

        cache.Read(0, 1);
        Assert.Equal(2, cache.ConsumePendingStalls()); // 10 - 8, not 10
    }

    // ── Write-back / write-allocate policies ──────────────────────────────────

    [Fact]
    public void WriteBack_StoreHit_BackingUnchanged_LineDirty() {
        var mem = new FlatMemory(256);
        mem.Load(0, [0xAA,]);
        var cache = new SetAssociativeCache(
            mem, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack
        );
        cache.Read(0, 1); // warm the cache
        cache.ConsumePendingStalls();

        cache.Write(0, 0xBB, 1);

        // Backing unchanged — write stayed in cache.
        Assert.Equal(0xAAUL, mem.Read(0, 1));
        // DoCache line is dirty.
        CacheLine? line = cache.GetSnapshot().FirstOrDefault(l => l.Valid);
        Assert.NotNull(line);
        Assert.True(line.Dirty);
        Assert.Equal(0, cache.DirtyEvictions); // no eviction yet
    }

    [Fact]
    public void WriteBack_DirtyEviction_FlushesToBacking() {
        // Direct-mapped (1-way), 16-byte block, 4 sets → addresses 0 and 64 share set 0.
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(
            mem, 64, 1, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate
        );

        // Write-allocate write miss at address 0: installs line then writes 0xBB.
        cache.Write(0, 0xBB, 1);
        cache.ConsumePendingStalls();
        Assert.Equal(0UL, mem.Read(0, 1)); // backing still 0 (write stayed in cache)

        // Read address 64 (same set, same 1 way) → evicts dirty line for address 0.
        cache.Read(64, 1);

        Assert.Equal(0xBBUL, mem.Read(0, 1)); // dirty eviction flushed 0xBB to backing
        Assert.Equal(1, cache.DirtyEvictions);
    }

    [Fact]
    public void WriteAllocate_WriteMiss_InstallsLine() {
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(
            mem, 64, 4, 16, 10,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate
        );

        cache.Write(0, 0xBB, 1);

        Assert.Contains(cache.GetSnapshot(), l => l.Valid);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void WriteBack_NoWriteAllocate_WriteMiss_HitsBacking_NoInstall() {
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(
            mem, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.NoWriteAllocate
        );

        cache.Write(0, 0xBB, 1);

        // Write-back + no-write-allocate write miss → write directly to backing, no install.
        Assert.Equal(0xBBUL, mem.Read(0, 1));
        Assert.DoesNotContain(cache.GetSnapshot(), l => l.Valid);
        Assert.Equal(1, cache.Misses);
    }

    // ── Write-back buffer ─────────────────────────────────────────────────────

    private static SetAssociativeCache MakeWbCache(IMemory backing, int wbCapacity = 4) =>
        new(
            backing, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate,
            wbCapacity: wbCapacity
        );

    [Fact]
    public void WbBuffer_DirtyEviction_NoStall_BackingStillStale() {
        // A dirty eviction with buffer room charges 0 stall and does NOT yet update backing.
        var mem = new FlatMemory(256);
        mem.Load(0, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,]);
        // Line at address 128 occupies the same set (1-set cache → all tags fight for the single set).
        for (var i = 0; i < 16; i++) mem.Load(128 + (ulong)i, [(byte)(0x80 + i),]);

        SetAssociativeCache cache = MakeWbCache(mem);

        // Install and dirty line 0.
        cache.Write(0, 0xAA, 1); // WA miss → fill + dirty
        cache.ConsumePendingStalls();

        // Force eviction of line 0 by filling all 4 ways with different-tag lines.
        // Capacity=64, ways=4, block=16 → 1 set → 4 ways. Addresses 0,16,32,48,64 each map to set 0
        // with different tags; installing 4 more displaces way holding line 0.
        for (ulong addr = 16; addr <= 64; addr += 16) cache.Read(addr, 1);

        // Line 0 should be in WB buffer: no stall beyond miss latency, but backing still old.
        Assert.Equal(1UL, mem.Read(0, 1)); // NOT 0xAA yet
        Assert.Equal(1, cache.DirtyEvictions);
        Assert.Equal(1, cache.WbOccupancy);
    }

    [Fact]
    public void WbBuffer_TickDrains_BackingUpdated() {
        var mem = new FlatMemory(256);
        for (var i = 0; i < 16; i++) mem.Load((ulong)i, [0x00,]);

        SetAssociativeCache cache = MakeWbCache(mem);
        cache.Write(0, 0xCC, 1); // WA fill + dirty
        cache.ConsumePendingStalls();
        // Evict by filling 4 other lines.
        for (ulong addr = 16; addr <= 64; addr += 16) cache.Read(addr, 1);
        Assert.Equal(1, cache.WbOccupancy);

        cache.TickWb(); // one drain

        Assert.Equal(0xCCUL, mem.Read(0, 1)); // now committed to backing
        Assert.Equal(0, cache.WbOccupancy);
        Assert.Equal(1, cache.WbDrains);
    }

    [Fact]
    public void WbBuffer_ReadMissForwards_NoBacking() {
        // A demand read that misses in cache but hits the WB buffer should get the dirty value
        // without going to backing, and the line should re-enter cache as dirty.
        var mem = new FlatMemory(256);
        for (var i = 0; i < 16; i++) mem.Load((ulong)i, [0x00,]);

        SetAssociativeCache cache = MakeWbCache(mem);
        cache.Write(0, 0xDD, 1); // install + dirty
        cache.ConsumePendingStalls();
        // Evict line 0 to WB buffer.
        for (ulong addr = 16; addr <= 64; addr += 16) cache.Read(addr, 1);
        Assert.Equal(1, cache.WbOccupancy);

        // Re-read address 0: should forward from WB buffer, NOT return 0x00 from backing.
        cache.ConsumePendingStalls();
        ulong val = cache.Read(0, 1);
        Assert.Equal(0xDDUL, val);
        Assert.Equal(0, cache.WbOccupancy); // consumed from buffer
        // Reinstalled line should be dirty (data hasn't hit backing yet).
        Assert.Contains(cache.GetSnapshot(), l => l is { Valid: true, Dirty: true, });
    }

    [Fact]
    public void WbBuffer_Full_SyncDrainAndStall() {
        // With a buffer of 1 entry: first dirty eviction goes to buffer (no stall),
        // second dirty eviction overflows → sync drain of first, MissLatency stall charged.
        var mem = new FlatMemory(512);
        SetAssociativeCache cache = new(
            mem, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate,
            wbCapacity: 1
        );

        // Dirty line 0.
        cache.Write(0, 0x11, 1);
        cache.ConsumePendingStalls();
        // Evict it → goes to buffer (1 free slot, no stall).
        for (ulong a = 16; a <= 64; a += 16) cache.Read(a, 1);
        cache.ConsumePendingStalls();
        Assert.Equal(1, cache.WbOccupancy);

        // Dirty line 80 (different tag, same set → force another eviction).
        cache.Write(80, 0x22, 1);
        cache.ConsumePendingStalls();
        // Evict line 80 → buffer full → sync drain of slot 0, MissLatency stall charged.
        for (ulong a = 96; a <= 160; a += 16) cache.Read(a, 1);
        long stalls = cache.ConsumePendingStalls();

        Assert.True(stalls >= 10);            // at least one MissLatency charged for sync drain
        Assert.Equal(0x11UL, mem.Read(0, 1)); // first eviction reached backing
    }

    [Fact]
    public void WbBuffer_CrossBoundaryStore_NotClobberedByDrain() {
        // Cross-boundary write over a WB buffer entry must drain the entry first;
        // otherwise the async drain would later overwrite the store's bytes.
        var mem = new FlatMemory(256);
        // Prime backing so bytes 15..16 have known values.
        mem.Load(0, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        SetAssociativeCache cache = new(
            mem, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate,
            wbCapacity: 4
        );

        // Install and dirty the line covering bytes 0..15.
        cache.Write(0, 0xAABBCCDDUL, 4);
        cache.ConsumePendingStalls();
        // Evict it into the WB buffer.
        for (ulong a = 16; a <= 64; a += 16) cache.Read(a, 1);
        Assert.Equal(1, cache.WbOccupancy);

        // Cross-boundary store spanning bytes 15..16 (straddles the line boundary).
        cache.Write(15, 0xFFEE, 2);

        // WB buffer must have been drained synchronously before the backing write.
        Assert.Equal(0, cache.WbOccupancy);
        // Backing byte 15 = 0xEE (low byte of 0xFFEE in little-endian).
        Assert.Equal(0xEEUL, mem.Read(15, 1));
        // Backing byte 16 = 0xFF (high byte).
        Assert.Equal(0xFFUL, mem.Read(16, 1));
    }

    [Fact]
    public void WbBuffer_NwaMiss_DrainsThenWritesBacking() {
        // WB+NWA: after a dirty line is evicted into the WB buffer, a subsequent NWA write miss
        // targeting the same line must drain the buffer entry first so a deferred drain does not
        // later overwrite the new store's bytes.
        var mem = new FlatMemory(256);
        mem.Load(0, Enumerable.Repeat((byte)0x11, 16).ToArray());

        SetAssociativeCache cache = new(
            mem, 64, 4, 16, 10,
            writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.NoWriteAllocate,
            wbCapacity: 4
        );

        // Read to install line 0, then write-hit to dirty byte 0 = 0xAA.
        cache.Read(0, 1);
        cache.Write(0, 0xAA, 1);
        cache.ConsumePendingStalls();

        // Evict line 0 into the WB buffer (fill all 4 ways with different tags).
        for (ulong a = 16; a <= 64; a += 16) cache.Read(a, 1);
        Assert.Equal(1, cache.WbOccupancy);
        Assert.Equal(0x11UL, mem.Read(0, 1)); // backing still stale

        // NWA write miss to line 0: must drain the WB entry first, then write 0xBB to backing.
        cache.Write(0, 0xBB, 1);

        Assert.Equal(0xBBUL, mem.Read(0, 1)); // new value wins, not 0xAA from stale WB data
        Assert.Equal(0, cache.WbOccupancy);   // WB entry was consumed during NWA miss
    }

    // ── MSHR ─────────────────────────────────────────────────────────────────

    // Helper: 1-set fully-associative cache with explicit MSHR count.
    private static SetAssociativeCache MakeMshr(IMemory backing, int mshrCount, int missLatency = 10) =>
        new(backing, 64, 4, 16, missLatency, mshrCount: mshrCount);

    [Fact]
    public void Mshr_Unlimited_LegacyBehavior() {
        // MshrCount == 0 means unlimited: every miss charges MissLatency exactly as before.
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(mem);

        cache.Read(0, 1);
        Assert.Equal(10, cache.ConsumePendingStalls());
        cache.Read(16, 1);
        Assert.Equal(10, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.MshrCount);
        Assert.Equal(0, cache.MshrOccupancy);
    }

    [Fact]
    public void Mshr_PrimaryMiss_AllocatesSlot_SlotFreedByTick() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeMshr(mem, 2);

        // Cold miss: allocates MSHR slot, charges MissLatency.
        cache.Read(0, 1);
        Assert.Equal(10, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.MshrOccupancy);

        // TickMshr 9 times: slot still occupied.
        for (var i = 0; i < 9; i++) cache.TickMshr();
        Assert.Equal(1, cache.MshrOccupancy);

        // One more tick: slot freed.
        cache.TickMshr();
        Assert.Equal(0, cache.MshrOccupancy);
    }

    [Fact]
    public void Mshr_HitOnInFlightLine_ChargesRemainingAndFreesSlot() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeMshr(mem, 2);

        // Miss on line 0: MSHR[0] = 10.
        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        Assert.Equal(1, cache.MshrOccupancy);

        // Advance 3 ticks: MSHR[0] = 7.
        for (var i = 0; i < 3; i++) cache.TickMshr();

        // Hit on the same line (byte 5 is within the 16-byte block starting at 0).
        // Should charge the remaining 7 cycles and free the slot.
        cache.Read(5, 1);
        Assert.Equal(7, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.MshrOccupancy);
        Assert.Equal(1, cache.MshrMerges);
    }

    [Fact]
    public void Mshr_CapacityStall_AllSlotsBusy_ChargesMinPlusMissLatency() {
        // 1 MSHR slot, MissLatency = 10. Miss A fills slot (remaining=10).
        // After 4 ticks (remaining=6), miss B (unique line) cannot get a slot:
        // stall = 6 + 10 = 16; MshrCapacityStalls++ and slot is NOT allocated for B.
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeMshr(mem, 1);

        cache.Read(0, 1); // Miss A: slot → MSHR[0] = 10
        cache.ConsumePendingStalls();
        Assert.Equal(1, cache.MshrOccupancy);

        for (var i = 0; i < 4; i++) cache.TickMshr(); // MSHR[0] = 6

        // Miss B (line 16): all slots full. Stall = 6 + 10 = 16.
        cache.Read(16, 1);
        Assert.Equal(16, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.MshrCapacityStalls);
        // Slot A is still tracked (was not overwritten).
        Assert.Equal(1, cache.MshrOccupancy);
    }

    [Fact]
    public void Mshr_TwoIndependentSlots_BothTracked() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeMshr(mem, 2);

        cache.Read(0, 1); // Miss A: MSHR[0] = 10
        cache.ConsumePendingStalls();
        cache.Read(16, 1); // Miss B: MSHR[1] = 10
        cache.ConsumePendingStalls();
        Assert.Equal(2, cache.MshrOccupancy);

        for (var i = 0; i < 5; i++) cache.TickMshr(); // both at 5

        // Hit on line A: pays remaining 5.
        cache.Read(0, 1);
        Assert.Equal(5, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.MshrOccupancy); // B still in-flight
        Assert.Equal(1, cache.MshrMerges);
    }

    // ── Critical-word-first / early restart ─────────────────────────────────────

    // Helper: 1-set fully-associative cache with MSHRs and early restart.
    private static SetAssociativeCache MakeCwf(IMemory backing, int mshrCount, int missLatency, int cwl) =>
        new(backing, 64, 4, 16, missLatency, mshrCount: mshrCount, criticalWordLatency: cwl);

    [Fact]
    public void CriticalWordLatency_DefaultsToZero_Disabled() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256));
        Assert.Equal(0, cache.CriticalWordLatency);
    }

    [Fact]
    public void CriticalWordLatency_FreshMiss_ChargesReducedLatencyToRequester() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeCwf(mem, 2, 10, 3);

        cache.Read(0, 1);
        Assert.Equal(3, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.MshrOccupancy);
    }

    [Fact]
    public void CriticalWordLatency_SecondaryHitOnSameInFlightLine_PaysFullRemaining() {
        // The requester gets the reduced critical-word latency, but the MSHR entry still tracks
        // the full MissLatency in the background — a different access to the same in-flight line
        // (before it fully arrives) must pay the full remaining latency, not the critical-word one.
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeCwf(mem, 2, 10, 3);

        cache.Read(0, 1); // requester: charged 3
        cache.ConsumePendingStalls();
        Assert.Equal(1, cache.MshrOccupancy);

        cache.Read(5, 1); // same line, no ticks elapsed: merges and pays full remaining (10)
        Assert.Equal(10, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.MshrOccupancy);
        Assert.Equal(1, cache.MshrMerges);
    }

    [Fact]
    public void CriticalWordLatency_CapacityStall_UsesReducedLatencyPlusMinRemaining() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeCwf(mem, 1, 10, 3);

        cache.Read(0, 1); // Miss A: slot → MSHR[0] = 10, requester charged 3
        cache.ConsumePendingStalls();

        for (var i = 0; i < 4; i++) cache.TickMshr(); // MSHR[0] = 6

        cache.Read(16, 1); // Miss B: all slots full. Stall = 6 + 3 = 9.
        Assert.Equal(9, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.MshrCapacityStalls);
    }

    [Fact]
    public void CriticalWordLatency_RequiresMshrCount_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, criticalWordLatency: 3)
        );
    }

    [Fact]
    public void CriticalWordLatency_CannotExceedMissLatency_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(
                                             mem, 64, 4, 16, 10, mshrCount: 2, criticalWordLatency: 11
                                         )
        );
    }

    // ── Sequential tag/data access mode ─────────────────────────────────────────

    [Fact]
    public void HitLatency_ParallelMode_IsMaxOfTagAndData() {
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(
            mem, 64, 4, 16, 10, tagLatency: 2, dataLatency: 5, accessMode: CacheAccessModeKind.Parallel
        );

        Assert.Equal(CacheAccessModeKind.Parallel, cache.AccessMode);
        Assert.Equal(5, cache.HitLatency);
    }

    [Fact]
    public void HitLatency_SequentialMode_IsSumOfTagAndData() {
        var mem = new FlatMemory(256);
        var cache = new SetAssociativeCache(
            mem, 64, 4, 16, 10, tagLatency: 2, dataLatency: 5, accessMode: CacheAccessModeKind.Sequential
        );

        Assert.Equal(CacheAccessModeKind.Sequential, cache.AccessMode);
        Assert.Equal(7, cache.HitLatency);
    }

    [Fact]
    public void HitLatency_DefaultsToParallel() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256));
        Assert.Equal(CacheAccessModeKind.Parallel, cache.AccessMode);
    }

    // ── Banked caches and port limits ────────────────────────────────────────

    // 4-way, 64-byte capacity, 16-byte blocks → 1 set (all lines coexist), split into banks.
    private static SetAssociativeCache MakeBanked(
        IMemory backing,
        int bankCount,
        int readPorts = 0,
        int writePorts = 0,
        int missLatency = 10
    ) => new(backing, 64, 4, 16, missLatency, bankCount: bankCount, readPorts: readPorts, writePorts: writePorts);

    [Fact]
    public void Banking_DefaultsToUnbankedUnlimited() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256));
        Assert.Equal(1, cache.BankCount);
        Assert.Equal(0, cache.ReadPorts);
        Assert.Equal(0, cache.WritePorts);
    }

    [Fact]
    public void Banking_DifferentBanksConcurrentHits_NoConflict() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeBanked(mem, 2, 1);

        cache.Read(0, 1); // line 0 -> bank 0
        cache.ConsumePendingStalls();
        cache.TickPorts();
        cache.Read(16, 1); // line 1 -> bank 1
        cache.ConsumePendingStalls();
        cache.TickPorts();

        cache.Read(0, 1); // bank 0 hit, first this cycle
        cache.ConsumePendingStalls();
        cache.Read(16, 1); // bank 1 hit, same cycle as the bank-0 access above: no conflict
        Assert.Equal(0, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.BankConflicts);
    }

    [Fact]
    public void Banking_SameBankConcurrentHits_SecondPaysConflictStall() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeBanked(mem, 2, 1);

        cache.Read(0, 1); // line 0 -> bank 0
        cache.ConsumePendingStalls();
        cache.TickPorts();
        cache.Read(32, 1); // line 2 -> bank 0 too (2 % 2 == 0)
        cache.ConsumePendingStalls();
        cache.TickPorts();

        cache.Read(0, 1); // bank 0 hit, uses the one read port this cycle
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // bank 0 hit, same cycle: port already used, pays 1-cycle conflict stall
        Assert.Equal(1, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.BankConflicts);
    }

    [Fact]
    public void Banking_TickPorts_ResetsUsageEachCycle() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeBanked(mem, 1, 1);

        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        cache.TickPorts();
        cache.Read(4, 1); // new cycle, same (only) bank: no conflict
        Assert.Equal(0, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.BankConflicts);
    }

    [Fact]
    public void Banking_ReadAndWritePorts_AreIndependentPools() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeBanked(mem, 1, 1, 1);

        cache.Read(0, 1); // consumes the one read port
        cache.ConsumePendingStalls();
        cache.Write(0, 0xAB, 1); // consumes the one write port: separate pool, no conflict
        Assert.Equal(0, cache.ConsumePendingStalls());
        Assert.Equal(0, cache.BankConflicts);
    }

    [Fact]
    public void Banking_WritePortConflict_ChargesStallAndCounts() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeBanked(mem, 1, writePorts: 1);

        cache.Write(0, 1, 1); // uses the one write port this cycle
        cache.ConsumePendingStalls();
        cache.Write(4, 2, 1); // same (only) bank, same cycle: conflict
        cache.ConsumePendingStalls();
        Assert.Equal(1, cache.BankConflicts);
    }

    [Fact]
    public void Banking_ZeroBankCount_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, bankCount: 0));
    }

    [Fact]
    public void Banking_NegativeReadPorts_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, readPorts: -1));
    }

    [Fact]
    public void Banking_NegativeWritePorts_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, writePorts: -1));
    }

    // ── Per-sector dirty/valid bits ──────────────────────────────────────────

    // 4-way, 64-byte capacity, 16-byte blocks → 1 set, split into 2 sectors of 8 bytes each.
    private static SetAssociativeCache MakeSectored(IMemory backing, int sectorBytes, int missLatency = 10) =>
        new(backing, 64, 4, 16, missLatency, sectorBytes: sectorBytes);

    private static SetAssociativeCache MakeSectoredWriteBack(IMemory backing, int sectorBytes, int missLatency = 10) =>
        new(
            backing, 64, 4, 16, missLatency, writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate, sectorBytes: sectorBytes
        );

    [Fact]
    public void Sectoring_DefaultsToDisabled() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256));
        Assert.Equal(0, cache.SectorBytes);
    }

    [Fact]
    public void Sectoring_Disabled_IsSectorResidentMatchesTagResidency() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeFullyAssoc(mem);

        Assert.False(cache.IsSectorResident(0)); // line not resident at all yet
        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        Assert.True(cache.IsSectorResident(0));
        Assert.True(cache.IsSectorResident(8)); // same line, no sector granularity when disabled
    }

    [Fact]
    public void Sectoring_ColdMiss_OnlyFetchesTouchedSector() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeSectored(mem, 8);

        cache.Read(0, 1); // fresh miss: fetches sector 0 [0..7] only
        cache.ConsumePendingStalls();

        Assert.True(cache.IsSectorResident(0));
        Assert.False(cache.IsSectorResident(8)); // sector 1 left untouched
        Assert.Equal(1, cache.SectorFills);
    }

    [Fact]
    public void Sectoring_LaterAccessToUntouchedSector_PaysMissLatencyAgain() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeSectored(mem, 8);

        cache.Read(0, 1); // full miss: tag install + sector 0 fetch
        Assert.Equal(10, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.SectorFills);

        cache.Read(8, 1);                               // tag hit, but sector 1 was never fetched: a sector miss
        Assert.Equal(10, cache.ConsumePendingStalls()); // pays MissLatency again, for the sector alone
        Assert.Equal(1, cache.Hits);                    // tag-level hit/miss counters are unaffected by sector misses
        Assert.Equal(1, cache.Misses);
        Assert.Equal(2, cache.SectorFills);
        Assert.True(cache.IsSectorResident(8));
    }

    [Fact]
    public void Sectoring_DirtyWriteback_OnlyFlushesDirtySectors() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 16; a++) mem.Write(a, 0xEE, 1); // seed line 0 with a known pattern

        SetAssociativeCache cache = MakeSectoredWriteBack(mem, 8);

        cache.Write(0, 0x11, 1); // write-allocate miss: fetches + dirties sector 0 only
        cache.ConsumePendingStalls();

        // 4-way, 1 set: fill the remaining 3 ways, then a 5th distinct line evicts line 0 (LRU).
        cache.Read(16, 1);
        cache.Read(32, 1);
        cache.Read(48, 1);
        cache.ConsumePendingStalls();
        cache.Read(64, 1);
        cache.ConsumePendingStalls();

        Assert.Equal(0x11UL, mem.Read(0, 1)); // dirty sector 0 written back
        Assert.Equal(0xEEUL, mem.Read(8, 1)); // sector 1 was never touched: backing untouched
    }

    [Fact]
    public void Sectoring_Prefetch_WarmsWholeLine() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeSectored(mem, 8);

        cache.Prefetch(0);

        Assert.True(cache.IsSectorResident(0));
        Assert.True(cache.IsSectorResident(8));
    }

    [Fact]
    public void Sectoring_NonPowerOfTwoSectorBytes_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, sectorBytes: 6));
    }

    [Fact]
    public void Sectoring_SectorBytesExceedingBlockBytes_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(mem, 64, 4, 16, 10, sectorBytes: 32));
    }

    [Fact]
    public void Sectoring_CombinedWithWbCapacity_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(
                                             mem, 64, 4, 16, 10, writePolicy: WritePolicyKind.WriteBack, wbCapacity: 4,
                                             sectorBytes: 8
                                         )
        );
    }

    // ── Victim cache (Jouppi) ─────────────────────────────────────────────────

    // 1-way, 32-byte capacity, 16-byte blocks → 2 sets, 1 way each: a second distinct tag in the
    // same set is always a conflict eviction (no spare ways to absorb it). Addresses 0/32/64/96/128
    // all decompose to set 0 (index = (addr >> 4) & 1); address 16 lands in the other set.
    private static SetAssociativeCache MakeVictimCache(
        IMemory backing,
        int victimCacheEntries,
        int victimCacheHitLatency = 1,
        int missLatency = 10,
        int mshrCount = 0
    ) =>
        new(
            backing, 32, 1, 16, missLatency, mshrCount: mshrCount,
            victimCacheEntries: victimCacheEntries, victimCacheHitLatency: victimCacheHitLatency
        );

    private static SetAssociativeCache MakeVictimCacheWriteBack(
        IMemory backing,
        int victimCacheEntries,
        int victimCacheHitLatency = 1,
        int missLatency = 10
    ) =>
        new(
            backing, 32, 1, 16, missLatency, writePolicy: WritePolicyKind.WriteBack,
            writeMissPolicy: WriteMissPolicyKind.WriteAllocate,
            victimCacheEntries: victimCacheEntries, victimCacheHitLatency: victimCacheHitLatency
        );

    [Fact]
    public void VictimCache_DefaultsToDisabled() {
        SetAssociativeCache cache = MakeFullyAssoc(new FlatMemory(256));
        Assert.Equal(0, cache.VictimCacheEntries);
        Assert.Equal(0, cache.VictimCacheOccupancy);
    }

    [Fact]
    public void VictimCache_ConflictMiss_CapturesEvictedLine() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeVictimCache(mem, 2);

        cache.Read(0, 1); // cold miss, installs tag(0)
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // conflict miss: evicts tag(0), captures it instead of discarding
        cache.ConsumePendingStalls();

        Assert.Equal(1, cache.VictimCacheCaptures);
        Assert.Equal(1, cache.VictimCacheOccupancy);
    }

    [Fact]
    public void VictimCache_SubsequentAccessToEvictedLine_HitsInBuffer() {
        var mem = new FlatMemory(256);
        mem.Load(0, [11,]);
        SetAssociativeCache cache = MakeVictimCache(mem, 2);

        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // evicts+captures line 0
        cache.ConsumePendingStalls();

        ulong val = cache.Read(0, 1); // victim-buffer hit
        Assert.Equal(11UL, val);
        Assert.Equal(1, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.VictimCacheHits);
        Assert.Equal(1, cache.Hits);   // counts as a Hit, not a Miss
        Assert.Equal(2, cache.Misses); // unchanged by the victim-buffer hit
    }

    [Fact]
    public void VictimCache_HitSwapsDisplacedLineBackIntoBuffer() {
        var mem = new FlatMemory(256);
        mem.Load(0, [11,]);
        mem.Load(32, [22,]);
        SetAssociativeCache cache = MakeVictimCache(mem, 2);

        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // evicts+captures line 0; line 32 now resident
        cache.ConsumePendingStalls();
        cache.Read(0, 1); // victim-buffer hit: swaps line 32 out into the buffer
        cache.ConsumePendingStalls();

        // Line 32 was displaced by the swap, not discarded — it should now be a cheap
        // victim-buffer hit too, proving the full bidirectional swap.
        ulong val = cache.Read(32, 1);
        Assert.Equal(22UL, val);
        Assert.Equal(1, cache.ConsumePendingStalls());
        Assert.Equal(2, cache.VictimCacheHits);
    }

    [Fact]
    public void VictimCache_NoMshrAllocatedOnHit() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeVictimCache(mem, 2, mshrCount: 2);

        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // evicts+captures line 0
        cache.ConsumePendingStalls();
        for (var i = 0; i < 10; i++) cache.TickMshr(); // drain any in-flight MSHR slots
        Assert.Equal(0, cache.MshrOccupancy);

        cache.Read(0, 1); // victim-buffer hit
        cache.ConsumePendingStalls();

        Assert.Equal(0, cache.MshrOccupancy);
        Assert.Equal(0, cache.MshrMerges);
    }

    [Fact]
    public void VictimCache_FifoOverflow_EvictsOldestEntryFirst() {
        var mem = new FlatMemory(256);
        SetAssociativeCache cache = MakeVictimCache(mem, 2, missLatency: 10);

        cache.Read(0, 1);
        cache.ConsumePendingStalls();
        cache.Read(32, 1); // evicts+captures tag(0) → buffer=[0]
        cache.ConsumePendingStalls();
        cache.Read(64, 1); // evicts+captures tag(32) → buffer=[0,32]
        cache.ConsumePendingStalls();
        cache.Read(96, 1); // evicts tag(64); buffer full → overflow disposes oldest (tag 0) → buffer=[32,64]
        cache.ConsumePendingStalls();

        // Newer entry (32) survived the overflow: still a cheap victim-buffer hit.
        cache.Read(32, 1);
        Assert.Equal(1, cache.ConsumePendingStalls());
        Assert.Equal(1, cache.VictimCacheHits);

        // Oldest entry (0) was evicted by the FIFO overflow: pays the full miss latency again.
        cache.Read(0, 1);
        Assert.Equal(10, cache.ConsumePendingStalls());
    }

    [Fact]
    public void VictimCache_CapturedDirtyLine_NoWritebackAtCaptureTime() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 128; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 2);

        cache.Write(0, 0x11, 1); // write-allocate miss, dirties line 0
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty line 0 — no writeback should occur yet
        cache.ConsumePendingStalls();

        Assert.Equal(1, cache.VictimCacheCaptures);
        Assert.Equal(0xEEUL, mem.Read(0, 1)); // backing untouched: dirty bit travels with the data
    }

    [Fact]
    public void VictimCache_FifoOverflow_DirtyLine_WritesBackToBacking() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 128; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 1);

        cache.Write(0, 0x11, 1); // dirty tag(0)
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty tag(0) → buffer=[0]
        cache.ConsumePendingStalls();
        cache.Write(64, 0x33, 1); // evicts tag(32); buffer full(1) → overflow disposes tag(0), writing it back
        cache.ConsumePendingStalls();

        Assert.Equal(0x11UL, mem.Read(0, 1));
    }

    [Fact]
    public void VictimCache_DirtyBitTravelsThroughSwap() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 160; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 1);

        cache.Write(0, 0x11, 1); // dirty tag(0)
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty tag(0) → buffer=[0(dirty)]
        cache.ConsumePendingStalls();
        cache.Read(0, 1); // victim-buffer hit: swaps tag(0) back in, displaces dirty tag(32) → buffer=[32(dirty)]
        cache.ConsumePendingStalls();
        cache.Write(64, 0x44, 1); // evicts tag(0) again; buffer full(1) → overflow disposes tag(32), writing 0x22 back
        cache.ConsumePendingStalls();
        cache.Write(96, 0x55, 1); // evicts tag(64); buffer full(1) → overflow disposes tag(0) — still dirty
        cache.ConsumePendingStalls();

        Assert.Equal(0x11UL, mem.Read(0, 1)); // the original dirty write survived two captures and a swap
    }

    [Fact]
    public void VictimCache_CombinedWithSectorBytes_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(
                                             mem, 64, 4, 16, 10, sectorBytes: 8, victimCacheEntries: 2
                                         )
        );
    }

    [Fact]
    public void VictimCache_NegativeEntries_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetAssociativeCache(
                                                       mem, 64, 4, 16, 10, victimCacheEntries: -1
                                                   )
        );
    }

    [Fact]
    public void VictimCache_NegativeHitLatency_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetAssociativeCache(
                                                       mem, 64, 4, 16, 10, victimCacheHitLatency: -1
                                                   )
        );
    }

    [Fact]
    public void VictimCache_HitLatencyExceedingMissLatency_Throws() {
        var mem = new FlatMemory(256);
        Assert.Throws<ArgumentException>(() => new SetAssociativeCache(
                                             mem, 64, 4, 16, 10, victimCacheEntries: 2, victimCacheHitLatency: 11
                                         )
        );
    }

    [Fact]
    public void VictimCache_CleanLine_OnBufferedEntry_FlushesButKeepsResident() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 64; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 2);

        cache.Write(0, 0x11, 1);
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty tag(0) into the buffer
        cache.ConsumePendingStalls();

        cache.CleanLine(0);

        Assert.Equal(0x11UL, mem.Read(0, 1));        // written back
        Assert.Equal(1, cache.VictimCacheOccupancy); // still resident in the buffer

        ulong val = cache.Read(0, 1); // still hits in the buffer after cleaning
        Assert.Equal(0x11UL, val);
        Assert.Equal(1, cache.VictimCacheHits);
    }

    [Fact]
    public void VictimCache_FlushLine_OnBufferedEntry_FlushesAndRemoves() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 64; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 2);

        cache.Write(0, 0x11, 1);
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty tag(0) into the buffer
        cache.ConsumePendingStalls();

        cache.FlushLine(0);

        Assert.Equal(0x11UL, mem.Read(0, 1));        // written back
        Assert.Equal(0, cache.VictimCacheOccupancy); // removed from the buffer
    }

    [Fact]
    public void VictimCache_InvalidateLine_OnBufferedEntry_DiscardsWithoutWriteback() {
        var mem = new FlatMemory(256);
        for (ulong a = 0; a < 64; a++) mem.Write(a, 0xEE, 1);
        SetAssociativeCache cache = MakeVictimCacheWriteBack(mem, 2);

        cache.Write(0, 0x11, 1);
        cache.ConsumePendingStalls();
        cache.Write(32, 0x22, 1); // evicts+captures dirty tag(0) into the buffer
        cache.ConsumePendingStalls();

        cache.InvalidateLine(0);

        Assert.Equal(0xEEUL, mem.Read(0, 1));        // discarded, not written back
        Assert.Equal(0, cache.VictimCacheOccupancy); // removed from the buffer
    }
}