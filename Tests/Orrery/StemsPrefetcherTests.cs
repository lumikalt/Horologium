#region

using Orrery.Cache;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for StemsPrefetcher (Somogyi et al., ISCA 2009).
///     blockBytes=32 throughout (2 KB region = 64 blocks per region, matching
///     <see cref="SmsPrefetcherTests" />'s convention).
/// </summary>
public sealed class StemsPrefetcherTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new StemsPrefetcher(33));
    }

    [Fact]
    public void Constructor_TooSmallBlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new StemsPrefetcher(16));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void TriggerAccess_NoHistory_ReturnsZero() {
        var stems = new StemsPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = stems.OnAccess(0x1000UL, 0x8000UL, false, buf);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void HitAccess_NeverPredicts() {
        // Only misses participate in STeMS's bookkeeping (see the class doc comment) —
        // a hit must never train or predict, unlike SmsPrefetcher.
        var stems = new StemsPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = stems.OnAccess(0x1000UL, 0x8000UL, true, buf);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void EmptyTargetSpan_NoCrash() {
        var stems = new StemsPrefetcher();
        Span<ulong> empty = [];
        int cnt = stems.OnAccess(0x1000UL, 0x8000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var stems = new StemsPrefetcher();
        Span<ulong> buf = stackalloc ulong[32];
        const int blockBytes = 32;
        const int regionBytes = 2048;
        for (var i = 0; i < 5000; i++) {
            var pc = (ulong)(0x1000 + i % 128 * 4);
            var region = (ulong)(i % 200 * regionBytes);
            var offset = (ulong)(i % 64 * blockBytes);
            buf.Clear();
            int cnt = stems.OnAccess(pc, region + offset, i % 4 == 0, buf);
            Assert.True(cnt >= 0 && cnt <= buf.Length);
        }
    }

    // ── Reconstruction, verified against the paper's own worked example ──────

    /// <summary>
    ///     Reproduces Fig. 3/5 of Somogyi et al. (ISCA 2009) exactly: the observed miss order
    ///     A, A+4, B, A+2, B+6, A−1, C, D, D+1, D+2 (10 misses across four regions A/B/C/D,
    ///     one trigger PC per region) is played once as training. Region C is a single-access
    ///     generation (never promoted past the filter table, so it contributes a trigger only,
    ///     no spatial entries — matching the figure, where C has no recorded spatial sequence).
    ///     Region A, B, and D's accumulation-table entries are then evicted into the PST by
    ///     capacity pressure from 64 unrelated filler generations (mirroring
    ///     <see cref="SmsPrefetcherTests" />'s <c>RepeatedRegionPattern_PredictsPrefetches</c>
    ///     technique), which is required before their sequences are visible to reconstruction
    ///     and before region A's second trigger is treated as a fresh generation rather than a
    ///     continuation of the first. Re-triggering region A must then reconstruct the original
    ///     9-entry continuation in the exact original order: A+4, B, A+2, B+6, A−1, C, D, D+1, D+2
    ///     — verifying both the delta bookkeeping (independently re-derived from the paper's
    ///     figure by hand before writing this class) and the position-recurrence reconstruction
    ///     together, end to end, through the public API only.
    /// </summary>
    [Fact]
    public void Reconstruction_MatchesPapersWorkedExample() {
        const int blockBytes = 32;
        const int regionBytes = 2048;

        const ulong pcA = 0x1000, pcB = 0x2000, pcC = 0x3000, pcD = 0x4000;
        const ulong regionA = 0 * regionBytes,
                    regionB = 1 * regionBytes,
                    regionC = 2 * regionBytes,
                    regionD = 3 * regionBytes;

        // Offsets chosen so "A-1" stays within the region (triggerOffsetA > 0).
        const int offA = 10, offA4 = 14, offA2 = 12, offAminus1 = 9;
        const int offB = 20, offB6 = 26;
        const int offC = 30;
        const int offD = 5, offD1 = 6, offD2 = 7;

        var stems = new StemsPrefetcher();
        var scratch = new ulong[8];

        // ── Main pass: the observed miss order from Fig. 3 ───────────────────
        stems.OnAccess(pcA, Addr(regionA, offA), false, scratch);       // A       (trigger)
        stems.OnAccess(pcA, Addr(regionA, offA4), false, scratch);      // A+4
        stems.OnAccess(pcB, Addr(regionB, offB), false, scratch);       // B       (trigger)
        stems.OnAccess(pcA, Addr(regionA, offA2), false, scratch);      // A+2
        stems.OnAccess(pcB, Addr(regionB, offB6), false, scratch);      // B+6
        stems.OnAccess(pcA, Addr(regionA, offAminus1), false, scratch); // A-1
        stems.OnAccess(pcC, Addr(regionC, offC), false, scratch);       // C       (trigger, single-access)
        stems.OnAccess(pcD, Addr(regionD, offD), false, scratch);       // D       (trigger)
        stems.OnAccess(pcD, Addr(regionD, offD1), false, scratch);      // D+1
        stems.OnAccess(pcD, Addr(regionD, offD2), false, scratch);      // D+2

        // ── Force A/B/D's accumulation entries to retire into the PST, and region A's own
        // accum residency to clear, via 64 unrelated filler generations (AccumSize capacity). ──
        const ulong fillerRegionBase = 1_000_000UL * regionBytes;
        for (var i = 0; i < 64; i++) {
            ulong fillerRegion = fillerRegionBase + (ulong)i * regionBytes;
            stems.OnAccess(0x9000UL, fillerRegion, false, scratch);              // trigger
            stems.OnAccess(0x9000UL, fillerRegion + blockBytes, false, scratch); // promote
        }

        // ── Re-trigger region A; reconstruction should replay the original continuation. ──
        var buf = new ulong[9];
        int count = stems.OnAccess(pcA, Addr(regionA, offA), false, buf);

        ulong[] expected = [
            Addr(regionA, offA4),
            Addr(regionB, offB),
            Addr(regionA, offA2),
            Addr(regionB, offB6),
            Addr(regionA, offAminus1),
            Addr(regionC, offC),
            Addr(regionD, offD),
            Addr(regionD, offD1),
            Addr(regionD, offD2),
        ];

        Assert.Equal(9, count);
        Assert.Equal(expected, buf);
        return;

        ulong Addr(ulong region, int offset) => region + (ulong)offset * blockBytes;
    }

    [Fact]
    public void Reconstruction_TruncatesToTargetSpanInOrder() {
        // Same setup as the full reconstruction test, but with a target buffer too small to
        // hold the whole reconstructed sequence — the first entries in position order must
        // still be returned, not an arbitrary subset.
        const int blockBytes = 32;
        const int regionBytes = 2048;
        const ulong pcA = 0x1000, pcB = 0x2000;
        const ulong regionA = 0 * regionBytes, regionB = 1 * regionBytes;
        const int offA = 10, offA4 = 14;
        const int offB = 20;

        var stems = new StemsPrefetcher();
        var scratch = new ulong[8];

        stems.OnAccess(pcA, Addr(regionA, offA), false, scratch);
        stems.OnAccess(pcA, Addr(regionA, offA4), false, scratch);
        stems.OnAccess(pcB, Addr(regionB, offB), false, scratch);

        const ulong fillerRegionBase = 2_000_000UL * regionBytes;
        for (var i = 0; i < 64; i++) {
            ulong fillerRegion = fillerRegionBase + (ulong)i * regionBytes;
            stems.OnAccess(0x9001UL, fillerRegion, false, scratch);
            stems.OnAccess(0x9001UL, fillerRegion + blockBytes, false, scratch);
        }

        var buf = new ulong[2];
        int count = stems.OnAccess(pcA, Addr(regionA, offA), false, buf);

        Assert.Equal(2, count);
        Assert.Equal(Addr(regionA, offA4), buf[0]);
        Assert.Equal(Addr(regionB, offB), buf[1]);
        return;

        ulong Addr(ulong region, int offset) => region + (ulong)offset * blockBytes;
    }
}