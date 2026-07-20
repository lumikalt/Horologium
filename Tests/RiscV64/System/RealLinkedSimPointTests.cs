#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32.Analysis;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64;
using RiscV64.Memory;

#endregion

namespace Tests.RiscV64.System;

/// <summary>
///     Validates <see cref="Experiment.RunWithSimPointCheckpoints" /> (the machinery behind
///     <c>--simpoint-warmup</c>) against a genuinely compiled, statically-linked binary —
///     <c>simpoint_kernel.elf</c>, a static-array (no malloc) compute kernel with one <c>printf</c>
///     at the very end. <see cref="RealLinkedBinaryTests" /> already proved a real linked binary's
///     argv/stdout round-trip through a single straight-through run; this is the first time the
///     BBV-profiling/clustering/checkpoint-restore path itself sees real compiled code instead of a
///     bare-metal HTIF probe.
///     <para>
///         <see cref="ArchitecturalCheckpoint" /> itself only covers guest architectural state
///         (registers, memory, ISA blob) — a <see cref="LinuxSyscallEmulator" />'s own mutable state
///         (brk/mmap cursors, fd table, stdin position) is captured/restored separately via
///         <see cref="ICheckpointableSyscallHandler" />, wired into
///         <see cref="Experiment.CaptureSimPointCheckpoints" />/<see cref="Experiment.MeasureSimPointCheckpoints" />
///         through <see cref="IMechanism.SyscallHandler" />.
///         <see cref="CaptureAndMeasureSimPointCheckpoints_OnRealBinary_RoundTripsSyscallStateWithoutError" />
///         below proves that wiring actually fires against this real binary's compiled code, not just
///         that it compiles against the interface — see that test's doc comment for why the deeper
///         semantic proof (a broken restore actually producing wrong behavior) has to live elsewhere,
///         against a syscall sequence this particular binary doesn't have.
///     </para>
/// </summary>
public class RealLinkedSimPointTests {
    private const int WordSize = 8;
    private static readonly string[] Argv = ["simpoint_kernel.elf",];
    private static string ElfPath => Path.Combine(AppContext.BaseDirectory, "simpoint_kernel.elf");

    private static Rv64ElfWorkload MakeWorkload() => new(ElfPath, 8 * 1024 * 1024);

    private static ISteppableTrain DetailedFactory(
        IMechanism mechanism,
        IMemory mem,
        ulong entryPoint,
        InstructionCounter counter
    ) =>
        new OooeTrain(mechanism, mem, entryPoint, commitObserver: counter);

    [Fact]
    public void EcallsOnlyOccurAtStartupAndShutdown_NotInsideTheComputeLoop() {
        // Independent ground truth for the scope-reduction claim above: a plain functional pass,
        // not RunWithSimPointCheckpoints itself, so this can't share a bug with the code under test.
        Rv64ElfWorkload workload = MakeWorkload();
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        ulong stackTop = workload.BaseAddress + (ulong)workload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            mem, stackTop, RealLinkedSimPointTests.WordSize, RealLinkedSimPointTests.Argv, [],
            InitialStackBuilder.BuildStandardAuxv(0, 0, 0, workload.EntryPoint)
        );

        var sw = new StringWriter();
        var tracker = new EcallCommitTracker();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw, RealLinkedSimPointTests.WordSize);
        var mech = new Rv64Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint, commitObserver: tracker);
        train.ArchState.IntegerRegisters.Write(2, sp);
        train.Run(8_000_000);

        Assert.True(train.IsIdle);
        // Independent reference: native x86-64 gcc build of the identical C source (`gcc -O1`), not
        // anything routed through Horologium/RISC-V — only C semantics are shared, which is exactly
        // what's being cross-checked (does the RV64 simulation + musl stdio reproduce them?).
        Assert.Equal("sum=98404805115\n", sw.ToString());

        Assert.NotEmpty(tracker.EcallCommitIndices);
        // The kernel's own loop body has no ECALLs at all — every one recorded here comes from
        // musl's _start bookkeeping (a handful, right at the beginning) or the final
        // printf/exit_group (right at the very end). Rather than guessing where the boundary
        // between "startup" and "shutdown" falls, find the largest syscall-free gap directly: it
        // must cover the overwhelming majority of the program, i.e. the compute loop really is one
        // dominant syscall-free region, not scattered ECALLs throughout.
        List<long> boundaries = [0, ..tracker.EcallCommitIndices, tracker.TotalCommits,];
        long maxGap = boundaries.Zip(boundaries.Skip(1), (a, b) => b - a).Max();
        Assert.True(
            maxGap > tracker.TotalCommits * 0.9,
            $"expected one dominant syscall-free region spanning >90% of the program; " +
            $"largest gap was {maxGap} of {tracker.TotalCommits} total commits"
        );
    }

    [Fact]
    public void SimPointCheckpoints_OnRealBinary_MeasureSyscallFreeWindows() {
        Rv64ElfWorkload workload = MakeWorkload();
        const long intervalSize = 20_000;
        const long warmup = 2_000;

        SimPointCheckpointResult result = Experiment.RunWithSimPointCheckpoints(
            workload,
            () => new Rv64Mechanism(
                syscallHandler: new LinuxSyscallEmulator(
                    workload.InitialBreak, TextWriter.Null, RealLinkedSimPointTests.WordSize
                )
            ),
            DetailedFactory,
            intervalSize, warmup, maxK: 4, argv: RealLinkedSimPointTests.Argv,
            wordSize: RealLinkedSimPointTests.WordSize
        );

        Assert.NotEmpty(result.PointResults);
        Assert.True(result.EstimatedCpi > 0);

        // Independent ground truth for "no selected point's window needs a syscall" (see the sibling
        // test for how these indices were captured).
        Rv64ElfWorkload refWorkload = MakeWorkload();
        var refMem = new FlatMemory(refWorkload.MemorySize, refWorkload.BaseAddress);
        refWorkload.Load(refMem);
        ulong stackTop = refWorkload.BaseAddress + (ulong)refWorkload.MemorySize;
        ulong sp = InitialStackBuilder.BuildInitialStack(
            refMem, stackTop, RealLinkedSimPointTests.WordSize, RealLinkedSimPointTests.Argv, [],
            InitialStackBuilder.BuildStandardAuxv(0, 0, 0, refWorkload.EntryPoint)
        );
        var tracker = new EcallCommitTracker();
        var refHandler = new LinuxSyscallEmulator(
            refWorkload.InitialBreak, TextWriter.Null, RealLinkedSimPointTests.WordSize
        );
        var refTrain = new SingleCycleTrain(
            new Rv64Mechanism(syscallHandler: refHandler), refMem, refWorkload.EntryPoint, commitObserver: tracker
        );
        refTrain.ArchState.IntegerRegisters.Write(2, sp);
        refTrain.Run(8_000_000);
        Assert.True(refTrain.IsIdle);

        // The interval covering program startup (musl's _start bookkeeping) and the interval
        // covering shutdown (the final printf/exit_group) always include ECALLs by construction —
        // "phase 0" and "phase end" are cold-start/teardown code, and no choice of SimPoint
        // parameters moves that. Those two edge phases are still skipped below, but not because
        // they'd measure incorrectly (see the class doc comment: emulator-state checkpointing now
        // covers them) — this test's actual purpose is the complementary invariant: every simulation
        // point representing the repeated compute-loop phase this sampling technique targets must be
        // genuinely syscall-free, which the edge phases trivially aren't and would just add noise here.
        // Identify the dominant syscall-free gap directly (same technique as the sibling test)
        // rather than guessing where the startup/shutdown boundary falls.
        List<long> boundaries = [0, ..tracker.EcallCommitIndices, tracker.TotalCommits,];
        int gapIdx = Enumerable.Range(1, boundaries.Count - 1).MaxBy(k => boundaries[k] - boundaries[k - 1]);
        long safeStart = boundaries[gapIdx - 1];
        long safeEnd = boundaries[gapIdx];

        var fullyInsideSafeGap = 0;
        foreach (SimPointPointResult point in result.PointResults) {
            long intervalStart = point.Point.IntervalIndex * intervalSize;
            long actualWarmup = Math.Min(warmup, intervalStart);
            long windowStart = intervalStart - actualWarmup;
            long windowEnd = intervalStart + intervalSize;

            if (windowStart < safeStart || windowEnd > safeEnd)
                continue; // startup/shutdown edge phase — expected, not asserted
            fullyInsideSafeGap++;

            Assert.False(
                tracker.EcallCommitIndices.Any(i => i > windowStart && i <= windowEnd),
                $"simulation point at interval {point.Point.IntervalIndex} (window [{windowStart}, {windowEnd})) " +
                "needs a syscall despite falling inside the dominant syscall-free gap — the compute loop " +
                "should be genuinely syscall-free."
            );
        }

        Assert.True(
            fullyInsideSafeGap > 0,
            "expected at least one selected simulation point inside the compute loop, not just at the startup/shutdown edges"
        );
    }

    /// <summary>
    ///     Wiring proof, against this real ELF, for full syscall-emulator-state checkpointing (see the
    ///     class doc comment): <see cref="Experiment.CaptureSimPointCheckpoints" /> must actually
    ///     resolve <see cref="IMechanism.SyscallHandler" /> and call
    ///     <see cref="ICheckpointableSyscallHandler.WriteState" /> for every simulation point (not
    ///     just compile against the interface), and <see cref="Experiment.MeasureSimPointCheckpoints" />
    ///     must restore it (<see cref="ICheckpointableSyscallHandler.ReadState" />) without error.
    ///     <para>
    ///         This binary's own ECALLs (<c>set_tid_address</c>/<c>ioctl</c>/<c>writev</c>/<c>exit_group</c>
    ///         — see <see cref="EcallsOnlyOccurAtStartupAndShutdown_NotInsideTheComputeLoop" />) happen to
    ///         be state-inert w.r.t. every cursor <see cref="LinuxSyscallEmulator" /> checkpoints (no
    ///         <c>brk</c>/<c>mmap</c>/file/stdin/<c>getrandom</c>/<c>clock_gettime</c> here — a
    ///         static-array, no-<c>malloc</c> kernel, by design), so this test cannot itself prove the
    ///         restored state is semantically correct — that requires a state-carrying syscall to
    ///         actually diverge a broken restore from a correct one. That proof lives elsewhere:
    ///         <c>SyscallCheckpointTests</c> (RiscV32.System) round-trips
    ///         <see cref="LinuxSyscallEmulator" />'s brk/fd/stdin/rand/nanos state directly, and
    ///         <c>SimPointCheckpointTests.MeasureSimPointCheckpoints_RestoresSyscallState_...</c>
    ///         (RiscV32.Analysis) drives a hand-assembled <c>brk</c>-extend-then-query program through
    ///         this exact <see cref="Experiment.MeasureSimPointCheckpoints" /> code path and asserts the
    ///         query only sees the extended break when the syscall state was actually restored —
    ///         discriminatingly, with a same-shape control that omits the restore and gets the wrong
    ///         answer.
    ///     </para>
    /// </summary>
    [Fact]
    public void CaptureAndMeasureSimPointCheckpoints_OnRealBinary_RoundTripsSyscallStateWithoutError() {
        Rv64ElfWorkload workload = MakeWorkload();
        const long intervalSize = 20_000;
        const long warmup = 2_000;

        Func<IMechanism> mechanismFactory = () => new Rv64Mechanism(
            syscallHandler: new LinuxSyscallEmulator(
                workload.InitialBreak, TextWriter.Null, RealLinkedSimPointTests.WordSize
            )
        );

        SimPointCheckpointSet captured = Experiment.CaptureSimPointCheckpoints(
            workload, mechanismFactory, intervalSize, warmup, maxK: 4, argv: RealLinkedSimPointTests.Argv,
            wordSize: RealLinkedSimPointTests.WordSize
        );

        Assert.NotEmpty(captured.SyscallStates);
        // Every point's syscall state was actually captured — proves IMechanism.SyscallHandler
        // resolution + ICheckpointableSyscallHandler.WriteState fired during this real ELF's capture
        // pass, not just that the code compiles against the interface.
        Assert.All(captured.SyscallStates, Assert.NotNull);

        // MeasureSimPointCheckpoints must restore that state (ReadState) without throwing, for every
        // point, including the startup/shutdown edge points whose window contains this binary's real
        // ECALLs.
        SimPointCheckpointResult result = Experiment.MeasureSimPointCheckpoints(
            workload, captured, mechanismFactory, DetailedFactory
        );

        Assert.NotEmpty(result.PointResults);
        Assert.True(result.EstimatedCpi > 0);
    }

    private sealed class EcallCommitTracker : ICommitObserver {
        public long TotalCommits { get; private set; }
        public List<long> EcallCommitIndices { get; } = [];

        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
            TotalCommits++;
            if (rawEncoding == 0x00000073u) EcallCommitIndices.Add(TotalCommits);
        }
    }
}