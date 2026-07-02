using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for SetAssociativeCache.
/// All configurations use blockSize=16, sets computed from capacity/ways/block.
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
}