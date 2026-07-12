using Orrery.Cache;
using Orrery.Spec;
using RiscV32.Memory;

namespace Tests.Orrery;

public class CacheHierarchySpecTests {
    // Tiny backing memory: 256 bytes, pre-filled with index values.
    private static FlatMemory MakeBacking() {
        var m = new FlatMemory(256);
        for (var i = 0; i < 256; i++) m.Write((ulong)i, (ulong)i, 1);
        return m;
    }

    // ── CachePathSpec / MemoryLayers.Build overload ───────────────────────────

    [Fact]
    public void Build_NoCaches_ReadsDirectlyFromBacking() {
        FlatMemory backing = MakeBacking();
        var layers = MemoryLayers.Build(backing, CachePathSpec.Empty);

        Assert.Equal(42UL, layers.Accessor.Read(42, 1));
        Assert.Null(layers.Cache);
        Assert.Null(layers.L2Cache);
        Assert.Null(layers.L3Cache);
    }

    [Fact]
    public void Build_OnePrivateLevel_ColdMissChargesLatency() {
        FlatMemory backing = MakeBacking();
        var path = new CachePathSpec([new CacheLevelSpec(256, 4, 16, 8),]);
        var layers = MemoryLayers.Build(backing, path);

        layers.Accessor.Read(0, 1);
        Assert.Equal(1L, layers.Cache!.Misses);
        Assert.Equal(8, layers.ConsumeAllStalls());
    }

    [Fact]
    public void Build_OnePrivateLevel_WarmHitNoStall() {
        FlatMemory backing = MakeBacking();
        var path = new CachePathSpec([new CacheLevelSpec(256, 4, 16, 8),]);
        var layers = MemoryLayers.Build(backing, path);

        layers.Accessor.Read(0, 1);
        layers.ConsumeAllStalls();
        layers.Accessor.Read(0, 1);

        Assert.Equal(1L, layers.Cache!.Hits);
        Assert.Equal(0, layers.ConsumeAllStalls());
    }

    [Fact]
    public void Build_PrivateAndSharedLevel_MissChargesTotalLatency() {
        FlatMemory backing = MakeBacking();
        var path = new CachePathSpec([new CacheLevelSpec(64, 2, 16, 5),]);
        IReadOnlyList<CacheLevelSpec> shared = [new(512, 4, 16, 20),];
        var layers = MemoryLayers.Build(backing, path, shared);

        layers.Accessor.Read(0, 1); // L1 miss → L2 miss → backing

        Assert.Equal(1L, layers.Cache!.Misses);
        Assert.Equal(1L, layers.L2Cache!.Misses);
        Assert.Equal(25, layers.ConsumeAllStalls());
    }

    [Fact]
    public void Build_PrivateAndSharedLevel_L2HitAfterL1Eviction() {
        // L1: 1 set, 2-way, 16-byte blocks = 32 bytes. Fill both ways, then access a third
        // line to force an eviction; the evicted line should now be in L2.
        FlatMemory backing = MakeBacking();
        var path = new CachePathSpec([new CacheLevelSpec(32, 2, 16, 5),]);
        IReadOnlyList<CacheLevelSpec> shared = [new(1024, 8, 16, 20),];
        var layers = MemoryLayers.Build(backing, path, shared);

        layers.Accessor.Read(0, 1);  // line 0  → L1 miss, L2 miss
        layers.Accessor.Read(16, 1); // line 16 → L1 miss, L2 miss
        layers.Accessor.Read(32, 1); // line 32 → L1 miss, L2 miss; evicts one line from L1
        layers.ConsumeAllStalls();

        long l2HitsBefore = layers.L2Cache!.Hits;
        layers.Accessor.Read(0, 1);
        layers.Accessor.Read(16, 1);
        long l2HitsAfter = layers.L2Cache.Hits;

        Assert.True(l2HitsAfter > l2HitsBefore, "Expected at least one L2 hit after L1 eviction.");
    }

    // ── Per-level replacement policy ─────────────────────────────────────────

    [Fact]
    public void PerLevelPolicy_PrivateAndShared_UseSeparatePolicies() {
        FlatMemory backing = MakeBacking();
        var l1Spec = new CacheLevelSpec(64, 2, 16, 5, ReplacementPolicyKind.Fifo);
        var l2Spec = new CacheLevelSpec(512, 4, 16, 20);
        var path = new CachePathSpec([l1Spec,]);
        var layers = MemoryLayers.Build(backing, path, [l2Spec,]);

        layers.Accessor.Read(0, 1);
        Assert.Equal(1L, layers.Cache!.Misses);
        Assert.Equal(1L, layers.L2Cache!.Misses);
        layers.ConsumeAllStalls();
        // Warm read — L1 hit; L2 not accessed again.
        layers.Accessor.Read(0, 1);
        Assert.Equal(1L, layers.Cache.Hits);
        Assert.Equal(1L, layers.L2Cache.Misses);
    }

    // ── Per-level prefetcher ──────────────────────────────────────────────────

    [Fact]
    public void Prefetcher_OnInnermostLevel_WiredToMemoryLayers() {
        // A level spec with NextLine prefetcher should result in MemoryLayers.Prefetcher != null.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(256, 4, 16, 8, Prefetcher: PrefetcherKind.NextLine, PrefetchLatency: 0);
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]));

        Assert.NotNull(layers.Prefetcher);
    }

    [Fact]
    public void Prefetcher_OnInnermostLevel_NextLineInstallsAdjacentLine() {
        // After a miss on line A, calling TryPrefetch should install line B (the next 16-byte line).
        // With PrefetchLatency = 0 the line arrives immediately; reading B is a hit.
        var backing = new FlatMemory(256);
        var l1 = new CacheLevelSpec(256, 4, 16, 8, Prefetcher: PrefetcherKind.NextLine, PrefetchLatency: 0);
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]));

        // Miss on address 0 (line 0–15); drain that miss stall, then prefetch next line.
        layers.Accessor.Read(0, 1);
        layers.ConsumeAllStalls();

        Span<ulong> buf = stackalloc ulong[4];
        int cnt = layers.Prefetcher!.OnAccess(0, 0, false, buf);
        Assert.True(cnt > 0);
        layers.TryPrefetch(buf[0]);

        // Reading the prefetched line should be a cache hit with no stall.
        layers.Accessor.Read(buf[0], 1);
        Assert.Equal(1L, layers.Cache!.Hits);
        Assert.Equal(0, layers.ConsumeAllStalls());
    }

    [Fact]
    public void Prefetcher_OnlyOnOuterLevel_NotWiredToMemoryLayers() {
        // If the prefetcher is on L2 (shared) but not L1 (innermost), MemoryLayers.Prefetcher
        // is null — TryPrefetch targets DoCache (L1), so an L2-only strategy is not activated.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(64, 2, 16, 5); // no prefetcher
        var l2 = new CacheLevelSpec(512, 4, 16, 20, Prefetcher: PrefetcherKind.NextLine);
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        Assert.Null(layers.Prefetcher);
    }

    [Fact]
    public void Prefetcher_PrefetchLatency_PassedToInnermostCache() {
        // A non-zero PrefetchLatency means the prefetched line is in-flight (not yet usable).
        var backing = new FlatMemory(256);
        var l1 = new CacheLevelSpec(256, 4, 16, 8, Prefetcher: PrefetcherKind.NextLine, PrefetchLatency: 5);
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]));

        Span<ulong> buf = stackalloc ulong[4];
        int cnt = layers.Prefetcher!.OnAccess(0, 0, false, buf);
        Assert.True(cnt > 0);
        layers.TryPrefetch(buf[0]);

        // Line is in-flight; cache should record one in-flight prefetch.
        Assert.Equal(1, layers.Cache!.InFlightPrefetchCount);
    }

    // ── L0 + L1 private levels ────────────────────────────────────────────────

    [Fact]
    public void Build_L0AndL1Private_InnerMostIsL0() {
        // L0: 16 bytes (1 set, 1-way) — only fits one 16-byte line.
        // L1: 128 bytes.
        // First access: L0 miss → L1 miss → backing (both charge latency).
        // Second access (same address): L0 hit, no stall.
        FlatMemory backing = MakeBacking();
        var l0 = new CacheLevelSpec(16, 1, 16, 2);
        var l1 = new CacheLevelSpec(128, 4, 16, 8);
        var path = new CachePathSpec([l0, l1,]);
        var layers = MemoryLayers.Build(backing, path);

        layers.Accessor.Read(0, 1);
        Assert.Equal(1L, layers.Cache!.Misses);      // DoCache = L0 (innermost)
        Assert.Equal(1L, layers.L2Cache!.Misses);    // L2Cache = L1 (next out)
        Assert.Equal(10, layers.ConsumeAllStalls()); // 2 + 8

        layers.Accessor.Read(0, 1);
        Assert.Equal(1L, layers.Cache.Hits); // L0 hit
        Assert.Equal(0, layers.ConsumeAllStalls());
    }

    [Fact]
    public void Build_L0Eviction_FallsBackToL1ThenL2() {
        // L0 holds 1 line. Access line A, then line B (evicts A from L0 to L1).
        // Re-access A: L0 miss, L1 hit.
        FlatMemory backing = MakeBacking();
        var l0 = new CacheLevelSpec(16, 1, 16, 2);
        var l1 = new CacheLevelSpec(128, 4, 16, 8);
        var path = new CachePathSpec([l0, l1,]);
        IReadOnlyList<CacheLevelSpec> shared = [new(1024, 8, 16, 30),];
        var layers = MemoryLayers.Build(backing, path, shared);

        layers.Accessor.Read(0, 1);  // A: L0 miss, L1 miss, L2 miss
        layers.Accessor.Read(16, 1); // B: L0 miss, L1 miss, L2 miss; A evicted from L0 → L1
        layers.ConsumeAllStalls();

        long l1HitsBefore = layers.L2Cache!.Hits; // L2Cache == L1 here
        layers.Accessor.Read(0, 1);               // A: L0 miss, L1 hit
        long l1HitsAfter = layers.L2Cache.Hits;

        Assert.True(l1HitsAfter > l1HitsBefore, "Expected L1 hit after L0 eviction.");
    }

    // ── SharedAcross annotation ───────────────────────────────────────────────

    [Fact]
    public void SharedAcross_AnnotationPreserved() {
        // SharedAcross is structural metadata; verify it round-trips on the spec.
        var l2 = new CacheLevelSpec(512 * 1024, 8, 64, 20, SharedAcross: 4);
        Assert.Equal(4, l2.SharedAcross);
    }

    // ── CacheHierarchySpec ────────────────────────────────────────────────────

    [Fact]
    public void HierarchySpec_SplitIPaths_IndependentLatencies() {
        FlatMemory backing = MakeBacking();
        CacheHierarchySpec spec = CacheHierarchySpec.SplitId(
            new CachePathSpec([new CacheLevelSpec(256, 4, 16, 6),]),
            new CachePathSpec([new CacheLevelSpec(256, 4, 16),])
        );

        MemoryLayers iLayers = spec.BuildILayers(backing);
        MemoryLayers dLayers = spec.BuildDLayers(backing);

        iLayers.Accessor.Read(0, 1);
        dLayers.Accessor.Read(0, 1);

        Assert.Equal(6, iLayers.ConsumeAllStalls());
        Assert.Equal(10, dLayers.ConsumeAllStalls());
    }

    [Fact]
    public void HierarchySpec_SharedLevels_BothPathsGetSharedLevel() {
        FlatMemory backing = MakeBacking();
        CacheHierarchySpec spec = CacheHierarchySpec.SplitId(
            new CachePathSpec([new CacheLevelSpec(64, 2, 16, 4),]),
            new CachePathSpec([new CacheLevelSpec(64, 2, 16, 4),]),
            [new CacheLevelSpec(1024, 8, 16, 20),]
        );

        MemoryLayers iLayers = spec.BuildILayers(backing);
        MemoryLayers dLayers = spec.BuildDLayers(backing);

        Assert.NotNull(iLayers.L2Cache);
        Assert.NotNull(dLayers.L2Cache);

        iLayers.Accessor.Read(0, 1);
        dLayers.Accessor.Read(0, 1);

        // Phase 1: separate instances per path; sharing is a Phase 3 concern.
        Assert.Equal(1L, iLayers.L2Cache!.Misses);
        Assert.Equal(1L, dLayers.L2Cache!.Misses);
    }

    [Fact]
    public void HierarchySpec_ThreeLevelShared_AllSurfaced() {
        FlatMemory backing = MakeBacking();
        CacheHierarchySpec spec = CacheHierarchySpec.WithPath(
            CacheHierarchySpec.D,
            new CachePathSpec([new CacheLevelSpec(64, 2, 16, 4),]),
            [
                new CacheLevelSpec(512, 4, 16),
                new CacheLevelSpec(4096, 8, 16, 30),
            ]
        );

        MemoryLayers layers = spec.BuildDLayers(backing);

        Assert.NotNull(layers.Cache);   // L1 private
        Assert.NotNull(layers.L2Cache); // shared L2
        Assert.NotNull(layers.L3Cache); // shared L3

        layers.Accessor.Read(0, 1);
        // L1 miss (4) + L2 miss (10) + L3 miss (30) = 44
        Assert.Equal(44, layers.ConsumeAllStalls());
    }

    [Fact]
    public void HierarchySpec_Unified_SameSpecBothPaths() {
        // Unified factory: I and D paths share the same CachePathSpec.
        // Each Build call still produces a separate SetAssociativeCache instance.
        FlatMemory backing = MakeBacking();
        var shared = new CachePathSpec([new CacheLevelSpec(256, 4, 16, 5),]);
        CacheHierarchySpec spec = CacheHierarchySpec.Unified(shared);

        MemoryLayers iLayers = spec.BuildILayers(backing);
        MemoryLayers dLayers = spec.BuildDLayers(backing);

        iLayers.Accessor.Read(0, 1);
        dLayers.Accessor.Read(0, 1);

        Assert.Equal(5, iLayers.ConsumeAllStalls());
        Assert.Equal(5, dLayers.ConsumeAllStalls());
        Assert.NotSame(iLayers.Cache, dLayers.Cache); // separate instances
    }

    [Fact]
    public void HierarchySpec_CustomPath_BuiltByName() {
        // A third path (e.g. "vector") can be added alongside I and D.
        FlatMemory backing = MakeBacking();
        var vectorPath = new CachePathSpec([new CacheLevelSpec(128, 4, 16, 3),]);
        var spec = new CacheHierarchySpec(
            new Dictionary<string, CachePathSpec> {
                [CacheHierarchySpec.I] = new([new CacheLevelSpec(256, 4, 16, 8),]),
                [CacheHierarchySpec.D] = new([new CacheLevelSpec(256, 4, 16, 8),]),
                ["vector"] = vectorPath,
            }
        );

        MemoryLayers vLayers = spec.Build(backing, "vector");
        vLayers.Accessor.Read(0, 1);
        Assert.Equal(3, vLayers.ConsumeAllStalls());
    }

    [Fact]
    public void HierarchySpec_MissingPath_ReturnsNoCacheLayers() {
        // Requesting a path name not in Paths falls back to CachePathSpec.Empty (no caches).
        FlatMemory backing = MakeBacking();
        CacheHierarchySpec spec = CacheHierarchySpec.SplitId(
            new CachePathSpec([new CacheLevelSpec(256, 4, 16, 8),]),
            new CachePathSpec([new CacheLevelSpec(256, 4, 16, 8),])
        );

        MemoryLayers layers = spec.Build(backing, "vector"); // not registered
        Assert.Null(layers.Cache);
        Assert.Equal(42UL, layers.Accessor.Read(42, 1));
    }

    [Fact]
    public void HierarchySpec_Empty_NoCacheLayers() {
        FlatMemory backing = MakeBacking();
        MemoryLayers layers = CacheHierarchySpec.Empty.BuildDLayers(backing);

        Assert.Null(layers.Cache);
        Assert.Null(layers.L2Cache);
        Assert.Null(layers.L3Cache);
        Assert.Equal(99UL, layers.Accessor.Read(99, 1));
    }
}