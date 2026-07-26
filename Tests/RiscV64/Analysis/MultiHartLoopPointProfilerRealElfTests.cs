#region

using Mechanism;
using Pipeline;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.Analysis;

/// <summary>
///     The one composition <c>MultiHartLoopPointProfilerTests</c> (synthetic fixtures) and
///     <c>SpinLoopFilteringEndToEndTests</c> (bare <see cref="LoopHeaderTracker" />s, not the
///     coordinator) don't cover: <see cref="MultiHartLoopPointProfiler" /> itself, wired into
///     <see cref="MultiHartKernel" /> against a real multi-threaded ELF, with its output actually fed
///     into <c>SimPointAnalysis.Analyze</c> — the entire point of item 9 ("reuse SimPointAnalysis's
///     existing k-means/BIC clustering unchanged"). Namespaced <c>ulong</c> keys are synthetic (no
///     real ELF symbol lives at those addresses), so this also confirms <c>Analyze</c>'s hash-based
///     random projection tolerates them without needing to special-case anything.
///     <para>
///         <c>pthread_probe.elf</c> naturally produces variable active-hart-count regions (main runs
///         alone until both <c>pthread_create</c> calls spawn hart 1/2, then narrows again as each
///         child exits) — exactly the non-homogeneous-parallelism regions the paper's Fig. 3 depicts.
///         Each hart's own per-region contribution is normalized to the same fixed total
///         (<c>NormalizationScale</c>) independent of how many other harts were active, so a region's
///         active-hart-count changes its combined magnitude (<c>Project()</c> divides by that
///         region's own total, not a cross-region constant) without disturbing any single thread's
///         own within-region block proportions.
///     </para>
/// </summary>
public class MultiHartLoopPointProfilerRealElfTests {
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");

    [Fact]
    public void RealMultiThreadedElf_RegionBbvsFeedSimPointAnalysis_AcrossVaryingActiveHartCounts() {
        const int memorySizeBytes = 16 * 1024 * 1024;
        var workload = new Rv64ElfWorkload(PthreadProbeElf, memorySizeBytes);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, 8, ["pthread_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        ulong mmapBase = workload.BaseAddress + 8UL * 1024 * 1024;
        ulong mmapLimit = workload.BaseAddress + 14UL * 1024 * 1024;
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, new StringWriter(), 8, mmapBase, mmapLimit);

        Rv64Mechanism[] mechanisms = [
            new Rv64Mechanism(syscallHandler: handler, hartId: 0),
            new Rv64Mechanism(syscallHandler: handler, hartId: 1),
            new Rv64Mechanism(syscallHandler: handler, hartId: 2),
        ];
        var kernel = new MultiHartKernel(mem, 1, mechanisms);
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        IReadOnlyList<(ulong Start, ulong End)> excludedRanges = SyncLibrarySymbols.ExcludedRanges(workload);
        ulong rangeEnd = workload.BaseAddress + (ulong)workload.CodeSize + 0x10000;
        // 300, not anything close to the paper's N x 100M: pthread_probe.elf is tiny (prints two
        // short strings and exits — a few thousand non-excluded instructions total across all
        // harts), so a realistic slice size would yield exactly one region. 300 is chosen purely to
        // exercise multiple regions on this deliberately small fixture, not a realistic target.
        var profiler = new MultiHartLoopPointProfiler(
            [mechanisms[0].Decoder, mechanisms[1].Decoder, mechanisms[2].Decoder,],
            workload.BaseAddress, rangeEnd, 300, excludedRanges
        );
        for (var i = 0; i < mechanisms.Length; i++) kernel.SetObserver(i, profiler.HartObserver(i));

        kernel.Run(2_000_000);
        profiler.Complete();

        Assert.False(kernel.IsDormant(1));
        Assert.False(kernel.IsDormant(2));
        Assert.True(profiler.RegionBbvs.Count >= 2); // enough regions for both clustering and variety below

        // Active-hart-count per region: how many distinct namespace prefixes (top 16 bits) contributed
        // any weight at all. pthread_probe.elf must actually pass through more than one such count
        // (single-threaded startup/teardown vs. 3-hart steady state) for this to be a real test of
        // variable parallelism rather than an accidental single case.
        var activeHartCounts = profiler.RegionBbvs
            .Select(region => region.Keys.Select(k => k >> 48).Distinct().Count())
            .ToList();
        Assert.True(
            activeHartCounts.Distinct().Count() >= 2,
            $"expected at least 2 distinct active-hart-counts across regions, got: [{string.Join(",", activeHartCounts)}]"
        );

        // The actual deliverable: SimPointAnalysis.Analyze must accept this directly, unchanged.
        SimPointResult result = SimPointAnalysis.Analyze(profiler.RegionBbvs);
        Assert.Equal(profiler.RegionBbvs.Count, result.IntervalCount);
        Assert.True(result.K >= 1);
        Assert.NotEmpty(result.Points);
        Assert.InRange(result.SingleSimulationPoint, 0, profiler.RegionBbvs.Count - 1);
    }
}
