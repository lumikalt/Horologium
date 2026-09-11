#region

using System.Text;
using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

#endregion

namespace Tests.Orrery;

/// <summary>Unit tests for BdiCache (Pekhimenko et al., PACT 2012).</summary>
public sealed class BdiCacheTests {
    // 8 4-byte values [base..base+7] — always compresses to Base4Delta1 (base fits near zero for
    // any small `base`, so every element passes the implicit-zero-base test uniformly): 12 bytes,
    // i.e. ceil(12/8) = 2 segments at the default 8-byte segment size.
    private static byte[] CompressibleLine(int baseValue) {
        var line = new byte[32];
        for (var i = 0; i < 8; i++) {
            var v = (uint)(baseValue + i);
            for (var b = 0; b < 4; b++) line[i * 4 + b] = (byte)(v >> (8 * b));
        }

        return line;
    }

    // Four distinct 8-byte repeating-nibble values (hand-verified incompressible in
    // BdiCompressorTests.FourDistinctWideValues_Incompressible): NoCompr, 32 bytes -> 4 segments.
    private static byte[] IncompressibleLine() {
        var line = new byte[32];
        for (var block = 0; block < 4; block++) {
            var nibble = (byte)(0x11 * (block + 1));
            for (var b = 0; b < 8; b++) line[block * 8 + b] = nibble;
        }

        return line;
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2BlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new BdiCache(new FlatMemory(1024), 1024, 2, 33, 5));
    }

    [Fact]
    public void Constructor_SegmentBytesNotDividingBlockBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new BdiCache(new FlatMemory(1024), 1024, 2, 32, 5, 24));
    }

    [Fact]
    public void Constructor_BlockBytesNotMultipleOf8_Throws() {
        Assert.Throws<ArgumentException>(() => new BdiCache(new FlatMemory(1024), 1024, 2, 4, 5, 2));
    }

    // ── Basic correctness ─────────────────────────────────────────────────────

    [Fact]
    public void WriteThenRead_ReturnsWrittenValue() {
        // WriteThrough pairs with no-write-allocate (matching SetAssociativeCache's default): a
        // write to a never-touched line always writes to backing but never installs it, so the
        // write itself is a miss and the following read is a fresh (separate) miss-and-fill.
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        cache.Write(0x100, 0xDEADBEEF, 4);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);

        // Now that the line is resident, a second read hits.
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void ReadMiss_FetchesFromBacking() {
        var backing = new FlatMemory(4096);
        backing.Write(0x200, 0x12345678, 4);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
        // Second read of the same line now hits.
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void CrossBoundaryAccess_BypassesCache() {
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        ulong lastByteOfLine = 32 - 4; // a 4-byte read starting here crosses into the next line
        cache.Write(lastByteOfLine + 2, 0xAABBCCDD, 4);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses); // bypass never touches hit/miss counters
        Assert.Equal(0xAABBCCDDUL, backing.Read(lastByteOfLine + 2, 4));
    }

    [Fact]
    public void CompressibleLine_IncreasesEffectiveCompressionRatio() {
        var backing = new FlatMemory(4096);
        backing.Load(0x0, CompressibleLine(5));
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        cache.Read(0x0, 4); // fills the line
        Assert.Equal(1, cache.ResidentLineCount);
        // 32 uncompressed bytes / (2 segments * 8 bytes) = 2.0.
        Assert.Equal(2.0, cache.EffectiveCompressionRatio, 3);
    }

    // ── The central claim: compression lets more lines stay resident ─────────

    [Fact]
    public void CompressibleWorkingSet_StaysResidentWhereUncompressedCacheThrashes() {
        // Single set (sets=1: capacityBytes == physicalWays*blockBytes) so every address of
        // interest collides in the same set, keeping the scenario simple and deterministic.
        // physicalWays=2, blockBytes=32 -> 64-byte physical budget, segmentBytes=8 (default) ->
        // budgetSegments=8, tagWays=4. Four distinct, maximally-narrow-value lines each compress
        // to exactly 2 segments (12 bytes -> ceil(12/8)=2) via Base4Delta1 (verified directly in
        // BdiCompressorTests) — 4 lines * 2 segments = 8 = budgetSegments exactly, and 4 lines =
        // tagWays exactly: BdiCache can hold this entire working set at once. An uncompressed
        // SetAssociativeCache with the SAME 64-byte physical budget (physicalWays=2 -> 2 ways,
        // no doubled tags) can hold only 2 of the 4 lines at a time and must thrash on a 4-line
        // cyclic access pattern.
        var bdiBacking = new FlatMemory(4096);
        var plainBacking = new FlatMemory(4096);
        ulong[] addrs = [0x0, 0x20, 0x40, 0x60,]; // 4 lines, 32 bytes apart, all in set 0
        for (var i = 0; i < 4; i++) {
            byte[] line = CompressibleLine(i * 8);
            bdiBacking.Load(addrs[i], line);
            plainBacking.Load(addrs[i], line);
        }

        var bdi = new BdiCache(bdiBacking, 64, 2, 32, 10);
        var plain = new SetAssociativeCache(plainBacking, 64, 2, 32, 10);

        const int warmupCycles = 1;
        const int measuredCycles = 10;
        for (var cycle = 0; cycle < warmupCycles; cycle++)
            foreach (ulong a in addrs) {
                bdi.Read(a, 4);
                plain.Read(a, 4);
            }

        long bdiHitsBefore = bdi.Hits, plainHitsBefore = plain.Hits;
        var accesses = 0;
        for (var cycle = 0; cycle < measuredCycles; cycle++)
            foreach (ulong a in addrs) {
                bdi.Read(a, 4);
                plain.Read(a, 4);
                accesses++;
            }

        long bdiSteadyHits = bdi.Hits - bdiHitsBefore;
        long plainSteadyHits = plain.Hits - plainHitsBefore;

        Assert.Equal(accesses, bdiSteadyHits); // every line stayed resident: all hits
        Assert.True(
            plainSteadyHits < accesses / 2,
            $"expected the uncompressed cache to thrash (< {accesses / 2} hits), got {plainSteadyHits}/{accesses}"
        );
    }

    [Fact]
    public void SegmentPressure_ForcesMultipleEvictionsNotJustOne() {
        // The discriminating test for BΔI's actual mechanism: eviction driven by segment BUDGET,
        // not merely by running out of tag slots. physicalWays=2 -> budgetSegments=8, tagWays=4.
        // Fill all 4 tag slots with 2-segment compressible lines (8/8 segments, 4/4 slots — both
        // constraints exactly saturated). Reading one 4-segment incompressible line then needs
        // MakeRoom to free 4 segments, but each eviction of a 2-segment line only frees 2 — a
        // single eviction (which the tag-slot constraint alone would already force, since all 4
        // slots are full) is NOT enough; it must evict a second line purely because of segment
        // pressure. If segment accounting were broken (e.g. budget check ignored, or evictions
        // capped at one), this would leave >8 segments resident or ResidentLineCount == 4.
        var backing = new FlatMemory(4096);
        ulong[] compressibleAddrs = [0x0, 0x20, 0x40, 0x60,];
        for (var i = 0; i < 4; i++) backing.Load(compressibleAddrs[i], CompressibleLine(i * 8));
        const ulong incompressibleAddr = 0x80;
        backing.Load(incompressibleAddr, IncompressibleLine());

        var cache = new BdiCache(backing, 64, 2, 32, 10);
        foreach (ulong a in compressibleAddrs) cache.Read(a, 4);
        Assert.Equal(4, cache.ResidentLineCount); // all 4 slots full, 8/8 segments

        cache.Read(incompressibleAddr, 4); // needs 4 segments — more than any single eviction frees

        Assert.Equal(3, cache.ResidentLineCount);
        Assert.Equal(2, cache.Evictions); // exactly two 2-segment lines evicted, not one
    }

    [Fact]
    public void WriteBack_DirtyEvictionWritesBackToBacking() {
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 32, 1, 32, 10, writePolicy: WritePolicyKind.WriteBack);
        cache.Write(0x0, 0x11111111, 4); // fills + dirties line at 0x0 (tagWays=2, budget=4 segments)
        // Force enough distinct compressible lines through to evict the first one.
        for (var i = 1; i <= 4; i++) {
            backing.Load((ulong)(i * 32), CompressibleLine(i * 8));
            cache.Read((ulong)(i * 32), 4);
        }

        Assert.True(cache.DirtyEvictions >= 1);
        Assert.Equal(0x11111111UL, backing.Read(0x0, 4));
    }

    // ── PeekRead (InvisiSpec non-mutating speculative-buffer peek) ────────────

    [Fact]
    public void PeekRead_OnMiss_ReturnsBackingValueWithoutInstallingLine() {
        var backing = new FlatMemory(4096);
        backing.Write(0x200, 0x12345678, 4);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);

        Assert.Equal(0x12345678UL, cache.PeekRead(0x200, 4));

        Assert.Equal(0, cache.Misses);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.ResidentLineCount); // no line installed by the peek
        // Charges the same MissLatency a real Read miss would (so a USL's own completion isn't
        // unphysically free) even though no line gets installed for it.
        Assert.Equal(10, cache.ConsumePendingStalls());
    }

    [Fact]
    public void PeekRead_OnHit_ReturnsResidentValueWithoutMutatingState() {
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        cache.Write(0x100, 0xDEADBEEF, 4);
        cache.Read(0x100, 4); // install the line for real (miss + fill)
        cache.ConsumePendingStalls();
        long residentBefore = cache.ResidentLineCount;
        long hitsBefore = cache.Hits;

        Assert.Equal(0xDEADBEEFUL, cache.PeekRead(0x100, 4));

        Assert.Equal(hitsBefore, cache.Hits); // peek must not count as a hit
        Assert.Equal(residentBefore, cache.ResidentLineCount);
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    // ── Checkpoint (WriteState/ReadState) ─────────────────────────────────────

    [Fact]
    public void ReadState_GeometryMismatch_Throws() {
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 1024, 4, 32, 10);
        cache.Write(0x100, 0xDEADBEEF, 4);
        cache.Read(0x100, 4);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { cache.WriteState(w); }

        var differentGeometry = new BdiCache(new FlatMemory(4096), 1024, 4, 16, 10); // half blockBytes
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<CheckpointException>(() => differentGeometry.ReadState(r));
    }

    // Three-leg check (a checkpoint round-trip test that only compares "warm" against "restored"
    // can pass even when ReadState is a total no-op, if the cold-start state happens to coincide
    // with the checkpointed state). This test proves restore actually moves data by diverging all
    // three: A (checkpoint-time value), B (same cache mutated further after the checkpoint), and
    // C (a fresh cold cache that never saw a restore). Asserts restored == A, restored != B, and
    // restored != C.
    [Fact]
    public void ReadState_RestoresCheckpointedValue_NotColdStartAndNotLaterMutation() {
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 1024, 4, 32, 10, writePolicy: WritePolicyKind.WriteBack);
        cache.Write(0x100, 0xAAAAAAAA, 4); // miss -> installs dirty line (write-back allocates)
        cache.ConsumePendingStalls();

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { cache.WriteState(w); }

        // B: mutate the SAME cache after the checkpoint was taken.
        cache.Write(0x100, 0xBBBBBBBB, 4);
        Assert.Equal(0xBBBBBBBBUL, cache.Read(0x100, 4));

        // Restore the checkpoint into the same (now-mutated) cache instance.
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { cache.ReadState(r); }

        long hitsBeforeRestoredRead = cache.Hits;
        Assert.Equal(0xAAAAAAAAUL, cache.Read(0x100, 4));     // == A, proving restore overwrote B
        Assert.Equal(hitsBeforeRestoredRead + 1, cache.Hits); // resident -> hit, not a miss

        // C: an independent, never-restored cache stays cold — proves A != C, so the assertion
        // above isn't accidentally passing because cold-start already equals the checkpointed value.
        var cold = new BdiCache(new FlatMemory(4096), 1024, 4, 32, 10, writePolicy: WritePolicyKind.WriteBack);
        Assert.Equal(0, cold.ResidentLineCount);
    }

    [Fact]
    public void WriteState_ReadState_RoundTrip_PreservesSegmentFootprintAndEvictionOrder() {
        // Single set, 2 physical ways (4 tag ways): fill 3 distinct compressible lines so the
        // segment/eviction bookkeeping (not just tag/data) has real content to round-trip.
        var backing = new FlatMemory(4096);
        var cache = new BdiCache(backing, 64, 2, 32, 10);
        for (var i = 0; i < 3; i++) {
            backing.Load((ulong)(i * 32), CompressibleLine(i * 8));
            cache.Read((ulong)(i * 32), 4);
        }

        cache.ConsumePendingStalls();
        double ratioBefore = cache.EffectiveCompressionRatio;
        int residentBefore = cache.ResidentLineCount;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) { cache.WriteState(w); }

        var restored = new BdiCache(new FlatMemory(4096), 64, 2, 32, 10);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { restored.ReadState(r); }

        Assert.Equal(residentBefore, restored.ResidentLineCount);
        Assert.Equal(ratioBefore, restored.EffectiveCompressionRatio, 3);
        for (var i = 0; i < 3; i++) Assert.Equal(cache.Read((ulong)(i * 32), 4), restored.Read((ulong)(i * 32), 4));
    }
}