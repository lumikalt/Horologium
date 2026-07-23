#region

using Mechanism;
using Script;

#endregion

namespace Tests.Face;

public class ConfiguratorEngineTests {
    // addi x1, x0, 42  →  sw x1, 256(x0)  →  ebreak   (mem[256] = 42)
    private static readonly byte[] Program = Encode(
        0x02a00093, 0x10102023, 0x00100073
    );

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static ByteArrayWorkload MakeWorkload() =>
        new(ConfiguratorEngineTests.Program, memorySizeBytes: 0x1000);

    [Fact]
    public async Task BuildAsync_ValidScript_ReturnsRunnableHandle() {
        const string script = "new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism())";

        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());

        Assert.True(result.Success);
        Assert.Null(result.Error);
        result.Handle!.Run(1_000);
        Assert.Equal(42uL, result.Handle.Train.ArchState!.IntegerRegisters.Read(1));
    }

    [Fact]
    public async Task BuildAsync_SyntaxError_ReturnsErrorNotException() {
        const string script = "this is not valid C#";

        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());

        Assert.False(result.Success);
        Assert.Null(result.Handle);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task BuildAsync_WrongReturnType_ReturnsErrorNotException() {
        const string script = "42";

        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());

        Assert.False(result.Success);
        Assert.Contains("MachineSpec", result.Error);
    }

    [Fact]
    public async Task SnapshotStats_NoCacheScript_HasNullCacheCountersButNonEmptyDials() {
        const string script = "new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism())";
        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());
        result.Handle!.Run(1_000);

        ConfiguratorStats stats = ConfiguratorEngine.SnapshotStats(result.Handle);

        Assert.Null(stats.L1Hits);
        Assert.Null(stats.TlbHits);
        Assert.NotEmpty(stats.Dials);
    }

    [Fact]
    public async Task LiveStepping_BeginSteppingThenStepCycleThenSnapshot_MatchesUiUsage() {
        // Mirrors ConfiguratorViewModel exactly: it never calls Handle.Run() — it drives the
        // machine via BeginStepping()/StepCycle() so it can refresh live stats between cycles.
        // That's a distinct lifecycle path from Run() (see Train.cs), so it needs its own test
        // rather than relying on the Run()-based coverage above.
        const string script =
            "new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism(), "
          + "CacheHierarchySpec.Unified(new CachePathSpec([new CacheLevelSpec(4096, 4, 64),])))";
        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());
        Assert.True(result.Success);

        result.Handle!.Train.BeginStepping();
        for (var i = 0; i < 3; i++) result.Handle.Train.StepCycle();

        ConfiguratorStats stats = ConfiguratorEngine.SnapshotStats(result.Handle);

        Assert.NotEmpty(stats.Dials);
        Assert.NotNull(stats.L1Hits);
        Assert.True(stats.L1Hits + stats.L1Misses > 0);
    }

    [Fact]
    public async Task SnapshotStats_WithCacheScript_HasNonNullCacheCounters() {
        const string script =
            "new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism(), "
          + "CacheHierarchySpec.Unified(new CachePathSpec([new CacheLevelSpec(4096, 4, 64),])))";
        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(script, MakeWorkload());
        result.Handle!.Run(1_000);

        ConfiguratorStats stats = ConfiguratorEngine.SnapshotStats(result.Handle);

        Assert.NotNull(stats.L1Hits);
        Assert.NotNull(stats.L1Misses);
        Assert.True(stats.L1Hits + stats.L1Misses > 0);
    }
}