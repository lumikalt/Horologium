#region

using Pipeline;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.Analysis;

/// <summary>
///     <see cref="SyncLibrarySymbols" />' name-prefix classification against a real compiled
///     binary's own symbol table (<c>TestBinaries/pthread_probe.c</c>) — not a synthetic fixture,
///     since the whole point is matching real musl-internal symbol names.
/// </summary>
public class SyncLibrarySymbolsTests {
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");

    [Fact]
    public void ExcludedRanges_CoversKnownMuslSyncFunctions_ButNotMain() {
        var workload = new Rv64ElfWorkload(PthreadProbeElf, 16 * 1024 * 1024);
        IReadOnlyList<(ulong Start, ulong End)> ranges = SyncLibrarySymbols.ExcludedRanges(workload);

        Assert.NotEmpty(ranges);

        // __tl_lock (the thread-list lock's LR/SC retry loop — genuine busy-waiting) and
        // __pthread_create must both fall inside some excluded range.
        ulong tlLock = workload.FindSymbol("__tl_lock");
        ulong pthreadCreate = workload.FindSymbol("__pthread_create");
        Assert.Contains(ranges, r => tlLock >= r.Start && tlLock < r.End);
        Assert.Contains(ranges, r => pthreadCreate >= r.Start && pthreadCreate < r.End);

        // main (user code, not a synchronization primitive) must not be covered by any range.
        ulong main = workload.FindSymbol("main");
        Assert.DoesNotContain(ranges, r => main >= r.Start && main < r.End);
    }

    [Fact]
    public void ExcludedRanges_RespectsACustomPrefixList() {
        var workload = new Rv64ElfWorkload(PthreadProbeElf, 16 * 1024 * 1024);
        ulong tlLock = workload.FindSymbol("__tl_lock");

        IReadOnlyList<(ulong Start, ulong End)> onlyMain = SyncLibrarySymbols.ExcludedRanges(workload, ["main",]);

        Assert.DoesNotContain(onlyMain, r => tlLock >= r.Start && tlLock < r.End);
        ulong main = workload.FindSymbol("main");
        Assert.Contains(onlyMain, r => main >= r.Start && main < r.End);
    }
}