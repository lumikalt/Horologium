#region

using Orrery.Cache;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for PythiaPrefetcher (Bera et al., MICRO 2021).
///     blockBytes=32 throughout; pageBytes=4096 unless stated otherwise.
/// </summary>
public sealed class PythiaPrefetcherTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new PythiaPrefetcher(33));
    }

    [Fact]
    public void Constructor_NonPow2PageBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new PythiaPrefetcher(pageBytes: 3000));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void SingleAccess_NoCrash_ReturnsZeroOrOne() {
        var p = new PythiaPrefetcher();
        Span<ulong> buf = stackalloc ulong[2];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, buf);
        Assert.True(cnt is 0 or 1);
    }

    [Fact]
    public void SingleAccess_EmptySpan_NeverCrashes() {
        var p = new PythiaPrefetcher();
        Span<ulong> empty = [];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, empty);
        Assert.Equal(0, cnt);
    }

    // ── Page-crossing suppression ─────────────────────────────────────────────

    [Fact]
    public void AllPrefetches_StayOnSamePage() {
        // Use a large page so page-crossing rarely occurs due to page size,
        // but still exercise the guard.  With blockBytes=32 and 4KB pages,
        // an access at the very end of a page would trigger the guard.
        var p = new PythiaPrefetcher();
        Span<ulong> buf = stackalloc ulong[1];
        const ulong pc = 0xABCDUL;

        // Access lines near the end of page 0 (lines 120..127 = addrs 3840..4064).
        for (var i = 0; i < 300; i++) {
            var addr = (ulong)(120 * 32 + i % 8 * 32); // cycles within lines 120..127
            buf.Clear();
            int cnt = p.OnAccess(pc, addr, false, buf);
            if (cnt > 0) {
                ulong prefetchPage = buf[0] >> 12;
                ulong demandPage = addr >> 12;
                Assert.Equal(demandPage, prefetchPage);
            }
        }
    }

    // ── RL mechanics ─────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        // Stress test: 2000 accesses with varying PCs and addresses.
        var p = new PythiaPrefetcher();
        Span<ulong> buf = stackalloc ulong[1];
        for (var i = 0; i < 2000; i++) {
            var pc = (ulong)(0x1000 + i % 64 * 4);
            var addr = (ulong)(0x2000 + i % 256 * 32);
            buf.Clear();
            int cnt = p.OnAccess(pc, addr, i % 3 == 0, buf);
            Assert.True(cnt is 0 or 1);
        }
    }

    [Fact]
    public void ConstantStride_ConvergesAndPrefetchesAhead() {
        // After enough accesses with a constant stride of +1 line, the RL agent
        // should converge to a positive-offset action (forward prefetch).
        // Use a 1 MB page so stride-1 accesses never cross a page boundary.
        // Track all recently-issued prefetch addresses in a circular log so that
        // wasHit=true whenever the cache would contain the installed prefetch.
        var p = new PythiaPrefetcher(32, 1 << 20);
        Span<ulong> buf = stackalloc ulong[1];
        const ulong pc = 0x1000UL;
        ulong addr = 200 * 32UL;
        const int trackLen = 40;
        var prefetchLog = new ulong[trackLen];
        var logHead = 0;
        var forwardCount = 0;
        const int warmup = 5000;
        const int check = 200;

        for (var i = 0; i < warmup + check; i++) {
            // wasHit = true when any tracked prefetch matches this demand
            var wasHit = false;
            for (var j = 0; j < trackLen; j++)
                if (prefetchLog[j] != 0 && prefetchLog[j] == addr) {
                    wasHit = true;
                    break;
                }

            buf.Clear();
            p.OnAccess(pc, addr, wasHit, buf);

            if (buf[0] != 0) {
                prefetchLog[logHead % trackLen] = buf[0];
                logHead++;
            }

            if (i >= warmup)
                // "Forward" = prefetch is strictly ahead and within the 32-line action range
                if (buf[0] > addr && buf[0] <= addr + 32 * 32)
                    forwardCount++;

            addr += 32;
        }

        // After convergence the prefetcher should reliably issue forward prefetches.
        Assert.True(
            forwardCount >= 150,
            $"Convergence: expected ≥150 forward prefetches in last 200, got {forwardCount}"
        );
    }
}