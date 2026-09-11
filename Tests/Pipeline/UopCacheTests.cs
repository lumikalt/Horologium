#region

using Mechanism;
using Orrery.Cache;
using Pipeline;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Unit tests for <see cref="UopCache" /> in isolation (µops-section item, Solomon
///     et al. ISLPED 2001) — no ISA dependency, just the cache structure itself: line-based
///     storage tagged by basic-block start PC, single-entry-point lookup semantics, and LRU
///     eviction reused from <see cref="IReplacementPolicy" />.
/// </summary>
public class UopCacheTests {
    private static FakeTooth Tooth(ulong pc, ToothClass cls = ToothClass.IntegerAlu) => new() { Pc = pc, Class = cls, };

    [Fact]
    public void Lookup_UnknownPc_Misses() {
        var cache = new UopCache(4, 2, 4);
        Assert.False(cache.TryLookup(0x1000, out _, out _));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void Insert_Then_Lookup_ReturnsExactUops() {
        var cache = new UopCache(4, 2, 4);
        ITooth[] uops = [Tooth(0x1000), Tooth(0x1004), Tooth(0x1008, ToothClass.ConditionalBranch),];

        cache.Insert(0x1000, uops, true);

        Assert.True(cache.TryLookup(0x1000, out IReadOnlyList<ITooth> hit, out bool endsInBranch));
        Assert.Equal(3, hit.Count);
        Assert.Same(uops[0], hit[0]);
        Assert.Same(uops[1], hit[1]);
        Assert.Same(uops[2], hit[2]);
        Assert.True(endsInBranch);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Builds);
    }

    [Fact]
    public void Lookup_AtNonStartPc_MissesEvenInsideAnExistingLine() {
        var cache = new UopCache(4, 2, 4);
        cache.Insert(0x1000, [Tooth(0x1000), Tooth(0x1004), Tooth(0x1008),], false);

        // Basic blocks are entered only via their first instruction (paper §2.4) — mid-line PCs
        // are always a miss, even though the address is "inside" the cached bytes.
        Assert.False(cache.TryLookup(0x1004, out _, out _));
    }

    [Fact]
    public void Insert_SameStartPc_OverwritesInPlace() {
        var cache = new UopCache(1, 2, 4);
        cache.Insert(0x1000, [Tooth(0x1000),], false);
        cache.Insert(0x2000, [Tooth(0x2000),], false);

        ITooth[] rebuilt = [Tooth(0x1000), Tooth(0x1004, ToothClass.Branch),];
        cache.Insert(0x1000, rebuilt, true);

        // Still only 2 distinct blocks resident — the re-insert updated 0x1000's line rather
        // than evicting 0x2000's.
        Assert.True(cache.TryLookup(0x1000, out IReadOnlyList<ITooth> hit1000, out bool ends1000));
        Assert.Equal(2, hit1000.Count);
        Assert.True(ends1000);
        Assert.True(cache.TryLookup(0x2000, out IReadOnlyList<ITooth> hit2000, out _));
        Assert.Single(hit2000);
    }

    [Fact]
    public void Insert_ThrowsWhenEmpty() {
        var cache = new UopCache(4, 2, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Insert(0x1000, [], false));
    }

    [Fact]
    public void Insert_ThrowsWhenExceedingLineCapacity() {
        var cache = new UopCache(4, 2, 2);
        ITooth[] tooMany = [Tooth(0x1000), Tooth(0x1004), Tooth(0x1008),];
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Insert(0x1000, tooMany, false));
    }

    [Fact]
    public void Eviction_DiscardsLeastRecentlyUsedWay() {
        // Force every start PC used here into set 0 (blockShift large enough that all three
        // addresses collide), 2 ways — the third insert must evict one of the first two.
        var cache = new UopCache(1, 2, 1, 16);

        cache.Insert(0x1000, [Tooth(0x1000),], false);
        cache.Insert(0x2000, [Tooth(0x2000),], false);

        // Touch 0x1000 so it becomes MRU; 0x2000 is now the LRU victim.
        Assert.True(cache.TryLookup(0x1000, out _, out _));

        cache.Insert(0x3000, [Tooth(0x3000),], false);

        Assert.True(cache.TryLookup(0x1000, out _, out _));  // survived
        Assert.True(cache.TryLookup(0x3000, out _, out _));  // just inserted
        Assert.False(cache.TryLookup(0x2000, out _, out _)); // evicted
    }

    [Fact]
    public void HitsMissesBuilds_TrackActivity() {
        var cache = new UopCache(4, 2, 4);

        Assert.False(cache.TryLookup(0x1000, out _, out _)); // miss #1
        cache.Insert(0x1000, [Tooth(0x1000),], false);       // build #1
        Assert.True(cache.TryLookup(0x1000, out _, out _));  // hit #1
        Assert.True(cache.TryLookup(0x1000, out _, out _));  // hit #2
        Assert.False(cache.TryLookup(0x9999, out _, out _)); // miss #2

        Assert.Equal(2, cache.Hits);
        Assert.Equal(2, cache.Misses);
        Assert.Equal(1, cache.Builds);
    }

    private sealed class FakeTooth : ITooth {
        public ulong Pc { get; init; }
        public uint RawEncoding => 0;
        public int SizeBytes => 4;
        public int DestinationRegister => -1;
        public IReadOnlyList<int> SourceRegisters { get; } = [];
        public ToothClass Class { get; init; }
        public object? Payload => null;
    }
}