#region

using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

public class MachineSpecTests {
    // addi x1, x0, 42  →  sw x1, 256(x0)  →  ebreak   (mem[256] = 42)
    private const uint Addi = 0x02a00093;
    private const uint Sw256 = 0x10102023;
    private const uint Ebreak = 0x00100073;

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static FlatMemory LoadedMem() {
        var mem = new FlatMemory(0x1000);
        mem.Load(0x00, Encode(MachineSpecTests.Addi, MachineSpecTests.Sw256, MachineSpecTests.Ebreak));
        return mem;
    }

    private static Func<IMechanism> Rv32() => () => new Rv32Mechanism();

    // ── Basic build and run ───────────────────────────────────────────────────

    [Fact]
    public void SingleCycle_NoCaches_ProducesCorrectResult() {
        FlatMemory mem = LoadedMem();
        new MachineSpec(new SingleCycleSpec(), Rv32()).Build(mem).Run(1_000);
        Assert.Equal(42uL, mem.Read(256, 4));
    }

    [Fact]
    public void FiveStage_NoCaches_ProducesCorrectResult() {
        FlatMemory mem = LoadedMem();
        new MachineSpec(new FiveStageSpec(), Rv32()).Build(mem).Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
    }

    [Fact]
    public void OutOfOrder_NoCaches_ProducesCorrectResult() {
        FlatMemory mem = LoadedMem();
        new MachineSpec(new OutOfOrderSpec(), Rv32()).Build(mem).Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
    }

    // ── Layers exposure ───────────────────────────────────────────────────────

    [Fact]
    public void NoCaches_LayersIsNull() {
        MachineHandle handle = new MachineSpec(new SingleCycleSpec(), Rv32()).Build(new FlatMemory(0x100));
        Assert.Null(handle.Layers);
    }

    [Fact]
    public void WithCache_LayersIsNotNull() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(new FlatMemory(0x1000));
        Assert.NotNull(handle.Layers);
        Assert.NotNull(handle.Layers.Cache);
    }

    // ── DoCache miss counting ───────────────────────────────────────────────────

    [Fact]
    public void WithL1_CacheSeesMissesOnColdRun() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(mem);
        handle.Run(1_000);
        Assert.True(handle.Layers!.Cache!.Misses > 0);
    }

    [Fact]
    public void WithMultiLevel_L2CacheIsExposed() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64, 5);
        var l2Spec = new CacheLevelSpec(32768, 8, 64, 15);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]), [l2Spec,])
        ).Build(mem);
        handle.Run(1_000);
        Assert.NotNull(handle.Layers!.L2Cache);
        Assert.True(handle.Layers.Cache!.Misses > 0);
    }

    // ── Pipeline variants with cache ──────────────────────────────────────────

    [Fact]
    public void FiveStage_WithL1_BuildsAndRuns() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new FiveStageSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(mem);
        handle.Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.True(handle.Layers!.Cache!.Misses > 0);
    }

    [Fact]
    public void OutOfOrder_WithL1_BuildsAndRuns() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new OutOfOrderSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(mem);
        handle.Run(50_000);
        Assert.True(handle.Layers!.Cache!.Misses > 0);
    }

    // ── MmioRegion wiring ─────────────────────────────────────────────────────

    [Fact]
    public void MmioRegion_PropagatesUncacheableBoundsToLayerStack() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(new FlatMemory(0x1000), mmioRegion: (Base: 0x800, Size: 0x100));
        Assert.Equal(0x800uL, handle.Layers!.UncacheableBase);
        Assert.Equal(0x100uL, handle.Layers.UncacheableSize);
    }

    // ── Split I/D ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unified_IAndDLayersAreSameInstance() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(), CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]))
        ).Build(new FlatMemory(0x1000));
        Assert.NotNull(handle.ILayers);
        Assert.NotNull(handle.DLayers);
        Assert.Same(handle.ILayers, handle.DLayers);
    }

    [Fact]
    public void SplitId_IAndDLayersAreSeparateInstances() {
        var iSpec = new CacheLevelSpec(4096, 4, 64);
        var dSpec = new CacheLevelSpec(8192, 4, 64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.SplitId(new CachePathSpec([iSpec,]), new CachePathSpec([dSpec,]))
        ).Build(new FlatMemory(0x1000));
        Assert.NotNull(handle.ILayers);
        Assert.NotNull(handle.DLayers);
        Assert.NotSame(handle.ILayers, handle.DLayers);
    }

    [Fact]
    public void SplitId_BothCachesSeeMissesOnColdRun() {
        var iSpec = new CacheLevelSpec(4096, 4, 64);
        var dSpec = new CacheLevelSpec(4096, 4, 64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.SplitId(new CachePathSpec([iSpec,]), new CachePathSpec([dSpec,]))
        ).Build(mem);
        handle.Run(1_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.True(handle.ILayers!.Cache!.Misses > 0);
        Assert.True(handle.DLayers!.Cache!.Misses > 0);
    }

    [Fact]
    public void SplitId_WithFiveStage_ProducesCorrectResult() {
        var iSpec = new CacheLevelSpec(4096, 4, 64);
        var dSpec = new CacheLevelSpec(4096, 4, 64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new FiveStageSpec(), Rv32(),
            CacheHierarchySpec.SplitId(new CachePathSpec([iSpec,]), new CachePathSpec([dSpec,]))
        ).Build(mem);
        handle.Run(10_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Assert.NotSame(handle.ILayers, handle.DLayers);
    }

    // ── TLB ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithTlb_TlbIsExposed() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        var tlbSpec = new TlbSpec(64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,], tlbSpec))
        ).Build(new FlatMemory(0x1000));
        Assert.NotNull(handle.Layers!.Tlb);
    }

    [Fact]
    public void WithTlb_SeesAccessesOnRun() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        var tlbSpec = new TlbSpec(64);
        FlatMemory mem = LoadedMem();
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,], tlbSpec))
        ).Build(mem);
        handle.Run(1_000);
        Assert.Equal(42uL, mem.Read(256, 4));
        Tlb tlb = handle.Layers!.Tlb!;
        Assert.True(tlb.Hits + tlb.Misses > 0);
    }

    [Fact]
    public void SplitId_WithTlb_ITlbAndDTlbAreDistinctInstances() {
        var l1Spec = new CacheLevelSpec(4096, 4, 64);
        var tlbSpec = new TlbSpec(64);
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(), Rv32(),
            CacheHierarchySpec.SplitId(
                new CachePathSpec([l1Spec,], tlbSpec),
                new CachePathSpec([l1Spec,], tlbSpec)
            )
        ).Build(new FlatMemory(0x1000));
        Assert.NotNull(handle.ILayers!.Tlb);
        Assert.NotNull(handle.DLayers!.Tlb);
        Assert.NotSame(handle.ILayers.Tlb, handle.DLayers.Tlb);
    }
}