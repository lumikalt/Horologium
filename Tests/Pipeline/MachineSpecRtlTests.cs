#region

using Mechanism.RtlFu;
using Orrery.Cache;
using Orrery.Spec;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Execute;
using RiscV32.Memory;
using Tests.Mechanism;
using Tests.Orrery;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     RTL-unit attachment through the spec layer — the surface `.csx`/`.fsx` scripts build
///     against (see <c>scripts/example-rtl.csx</c>). The first tests verify the
///     <see cref="CacheLevelSpec" /> factory plumbing with plain C# stubs (no RTL toolchain
///     needed); the last builds a machine with all four RTL substitutions live and runs it.
/// </summary>
public sealed class MachineSpecRtlTests {
    [Fact]
    public void CacheLevelSpec_FactoriesReachBuiltLayers() {
        var marker = new MarkerPrefetcher();
        (int Sets, int Ways)? policyArgs = null;

        var level = new CacheLevelSpec(
            8192,
            PolicyFactory: (sets, ways) => {
                policyArgs = (sets, ways);
                return new SrripPolicy(sets, ways);
            },
            PrefetcherFactory: () => marker
        );
        MachineHandle handle = new MachineSpec(
            new OutOfOrderSpec(),
            () => new Rv32Mechanism(),
            CacheHierarchySpec.SplitId(
                new CachePathSpec([new CacheLevelSpec(4096),]),
                new CachePathSpec([level,])
            )
        ).Build(new FlatMemory(0x1000));

        Assert.Equal((64, 4), policyArgs);
        Assert.Same(marker, handle.DLayers!.Prefetcher);
    }

    [Fact]
    public void CacheLevelSpec_NullPolicyFactoryResult_FallsBackToKind() {
        // A factory that declines (geometry mismatch) must leave the configured kind active.
        var level = new CacheLevelSpec(
            8192,
            ReplacementPolicy: ReplacementPolicyKind.Srrip,
            PolicyFactory: (_, _) => null
        );
        MachineHandle handle = new MachineSpec(
            new SingleCycleSpec(),
            () => new Rv32Mechanism(),
            CacheHierarchySpec.Unified(new CachePathSpec([level,]))
        ).Build(new FlatMemory(0x1000));

        // SRRIP metadata starts at RRPV 3 (distant) — LRU would report age 0.
        Assert.Equal(3, handle.Layers!.Cache!.GetSnapshot()[0].LruAge);
    }

    [SkippableFact]
    public void AllFourRtlSubstitutions_BuildAndRunThroughSpec() {
        Skip.If(
            RtlDivLibrary.Path is null || RtlBpLibrary.Path is null
                                       || RtlRpLibrary.Path is null || RtlPfLibrary.Path is null,
            "verilator toolchain unavailable — skipping."
        );

        // Loop with a div in it: x1 = 5; loop: x2 += 100/x1; x1 -= 1; bne x1,x0,loop;
        // then store x2 to mem[256]. Result: 100/5+100/4+100/3+100/2+100/1 = 20+25+33+50+100 = 228.
        uint[] words = [
            0x00500093, // addi x1, x0, 5
            0x06400193, // addi x3, x0, 100
            0x0211C333, // loop: div x6, x3, x1
            0x00610133, // add  x2, x2, x6
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne  x1, x0, loop (-12)
            0x10202023, // sw   x2, 256(x0)
            0x00100073, // ebreak
        ];
        var mem = new FlatMemory(0x1000);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(0, bytes);

        // Built eagerly (the factory just hands it over) so the using-scoped FFI unit is
        // not captured by a lambda that could, in principle, outlive it.
        using var divUnit = new RtlFfiFunctionalUnit(RtlDivLibrary.Path);
        var mech = new Rv32Mechanism();
        mech.Executor = new RtlBackedExecutor(mech.Executor, divUnit, RvRtlDiv.Select);
        MachineHandle handle = new MachineSpec(
            new OutOfOrderSpec(
                BranchPredictorFactory: () => new RtlFfiBranchPredictor(RtlBpLibrary.Path)
            ),
            () => mech,
            CacheHierarchySpec.SplitId(
                new CachePathSpec([new CacheLevelSpec(8192),]),
                new CachePathSpec(
                    [
                        new CacheLevelSpec(
                            8192,
                            PolicyFactory: (sets, ways) =>
                                RtlFfiReplacementPolicy.TryCreate(RtlRpLibrary.Path, sets, ways),
                            PrefetcherFactory: () => new RtlFfiPrefetcher(RtlPfLibrary.Path)
                        ),
                    ]
                )
            )
        ).Build(mem);

        Assert.IsType<RtlFfiPrefetcher>(handle.DLayers!.Prefetcher);
        handle.Run(100_000);
        Assert.Equal(228uL, mem.Read(256, 4));
    }

    private sealed class MarkerPrefetcher : IPrefetcher {
        public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) => 0;
    }
}