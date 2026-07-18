using Orrery.Cache;

namespace Tests.Orrery;

/// <summary>
///     Unit tests for SppPrefetcher (Kim et al., MICRO 2016).
///     blockBytes=32, pageBytes=4096 (128 lines/page) throughout.
/// </summary>
public sealed class SppPrefetcherTests {
    private const int Line = 32;
    private const int LinesPerPage = 128;

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new SppPrefetcher(33));
    }

    [Fact]
    public void Constructor_PageNotLargerThanBlock_Throws() {
        Assert.Throws<ArgumentException>(() => new SppPrefetcher(64, 64));
    }

    // ── Single-access sanity ──────────────────────────────────────────────────

    [Fact]
    public void FirstAccess_NoHistory_NoPrefetch() {
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        int cnt = p.OnAccess(0x1000UL, 0x2000UL, false, buf);
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void EmptySpan_NeverCrashes() {
        var p = new SppPrefetcher();
        Span<ulong> empty = [];
        for (var i = 0; i < 300; i++)
            _ = p.OnAccess(0x1000UL, (ulong)(0x2000 + i * SppPrefetcherTests.Line), false, empty);
    }

    [Fact]
    public void ManyAccesses_NoCrash() {
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        var x = 99UL;
        for (var i = 0; i < 20_000; i++) {
            x = x * 6364136223846793005UL + 1442695040888963407UL;
            var addr = (ulong)(0x2000 + (long)(x >> 20) % 4096 * SppPrefetcherTests.Line);
            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, i % 3 == 0, buf);
            Assert.True(cnt is >= 0 and <= 8);
        }
    }

    // ── Pattern learning & coverage ───────────────────────────────────────────

    [Fact]
    public void SequentialStream_CoversItself() {
        // Stride-1 stream: signatures converge, the pattern table learns (+1) with
        // full confidence, and lookahead runs the prefetch frontier ahead of the
        // demand stream. In steady state nearly every accessed line must have been
        // prefetched beforehand.
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        var issued = new HashSet<ulong>();

        ulong lineAddr = 0;
        var covered = 0;
        const int warmup = 1500;
        const int check = 1000;

        for (var i = 0; i < warmup + check; i++) {
            ulong addr = lineAddr * SppPrefetcherTests.Line;
            if (i >= warmup && issued.Contains(addr)) covered++;
            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, false, buf);
            for (var k = 0; k < cnt; k++) issued.Add(buf[k]);
            lineAddr++;
        }

        Assert.True(covered >= check * 9 / 10, $"coverage {covered}/{check}");
    }

    [Fact]
    public void AlternatingDeltaPattern_LearnedAndPredicted() {
        // (+1, +3) repeating — a pattern no single stride/offset can cover exactly.
        // After training, the issued prefetch targets must all be future lines of
        // the pattern (accuracy), and coverage must be high.
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        var issued = new HashSet<ulong>();

        // Pattern within pages: offsets 0,1,4,5,8,9,12,... (period 4 lines).
        ulong lineAddr = 0;
        var step = 0;
        var covered = 0;
        var accurate = 0;
        var totalIssued = 0;
        const int warmup = 2000;
        const int check = 1000;

        for (var i = 0; i < warmup + check; i++) {
            ulong addr = lineAddr * SppPrefetcherTests.Line;
            if (i >= warmup && issued.Contains(addr)) covered++;
            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, false, buf);
            for (var k = 0; k < cnt; k++) {
                issued.Add(buf[k]);
                if (i >= warmup) {
                    totalIssued++;
                    // Pattern lines are ≡ 0 or 1 (mod 4).
                    ulong l = buf[k] / SppPrefetcherTests.Line;
                    if (l % 4 <= 1) accurate++;
                }
            }

            lineAddr += step % 2 == 0 ? 1UL : 3UL;
            step++;
        }

        Assert.True(covered >= check * 8 / 10, $"coverage {covered}/{check}");
        Assert.True(totalIssued > 0);
        Assert.True(accurate >= totalIssued * 9 / 10, $"accuracy {accurate}/{totalIssued}");
    }

    // ── Page-boundary learning (the signature SPP feature) ────────────────────

    [Fact]
    public void PageBoundaryLearning_PrefetchesOnFirstTouchOfNewPage() {
        // Train a stride-1 stream through one full physical page. Predictions off
        // the page end are recorded in the GHR. The FIRST access to a completely
        // different (non-adjacent) physical page, at the offset the GHR predicted,
        // must bootstrap the signature and prefetch immediately — no warmup.
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];

        const ulong pageA = 5;
        for (var off = 0; off < SppPrefetcherTests.LinesPerPage; off++) {
            buf.Clear();
            p.OnAccess(
                0x1000UL, (pageA * SppPrefetcherTests.LinesPerPage + (ulong)off) * SppPrefetcherTests.Line, false, buf
            );
        }

        // Physically distant page, first touch at offset 0 (= (127 + 1) mod 128).
        const ulong pageB = 42;
        buf.Clear();
        ulong baseAddr = pageB * SppPrefetcherTests.LinesPerPage * SppPrefetcherTests.Line;
        int cnt = p.OnAccess(0x1000UL, baseAddr, false, buf);

        Assert.True(cnt >= 1, "no prefetch on first touch of the new page");
        Assert.Equal(baseAddr + SppPrefetcherTests.Line, buf[0]); // continues the (+1) pattern
    }

    // ── Throttling ────────────────────────────────────────────────────────────

    [Fact]
    public void RandomPattern_MostlySilent() {
        // Random deltas never build signature confidence: C_delta/C_sig dilutes
        // below the prefetch threshold and SPP stays quiet. The address range must
        // be wide enough that pages are effectively never revisited within the
        // run — the Signature/Pattern Tables are small (256/512 entries), and a
        // narrow range makes "random" traffic quasi-periodic (the same handful of
        // pages recur often enough to accumulate genuine, if accidental,
        // correlations — not a bug, just not what this test means by "random").
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        var x = 7UL;
        var issuedLate = 0;
        const int total = 12_000;
        const int window = 2000;

        for (var i = 0; i < total; i++) {
            x = x * 6364136223846793005UL + 1442695040888963407UL;
            ulong addr = (x >> 16) % (1UL << 30) * SppPrefetcherTests.Line;
            buf.Clear();
            int cnt = p.OnAccess(0x1000UL, addr, false, buf);
            if (i >= total - window) issuedLate += cnt;
        }

        Assert.True(issuedLate < window / 4, $"issued {issuedLate} prefetches in last {window} random accesses");
    }

    // ── Redundancy filter ─────────────────────────────────────────────────────

    [Fact]
    public void PrefetchFilter_DropsRedundantRequests() {
        // Warm a stride-1 stream, then re-access the same line twice: delta 0 does
        // not advance the signature, so the same predictions recur — and every one
        // of them must be dropped by the filter as already-prefetched.
        var p = new SppPrefetcher();
        Span<ulong> buf = stackalloc ulong[8];
        ulong lineAddr = 0;
        for (var i = 0; i < 500; i++) {
            buf.Clear();
            p.OnAccess(0x1000UL, lineAddr * SppPrefetcherTests.Line, false, buf);
            lineAddr++;
        }

        ulong addr = lineAddr * SppPrefetcherTests.Line;
        buf.Clear();
        p.OnAccess(0x1000UL, addr, false, buf);
        buf.Clear();
        int second = p.OnAccess(0x1000UL, addr, false, buf);
        Assert.Equal(0, second);
    }
}