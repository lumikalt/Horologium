using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Memory;

namespace Tests.RiscV32.Analysis;

/// <summary>
///     <see cref="Experiment.RunWithSimPointCheckpoints" /> end to end: profiling, single-pass
///     checkpoint capture, restore-and-measure on a detailed OoO train, and weighted-CPI
///     combination. These are the tests the SimPoint-checkpoint machinery lives or dies by — the
///     BBV clustering itself is already covered in <c>SimPointTests</c>/<c>SimPointAnalysisTests</c>.
/// </summary>
public class SimPointCheckpointTests {
    // addi x1, x0, 40        — loop counter
    // addi x2, x0, 0         — accumulator
    // loop: addi x2, x2, 3   — accumulate
    //       addi x1, x1, -1  — decrement
    //       bne  x1, x0, loop
    // ebreak
    private static readonly byte[] LoopProgram = SimPointCheckpointTests.Encode(
        0x02800093u, 0x00000113u, 0x00310113u, 0xFFF08093u, 0xFE009EE3u, 0x00100073u
    );

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static ByteArrayWorkload MakeWorkload() => new(SimPointCheckpointTests.LoopProgram, memorySizeBytes: 0x1000);

    private static ISteppableTrain DetailedFactory(IMechanism mechanism, IMemory mem, ulong entryPoint, InstructionCounter counter) =>
        new OooeTrain(mechanism, mem, entryPoint, commitObserver: counter);

    [Fact]
    public void SingleSimulationPoint_WithZeroWarmup_MatchesStraightThroughRun() {
        // intervalSize far exceeds the program's instruction count, so BBV profiling produces
        // exactly one (partial) interval, one cluster, one simulation point at weight 1.0. With
        // zero warmup its checkpoint target is instruction 0 (the pristine start — captured
        // directly, not via InstructionCounter's post-commit callback), so the whole-program CPI
        // estimate must exactly equal running the same detailed train straight through from the
        // true entry point.
        const long intervalSize = 10_000;

        SimPointCheckpointResult result = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory, intervalSize, 0
        );

        Assert.Equal(1, result.SimPoints.IntervalCount);
        Assert.Equal(1, result.SimPoints.K);
        SimPointPointResult point = Assert.Single(result.PointResults);
        Assert.Equal(1.0, point.Point.Weight);
        Assert.Equal(0, point.Point.IntervalIndex);

        // Reference: one continuous detailed run from the true entry point to natural halt.
        var refCounter = new InstructionCounter();
        IWorkload refWorkload = MakeWorkload();
        var refMem = new FlatMemory(refWorkload.MemorySize, refWorkload.BaseAddress);
        refWorkload.Load(refMem);
        var refTrain = new OooeTrain(new Rv32Mechanism(), refMem, refWorkload.EntryPoint, commitObserver: refCounter);
        RevolutionResult refResult = refTrain.Run(1_000_000);

        Assert.Equal(refResult.TotalTicks, point.Revolution.TotalTicks);
        Assert.Equal(refCounter.Count, point.MeasuredInstructions);
        Assert.Equal((double)refResult.TotalTicks / refCounter.Count, result.EstimatedCpi, 12);
    }

    [Fact]
    public void SinglePoint_WithWarmup_MatchesManualCheckpointAtTheSameTarget() {
        // Forces exactly one simulation point (maxK: 1) so the checkpoint target is deterministic
        // given whichever interval the analysis picks as most representative — this test doesn't
        // need to know that index ahead of time, since it reads it back from the result and
        // reproduces the same checkpoint target/warmup manually. What it actually exercises is
        // Train.FinishStepping(baseline)'s tick-relative fix: with actualWarmup > 0, a pre-fix
        // build would return the *absolute* escapement tick (including warmup ticks) from
        // WarmupMeasureDriver instead of the measured-phase-only tick count, inflating every
        // per-point CPI.
        const long intervalSize = 15;
        const long warmup = 6;

        SimPointCheckpointResult result = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory, intervalSize, warmup, maxK: 1
        );
        SimPointPointResult point = Assert.Single(result.PointResults);

        // Per-point warmup is clamped to the interval's own start (Experiment.RunWithSimPointCheckpoints
        // never measures before instruction 0, and never skips part of the interval itself to do it) —
        // the reference below must clamp identically.
        long intervalStart = point.Point.IntervalIndex * intervalSize;
        long actualWarmup = Math.Min(warmup, intervalStart);
        long target = intervalStart - actualWarmup;

        // Manual reference: fast-forward functionally to `target`, checkpoint, restore into a
        // fresh OoO train, warm up `actualWarmup` instructions (unmeasured), then measure `intervalSize`.
        IWorkload workload = MakeWorkload();
        var ffCounter = new InstructionCounter();
        var ffMem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(ffMem);
        var ffTrain = new SingleCycleTrain(new Rv32Mechanism(), ffMem, workload.EntryPoint, commitObserver: ffCounter);
        ffTrain.BeginStepping();
        while (ffCounter.Count < target && ffTrain.StepCycle()) { }
        ffTrain.FinishStepping();

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, ffTrain.ArchState, ffMem, 0);
        ms.Position = 0;
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(ms);

        // Ground truth for the measured-phase tick count, computed from OooeTrain.CurrentTick
        // deltas directly — independent of WarmupMeasureDriver/Train.FinishStepping(baseline), so
        // this actually catches a regression of the tick-relative fix rather than comparing two
        // computations that would both be equally wrong.
        var refCounter = new InstructionCounter();
        var refMem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(refMem);
        var refTrain = new OooeTrain(new Rv32Mechanism(), refMem, chk.Pc, commitObserver: refCounter);
        chk.RestoreInto(refTrain.ArchState!, refMem);

        refTrain.BeginStepping();
        while (refCounter.Count < actualWarmup && refTrain.StepCycle()) { }
        long tickAtBaseline = refTrain.CurrentTick;
        long measureTarget = actualWarmup + intervalSize;
        while (refCounter.Count < measureTarget && refTrain.StepCycle()) { }
        long groundTruthMeasuredTicks = refTrain.CurrentTick - tickAtBaseline;
        refTrain.FinishStepping();

        Assert.Equal(groundTruthMeasuredTicks, point.Revolution.TotalTicks);
        Assert.Equal(refCounter.Count - actualWarmup, point.MeasuredInstructions);
    }

    [Fact]
    public void SingleSimulationPoint_EarlyInterval_ClampsWarmupToIntervalStart() {
        // intervalSize far exceeds the program's instruction count, so there is exactly one
        // interval, at index 0 — its intervalStart is 0. Per-point warmup must clamp to
        // min(requestedWarmup, intervalStart) = 0 regardless of the requested warmup; otherwise
        // the measured window would shift to [requestedWarmup, requestedWarmup+intervalSize)
        // instead of [0, intervalSize), silently skipping the interval's own first instructions
        // and running that many past its end.
        const long intervalSize = 10_000;
        const long requestedWarmup = 7;

        SimPointCheckpointResult result = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory,
            intervalSize, requestedWarmup
        );

        SimPointPointResult point = Assert.Single(result.PointResults);
        Assert.Equal(0, point.Point.IntervalIndex);

        // Reference: one continuous detailed run from the true entry point to natural halt — must
        // match exactly despite requestedWarmup > 0, because intervalStart (0) clamps it away.
        var refCounter = new InstructionCounter();
        IWorkload refWorkload = MakeWorkload();
        var refMem = new FlatMemory(refWorkload.MemorySize, refWorkload.BaseAddress);
        refWorkload.Load(refMem);
        var refTrain = new OooeTrain(new Rv32Mechanism(), refMem, refWorkload.EntryPoint, commitObserver: refCounter);
        RevolutionResult refResult = refTrain.Run(1_000_000);

        Assert.Equal(refResult.TotalTicks, point.Revolution.TotalTicks);
        Assert.Equal(refCounter.Count, point.MeasuredInstructions);
    }

    [Fact]
    public void CaptureThenMeasure_MatchesRunWithSimPointCheckpoints_ForTheSameConfig() {
        // RunWithSimPointCheckpoints is now a thin CaptureSimPointCheckpoints + MeasureSimPointCheckpoints
        // wrapper (see their doc comments) — this pins that the split didn't change the single-config
        // result at all, byte-for-byte in the measured tick/instruction counts.
        const long intervalSize = 15;
        const long warmup = 6;

        SimPointCheckpointResult direct = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory, intervalSize, warmup, maxK: 1
        );

        SimPointCheckpointSet captured = Experiment.CaptureSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), intervalSize, warmup, maxK: 1
        );
        SimPointCheckpointResult viaSplit = Experiment.MeasureSimPointCheckpoints(
            MakeWorkload(), captured, () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory
        );

        Assert.Equal(direct.PointResults.Count, viaSplit.PointResults.Count);
        for (var i = 0; i < direct.PointResults.Count; i++) {
            Assert.Equal(direct.PointResults[i].Revolution.TotalTicks, viaSplit.PointResults[i].Revolution.TotalTicks);
            Assert.Equal(direct.PointResults[i].MeasuredInstructions, viaSplit.PointResults[i].MeasuredInstructions);
        }

        Assert.Equal(direct.EstimatedCpi, viaSplit.EstimatedCpi, 12);
    }

    [Fact]
    public void MeasureSimPointCheckpoints_ReusesOneCaptureAcrossDifferentPipelinesAndRepeatedCalls() {
        // The actual point of the split (see the --sweep use in Program.cs): one capture must be
        // reusable across several detailed-pipeline configs, each measuring exactly as if it had
        // been captured freshly for that config alone — not aliased/mutated by an earlier measure
        // call. Uses single_cycle vs OoO specifically because they have different CPI on this loop
        // (no memory/branch-predictor divergence to confound it — a real, checkable difference).
        const long intervalSize = 15;
        const long warmup = 6;

        SimPointCheckpointSet captured = Experiment.CaptureSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), intervalSize, warmup, maxK: 1
        );

        ISteppableTrain SingleCycleFactory(IMechanism mech, IMemory mem, ulong entry, InstructionCounter counter) =>
            new SingleCycleTrain(mech, mem, entry, commitObserver: counter);

        SimPointCheckpointResult viaOoo = Experiment.MeasureSimPointCheckpoints(
            MakeWorkload(), captured, () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory
        );
        SimPointCheckpointResult viaSingleCycle = Experiment.MeasureSimPointCheckpoints(
            MakeWorkload(), captured, () => new Rv32Mechanism(), SingleCycleFactory
        );
        // Re-measure with the OoO config again, against the same captured set, to prove it wasn't
        // consumed or mutated by the two calls above.
        SimPointCheckpointResult viaOooAgain = Experiment.MeasureSimPointCheckpoints(
            MakeWorkload(), captured, () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory
        );

        SimPointCheckpointResult directOoo = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SimPointCheckpointTests.DetailedFactory, intervalSize, warmup, maxK: 1
        );
        SimPointCheckpointResult directSingleCycle = Experiment.RunWithSimPointCheckpoints(
            MakeWorkload(), () => new Rv32Mechanism(), SingleCycleFactory, intervalSize, warmup, maxK: 1
        );

        Assert.Equal(directOoo.EstimatedCpi, viaOoo.EstimatedCpi, 12);
        Assert.Equal(directSingleCycle.EstimatedCpi, viaSingleCycle.EstimatedCpi, 12);
        Assert.Equal(viaOoo.EstimatedCpi, viaOooAgain.EstimatedCpi, 12);
        // Sanity against a vacuous pass: OoO and single-cycle must actually measure differently here.
        Assert.NotEqual(viaOoo.EstimatedCpi, viaSingleCycle.EstimatedCpi);
    }
}
