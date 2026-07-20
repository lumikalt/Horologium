#region

using Orrery.Cache;
using Orrery.Spec;
using RiscV32.Memory;

#endregion

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

    // ── Sequential tag/data access mode ─────────────────────────────────────────

    [Fact]
    public void CacheLevelSpec_HitLatency_ParallelIsMaxOfTagAndData() {
        var spec = new CacheLevelSpec(256, 4, 16, 8, TagLatency: 2, DataLatency: 5);
        Assert.Equal(5, spec.HitLatency);
    }

    [Fact]
    public void CacheLevelSpec_HitLatency_SequentialIsSumOfTagAndData() {
        var spec = new CacheLevelSpec(
            256, 4, 16, 8, TagLatency: 2, DataLatency: 5, AccessMode: CacheAccessModeKind.Sequential
        );
        Assert.Equal(7, spec.HitLatency);
    }

    [Fact]
    public void Build_SequentialAccessMode_PropagatesToCache() {
        FlatMemory backing = MakeBacking();
        var path = new CachePathSpec(
            [
                new CacheLevelSpec(
                    256, 4, 16, 8, TagLatency: 2, DataLatency: 5, AccessMode: CacheAccessModeKind.Sequential
                ),
            ]
        );
        var layers = MemoryLayers.Build(backing, path);

        Assert.Equal(CacheAccessModeKind.Sequential, layers.Cache!.AccessMode);
        Assert.Equal(7, layers.Cache.HitLatency);
    }

    // ── Inclusion policy ──────────────────────────────────────────────────────

    [Fact]
    public void Nine_Default_OuterEvictionDoesNotTouchInner() {
        // Default (Nine) policy: an outer-level eviction must not invalidate the inner level's
        // still-resident copy — matches pre-existing (uncoordinated) behavior.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(64, 4, 16, 5);  // 1 set, 4-way
        var l2 = new CacheLevelSpec(32, 2, 16, 20); // 1 set, 2-way, Nine (default)
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Read(0, 1);
        layers.Accessor.Read(16, 1);
        layers.ConsumeAllStalls();
        layers.Accessor.Read(32, 1); // L2 (2-way, full) evicts line 0; L1 has room, keeps it
        layers.ConsumeAllStalls();

        Assert.Equal(0, layers.Cache!.BackInvalidations);
        long l1MissesBefore = layers.Cache.Misses;
        layers.Accessor.Read(0, 1); // still resident in L1 → hit, no new miss
        Assert.Equal(l1MissesBefore, layers.Cache.Misses);
    }

    [Fact]
    public void Inclusive_OuterEviction_BackInvalidatesInner() {
        // L2 (2-way, Inclusive) is smaller than L1 (4-way): filling a 3rd distinct line forces
        // L2 to evict line 0 while L1 still has room to keep it. Inclusion requires L1's copy of
        // line 0 to be dropped too.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(64, 4, 16, 5);                                                  // 1 set, 4-way
        var l2 = new CacheLevelSpec(32, 2, 16, 20, InclusionPolicy: InclusionPolicyKind.Inclusive); // 1 set, 2-way
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Read(0, 1);  // L1 + L2 install line 0
        layers.Accessor.Read(16, 1); // L1 + L2 install line 16; L2 now full (2/2)
        layers.ConsumeAllStalls();

        layers.Accessor.Read(32, 1); // L2 must evict (LRU: line 0); back-invalidates L1's copy
        layers.ConsumeAllStalls();

        Assert.Equal(1, layers.L2Cache!.Evictions);
        Assert.Equal(1, layers.Cache!.BackInvalidations);

        long l1MissesBefore = layers.Cache.Misses;
        layers.Accessor.Read(0, 1); // L1's copy was dropped → must miss again
        Assert.Equal(l1MissesBefore + 1, layers.Cache.Misses);
    }

    [Fact]
    public void Inclusive_DirtyInnerLine_FoldedIntoOuterBeforeBackInvalidate() {
        // A dirty write-back L1 line must not be lost when L2 back-invalidates it: the dirty
        // bytes fold into L2's copy, which L2 then flushes to backing on its own eviction.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(
            64, 4, 16, 5, WritePolicy: WritePolicyKind.WriteBack, WriteMissPolicy: WriteMissPolicyKind.WriteAllocate
        );
        var l2 = new CacheLevelSpec(32, 2, 16, 20, InclusionPolicy: InclusionPolicyKind.Inclusive);
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Write(0, 0xAB, 1); // dirty in L1 only (write-back); L2 still holds original byte
        layers.Accessor.Read(16, 1);       // L1 + L2 install line 16; L2 now full (2/2)
        layers.ConsumeAllStalls();

        layers.Accessor.Read(32, 1); // L2 evicts line 0: folds L1's dirty byte in, then flushes to backing
        layers.ConsumeAllStalls();

        Assert.Equal(0xABUL, backing.Read(0, 1));
    }

    [Fact]
    public void Exclusive_InnerEviction_InsertsVictimIntoOuter() {
        // L2 (1-way, Exclusive) never keeps a line that's resident in L1: fills remove the outer
        // copy, and an L1 eviction hands the line to L2 as a victim instead of discarding it.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(32, 2, 16, 5);                                                  // 1 set, 2-way
        var l2 = new CacheLevelSpec(16, 1, 16, 20, InclusionPolicy: InclusionPolicyKind.Exclusive); // 1 line
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Read(0, 1);  // L1 installs line 0; momentarily fills L2 then removes it
        layers.Accessor.Read(16, 1); // L1 installs line 16 (2/2 full); L2 emptied again
        layers.ConsumeAllStalls();

        layers.Accessor.Read(32, 1); // L1 evicts line 0 (LRU) → handed to L2 as a victim
        layers.ConsumeAllStalls();

        Assert.True(layers.L2Cache!.VictimInserts >= 1);

        long l2HitsBefore = layers.L2Cache.Hits;
        layers.Accessor.Read(0, 1); // gone from L1, but present in L2 as the victim → L2 hit
        Assert.True(layers.L2Cache.Hits > l2HitsBefore);
    }

    [Fact]
    public void Inclusive_OuterEvictionCapturedInVictimBuffer_StillBackInvalidatesInner() {
        // L2 (2-way, Inclusive) also has its own local victim buffer: an L2 eviction is captured
        // rather than discarded, but the inner (L1) back-invalidation must still happen — the
        // cascade doesn't get skipped just because the outer level happens to have a soft landing
        // spot for the line it evicted.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(64, 4, 16, 5); // 1 set, 4-way
        var l2 = new CacheLevelSpec(
            32, 2, 16, 20, InclusionPolicy: InclusionPolicyKind.Inclusive, VictimCacheEntries: 2
        ); // 1 set, 2-way
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Read(0, 1);  // L1 + L2 install line 0
        layers.Accessor.Read(16, 1); // L1 + L2 install line 16; L2 now full (2/2)
        layers.ConsumeAllStalls();

        layers.Accessor.Read(32, 1); // L2 evicts line 0, captures it locally, still back-invalidates L1
        layers.ConsumeAllStalls();

        Assert.Equal(1, layers.L2Cache!.Evictions);
        Assert.Equal(1, layers.L2Cache.VictimCacheCaptures);
        Assert.Equal(1, layers.Cache!.BackInvalidations);

        long l1MissesBefore = layers.Cache.Misses;
        layers.Accessor.Read(0, 1); // gone from L1 → miss; L2's main array doesn't have it either
        Assert.Equal(l1MissesBefore + 1, layers.Cache.Misses);
        Assert.True(layers.L2Cache.VictimCacheHits >= 1); // ...but L2's victim buffer still has it
    }

    [Fact]
    public void Exclusive_LocalVictimBufferOverflow_HandsOffToOuterExclusiveCache() {
        // L1 has its own local victim buffer AND its backing (L2) is Exclusive-configured: a line
        // leaving L1's main array is captured locally first; only once that local buffer overflows
        // does the line get handed off to L2 as an Exclusive victim, exactly like an L1 without a
        // local buffer would do immediately.
        FlatMemory backing = MakeBacking();
        var l1 = new CacheLevelSpec(32, 2, 16, 5, VictimCacheEntries: 1);                           // 1 set, 2-way
        var l2 = new CacheLevelSpec(16, 1, 16, 20, InclusionPolicy: InclusionPolicyKind.Exclusive); // 1 line
        var layers = MemoryLayers.Build(backing, new CachePathSpec([l1,]), [l2,]);

        layers.Accessor.Read(0, 1);  // L1 installs line 0
        layers.Accessor.Read(16, 1); // L1 installs line 16 (2/2 full)
        layers.ConsumeAllStalls();

        layers.Accessor.Read(32, 1); // L1 evicts line 0 → captured in L1's own victim buffer, not L2
        layers.ConsumeAllStalls();
        Assert.Equal(0, layers.L2Cache!.VictimInserts);
        Assert.Equal(1, layers.Cache!.VictimCacheCaptures);

        layers.Accessor.Read(48, 1); // L1 evicts line 16 → L1's buffer overflows, hands line 0 off to L2
        layers.ConsumeAllStalls();
        Assert.True(layers.L2Cache.VictimInserts >= 1);

        long l2HitsBefore = layers.L2Cache.Hits;
        layers.Accessor.Read(0, 1); // gone from L1's main array and its own buffer → falls through to L2
        Assert.True(layers.L2Cache.Hits > l2HitsBefore);
    }

    [Fact]
    public void Exclusive_MismatchedBlockSizes_ThrowsOnAttach() {
        var l1Cache = new SetAssociativeCache(new FlatMemory(256), 32, 2, 16, 5);
        var l2Cache = new SetAssociativeCache(
            new FlatMemory(256), 32, 2, 32, 20, inclusionPolicy: InclusionPolicyKind.Exclusive
        );

        Assert.Throws<ArgumentException>(() => l2Cache.AttachInner(l1Cache));
    }
}