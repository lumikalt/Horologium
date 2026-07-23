#region

using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Pipeline;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     FDIP's own decode-ahead lookahead reads must bypass the I-cache it's warming (see
///     <c>FdipPrefetcher</c>'s doc comment) — reading back through it counts as extra demand accesses
///     on the very counters used to measure the cache. `TrainConfig`/`Experiment`'s raw-`IMemory`
///     construction path always got this right; `.csx`/`MachineSpec`'s split-I/D branch didn't (it fed
///     FDIP <c>iLayers.Accessor</c>, post-cache) until <c>PipelineSpec.Build</c> grew an explicit
///     <c>fdipBackingMemory</c> parameter.
///     <para>
///         Separately, FDIP never engaged at all via <c>CacheHierarchySpec.Unified</c> — the unified
///         branch used to route through the *flat* <c>Build</c> overload (because <c>CprSpec</c>/
///         <c>DaeSpec</c> only implemented that one), which always rebuilds its own internal
///         <c>MemoryLayers</c> with <c>MemoryConfig.None</c>, leaving <c>iLayers.Cache</c> null
///         regardless of <c>fdipBackingMemory</c>. Fixed by giving every <see cref="PipelineSpec" />
///         subtype (including <c>CprSpec</c>/<c>DaeSpec</c>/<c>SmtSpec</c>) a real pre-built-
///         <c>MemoryLayers</c> <c>Build</c> override, so the unified branch can route through that
///         overload — like the split branch always did — with the genuine externally-built cache.
///     </para>
/// </summary>
public class MachineSpecFdipTests {
    // Loop 10 times, taken branch on each iteration — same program FdipPrefetcherTests.cs uses,
    // enough real fetch traffic across multiple I-cache lines for FDIP to actually look ahead.
    private static readonly uint[] LoopProgram = [
        0x00000093, // addi x1, x0, 0
        0x00A00113, // addi x2, x0, 10
        0x00108093, // addi x1, x1, 1   ← loop body (addr 8)
        0xFE20CEE3, // blt  x1, x2, -4  ← back-edge
        0x00100073, // ebreak
    ];

    private static FlatMemory LoadedMem() {
        var mem = new FlatMemory(4096);
        var bytes = new byte[MachineSpecFdipTests.LoopProgram.Length * 4];
        for (var i = 0; i < MachineSpecFdipTests.LoopProgram.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), MachineSpecFdipTests.LoopProgram[i]);
        mem.Load(0, bytes);
        return mem;
    }

    private static Func<IMechanism> Rv32() => () => new Rv32Mechanism();

    // 256 bytes, 4-way, 64-byte blocks — matches FdipPrefetcherTests.ICache()'s geometry.
    private static MemoryConfig IMemConfig() => new(256, 4, 64);
    private static CacheLevelSpec Il1Spec() => new(256, 4, 64);

    [Fact]
    public void Fdip_ViaSplitIdMachineSpec_MatchesDirectConstructionICacheAccessCount() {
        var direct = new FiveStageTrain(
            new Rv32Mechanism(), LoadedMem(), iMemConfig: IMemConfig(),
            fdipFtqCapacity: 32
        );
        direct.Run();

        MachineHandle handle = new MachineSpec(
            new FiveStageSpec(FdipFtqCapacity: 32), Rv32(),
            CacheHierarchySpec.SplitId(
                new CachePathSpec([Il1Spec(),]),
                new CachePathSpec([Il1Spec(),])
            )
        ).Build(LoadedMem());
        handle.Run();

        Assert.True(handle.ILayers!.Cache!.Prefetches > 0, "MachineSpec build should issue FDIP prefetches");
        // direct.ICache is I-fetch-only (dMemConfig was never given); handle.ILayers.Cache is the
        // split I-path cache (data goes to the separate D-path cache) — genuinely apples-to-apples,
        // unlike a Unified cache which would also carry data traffic.
        Assert.Equal(
            direct.ICache!.Hits + direct.ICache!.Misses,
            handle.ILayers!.Cache!.Hits + handle.ILayers!.Cache!.Misses
        );
    }

    [Fact]
    public void Fdip_OoO_ViaSplitIdMachineSpec_MatchesDirectConstructionICacheAccessCount() {
        var direct = new OooeTrain(
            new Rv32Mechanism(), LoadedMem(), iMemConfig: IMemConfig(),
            fdipFtqCapacity: 32
        );
        direct.Run();

        MachineHandle handle = new MachineSpec(
            new OutOfOrderSpec(FdipFtqCapacity: 32), Rv32(),
            CacheHierarchySpec.SplitId(
                new CachePathSpec([Il1Spec(),]),
                new CachePathSpec([Il1Spec(),])
            )
        ).Build(LoadedMem());
        handle.Run();

        Assert.True(handle.ILayers!.Cache!.Prefetches > 0, "MachineSpec build should issue FDIP prefetches");
        Assert.Equal(
            direct.ICache!.Hits + direct.ICache!.Misses,
            handle.ILayers!.Cache!.Hits + handle.ILayers!.Cache!.Misses
        );
    }

    // A Unified cache carries both I and D traffic through one instance — there's no direct-
    // construction equivalent to cross-check Hits+Misses against (FiveStageTrain/OooeTrain's public
    // ctor always builds separate I/D cache instances even given identical configs), so these just
    // confirm FDIP actually engages, which is the thing that was structurally broken.
    [Fact]
    public void Fdip_ViaUnifiedMachineSpec_IssuesPrefetches() {
        MachineHandle handle = new MachineSpec(
            new FiveStageSpec(FdipFtqCapacity: 32), Rv32(),
            CacheHierarchySpec.Unified(new CachePathSpec([Il1Spec(),]))
        ).Build(LoadedMem());
        handle.Run();

        Assert.True(handle.Layers!.Cache!.Prefetches > 0, "MachineSpec build should issue FDIP prefetches");
    }

    [Fact]
    public void Fdip_OoO_ViaUnifiedMachineSpec_IssuesPrefetches() {
        MachineHandle handle = new MachineSpec(
            new OutOfOrderSpec(FdipFtqCapacity: 32), Rv32(),
            CacheHierarchySpec.Unified(new CachePathSpec([Il1Spec(),]))
        ).Build(LoadedMem());
        handle.Run();

        Assert.True(handle.Layers!.Cache!.Prefetches > 0, "MachineSpec build should issue FDIP prefetches");
    }
}