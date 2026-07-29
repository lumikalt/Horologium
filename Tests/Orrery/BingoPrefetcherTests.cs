#region

using Orrery.Cache;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for BingoPrefetcher (Bakhshalipour et al., HPCA 2019).
///     blockBytes=32 throughout (2 KB region = 64 blocks, fitting a ulong pattern).
/// </summary>
public sealed class BingoPrefetcherTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new BingoPrefetcher(33));
    }

    [Fact]
    public void Constructor_TooSmallBlockBytes_Throws() {
        // blockBytes=16 -> 2048/16=128 blocks, overflow ulong pattern
        Assert.Throws<ArgumentException>(() => new BingoPrefetcher(16));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void TriggerAccess_NoHistoryEntry_ReturnsZero() {
        var bingo = new BingoPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = bingo.OnAccess(0x1000UL, 0x8000UL, false, buf);
        Assert.Equal(0, cnt); // history table is empty
    }

    [Fact]
    public void EmptyTargetSpan_NoCrash() {
        var bingo = new BingoPrefetcher();
        Span<ulong> empty = [];
        int cnt = bingo.OnAccess(0x1000UL, 0x8000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var bingo = new BingoPrefetcher();
        Span<ulong> buf = stackalloc ulong[32];
        const int blockBytes = 32;
        const int regionBytes = 2048;
        for (var i = 0; i < 5000; i++) {
            var pc = (ulong)(0x1000 + i % 128 * 4);
            var region = (ulong)(i % 200 * regionBytes);
            var offset = (ulong)(i % 64 * blockBytes);
            buf.Clear();
            int cnt = bingo.OnAccess(pc, region + offset, i % 4 == 0, buf);
            Assert.True(cnt >= 0 && cnt <= buf.Length);
        }
    }

    // ── Precise (long-event) match ───────────────────────────────────────────

    [Fact]
    public void PreciseMatch_SameRegionRevisited_UsesExactFootprint() {
        // Train region 0's pattern {0,1,2} into the history table (evicted from the
        // accumulation table once region 64 arrives, same AGT-capacity-pressure
        // technique as SmsPrefetcherTests). Revisiting the *exact same* region base
        // as a fresh trigger must hit the precise (Pc, RegionBase, Offset) match.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64; // matches BingoPrefetcher's internal AccumSize
        const ulong triggerPc = 0x4000UL;

        var bingo = new BingoPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 1; r++) {
            var regionBase = (ulong)(r * regionBytes);
            bingo.OnAccess(triggerPc, regionBase, false, buf); // trigger, offset 0
            bingo.OnAccess(triggerPc, regionBase + 1 * (ulong)blockBytes, false, buf); // promotes to accum
            bingo.OnAccess(triggerPc, regionBase + 2 * (ulong)blockBytes, false, buf);
        }

        // Region 0 has been evicted into history at the exact (triggerPc, 0, offset=0) key.
        Array.Clear(buf);
        int count = bingo.OnAccess(triggerPc, 0UL, false, buf); // retrigger region 0 exactly

        Assert.True(count >= 2, $"Expected >=2 prefetches, got {count}");
        Assert.Contains(1UL * blockBytes, buf[..count]);
        Assert.Contains(2UL * blockBytes, buf[..count]);
    }

    // ── Fallback (short-event) match ─────────────────────────────────────────

    [Fact]
    public void FallbackMatch_NeverSeenRegion_GeneralizesFromSameOffsetHistory() {
        // Same training as the precise-match test (region 0's pattern {0,1,2}
        // evicted into history), but this time trigger a region that was *never*
        // touched before. Pass 1 (exact address) cannot match; pass 2 (PC+Offset
        // only) must fall back to region 0's footprint and generalize it to the new
        // region's base address -- this is the paper's whole point: a footprint
        // learned on one page transfers to a page never seen before.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64;
        const ulong triggerPc = 0x4000UL;

        var bingo = new BingoPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 1; r++) {
            var regionBase = (ulong)(r * regionBytes);
            bingo.OnAccess(triggerPc, regionBase, false, buf);
            bingo.OnAccess(triggerPc, regionBase + 1 * (ulong)blockBytes, false, buf);
            bingo.OnAccess(triggerPc, regionBase + 2 * (ulong)blockBytes, false, buf);
        }

        var newBase = (ulong)((accumSize + 1) * regionBytes); // never touched above
        Array.Clear(buf);
        int count = bingo.OnAccess(triggerPc, newBase, false, buf);

        Assert.True(count >= 2, $"Expected >=2 prefetches, got {count}");
        Assert.Contains(newBase + 1 * (ulong)blockBytes, buf[..count]);
        Assert.Contains(newBase + 2 * (ulong)blockBytes, buf[..count]);
    }

    [Fact]
    public void FallbackMatch_AggregatesAcrossMultipleMatchingEntries_At20PercentThreshold() {
        // Ten history entries share (pc, offset=0): offset 5 is set in 2/10 (20%,
        // the paper's boundary -- must be included); offset 9 is set in 1/10 (10%,
        // below threshold -- must be excluded); offset 3 is set in 7/10 (70%,
        // padding, not asserted). Regions 0..9 (the first 10 touched) get evicted
        // as regions 10..73 fill the 64-entry accumulation table past capacity.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64;
        const ulong pc = 0x5000UL;

        var bingo = new BingoPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 10; r++) {
            var regionBase = (ulong)(r * regionBytes);
            bingo.OnAccess(pc, regionBase, false, buf); // trigger, offset 0

            int secondOffset = r switch {
                0 or 1        => 5, // 2/10 -> 20%, boundary, must be included
                2             => 9, // 1/10 -> 10%, must be excluded
                _ when r < 10 => 3, // 7/10 -> 70%, padding
                _             => 1, // residents, never evicted, irrelevant
            };
            bingo.OnAccess(pc, regionBase + (ulong)secondOffset * blockBytes, false, buf);
        }

        var newBase = (ulong)((accumSize + 10) * regionBytes);
        Array.Clear(buf);
        int count = bingo.OnAccess(pc, newBase, false, buf);

        Assert.Contains(newBase + 5UL * blockBytes, buf[..count]);
        Assert.DoesNotContain(newBase + 9UL * blockBytes, buf[..count]);
    }

    [Fact]
    public void PreciseMatch_PreferredOverFallback() {
        // A single conflicting fallback-pool entry A (pattern {0,8}) shares
        // (pc, offset=0) with P, whose own precise entry is pattern {0,7}. Pass 1
        // (exact address match) must win outright once it exists for the
        // retriggered region -- Bingo must never blend in the fallback pool's vote
        // once a precise match is found (Fig. 5's decision order). Filler regions
        // use a distinct PC so their own eventual history writes can never match a
        // (pc, ...) query and cannot contaminate this test regardless of how many
        // of them exist.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64; // matches BingoPrefetcher's internal AccumSize
        const ulong pc = 0x6000UL;
        const ulong fillerPc = 0x1111UL;

        var bingo = new BingoPrefetcher();
        var buf = new ulong[32];
        var nextRegion = 0;
        ulong NextBase() => (ulong)(nextRegion++ * regionBytes);

        // A (pattern {0,8}) becomes resident.
        ulong aBase = NextBase();
        bingo.OnAccess(pc, aBase, false, buf);
        bingo.OnAccess(pc, aBase + 8UL * blockBytes, false, buf);

        // 63 filler regions fill the accumulation table to capacity (A + 63 = 64).
        for (var i = 0; i < accumSize - 1; i++) {
            ulong fb = NextBase();
            bingo.OnAccess(fillerPc, fb, false, buf);
            bingo.OnAccess(fillerPc, fb + 1UL * blockBytes, false, buf);
        }

        // P (pattern {0,7}) evicts A -- the oldest resident -- into history.
        ulong pBase = NextBase();
        bingo.OnAccess(pc, pBase, false, buf);
        bingo.OnAccess(pc, pBase + 7UL * blockBytes, false, buf);

        // 64 more filler regions: the first 63 clear the remaining filler residents,
        // and the 64th evicts P (now the oldest resident) into history.
        for (var i = 0; i < accumSize; i++) {
            ulong fb = NextBase();
            bingo.OnAccess(fillerPc, fb, false, buf);
            bingo.OnAccess(fillerPc, fb + 1UL * blockBytes, false, buf);
        }

        // Retrigger P exactly -- pass 1 must find the precise (pc, pBase, offset=0) entry.
        Array.Clear(buf);
        int count = bingo.OnAccess(pc, pBase, false, buf);

        Assert.Contains(pBase + 7UL * blockBytes, buf[..count]);
        Assert.DoesNotContain(pBase + 8UL * blockBytes, buf[..count]);
    }

    [Fact]
    public void TriggerBlockNotPrefetched() {
        // The trigger block itself should not appear in the prefetch list.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const int accumSize = 64;
        const ulong pc = 0x2000UL;

        var bingo = new BingoPrefetcher();
        var buf = new ulong[32];

        for (var r = 0; r < accumSize + 1; r++) {
            var b = (ulong)(r * regionBytes);
            bingo.OnAccess(pc, b, false, buf);
            bingo.OnAccess(pc, b + 1 * (ulong)blockBytes, false, buf);
        }

        const ulong newBase = (accumSize + 1) * regionBytes;
        Array.Clear(buf);
        int count = bingo.OnAccess(pc, newBase, false, buf);

        Assert.True(count >= 1);
        // The trigger block (offset 0 = newBase) must not be in the prefetch list
        Assert.DoesNotContain(newBase, buf[..count]);
    }
}
