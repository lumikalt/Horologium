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
///     The end-to-end proof <c>SyncLibrarySymbolsTests</c> and <c>LoopHeaderTrackerTests</c> could
///     not provide on their own (see their doc comments, and the LoopPoint TODO.md entry for spin-
///     loop filtering): that real synchronization-library busy-waiting, under genuine multi-hart
///     contention, actually gets excluded from loop-header marking — not just that the exclusion
///     mechanism compiles and passes synthetic-fixture unit tests.
///     <para>
///         <c>hello64_musl.elf</c> (single-threaded) could never exercise this: an uncontended lock's
///         CAS/LR-SC retry loop is never taken backward, so its excluded ranges always end up unused.
///         <c>pthread_probe.elf</c> under <see cref="MultiHartKernel" /> (3 harts: main + 2 created
///         threads, real <c>clone()</c>/<c>futex()</c>/thread-list-lock contention from two concurrent
///         <c>pthread_create</c>/<c>pthread_join</c> pairs) does: without exclusion, real markers land
///         inside <c>SyncLibrarySymbols.ExcludedRanges</c>' ranges on every hart (musl's
///         <c>__tl_lock</c>/<c>__timedwait</c>/etc. retry loops actually get taken backward this time).
///         With the same ranges passed to <see cref="LoopHeaderTracker" />, those specific markers
///         disappear — and only those; every other marker survives unchanged.
///     </para>
/// </summary>
public class SpinLoopFilteringEndToEndTests {
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");

    private static (
        Rv64ElfWorkload Workload,
        MultiHartKernel Kernel,
        IReadOnlyList<(ulong Start, ulong End)> ExcludedRanges
    ) Boot(out Rv64Mechanism[] mechanisms) {
        const int memorySizeBytes = 16 * 1024 * 1024;
        var workload = new Rv64ElfWorkload(SpinLoopFilteringEndToEndTests.PthreadProbeElf, memorySizeBytes);
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

        mechanisms = [
            new Rv64Mechanism(syscallHandler: handler, hartId: 0),
            new Rv64Mechanism(syscallHandler: handler, hartId: 1),
            new Rv64Mechanism(syscallHandler: handler, hartId: 2),
        ];
        var kernel = new MultiHartKernel(mem, 1, mechanisms);
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        IReadOnlyList<(ulong Start, ulong End)> ranges = SyncLibrarySymbols.ExcludedRanges(workload);
        return (workload, kernel, ranges);
    }

    [Fact]
    public void WithoutExclusion_RealContendedSyncLibraryLoops_ProduceMarkersInsideExcludedRanges() {
        (Rv64ElfWorkload workload, MultiHartKernel kernel, IReadOnlyList<(ulong Start, ulong End)> ranges) =
            Boot(out Rv64Mechanism[] mechanisms);

        var trackers = new LoopHeaderTracker[mechanisms.Length];
        for (var i = 0; i < mechanisms.Length; i++) {
            trackers[i] = new LoopHeaderTracker(
                mechanisms[i].Decoder, workload.BaseAddress, workload.BaseAddress + (ulong)workload.CodeSize + 0x10000
            );
            kernel.SetObserver(i, trackers[i]);
        }

        kernel.Run(2_000_000);
        Assert.False(kernel.IsDormant(1)); // both pthread_create calls actually spawned
        Assert.False(kernel.IsDormant(2));

        bool anyMarkerInExcludedRange = trackers.Any(
            t => t.Markers.Any(m => ranges.Any(r => m.Pc >= r.Start && m.Pc < r.End))
        );
        Assert.True(anyMarkerInExcludedRange); // real contention actually exercises a spin-loop back-edge
    }

    [Fact]
    public void WithExclusion_ThoseSameMarkersDisappear_EveryOtherMarkerSurvivesUnchanged() {
        (Rv64ElfWorkload unfilteredWorkload, MultiHartKernel unfilteredKernel, IReadOnlyList<(ulong Start, ulong End)> ranges) =
            Boot(out Rv64Mechanism[] unfilteredMechanisms);

        var unfilteredTrackers = new LoopHeaderTracker[unfilteredMechanisms.Length];
        for (var i = 0; i < unfilteredMechanisms.Length; i++) {
            unfilteredTrackers[i] = new LoopHeaderTracker(
                unfilteredMechanisms[i].Decoder, unfilteredWorkload.BaseAddress,
                unfilteredWorkload.BaseAddress + (ulong)unfilteredWorkload.CodeSize + 0x10000
            );
            unfilteredKernel.SetObserver(i, unfilteredTrackers[i]);
        }

        unfilteredKernel.Run(2_000_000);

        (Rv64ElfWorkload workload, MultiHartKernel kernel, IReadOnlyList<(ulong Start, ulong End)> _) =
            Boot(out Rv64Mechanism[] mechanisms);

        var filteredTrackers = new LoopHeaderTracker[mechanisms.Length];
        for (var i = 0; i < mechanisms.Length; i++) {
            filteredTrackers[i] = new LoopHeaderTracker(
                mechanisms[i].Decoder, workload.BaseAddress, workload.BaseAddress + (ulong)workload.CodeSize + 0x10000,
                ranges
            );
            kernel.SetObserver(i, filteredTrackers[i]);
        }

        kernel.Run(2_000_000);

        for (var i = 0; i < mechanisms.Length; i++) {
            int excludedCount = unfilteredTrackers[i].Markers.Count(
                m => ranges.Any(r => m.Pc >= r.Start && m.Pc < r.End)
            );
            Assert.True(excludedCount > 0); // this hart genuinely hit an excluded header at least once

            Assert.DoesNotContain(filteredTrackers[i].Markers, m => ranges.Any(r => m.Pc >= r.Start && m.Pc < r.End));

            // Every marker outside an excluded range survives filtering untouched: the unfiltered
            // run's non-excluded markers count exactly matches the filtered run's marker count.
            int nonExcludedUnfilteredCount = unfilteredTrackers[i].Markers.Count(
                m => !ranges.Any(r => m.Pc >= r.Start && m.Pc < r.End)
            );
            Assert.Equal(nonExcludedUnfilteredCount, filteredTrackers[i].Markers.Count);
        }
    }
}
