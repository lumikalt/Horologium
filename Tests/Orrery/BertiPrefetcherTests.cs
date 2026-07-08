using Orrery.Cache;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for BertiPrefetcher (Navarro-Torres et al., MICRO 2022).
/// blockBytes=32 throughout unless stated otherwise.
/// </summary>
public sealed class BertiPrefetcherTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new BertiPrefetcher(blockBytes: 33));
    }

    [Fact]
    public void Constructor_ZeroLatency_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BertiPrefetcher(latency: 0));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void SingleAccess_NoCrash_ReturnsZero() {
        var p = new BertiPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, wasHit: false, buf);
        Assert.Equal(0, cnt); // no HT history yet → nothing trained
    }

    [Fact]
    public void SingleAccess_EmptySpan_NeverCrashes() {
        var p = new BertiPrefetcher();
        Span<ulong> empty = [];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, wasHit: false, empty);
        Assert.Equal(0, cnt);
    }

    // ── Stress test ───────────────────────────────────────────────────────────

    [Fact]
    public void ManyAccesses_NoCrash() {
        var p = new BertiPrefetcher();
        Span<ulong> buf = stackalloc ulong[4];
        for (var i = 0; i < 2000; i++) {
            var pc   = (ulong)(0x1000 + (i % 64) * 4);
            var addr = (ulong)(0x2000 + (i % 256) * 32);
            buf.Clear();
            int cnt = p.OnAccess(pc, addr, wasHit: i % 3 == 0, buf);
            Assert.True(cnt is >= 0 and <= 4);
        }
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void ConstantStride_ConvergesAndPrefetchesAhead() {
        // With stride=1 (one cache line per access) and latency=10, Berti learns
        // delta=10 (a timely prefetch must be issued 10 accesses before the miss).
        // After warmup the prefetcher should reliably produce a forward prefetch
        // somewhere in addr+1*32 .. addr+20*32.
        //
        // wasHit is approximated the same way as PythiaPrefetcherTests: a circular
        // log of the last 40 issued prefetches; wasHit=true when any entry matches.
        const int latency   = 10;
        var p = new BertiPrefetcher(blockBytes: 32, latency: latency);
        Span<ulong> buf = stackalloc ulong[4];
        const ulong pc = 0x1000UL;
        ulong addr = 200 * 32UL;

        const int trackLen = 40;
        var prefetchLog = new ulong[trackLen];
        var logHead     = 0;

        var forwardCount = 0;
        const int warmup = 2000;
        const int check  = 200;

        for (var i = 0; i < warmup + check; i++) {
            var wasHit = false;
            for (var j = 0; j < trackLen; j++) {
                if (prefetchLog[j] != 0 && prefetchLog[j] == addr) { wasHit = true; break; }
            }

            buf.Clear();
            p.OnAccess(pc, addr, wasHit, buf);

            foreach (ulong t in buf) {
                if (t != 0) {
                    prefetchLog[logHead % trackLen] = t;
                    logHead++;
                }
            }

            if (i >= warmup) {
                // Forward = strictly ahead, within 20-line range
                if (buf[0] > addr && buf[0] <= addr + 20 * 32)
                    forwardCount++;
            }

            addr += 32;
        }

        Assert.True(forwardCount >= 150,
            $"Convergence: expected ≥150 forward prefetches in last {check}, got {forwardCount}");
    }
}
