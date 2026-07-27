#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32.Analysis;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.Analysis;

/// <summary>
///     <see cref="MultiHartLoopPointExperiment" /> against a real, genuinely multi-threaded ELF
///     (<c>pthread_probe.elf</c>, real <c>clone()</c>/<c>futex()</c>) — the one combination
///     <see cref="Tests.RiscV32.Analysis.MultiHartLoopPointExperimentTests" />'s own class doc comment
///     flagged as untested when it was written (before <c>FiveStageTrain</c> gained
///     <c>ExecuteResult.RequestBlock</c> support).
///     <para>
///         Running this against a real pthread ELF surfaced a real, confirmed bug before
///         <see cref="LoopPointCheckpointSet.RepresentativeLiveHarts" /> existed: a checkpoint captured
///         before both <c>pthread_create</c> calls spawn (region 0, the pre-run state) has harts 1/2
///         still dormant — untouched <c>IArchState</c>, PC 0. Measuring them anyway built full
///         <c>FiveStageTrain</c>s that fetched and retired hundreds of phantom instructions from
///         whatever bytes happened to sit at PC 0, polluting the very global-instruction-count
///         <c>MultiHartWarmupMeasureDriver</c> uses to bound the measurement window. Both tests below
///         are written to fail if that liveness filter is reverted — <c>HartResults.Length</c> must
///         equal the live-hart count exactly, not just be "close enough" or "didn't crash."
///     </para>
///     <para>
///         What this does NOT prove: a tick-level comparison against a full cold multi-hart run
///         through the detailed pipeline from t=0. That would need <c>MultiHartPipeline</c> to support
///         dynamic hart activation (clone() spawning a hart onto a live detailed-pipeline run), which
///         doesn't exist and is out of scope here (own TODO.md item) — <c>MultiHartPipeline</c> only
///         ever drives harts that were already live when it was constructed. The achievable
///         independent cross-check instead is functional: <c>PthreadProbeTests</c> already proves the
///         same ELF terminates cleanly on the functional <see cref="MultiHartKernel" />, through a
///         different code path (no checkpoint restore) than the one this bug lived in.
///     </para>
/// </summary>
public class MultiHartLoopPointExperimentRealElfTests {
    private static string PthreadProbeElf => Path.Combine(AppContext.BaseDirectory, "pthread_probe.elf");
    private const int HartCount = 3;
    private const int WordSize = 8;

    private static (Rv64ElfWorkload Workload, ulong MmapBase, ulong MmapLimit) MakeWorkload() {
        var workload = new Rv64ElfWorkload(PthreadProbeElf, 16 * 1024 * 1024);
        ulong mmapBase = workload.BaseAddress + 8UL * 1024 * 1024;
        ulong mmapLimit = workload.BaseAddress + 14UL * 1024 * 1024;
        return (workload, mmapBase, mmapLimit);
    }

    private static (FlatMemory Mem, MultiHartKernel Kernel, IReadOnlyList<IMechanism> Mechanisms) Boot(
        Rv64ElfWorkload workload, ulong mmapBase, ulong mmapLimit
    ) {
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, WordSize, ["pthread_probe.elf",], [],
            InitialStackBuilder.BuildStandardAuxv(
                workload.PhdrAddress, workload.PhEntrySize, workload.PhNum, workload.EntryPoint
            )
        );

        var handler = new LinuxSyscallEmulator(workload.InitialBreak, new StringWriter(), WordSize, mmapBase, mmapLimit);
        Rv64Mechanism[] mechanisms = [
            new Rv64Mechanism(syscallHandler: handler, hartId: 0),
            new Rv64Mechanism(syscallHandler: handler, hartId: 1),
            new Rv64Mechanism(syscallHandler: handler, hartId: 2),
        ];
        var kernel = new MultiHartKernel(mem, 1, mechanisms);
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, workload.EntryPoint);
        kernel.StateOf(0).IntegerRegisters.Write(2, sp);

        return (mem, kernel, mechanisms);
    }

    private static (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) FreshMechanisms(
        Rv64ElfWorkload workload, ulong mmapBase, ulong mmapLimit
    ) {
        var freshHandler = new LinuxSyscallEmulator(workload.InitialBreak, TextWriter.Null, WordSize, mmapBase, mmapLimit);
        Rv64Mechanism[] freshMechanisms = [
            new Rv64Mechanism(syscallHandler: freshHandler, hartId: 0),
            new Rv64Mechanism(syscallHandler: freshHandler, hartId: 1),
            new Rv64Mechanism(syscallHandler: freshHandler, hartId: 2),
        ];
        return (freshMechanisms, freshHandler);
    }

    private static ISteppableTrain DetailedTrainFactory(IMechanism mech, IMemory runMem, ulong restartPc, InstructionCounter counter) =>
        new FiveStageTrain(mech, runMem, restartPc, commitObserver: counter);

    [Fact]
    public void PreSpawnRegion_OnlyMeasuresTheOneLiveHart_NotThePhantomDormantOnes() {
        (Rv64ElfWorkload workload, ulong mmapBase, ulong mmapLimit) = MakeWorkload();
        (FlatMemory mem, MultiHartKernel kernel, IReadOnlyList<IMechanism> mechanisms) = Boot(workload, mmapBase, mmapLimit);

        IReadOnlyList<(ulong Start, ulong End)> excludedRanges = SyncLibrarySymbols.ExcludedRanges(workload);
        ulong rangeEnd = workload.BaseAddress + (ulong)workload.CodeSize + 0x10000;

        // A target far beyond this tiny fixture's whole instruction count collapses the entire run
        // into region 0 alone — the pre-run checkpoint, captured before either pthread_create spawns
        // hart 1/2, so both are still dormant (PC 0, untouched IArchState) at this exact boundary.
        LoopPointCheckpointSet captured = MultiHartLoopPointExperiment.CaptureLoopPointCheckpoints(
            kernel, mechanisms, mem, workload.BaseAddress, rangeEnd, targetGlobalInstructions: 10_000_000,
            excludedRanges, profileMaxTicks: 2_000_000
        );

        Assert.False(kernel.IsDormant(1));
        Assert.False(kernel.IsDormant(2));
        Assert.Single(captured.RepresentativeCheckpoints);
        bool[] liveHarts = captured.RepresentativeLiveHarts.Values.Single();
        Assert.Equal([true, false, false,], liveHarts);

        LoopPointResult result = MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
            captured, () => FreshMechanisms(workload, mmapBase, mmapLimit), DetailedTrainFactory, warmupInstructions: 50
        );

        // The discriminating assertion: exactly one hart gets measured. Before RepresentativeLiveHarts
        // existed, this was 3 — the two dormant harts each got a full FiveStageTrain fetching from PC
        // 0 and retiring hundreds of phantom instructions (confirmed empirically while building this
        // fix, not just theorized).
        Assert.Single(result.RegionMeasurements);
        Assert.Single(result.RegionMeasurements[0].HartResults);
    }

    [Fact]
    public void SteadyStateRegion_MeasuresAllThreeLiveHarts_IncludingOneBlockedOnFutex() {
        (Rv64ElfWorkload workload, ulong mmapBase, ulong mmapLimit) = MakeWorkload();
        (FlatMemory mem, MultiHartKernel kernel, IReadOnlyList<IMechanism> mechanisms) = Boot(workload, mmapBase, mmapLimit);

        IReadOnlyList<(ulong Start, ulong End)> excludedRanges = SyncLibrarySymbols.ExcludedRanges(workload);
        ulong rangeEnd = workload.BaseAddress + (ulong)workload.CodeSize + 0x10000;

        // Small enough that at least one representative region lands in the 3-hart steady state
        // (both workers spawned, main hasn't joined yet) rather than degenerating to 1 hart.
        LoopPointCheckpointSet captured = MultiHartLoopPointExperiment.CaptureLoopPointCheckpoints(
            kernel, mechanisms, mem, workload.BaseAddress, rangeEnd, targetGlobalInstructions: 60,
            excludedRanges, profileMaxTicks: 2_000_000
        );

        Assert.Contains(captured.RepresentativeLiveHarts.Values, live => live.All(l => l));

        LoopPointResult result = MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
            captured, () => FreshMechanisms(workload, mmapBase, mmapLimit), DetailedTrainFactory, warmupInstructions: 10
        );

        // Every region's HartResults length must equal that region's own live-hart count exactly —
        // proven both directions: a 1-live-hart region above, and (here) a region with all three live,
        // one of which may legitimately retire 0 (blocked on a real futex wait, routed through
        // RequestBlock, not a phantom) without being dropped from HartResults.
        foreach (LoopPointRegionMeasurement rm in result.RegionMeasurements) {
            bool[] live = captured.RepresentativeLiveHarts[rm.RegionIndex];
            Assert.Equal(live.Count(l => l), rm.HartResults.Length);
        }

        Assert.True(result.EstimatedTotalTicks > 0);
    }
}
