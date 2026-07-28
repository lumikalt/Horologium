#region

using Orrery.Cache;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for MlopPrefetcher (Shakerinava et al., DPC-3 2019).
///     blockBytes=32, pageBytes=4096 throughout unless stated otherwise.
/// </summary>
public sealed class MlopPrefetcherTests {
    private const int Line = 32;

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new MlopPrefetcher(33));
    }

    [Fact]
    public void Constructor_PageNotLargerThanBlock_Throws() {
        Assert.Throws<ArgumentException>(() => new MlopPrefetcher(64, 64));
    }

    [Fact]
    public void Constructor_ZeroEvalPeriod_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MlopPrefetcher(evalPeriod: 0));
    }

    [Fact]
    public void BestOffsetForLookahead_OutOfRange_Throws() {
        var p = new MlopPrefetcher();
        Assert.Throws<ArgumentOutOfRangeException>(() => p.BestOffsetForLookahead(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.BestOffsetForLookahead(17));
    }

    // ── Cold-start silence ────────────────────────────────────────────────────

    [Fact]
    public void BeforeFirstEvaluationPeriod_NeverPrefetches() {
        // MLOP has no BOP-style default offset — it stays silent until the first
        // 500-access evaluation period completes and a level actually wins an offset.
        var p = new MlopPrefetcher();
        Span<ulong> buf = stackalloc ulong[32];
        ulong addr = 0;
        for (var i = 0; i < 400; i++) {
            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, false, buf);
            Assert.Equal(0, cnt);
            addr += MlopPrefetcherTests.Line;
        }

        for (var lvl = 1; lvl <= 16; lvl++) Assert.Equal(0, p.BestOffsetForLookahead(lvl));
    }

    [Fact]
    public void EmptySpan_NeverCrashes() {
        var p = new MlopPrefetcher();
        Span<ulong> empty = [];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void PlainHit_NotEligible_NoScoring() {
        var p = new MlopPrefetcher(evalPeriod: 10);
        Span<ulong> buf = stackalloc ulong[32];
        for (var i = 0; i < 20; i++) p.OnAccess(0x1000UL, 0x2000UL, true, buf);
        // A hit on a line never prefetched isn't eligible, so no evaluation period
        // could have completed — every level stays at its cold-start default.
        for (var lvl = 1; lvl <= 16; lvl++) Assert.Equal(0, p.BestOffsetForLookahead(lvl));
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var p = new MlopPrefetcher();
        Span<ulong> buf = stackalloc ulong[32];
        for (var i = 0; i < 20_000; i++) {
            var pc = (ulong)(0x1000 + i % 64 * 4);
            var addr = (ulong)(0x2000 + i % 256 * MlopPrefetcherTests.Line);
            buf.Clear();
            int cnt = p.OnAccess(pc, addr, i % 3 == 0, buf);
            Assert.True(cnt is >= 0 and <= 16);
        }
    }

    // ── The core oracle: per-lookahead-level offset selection ────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DenseStrideStream_LearnsOffsetEqualToStrideTimesLevel(int stride) {
        // For a dense stride-k demand-miss stream, the exclusion logic forces
        // bestOffset[L] == k * L for every lookahead level L = 1..16: at level L,
        // the L-1 most recently recorded positions are excluded, so the smallest
        // surviving multiple-of-k offset — and the one with the most scoring
        // opportunities — is exactly k*L. Huge pages remove page-boundary
        // interference; a large evaluation period gives many region-passes to
        // stabilize the ranking despite periodic AMT region resets.
        var p = new MlopPrefetcher(pageBytes: 1 << 24, evalPeriod: 2000);
        Span<ulong> buf = stackalloc ulong[32];
        ulong addr = 0;

        for (var i = 0; i < 8000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
            addr += (ulong)stride * MlopPrefetcherTests.Line;
        }

        for (var lvl = 1; lvl <= 16; lvl++)
            Assert.Equal(stride * lvl, p.BestOffsetForLookahead(lvl));
    }

    [Fact]
    public void DenseStrideStream_IssuesPrefetchesInLookaheadPriorityOrder() {
        // Once offsets are learned, targets must be written level-1-first (soonest
        // lead time) through level-16-last, deduplicated.
        var p = new MlopPrefetcher(pageBytes: 1 << 24, evalPeriod: 2000);
        Span<ulong> buf = stackalloc ulong[32];
        ulong addr = 0;

        for (var i = 0; i < 8000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
            addr += MlopPrefetcherTests.Line;
        }

        buf.Clear();
        int cnt = p.OnAccess(0x1000UL, addr, false, buf);
        Assert.Equal(16, cnt); // stride 1: offsets 1..16, all distinct, all same-page
        for (var lvl = 1; lvl <= 16; lvl++)
            Assert.Equal(addr + (ulong)lvl * MlopPrefetcherTests.Line, buf[lvl - 1]);
    }

    // ── Page-boundary clamp ───────────────────────────────────────────────────

    [Fact]
    public void PageBoundary_DropsCrossingTargets() {
        // Small 4KB pages (128 lines): once offsets grow past the remaining
        // in-page lines, those levels' targets must be dropped, not wrapped.
        var p = new MlopPrefetcher(evalPeriod: 2000);
        Span<ulong> buf = stackalloc ulong[32];
        ulong addr = 0;

        for (var i = 0; i < 8000; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, addr, false, buf);
            addr += MlopPrefetcherTests.Line;
            if (addr % 4096 == 0) addr = 0; // stay in-page-pattern across many pages
        }

        // Near the top of a page, only offsets that stay in-page may be issued.
        ulong nearTop = 4096UL - 3 * MlopPrefetcherTests.Line; // 3 lines from the boundary
        buf.Clear();
        int cnt = p.OnAccess(0x1000UL, nearTop, false, buf);
        for (var k = 0; k < cnt; k++)
            Assert.True(buf[k] >> 12 == nearTop >> 12, $"target {buf[k]:x} crossed the page boundary");
    }

    // ── Prefetched-hit eligibility ────────────────────────────────────────────

    [Fact]
    public void PrefetchedHit_KeepsStreamAlive() {
        // Once a stream is fully covered every demand access hits — on a line MLOP
        // itself prefetched. Those prefetched hits must stay eligible, or the
        // prefetcher would stall its own stream (same approach as the BOP test).
        var p = new MlopPrefetcher(pageBytes: 1 << 24, evalPeriod: 2000);
        Span<ulong> buf = stackalloc ulong[32];

        const int trackLen = 256;
        var prefetchLog = new ulong[trackLen];
        var logHead = 0;

        ulong addr = 0;
        var issuedInSteadyState = 0;
        const int warmup = 8000;
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
                for (var k = 0; k < cnt; k++) {
                    prefetchLog[logHead % trackLen] = buf[k];
                    logHead++;
                }

                if (i >= warmup) issuedInSteadyState++;
            }

            addr += MlopPrefetcherTests.Line;
        }

        Assert.True(
            issuedInSteadyState >= check * 3 / 4,
            $"steady-state prefetches: {issuedInSteadyState}/{check}"
        );
    }
}
