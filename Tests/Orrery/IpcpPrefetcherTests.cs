using Orrery.Cache;
using Xunit;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for IpcpPrefetcher (Pakalapati &amp; Panda, ISCA 2020).
/// All tests use blockBytes=32, pageBytes=4096 (128 lines/page), regionBytes=2048 (64 lines/region).
/// Dense threshold = ceil(75 % × 64) = 48.
/// </summary>
public sealed class IpcpPrefetcherTests {
    // Convenience wrappers
    private static IpcpPrefetcher Make() => new(blockBytes: 32, pageBytes: 4096, regionBytes: 2048);

    private static int Fire(IpcpPrefetcher p, ulong pc, ulong addr, Span<ulong> buf)
        => p.OnAccess(pc, addr, wasHit: false, buf);

    // ── First-access: no prefetch ─────────────────────────────────────────────

    [Fact]
    public void FirstAccess_NoIpEntry_NoPrefetch() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        Assert.Equal(0, Fire(p, 0x1000UL, 0x2000UL, buf));
    }

    // ── CS: Constant Stride ───────────────────────────────────────────────────

    [Fact]
    public void Cs_ConstantStride_GainsConfidenceAndPrefetches() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x1000UL;
        // Confidence model: first observation SETS stride (conf stays 0); each subsequent
        // match increments. Conf reaches 2 on the 3rd stride observation = 4th access.
        Fire(p, pc, 0,   buf); // init entry
        Fire(p, pc, 32,  buf); // CsStride=1 set, conf=0
        Fire(p, pc, 64,  buf); // stride=1 match → conf=1
        int cnt = Fire(p, pc, 96, buf); // stride=1 match → conf=2 → CS prefetch
        Assert.True(cnt > 0);
        Assert.Equal(128UL, buf[0]); // next line at stride=1 from addr=96
    }

    [Fact]
    public void Cs_Degree3_Issues3Prefetches() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[16];
        ulong pc = 0x1000UL;
        // Warm up CS: 3 accesses to establish stride of 1 line (32 bytes)
        Fire(p, pc, 0,   buf);
        Fire(p, pc, 32,  buf); // conf=1
        Fire(p, pc, 64,  buf); // conf=2 → starts prefetching
        int cnt = Fire(p, pc, 96, buf); // conf=3, prefetch 3 lines ahead
        Assert.Equal(3, cnt);
        Assert.Equal(128UL, buf[0]);
        Assert.Equal(160UL, buf[1]);
        Assert.Equal(192UL, buf[2]);
    }

    [Fact]
    public void Cs_PageCross_NoPrefetchBeyondPage() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x1000UL;
        // Line 126 and 127 are the last two lines of a 4096-byte page (128 lines × 32B).
        ulong line126 = 126 * 32UL;
        ulong line127 = 127 * 32UL;
        // Warm up: stride = 1 line
        Fire(p, pc, line126 - 32, buf); // line125
        Fire(p, pc, line126,      buf); // stride=1, conf=1
        Fire(p, pc, line127,      buf); // stride=1, conf=2
        int cnt = Fire(p, pc, line127 + 32, buf); // conf=3; prefetch would cross page → cnt=0
        // The next address would be page1:line1 (address 4128) — same page? No: page=1 for it.
        // Actually line127 is on page 0 (0..127 on page 0); line128 = 128*32=4096 is on page 1.
        Assert.Equal(0, cnt);
    }

    [Fact]
    public void Cs_StrideChange_ResetsConfidence() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x1000UL;
        // Establish stride=1 at conf=2
        Fire(p, pc, 0,  buf);
        Fire(p, pc, 32, buf);
        Fire(p, pc, 64, buf); // conf=2
        // Change stride to 2 lines
        int cnt = Fire(p, pc, 128, buf); // stride=2 != prev stride=1 → conf drops; check prefetch count
        // conf was 2, now stride mismatch: conf goes to 1 (since conf > 0, decrements)
        // New stride = 2, conf = 1 → no prefetch (conf < 2)
        Assert.Equal(0, cnt);
    }

    // ── CPLX: Complex Stride ─────────────────────────────────────────────────

    [Fact]
    public void Cplx_AlternatingStrides_LearnedAndPrefetched() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x2000UL;
        // Pattern: stride +1, +3 lines (32B, 96B alternating). CS never fires (alternating stride).
        // The 7-bit signature stabilises into the cycle 43↔85 from access 9 onwards.
        // CSPT[43] is trained with stride=3 at accesses 9 and 11 → conf=1 after access 11.
        // At access 12 (stride=1 from sig=85 → newSig=43): CSPT[43].conf=1 ≥ 1 → CPLX fires.
        ulong[] addrs = [0, 32, 128, 160, 256, 288, 384, 416, 512, 544, 640];
        foreach (ulong a in addrs) Fire(p, pc, a, buf);
        int cnt = Fire(p, pc, 672, buf); // access 12: CPLX fires via CSPT[43]
        Assert.True(cnt > 0, "Expected CPLX prefetch after signature cycle stabilised");
    }

    // ── GS: Global Stream ─────────────────────────────────────────────────────

    [Fact]
    public void Gs_DenseRegion_TriggersPrefetch() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[16];
        // Region 0: lines 0..63 (each 32 bytes). Dense threshold = ceil(64×0.75) = 48.
        // Vary PC so no single IP builds CS confidence; use distinct PCs per access.
        for (int i = 0; i < 48; i++) {
            Fire(p, (ulong)(0x3000 + i * 4), (ulong)(i * 32), buf);
        }

        // Re-use the last PC (which saw line 47 in dense region 0) and access line 48.
        ulong seenPc = 0x3000UL + 47 * 4;
        ulong addr48 = 48 * 32UL; // still in region 0 (< 2048)
        int cnt = Fire(p, seenPc, addr48, buf);
        // Region 0 has been dense since the 48th access and is still dense. GS should fire.
        Assert.True(cnt > 0);
    }

    [Fact]
    public void Gs_Tentative_WhenEnteringNewRegionAfterDense() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[16];
        ulong pc = 0x4000UL;
        // Fill region 0 to dense with a single PC, using stride = 1 line
        // (first access inits IP, subsequent ones advance CS confidence but also update RST).
        // Access lines 0..47 with the same PC.
        Fire(p, pc, 0, buf); // init
        for (int i = 1; i < 48; i++) {
            Fire(p, pc, (ulong)(i * 32), buf);
        }
        // Region 0 is now dense. Last line accessed was 47 (addr 1504). ip.LastRegionDense = true.
        // Cross into region 1 (address 2048): same PC, new region → tentative GS should fire.
        int cnt = Fire(p, pc, 2048, buf);
        Assert.True(cnt > 0, "Expected tentative GS prefetch when entering a new region after dense previous");
    }

    [Fact]
    public void Gs_SparseRegion_NoGsPrefetch() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x5000UL;
        // Access only 10 distinct lines in region 0 (far below dense threshold of 48)
        Fire(p, pc, 0, buf); // init
        for (int i = 1; i < 10; i++) {
            Fire(p, pc, (ulong)(i * 32), buf);
        }
        // Region is not dense. CS confidence = 2 at this point (stride=1 seen ≥2 times).
        // So CS should fire, not GS. But GS should NOT fire.
        int cnt = Fire(p, pc, 320, buf);
        // CS may fire (stride=1, conf≥2). Either way, no GS expected; count may be > 0 from CS.
        // Just verify we don't get DegreeGs=6 prefetches from a sparse region.
        Assert.True(cnt <= 3, $"Expected ≤3 (CS degree), got {cnt}");
    }

    // ── No-page-crossing ─────────────────────────────────────────────────────

    [Fact]
    public void NoCrossPagePrefetch_GsStopsAtBoundary() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[16];
        // Fill a region that straddles the page boundary to make it dense,
        // then access the last line on the page and verify GS stops.
        // Page 0: addresses 0..4095 (128 lines). Region 0: addresses 0..2047 (64 lines).
        // Region 1: addresses 2048..4095 (64 lines). The page boundary is at 4096.
        // Make region 1 dense (48 unique lines, all on page 0).
        ulong pc = 0x6000UL;
        Fire(p, pc, 2048, buf); // init, lineInRegion=0 of region 1
        for (int i = 1; i < 47; i++) {
            Fire(p, pc, 2048UL + (ulong)(i * 32), buf);
        }
        // 47 unique lines accessed (lineInRegion 0..46); dense needs 48.
        // Fire at 3552 (lineInRegion=47) is the 48th unique access → region 1 becomes dense.
        int cnt = Fire(p, pc, 3552, buf);
        Assert.True(cnt > 0);
        for (int i = 0; i < cnt; i++) {
            Assert.True(buf[i] < 4096UL, $"Prefetch {buf[i]} crossed page boundary");
        }
    }

    // ── RR filter deduplication ───────────────────────────────────────────────

    [Fact]
    public void RrFilter_SuppressesDuplicatePrefetches() {
        var p = Make();
        Span<ulong> buf = stackalloc ulong[8];
        ulong pc = 0x7000UL;
        // Warm up CS to conf=2 (needs 4 accesses: init, set-stride, match×1, match×2).
        Fire(p, pc, 0,   buf); // init
        Fire(p, pc, 32,  buf); // CsStride=1 set, conf=0
        Fire(p, pc, 64,  buf); // conf=1
        Fire(p, pc, 96,  buf); // conf=2 → CS issues 128, 160, 192 → all added to RR

        buf.Clear();
        // RR now contains lines 128, 160, 192.
        // CS from addr=128 would prefetch 160, 192, 224. 160 and 192 are in RR, only 224 is new.
        int cnt = Fire(p, pc, 128, buf);
        Assert.Equal(1, cnt);
        Assert.Equal(224UL, buf[0]);
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new IpcpPrefetcher(blockBytes: 33));
    }

    [Fact]
    public void Constructor_TooManyLinesPerRegion_Throws() {
        // blockBytes=16, regionBytes=2048 → linesPerRegion=128 > 64 → throws
        Assert.Throws<ArgumentException>(() => new IpcpPrefetcher(blockBytes: 16, regionBytes: 2048));
    }
}
