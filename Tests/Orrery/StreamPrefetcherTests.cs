using Orrery.Cache;
using Orrery.Spec;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for StreamPrefetcher (Jouppi, ISCA 1990).
/// </summary>
public sealed class StreamPrefetcherTests {
    private static int Fire(StreamPrefetcher p, ulong addr, bool wasHit, Span<ulong> buf)
        => p.OnAccess(0, addr, wasHit, buf);

    // ── Stream creation ───────────────────────────────────────────────────────

    [Fact]
    public void FirstMiss_StartsStream_ReturnsDepthLines() {
        var p = new StreamPrefetcher(4, 4, 16);
        Span<ulong> buf = stackalloc ulong[8];

        int cnt = Fire(p, 0, false, buf);

        // depth=4 lines should be queued: 16, 32, 48, 64
        Assert.Equal(4, cnt);
        Assert.Equal(16UL, buf[0]);
        Assert.Equal(32UL, buf[1]);
        Assert.Equal(48UL, buf[2]);
        Assert.Equal(64UL, buf[3]);
    }

    [Fact]
    public void FirstHit_NoStream_ReturnsZero() {
        var p = new StreamPrefetcher(4, 4, 16);
        Span<ulong> buf = stackalloc ulong[8];

        int cnt = Fire(p, 0, true, buf);

        Assert.Equal(0, cnt);
    }

    // ── Sequential stream continuation ────────────────────────────────────────

    [Fact]
    public void SequentialAccess_AfterMiss_AdvancesFrontier() {
        var p = new StreamPrefetcher(4, 4, 16);
        Span<ulong> buf = stackalloc ulong[8];

        // Miss at line 0 → fills L1–L4
        Fire(p, 0, false, buf);

        // Next access L1 (sequential, hit because we prefetched it)
        int cnt = Fire(p, 16, true, buf);

        Assert.Equal(1, cnt);
        Assert.Equal(80UL, buf[0]); // L5 (one more beyond depth=4 ahead of L1)
    }

    [Fact]
    public void SequentialStream_Continues_OnHits() {
        // Validate depth is maintained over multiple sequential hits.
        var p = new StreamPrefetcher(4, 4, 16);
        Span<ulong> buf = stackalloc ulong[8];

        Fire(p, 0, false, buf);           // miss at L0 → prefetch L1–L4
        Fire(p, 16, true, buf);           // L1 hit → prefetch L5
        Fire(p, 32, true, buf);           // L2 hit → prefetch L6
        int cnt = Fire(p, 48, true, buf); // L3 hit → prefetch L7

        Assert.Equal(1, cnt);
        Assert.Equal(112UL, buf[0]); // L7 = 7 * 16
    }

    [Fact]
    public void NonSequentialAccess_DoesNotMatchStream() {
        var p = new StreamPrefetcher(4, 4, 16);
        Span<ulong> buf = stackalloc ulong[8];

        Fire(p, 0, false, buf); // start stream at L0

        // Skip ahead — L3 is not sequential after L0
        int cnt = Fire(p, 48, false, buf);

        // Should start a new stream for L3, not continue the old one
        Assert.Equal(4, cnt);
        Assert.Equal(64UL, buf[0]); // L4 = 48 + 16
        Assert.Equal(80UL, buf[1]);
        Assert.Equal(96UL, buf[2]);
        Assert.Equal(112UL, buf[3]);
    }

    // ── Multi-way stream handling ─────────────────────────────────────────────

    [Fact]
    public void TwoInterleavedStreams_BothTrackedIndependently() {
        var p = new StreamPrefetcher(4, 2, 16);
        Span<ulong> buf = stackalloc ulong[8];

        // Start stream A at address 0
        Fire(p, 0, false, buf); // → L1=16, L2=32; front=48
        // Start stream B at address 1024 (16-byte aligned, different stream)
        Fire(p, 1024, false, buf); // → 1040, 1056; front=1072

        // Continue stream A: L1 hit → maintain depth=2 → prefetch L3
        int cntA = Fire(p, 16, true, buf);
        Assert.Equal(1, cntA);
        Assert.Equal(48UL, buf[0]); // L3 of stream A

        // Continue stream B: 1040 hit → maintain depth=2 → prefetch 1040+2*16=1072
        int cntB = Fire(p, 1040, true, buf);
        Assert.Equal(1, cntB);
        Assert.Equal(1072UL, buf[0]);
    }

    [Fact]
    public void LruEviction_OldestStream_ReplacedOnNewMiss() {
        // With streamCount=2 and three distinct streams, the oldest is evicted.
        var p = new StreamPrefetcher(2, 1, 16);
        Span<ulong> buf = stackalloc ulong[8];

        // Fill both slots
        Fire(p, 0, false, buf);   // slot 0: demandLine=0
        Fire(p, 100, false, buf); // slot 1: demandLine=100

        // New stream: must evict slot 0 (older by LRU)
        Fire(p, 200, false, buf); // slot 0 evicted, restarted at 200

        // Stream at 0 is gone — accessing L1=16 starts a fresh stream (miss)
        int cnt = Fire(p, 16, false, buf);
        Assert.Equal(1, cnt); // new stream: depth=1 → 1 prefetch
        Assert.Equal(32UL, buf[0]);
    }

    // ── Address alignment ─────────────────────────────────────────────────────

    [Fact]
    public void Access_MidLine_SnapsToLineBase() {
        var p = new StreamPrefetcher(4, 2);
        Span<ulong> buf = stackalloc ulong[8];

        // Address 10 is inside line 0 (bytes 0–31)
        int cnt = Fire(p, 10, false, buf);
        Assert.Equal(2, cnt);
        Assert.Equal(32UL, buf[0]);
        Assert.Equal(64UL, buf[1]);
    }

    // ── Depth=0 edge case ─────────────────────────────────────────────────────

    [Fact]
    public void Depth0_NoPrefetchesEver() {
        var p = new StreamPrefetcher(4, 0, 16);
        Span<ulong> buf = stackalloc ulong[8];

        Assert.Equal(0, Fire(p, 0, false, buf));
        Assert.Equal(0, Fire(p, 16, false, buf));
    }

    // ── Integration with MemoryLayers ─────────────────────────────────────────

    [Fact]
    public void StreamPrefetcher_WiredViaMemoryLayers_PrefetchesInstallInCache() {
        var backing = new FlatMemory(1024);
        var l1 = new CacheLevelSpec(
            256, 4, 16, 8, Prefetcher: PrefetcherKind.Stream,
            PrefetcherTableSize: 4, PrefetcherDepth: 2, PrefetchLatency: 0
        );
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]));

        // Miss at address 0 (16-byte block)
        layers.Accessor.Read(0, 1);
        layers.ConsumeAllStalls();

        // The prefetcher should fire and install the next 2 lines (depth=2)
        bool wasHit = layers.Cache!.LastAccessWasHit;
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = layers.Prefetcher!.OnAccess(0, 0, wasHit, buf);
        Assert.Equal(2, cnt);
        for (var i = 0; i < cnt; i++) layers.TryPrefetch(buf[i]);

        // Both prefetched lines should be cache hits
        layers.Accessor.Read(16, 1);
        Assert.Equal(1L, layers.Cache.Hits);
    }
}