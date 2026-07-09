using Orrery.Cache;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for SmsPrefetcher (Somogyi et al., ISCA 2006).
/// blockBytes=32 throughout (2 KB region = 64 blocks, fitting a ulong pattern).
/// </summary>
public sealed class SmsPrefetcherTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new SmsPrefetcher(33));
    }

    [Fact]
    public void Constructor_TooSmallBlockBytes_Throws() {
        // blockBytes=16 → 2048/16=128 blocks, overflow ulong pattern
        Assert.Throws<ArgumentException>(() => new SmsPrefetcher(16));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void TriggerAccess_NoPhtEntry_ReturnsZero() {
        var sms = new SmsPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = sms.OnAccess(0x1000UL, 0x8000UL, false, buf);
        Assert.Equal(0, cnt); // PHT is empty
    }

    [Fact]
    public void EmptyTargetSpan_NoCrash() {
        var sms = new SmsPrefetcher();
        Span<ulong> empty = [];
        int cnt = sms.OnAccess(0x1000UL, 0x8000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var sms = new SmsPrefetcher();
        Span<ulong> buf = stackalloc ulong[32];
        const int blockBytes = 32;
        const int regionBytes = 2048;
        for (var i = 0; i < 5000; i++) {
            var pc = (ulong)(0x1000 + i % 128 * 4);
            var region = (ulong)(i % 200 * regionBytes);
            var offset = (ulong)(i % 64 * blockBytes);
            buf.Clear();
            int cnt = sms.OnAccess(pc, region + offset, i % 4 == 0, buf);
            Assert.True(cnt >= 0 && cnt <= buf.Length);
        }
    }

    // ── Pattern learning and prediction ──────────────────────────────────────

    [Fact]
    public void RepeatedRegionPattern_PredictsPrefetches() {
        // Train SMS on AccumSize+1 = 65 distinct regions, each with three block
        // accesses at offsets 0 (trigger), 1, and 2.  All regions use the same
        // trigger PC.  When the accumulation table overflows, region 0's pattern
        // {0,1,2} is written to the PHT.  A subsequent trigger on a fresh region
        // with the same PC should return prefetches for offsets 1 and 2.

        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64; // matches SmsPrefetcher.AccumSize
        const ulong triggerPc = 0x4000UL;

        var sms = new SmsPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 1; r++) {
            var regionBase = (ulong)(r * regionBytes);
            // Trigger (offset 0) → filter allocation + PHT lookup (empty at first)
            sms.OnAccess(triggerPc, regionBase, false, buf);
            // Offset 1 → promotes filter entry to accumulation table
            sms.OnAccess(triggerPc, regionBase + 1 * blockBytes, false, buf);
            // Offset 2 → sets third bit in accumulation pattern
            sms.OnAccess(triggerPc, regionBase + 2 * (ulong)blockBytes, false, buf);
        }

        // At this point the PHT has (TriggerPc, offset=0) → pattern {0,1,2}.
        // A trigger access to any new region should produce prefetches for offsets 1 and 2.
        const ulong newBase = (accumSize + 1) * regionBytes;
        Array.Clear(buf);
        int count = sms.OnAccess(triggerPc, newBase, false, buf);

        Assert.True(count >= 2, $"Expected ≥2 prefetches, got {count}");
        Assert.Contains(newBase + 1 * (ulong)blockBytes, buf[..count]);
        Assert.Contains(newBase + 2 * (ulong)blockBytes, buf[..count]);
    }

    [Fact]
    public void TriggerBlockNotPrefetched() {
        // The trigger block itself should not appear in the prefetch list.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64;
        const ulong pc = 0x2000UL;

        var sms = new SmsPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 1; r++) {
            var b = (ulong)(r * regionBytes);
            sms.OnAccess(pc, b, false, buf);
            sms.OnAccess(pc, b + 1 * (ulong)blockBytes, false, buf);
        }

        const ulong newBase = (accumSize + 1) * regionBytes;
        Array.Clear(buf);
        int count = sms.OnAccess(pc, newBase, false, buf);

        Assert.True(count >= 1);
        // The trigger block (offset 0 = newBase) must not be in prefetch list
        Assert.DoesNotContain(newBase, buf[..count]);
    }
}