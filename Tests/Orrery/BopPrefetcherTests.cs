using Orrery.Cache;

namespace Tests.Orrery;

/// <summary>
///     Unit tests for BopPrefetcher (Michaud, HPCA 2016).
///     blockBytes=32, pageBytes=4096 (128 lines/page) throughout unless stated otherwise.
/// </summary>
public sealed class BopPrefetcherTests {
    private const int Line = 32;

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new BopPrefetcher(33));
    }

    [Fact]
    public void Constructor_ZeroLatency_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BopPrefetcher(latency: 0));
    }

    [Fact]
    public void Constructor_PageNotLargerThanBlock_Throws() {
        Assert.Throws<ArgumentException>(() => new BopPrefetcher(blockBytes: 64, pageBytes: 64));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void FirstMiss_PrefetchesNextLine() {
        // Initial state: prefetch on, D=1 → a miss on X prefetches X+1.
        var p = new BopPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, buf);
        Assert.Equal(1, cnt);
        Assert.Equal(0x2000UL + BopPrefetcherTests.Line, buf[0]);
    }

    [Fact]
    public void EmptySpan_NeverCrashes() {
        var p = new BopPrefetcher();
        Span<ulong> empty = [];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void PlainHit_NotEligible_NoPrefetch() {
        // A hit on a line this prefetcher never issued is not a prefetched hit,
        // hence not an eligible access: no learning step, no prefetch.
        var p = new BopPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, true, buf);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void PageBoundary_PrefetchDropped() {
        // Last line of a 4KB page with D=1: X+1 crosses the page → no prefetch.
        var p = new BopPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        ulong lastLineOfPage = 4096UL - BopPrefetcherTests.Line;
        int cnt = p.OnAccess(0x1000UL, lastLineOfPage, false, buf);
        Assert.Equal(0, cnt);
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var p = new BopPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        for (var i = 0; i < 20_000; i++) {
            var pc = (ulong)(0x1000 + i % 64 * 4);
            var addr = (ulong)(0x2000 + i % 256 * BopPrefetcherTests.Line);
            buf.Clear();
            int cnt = p.OnAccess(pc, addr, i % 3 == 0, buf);
            Assert.True(cnt is >= 0 and <= 1); // degree-one prefetcher
        }
    }

    // ── Timeliness-driven offset selection ────────────────────────────────────

    [Fact]
    public void SequentialStream_LearnsTimelyOffset() {
        // Stride-1 stream, one access per tick, prefetch completion latency L=10.
        // A candidate offset d scores iff the RR insertion for line X−d completed
        // by the time X is accessed, i.e. d ≥ L. The first offset to reach
        // SCOREMAX in round-robin order is the smallest such d — exactly 10,
        // which is in the offset list. After the first learning phase every
        // prefetch must target X + 10 lines.
        //
        // Superpage-sized pages: with 4KB pages the periodic page-boundary
        // prefetch drops alias against the offset round-robin (gcd effects in a
        // perfectly periodic synthetic stream) and can tie-break toward a larger
        // timely offset. 16MB pages remove boundary drops from the test range.
        var p = new BopPrefetcher(latency: 10, pageBytes: 1 << 24);
        Span<ulong> buf = stackalloc ulong[4];
        ulong addr = 0;

        // 52 offsets × ~31 rounds ≈ 1612 eligible accesses per phase; run well past it.
        // Misses throughout (the demand stream outruns its own late D=1 prefetches at
        // first; wasHit=false keeps every access eligible without a hit model).
        //
        // Only the FIRST phase result is exactly deterministic: it learns from a cold
        // RR table, so the smallest timely offset (10) wins the round-robin. At every
        // offset switch the in-flight prefetches are reconstructed with the new D
        // (base = Y − D, as in the hardware), punching a small hole in the RR table
        // that can legitimately tie-break later phases toward a different — equally
        // timely — offset. So: exact assert on phase 1, timeliness property afterward.
        var firstLearned = 0;
        for (var i = 0; i < 6000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
            if (firstLearned == 0 && p.CurrentOffset != 1) firstLearned = p.CurrentOffset;
            addr += BopPrefetcherTests.Line;
        }

        Assert.Equal(10, firstLearned);
        Assert.True(p.PrefetchEnabled);
        Assert.True(p.CurrentOffset >= 10, $"offset {p.CurrentOffset} would be a late prefetch");

        // Steady state: the next access prefetches exactly X + D lines.
        buf.Clear();
        int cnt = p.OnAccess(0x1000UL, addr, false, buf);
        Assert.Equal(1, cnt);
        Assert.Equal(addr + (ulong)p.CurrentOffset * BopPrefetcherTests.Line, buf[0]);
    }

    [Fact]
    public void StridedStream_LearnsMultipleOfStride() {
        // +3-line stride with latency 4: offset d hits iff d ≡ 0 (mod 3) and the
        // access d/3 strides back has completed its prefetch: d/3 ≥ 4 → d ≥ 12.
        // Smallest such offset in the list is 12. Superpages, as above.
        var p = new BopPrefetcher(latency: 4, pageBytes: 1 << 24);
        Span<ulong> buf = stackalloc ulong[4];
        ulong addr = 0;

        var firstLearned = 0;
        for (var i = 0; i < 6000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
            if (firstLearned == 0 && p.CurrentOffset != 1) firstLearned = p.CurrentOffset;
            addr += 3 * BopPrefetcherTests.Line;
        }

        Assert.Equal(12, firstLearned);
        Assert.True(p.PrefetchEnabled);
        Assert.True(
            p.CurrentOffset % 3 == 0 && p.CurrentOffset >= 12,
            $"offset {p.CurrentOffset} is not a timely multiple of the stride"
        );
    }

    // ── Throttling and recovery ───────────────────────────────────────────────

    [Fact]
    public void IrregularPattern_TurnsPrefetchOff_ThenRecovers() {
        var p = new BopPrefetcher(latency: 10, pageBytes: 1 << 24);
        Span<ulong> buf = stackalloc ulong[4];

        // Phase 1: pseudo-random lines spread over a huge range — no offset can
        // score, so the first learning phase ends after ROUNDMAX (100) rounds
        // (= 5200 eligible accesses) with best score ≤ BADSCORE → prefetch off.
        ulong x = 12345;
        for (var i = 0; i < 5300; i++) {
            x = x * 6364136223846793005UL + 1442695040888963407UL; // LCG
            ulong addr = (x >> 16) % (1UL << 40) & ~(ulong)(BopPrefetcherTests.Line - 1);
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
        }

        Assert.False(p.PrefetchEnabled);

        // Phase 2: while off, demand fills feed the RR table (D=0), so a
        // sequential stream re-scores timely offsets and prefetch turns back on.
        ulong seq = 1UL << 41; // far away from the random region
        for (var i = 0; i < 6000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, seq, false, buf);
            seq += BopPrefetcherTests.Line;
        }

        Assert.True(p.PrefetchEnabled);
        Assert.True(p.CurrentOffset >= 10, $"offset {p.CurrentOffset} would be a late prefetch");
    }

    // ── Prefetched-hit eligibility ────────────────────────────────────────────

    [Fact]
    public void PrefetchedHit_KeepsStreamAlive() {
        // Once a stream is fully covered every demand access hits — on a line BOP
        // itself prefetched. Those prefetched hits must stay eligible, or the
        // prefetcher would stall its own stream. Model L1 hits via a log of
        // recently issued prefetch lines (same approach as the Berti/Pythia tests).
        var p = new BopPrefetcher(latency: 10, pageBytes: 1 << 24);
        Span<ulong> buf = stackalloc ulong[4];

        const int trackLen = 64;
        var prefetchLog = new ulong[trackLen];
        var logHead = 0;

        ulong addr = 0;
        var issuedInSteadyState = 0;
        const int warmup = 6000;
        const int check = 500;

        for (var i = 0; i < warmup + check; i++) {
            var wasHit = false;
            for (var j = 0; j < trackLen; j++)
                if (prefetchLog[j] != 0 && prefetchLog[j] == addr) {
                    wasHit = true;
                    break;
                }

            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, wasHit, buf);
            if (cnt > 0) {
                prefetchLog[logHead % trackLen] = buf[0];
                logHead++;
                if (i >= warmup) issuedInSteadyState++;
            }

            addr += BopPrefetcherTests.Line;
        }

        // In steady state nearly every access is a prefetched hit and must keep
        // prefetching ahead (page-boundary lines lose their prefetch, so allow slack).
        Assert.True(p.PrefetchEnabled);
        Assert.True(
            issuedInSteadyState >= check * 3 / 4,
            $"steady-state prefetches: {issuedInSteadyState}/{check}"
        );
    }
}
