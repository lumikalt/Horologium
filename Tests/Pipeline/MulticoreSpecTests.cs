#region

using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

public class MulticoreSpecTests {
    // Encoded programs — results written to known memory addresses for verification.
    //
    // H0: addi x1,x0,42 / sw x1,256(x0) / ebreak   → mem[256] = 42
    // H1: addi x2,x0,99 / sw x2,260(x0) / ebreak   → mem[260] = 99
    //
    // Both share the same FlatMemory; programs are placed at 0x00 and 0x40 respectively.
    private const uint H0Addi = 0x02a00093; // addi x1, x0, 42
    private const uint H0Sw = 0x10102023;   // sw   x1, 256(x0)
    private const uint H1Addi = 0x06300113; // addi x2, x0, 99
    private const uint H1Sw = 0x10202223;   // sw   x2, 260(x0)
    private const uint Ebreak = 0x00100073;

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static FlatMemory SharedMem() {
        var mem = new FlatMemory(0x1000);
        mem.Load(0x00, Encode(MulticoreSpecTests.H0Addi, MulticoreSpecTests.H0Sw, MulticoreSpecTests.Ebreak));
        mem.Load(0x40, Encode(MulticoreSpecTests.H1Addi, MulticoreSpecTests.H1Sw, MulticoreSpecTests.Ebreak));
        return mem;
    }

    private static Func<IMechanism> Rv32() => () => new Rv32Mechanism();

    // ── No-cache wiring ───────────────────────────────────────────────────────

    [Fact]
    public void TwoHarts_NoCaches_BothProduceCorrectResult() {
        FlatMemory mem = SharedMem();
        new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40),
            ]
        ).Build(mem).Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.Equal(99uL, mem.Read(260, 4));
    }

    [Fact]
    public void HartCount_MatchesInputLength() {
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new SingleCycleSpec(), Rv32()),
            ]
        ).Build(new FlatMemory(0x100));
        Assert.Equal(3, handle.Trains.Count);
        Assert.Equal(3, handle.CoherentCaches.Count);
    }

    // ── Bus topology ──────────────────────────────────────────────────────────

    [Fact]
    public void SnoopingBus_IsCreatedByDefault() {
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new SingleCycleSpec(), Rv32()),
            ]
        ).Build(new FlatMemory(0x100));
        Assert.IsType<MoesifBus>(handle.Bus);
    }

    [Fact]
    public void DirectoryBus_IsCreatedWhenRequested() {
        MulticoreHandle handle = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),],
            Bus: CoherenceBusKind.Directory
        ).Build(new FlatMemory(0x100));
        Assert.IsType<DirectoryBus>(handle.Bus);
    }

    // ── Per-hart private caches ───────────────────────────────────────────────

    [Fact]
    public void PerHartL1_IsCreatedAndRegisteredOnBus() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec),
            ]
        ).Build(new FlatMemory(0x1000));

        Assert.NotNull(handle.CoherentCaches[0]);
        Assert.NotNull(handle.CoherentCaches[1]);
        Assert.NotSame(handle.CoherentCaches[0], handle.CoherentCaches[1]);
    }

    [Fact]
    public void NoCache_CoherentCachesEntryIsNull() {
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
            ]
        ).Build(new FlatMemory(0x100));
        Assert.Null(handle.CoherentCaches[0]);
    }

    [Fact]
    public void PerHartL1_SnoopingBus_CachesSeeMissesOnFirstAccess() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        FlatMemory mem = SharedMem();
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x00, cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, cacheSpec),
            ]
        ).Build(mem);
        handle.Run(10_000);

        Assert.True(handle.CoherentCaches[0]!.Misses > 0);
        Assert.True(handle.CoherentCaches[1]!.Misses > 0);
    }

    [Fact]
    public void PerHartL1_DirectoryBus_CachesSeeMissesOnFirstAccess() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        FlatMemory mem = SharedMem();
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x00, cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, cacheSpec),
            ],
            Bus: CoherenceBusKind.Directory
        ).Build(mem);
        handle.Run(10_000);

        Assert.True(handle.CoherentCaches[0]!.Misses > 0);
        Assert.True(handle.CoherentCaches[1]!.Misses > 0);
    }

    [Fact]
    public void MultiLevelPrivateCache_OutermostIsCoherentOnBus() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64, 5);
        var l2Spec = new CacheLevelSpec(32768, 8, 64, 15);
        // L1 in D-path (innermost); private L2 in SharedLevels (outermost, between I/D of same hart).
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]), [l2Spec,]);
        FlatMemory mem = SharedMem();
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x00, cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, cacheSpec),
            ]
        ).Build(mem);
        handle.Run(10_000);

        // CoherentCaches[i] is the outermost (L2) MoesifCache; the L1 is a SetAssociativeCache above it.
        Assert.NotNull(handle.CoherentCaches[0]);
        Assert.NotNull(handle.CoherentCaches[1]);
        Assert.True(handle.CoherentCaches[0]!.Misses > 0);
        Assert.True(handle.CoherentCaches[1]!.Misses > 0);
    }

    // ── Shared LLC ────────────────────────────────────────────────────────────

    [Fact]
    public void SharedLlc_IsExposedAndNonNull_WhenConfigured() {
        var llcSpec = new CacheLevelSpec(65536, 8, 64, 30);
        MulticoreHandle handle = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),],
            llcSpec
        ).Build(new FlatMemory(0x10000));
        Assert.NotNull(handle.SharedLlc);
    }

    [Fact]
    public void NoSharedLlc_SharedLlcIsNull() {
        MulticoreHandle handle = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),]
        ).Build(new FlatMemory(0x100));
        Assert.Null(handle.SharedLlc);
    }

    // ── Shared ScatterCache LLC + per-hart SDID ───────────────────────────────

    [Fact]
    public void SharedScatterLlc_IsExposedAndNonNull_WhenConfigured() {
        // Regression guard for CacheLevelSpec.Variant -> MulticoreSpec.Build's shared-LLC branch: a
        // dropped/mistyped Variant check would silently fall back to a plain SetAssociativeCache.
        var llcSpec = new CacheLevelSpec(
            65536, 8, 64, 30, Variant: CacheVariantKind.ScatterCache, ScatterRekeyInterval: 500
        );
        MulticoreHandle handle = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),],
            llcSpec
        ).Build(new FlatMemory(0x10000));

        Assert.NotNull(handle.SharedScatterLlc);
        Assert.Null(handle.SharedLlc); // a ScatterCache-variant LLC is not a SetAssociativeCache
    }

    [Fact]
    public void SharedLlc_UnsupportedVariant_Throws() {
        // Ceaser at the shared-LLC surface isn't wired (only None/ScatterCache are); this must throw
        // rather than silently fall back to a plain SetAssociativeCache that drops the requested CEASER
        // protection with no error.
        var llcSpec = new CacheLevelSpec(65536, 8, 64, 30, Variant: CacheVariantKind.Ceaser);
        Assert.Throws<NotSupportedException>(() => new MulticoreSpec(
                                                 [
                                                     new HartSpec(new SingleCycleSpec(), Rv32()),
                                                     new HartSpec(new SingleCycleSpec(), Rv32()),
                                                 ],
                                                 llcSpec
                                             ).Build(new FlatMemory(0x10000))
        );
    }

    [Fact]
    public void SharedLlc_UnsupportedCompression_Throws() {
        // BΔI at the shared-LLC surface isn't wired either; same silent-fallback hazard as Variant.
        var llcSpec = new CacheLevelSpec(65536, 8, 64, 30, Compression: CompressionKind.Bdi);
        Assert.Throws<NotSupportedException>(() => new MulticoreSpec(
                                                 [
                                                     new HartSpec(new SingleCycleSpec(), Rv32()),
                                                     new HartSpec(new SingleCycleSpec(), Rv32()),
                                                 ],
                                                 llcSpec
                                             ).Build(new FlatMemory(0x10000))
        );
    }

    [Fact]
    public void HartSdid_DefaultsToHartIndex_AndReachesMoesifCache() {
        // Regression guard for HartSpec.Sdid -> MoesifCache.Sdid wiring: each hart's private cache
        // must carry that hart's own Security-Domain ID so it can forward it to a shared
        // ScatterCache LLC via SetRequestSdid before every backing access.
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec, Sdid: 42),
            ]
        ).Build(new FlatMemory(0x1000));

        Assert.Equal(0, handle.CoherentCaches[0]!.Sdid); // defaults to hart index
        Assert.Equal(1, handle.CoherentCaches[1]!.Sdid);
        Assert.Equal(42, handle.CoherentCaches[2]!.Sdid); // explicit override
    }

    [Fact]
    public void TwoHarts_DistinctSdids_BothLandInSharedScatterLlcAtTheirOwnSdidRow() {
        // End-to-end close of the SDID-plumbing chain: not just that MoesifCache carries the right
        // Sdid (HartSdid_DefaultsToHartIndex_AndReachesMoesifCache) and not just that ScatterCache's
        // Idf varies by SDID (ScatterCacheTests' unit-level check), but that a genuine two-hart RUN
        // actually forwards each hart's own SetRequestSdid call all the way to the shared LLC before
        // every backing access, and that both resulting lines survive concurrently instead of one
        // treading on the other.
        //
        // H0: addi x1,x0,42 / lui x3,1    / sw x1,0(x3) / ebreak  -> mem[0x1000] = 42
        // H1: addi x2,x0,99 / lui x4,2    / sw x2,0(x4) / ebreak  -> mem[0x2000] = 99
        // Distinct, far-apart, block-aligned addresses so the two harts never share a coherence line.
        const uint h0Lui = 0x000011B7;        // lui x3, 1        (x3 = 0x1000)
        const uint h0SwIndirect = 0x0011A023; // sw x1, 0(x3)
        const uint h1Lui = 0x00002237;        // lui x4, 2        (x4 = 0x2000)
        const uint h1SwIndirect = 0x00222023; // sw x2, 0(x4)

        var mem = new FlatMemory(0x4000);
        mem.Load(0x00, Encode(MulticoreSpecTests.H0Addi, h0Lui, h0SwIndirect, MulticoreSpecTests.Ebreak));
        mem.Load(0x40, Encode(MulticoreSpecTests.H1Addi, h1Lui, h1SwIndirect, MulticoreSpecTests.Ebreak));

        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        var llcSpec = new CacheLevelSpec(4096, 4, 64, Variant: CacheVariantKind.ScatterCache);
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), Cache: cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, cacheSpec),
            ],
            llcSpec
        ).Build(mem);
        handle.Run(10_000);

        // Check LLC residency and per-SDID placement now, before any flush: each hart's write-miss
        // fetch (RFO/fill-from-backing) populates the shared LLC as a side effect, under that hart's
        // own SDID. FlushAllToBacking would go through ScatterCache.Load (see its class docs, §3.4
        // correctness note), which invalidates on flush — not what this assertion is after.
        ScatterCache llc = handle.SharedScatterLlc!;
        ScatterCacheLine[] snapshot = llc.GetSnapshot();
        ScatterCacheLine? line0 = snapshot.FirstOrDefault(l => l.Valid && l.Address == 0x1000);
        ScatterCacheLine? line1 = snapshot.FirstOrDefault(l => l.Valid && l.Address == 0x2000);
        Assert.NotNull(line0); // both lines resident simultaneously, not evicting each other
        Assert.NotNull(line1);

        llc.SetRequestSdid(0); // hart 0's default SDID
        Assert.Equal(line0.Row, llc.IdfRow(line0.Way, 0x1000));
        llc.SetRequestSdid(1); // hart 1's default SDID
        Assert.Equal(line1.Row, llc.IdfRow(line1.Way, 0x2000));

        // MoesifCache is write-back only; sequential Run (unlike RunConcurrent's per-tick bus drain)
        // never auto-flushes dirty private-cache lines down to backing, so an explicit flush is needed
        // before inspecting mem directly.
        handle.FlushAllToBacking();
        Assert.Equal(42uL, mem.Read(0x1000, 4));
        Assert.Equal(99uL, mem.Read(0x2000, 4));
    }

    // ── Mixed pipeline specs ──────────────────────────────────────────────────

    [Fact]
    public void MixedPipelineSpecs_BuildAndRun() {
        FlatMemory mem = SharedMem();
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new FiveStageSpec(), Rv32(), 0x40),
            ]
        ).Build(mem);
        handle.Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.Equal(99uL, mem.Read(260, 4));
    }

    // ── ConcurrentMode (DeferredBus / two-phase parallel tick) ───────────────

    [Fact]
    public void ConcurrentMode_TwoHartsWithL1_BothProduceCorrectResult() {
        // Store addresses must be in different 64-byte cache blocks so the two harts
        // never race on the same line within a single parallel phase-1 tick.
        // H0 → mem[256] (block 4), H1 → mem[320] (block 5).
        // sw x2, 320(x0) = 0x14202023
        const uint h1SwBlock5 = 0x14202023; // sw x2, 320(x0)

        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        var mem = new FlatMemory(0x1000);
        mem.Load(
            0x00, Encode(MulticoreSpecTests.H0Addi, MulticoreSpecTests.H0Sw, MulticoreSpecTests.Ebreak)
        );                                                                                        // sw x1, 256(x0)
        mem.Load(0x40, Encode(MulticoreSpecTests.H1Addi, h1SwBlock5, MulticoreSpecTests.Ebreak)); // sw x2, 320(x0)

        new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x00, cacheSpec),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, cacheSpec),
            ],
            ConcurrentMode: true
        ).Build(mem).RunConcurrent(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.Equal(99uL, mem.Read(320, 4));
    }

    [Fact]
    public void ConcurrentMode_DirectoryBus_Throws() {
        var spec = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),],
            Bus: CoherenceBusKind.Directory,
            ConcurrentMode: true
        );
        Assert.Throws<InvalidOperationException>(() => spec.Build(new FlatMemory(0x100)));
    }

    [Fact]
    public void NoConcurrentMode_RunConcurrent_Throws() {
        MulticoreHandle handle = new MulticoreSpec(
            [new HartSpec(new SingleCycleSpec(), Rv32()), new HartSpec(new SingleCycleSpec(), Rv32()),]
        ).Build(new FlatMemory(0x100));
        Assert.Throws<InvalidOperationException>(() => handle.RunConcurrent(100));
    }

    // ── Multi-pool memory ─────────────────────────────────────────────────────

    [Fact]
    public void TwoPools_TwoHartsEach_WriteIndependentBackingMemory() {
        // Same 2 addresses (256/260) used in both pools with different values — if pool-scoped
        // backing lookup were broken (e.g. every hart wired to backingByPool[0] regardless of its
        // own PoolId), pool 1's writes would land in pool 0's memory instead of its own, leaving
        // mem1 untouched (still zero) and mem0 holding pool 1's values instead of pool 0's.
        const uint pool1H0Addi = 0x00700093; // addi x1, x0, 7
        const uint pool1H1Addi = 0x00d00113; // addi x2, x0, 13

        var mem0 = new FlatMemory(0x1000);
        mem0.Load(0x00, Encode(MulticoreSpecTests.H0Addi, MulticoreSpecTests.H0Sw, MulticoreSpecTests.Ebreak));
        mem0.Load(0x40, Encode(MulticoreSpecTests.H1Addi, MulticoreSpecTests.H1Sw, MulticoreSpecTests.Ebreak));

        var mem1 = new FlatMemory(0x1000);
        mem1.Load(0x00, Encode(pool1H0Addi, MulticoreSpecTests.H0Sw, MulticoreSpecTests.Ebreak));
        mem1.Load(0x40, Encode(pool1H1Addi, MulticoreSpecTests.H1Sw, MulticoreSpecTests.Ebreak));

        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32()),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, PoolId: 0),
                new HartSpec(new SingleCycleSpec(), Rv32(), PoolId: 1),
                new HartSpec(new SingleCycleSpec(), Rv32(), 0x40, PoolId: 1),
            ]
        ).Build(new Dictionary<int, IMemory> { [0] = mem0, [1] = mem1, });
        handle.Run(10_000);

        Assert.Equal(42uL, mem0.Read(256, 4));
        Assert.Equal(99uL, mem0.Read(260, 4));
        Assert.Equal(7uL, mem1.Read(256, 4));
        Assert.Equal(13uL, mem1.Read(260, 4));
    }

    [Fact]
    public void TwoPools_HaveDistinctBusAndLlcInstances() {
        var llcSpec = new CacheLevelSpec(4096, 4, 64);
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), PoolId: 0),
                new HartSpec(new SingleCycleSpec(), Rv32(), PoolId: 1),
            ],
            llcSpec
        ).Build(new Dictionary<int, IMemory> { [0] = new FlatMemory(0x100), [1] = new FlatMemory(0x100), });

        Assert.Equal(2, handle.Buses.Count);
        Assert.NotSame(handle.Buses[0], handle.Buses[1]);
        Assert.Equal(2, handle.SharedLlcs.Count);
        Assert.NotNull(handle.SharedLlcs[0]);
        Assert.NotNull(handle.SharedLlcs[1]);
        Assert.NotSame(handle.SharedLlcs[0], handle.SharedLlcs[1]);
    }

    [Fact]
    public void MissingBackingForReferencedPool_Throws() {
        var spec = new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Rv32(), PoolId: 0),
                new HartSpec(new SingleCycleSpec(), Rv32(), PoolId: 1),
            ]
        );
        Assert.Throws<InvalidOperationException>(() => spec.Build(
                                                     new Dictionary<int, IMemory> { [0] = new FlatMemory(0x100), }
                                                 )
        );
    }

    // ── OoO ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OutOfOrderHart_WithPerHartL1_Snooping_BuildsAndRuns() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));
        FlatMemory mem = SharedMem();
        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new OutOfOrderSpec(), Rv32(), 0x00, cacheSpec),
                new HartSpec(new OutOfOrderSpec(), Rv32(), 0x40, cacheSpec),
            ]
        ).Build(mem);
        handle.Run(50_000);
        Assert.True(handle.CoherentCaches[0]!.Misses + handle.CoherentCaches[1]!.Misses > 0);
    }
}