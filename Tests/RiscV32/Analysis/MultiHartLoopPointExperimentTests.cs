#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Memory;
using RiscV32.MultiCore;

// ReSharper disable InconsistentNaming

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     <see cref="MultiHartLoopPointExperiment" /> end-to-end: capture → cluster → checkpoint →
///     restore → measure → extrapolate, against a hand-assembled two-hart, two-phase compute program
///     rather than a real pthread ELF.
///     <para>
///         Per the advisor review that scoped this test: a real compiled pthread binary still calls
///         <c>pthread_join</c> at the end, so whether any given representative region avoids blocking
///         depends on how <c>SimPointAnalysis</c> happens to cluster — building a new toolchain fixture
///         risks landing on a representative that hits <c>FiveStageTrain</c>'s <c>RequestBlock</c>
///         guard (see <c>Tests/Pipeline/FiveStageRequestBlockGuardTests.cs</c>) with no passing
///         end-to-end test to show for it. A hand-assembled program with no syscalls at all makes
///         blocking impossible while still exercising every real code path: two-hart interleaving,
///         two-phase clustering (non-trivial multipliers), checkpoint capture at data-dependent loop-
///         header boundaries, restore onto fresh <see cref="FiveStageTrain" />s, and Eq. 1 extrapolation.
///         The one combination this leaves untested — a real multi-hart pthread ELF through this same
///         orchestrator — is exactly the one the RequestBlock gap makes untestable today (joining
///         blocks); that gap is inherent, not a shortcut around real coverage.
///     </para>
/// </summary>
public class MultiHartLoopPointExperimentTests {
    private const uint AddiX1Plus1 = 0x00108093;  // addi x1, x1, 1
    private const uint AddiX2Minus1 = 0xFFF10113; // addi x2, x2, -1
    private const uint AddiX4Minus1 = 0xFFF20213; // addi x4, x4, -1
    private const uint Ebreak = 0x0010_0073;

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    // Two-phase program at `baseAddr`: phase A (loopA, 4 instructions/iteration) counted down by x2,
    // falls through into phase B (loopB, 3 instructions/iteration) counted down by x4, then halts.
    // Phase A's loop-start PC (baseAddr) and phase B's (baseAddr+0x10) are entirely distinct BBV keys,
    // so the two phases produce visibly different basic-block-vector signatures for clustering.
    private static void LoadTwoPhaseProgram(FlatMemory mem, ulong baseAddr) {
        mem.Load(
            baseAddr,
            ToBytes(
                MultiHartLoopPointExperimentTests.AddiX1Plus1, MultiHartLoopPointExperimentTests.AddiX1Plus1,
                MultiHartLoopPointExperimentTests.AddiX2Minus1, Bne(2, 0, -12), // 0x00 loopA (-> 0x00)
                MultiHartLoopPointExperimentTests.AddiX1Plus1, MultiHartLoopPointExperimentTests.AddiX4Minus1,
                Bne(4, 0, -8),                           // 0x10 loopB (-> 0x10)
                MultiHartLoopPointExperimentTests.Ebreak // 0x1C
            )
        );
    }

    [Fact]
    public void CaptureClusterMeasureExtrapolate_TwoHartTwoPhaseProgram_ProducesConsistentNonTrivialResult() {
        const ulong hart0Addr = 0x00;
        const ulong hart1Addr = 0x40;
        const int iterationsA = 20;
        const int iterationsB = 20;

        var mem = new FlatMemory(0x1000);
        LoadTwoPhaseProgram(mem, hart0Addr);
        LoadTwoPhaseProgram(mem, hart1Addr);

        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
        IReadOnlyList<IMechanism> mechanisms = [mech0, mech1,];
        var kernel = new MultiHartKernel(mem, mech0, mech1);
        kernel.SetEntryPoint(0, hart0Addr);
        kernel.SetEntryPoint(1, hart1Addr);
        kernel.StateOf(0).IntegerRegisters.Write(2, iterationsA);
        kernel.StateOf(0).IntegerRegisters.Write(4, iterationsB);
        kernel.StateOf(1).IntegerRegisters.Write(2, iterationsA);
        kernel.StateOf(1).IntegerRegisters.Write(4, iterationsB);

        // Small enough to produce several regions inside each phase (phase A: 4 instr/iter x 2 harts
        // = 8 global instr/iter, phase B: 3 instr/iter x 2 harts = 6) — not tuned to land exactly on
        // the phase boundary, since clustering only needs *some* regions dominated by each phase's
        // distinct BBV signature, not a clean split.
        const long targetGlobalInstructions = 16;
        LoopPointCheckpointSet captured = MultiHartLoopPointExperiment.CaptureLoopPointCheckpoints(
            kernel, mechanisms, mem, 0, 0x1000, targetGlobalInstructions
        );

        Assert.True(captured.SimPoints.IntervalCount >= 4, "expected multiple regions across both phases");
        // Two-phase program: at least two distinct block signatures worth clustering.
        Assert.True(captured.SimPoints.K >= 2, $"expected >= 2 clusters, got {captured.SimPoints.K}");
        Assert.Equal(captured.SimPoints.Points.Count, captured.RepresentativeCheckpoints.Count);
        Assert.Equal(captured.SimPoints.Points.Count, captured.Multipliers.Count);

        // Self-consistency invariant (Eq. 2's own definition): summing (multiplier * own inscount)
        // over every representative reconstructs the total filtered instruction count.
        double reconstructed = captured.Multipliers.Sum(kv => kv.Value * captured.RegionInstructionCounts[kv.Key]);
        Assert.Equal(captured.RegionInstructionCounts.Sum(), reconstructed, 1e-6);

        // Every multiplier is >= 1 (Eq. 2: a representative's own count is always part of its
        // cluster's total) and at least one is > 1 (some cluster has more than one member, i.e. the
        // clustering actually did something rather than every region being its own singleton phase).
        Assert.All(captured.Multipliers.Values, m => Assert.True(m >= 1.0));
        Assert.Contains(captured.Multipliers.Values, m => m > 1.0);

        (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) MechanismsFactory() {
            var m0 = new Rv32Mechanism();
            var m1 = new Rv32Mechanism();
            return ([m0, m1,], null);
        }

        // Both harts run the identical two-phase program shape, just at different, non-overlapping
        // address ranges (hart 0's code occupies [0x00, 0x20), hart 1's [0x40, 0x60) — see
        // LoadTwoPhaseProgram). MeasureLoopPointCheckpoints calls this factory once per hart per
        // representative region, in hart-index order (0, 1, 0, 1, ...) — so restartPc's value,
        // checked by call parity against the RIGHT hart's own address range, actually discriminates
        // a real regression: if the orchestrator's chk.PcOf(h) threading broke (this orchestrator's
        // own real, once-shipped bug — FiveStageTrain tracks fetch PC separately from IArchState.Pc,
        // seeded only once at construction, so a restore that doesn't pass the checkpoint's PC as
        // the constructor's entryPoint livelocks instead of measuring anything), hart 1's calls
        // would receive some other hart's (or a placeholder 0) PC — visibly outside its own range.
        // (A checkpoint's restored PC is captured right after the marker-triggering commit, so it's
        // typically one instruction past a loop header, not the header address itself exactly — the
        // range check tolerates that without needing to predict the precise offset.)
        // A same-address-both-harts check (e.g. "retired > 0") would NOT catch a cross-hart mixup:
        // hart 0's own loop code at 0x00 is itself a valid, self-terminating program, so a wrongly-
        // defaulted hart-1 train fetching from 0x00 would still retire instructions and reach ebreak
        // — a false pass (feedback_checkpoint_roundtrip_theater's pattern, hit again here on first
        // draft before this fix).
        var detailedTrainCalls = 0;

        ISteppableTrain DetailedTrainFactory(
            IMechanism mech,
            IMemory runMem,
            ulong restartPc,
            InstructionCounter counter
        ) {
            (ulong RangeStart, ulong RangeEnd) expectedRange = detailedTrainCalls % 2 == 0
                ? (hart0Addr, hart0Addr + 0x20)
                : (hart1Addr, hart1Addr + 0x20);
            Assert.InRange(restartPc, expectedRange.RangeStart, expectedRange.RangeEnd - 1);
            detailedTrainCalls++;
            return new FiveStageTrain(mech, runMem, restartPc, commitObserver: counter);
        }

        LoopPointResult result = MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
            captured, MechanismsFactory, DetailedTrainFactory, 4
        );

        Assert.Equal(captured.SimPoints.Points.Count * 2, detailedTrainCalls);
        Assert.Equal(captured.SimPoints.Points.Count, result.RegionMeasurements.Count);
        Assert.True(result.EstimatedTotalTicks > 0);
        foreach (LoopPointRegionMeasurement region in result.RegionMeasurements) {
            Assert.Equal(2, region.HartResults.Length);
            Assert.All(region.HartResults, r => Assert.True(r.TotalTicks > 0));
        }
    }

    /// <summary>
    ///     <see cref="MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints" />'s fail-loud guard: a
    ///     hart marked live (<see cref="LoopPointCheckpointSet.RepresentativeLiveHarts" />) whose own
    ///     checkpointed PC falls outside the workload's mapped memory must throw, not silently build a
    ///     detailed-pipeline train that fetches whatever bytes happen to sit there as if they were real
    ///     code — the same failure class a dormant hart's untouched (PC 0) state hits, hand-triggered
    ///     here directly rather than via a real clone()-spawn scenario.
    /// </summary>
    [Fact]
    public void MeasureLoopPointCheckpoints_LiveHartWithOutOfRangePc_ThrowsRatherThanMeasuringGarbage() {
        var mem = new FlatMemory(0x1000);
        var mech = new Rv32Mechanism();
        IArchState state = mech.CreateArchState();
        state.Pc = 0x5000; // outside [0, 0x1000)

        using var ms = new MemoryStream();
        MultiHartCheckpoint.Save(ms, [state,], mem, null, 0);
        byte[] chkBytes = ms.ToArray();

        var captured = new LoopPointCheckpointSet(
            new SimPointResult(1, 1, [0,], [new SimulationPoint(0, 0, 1.0),], 0),
            new Dictionary<int, byte[]> { [0] = chkBytes, },
            [1L,],
            new Dictionary<int, double> { [0] = 1.0, },
            mem.BaseAddress, mem.SizeBytes,
            new Dictionary<int, bool[]> { [0] = [true,], }
        );

        (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) Factory() =>
            ([new Rv32Mechanism(),], null);

        ISteppableTrain TrainFactory(IMechanism m, IMemory runMem, ulong pc, InstructionCounter c) =>
            new FiveStageTrain(m, runMem, pc, commitObserver: c);

        Assert.Throws<InvalidOperationException>(() => MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
                                                     captured, Factory, TrainFactory, 0
                                                 )
        );
    }

    /// <summary>
    ///     <see cref="MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints" />'s stall guard: a live
    ///     hart genuinely deadlocked within the measured window (its ecall permanently blocked, with no
    ///     other hart able to ever clear it) must throw rather than silently feeding a truncated,
    ///     inflated-tick measurement into Eq. 1/2 as if it were clean — this is what
    ///     <see cref="MultiHartWarmupMeasureDriver.RunOutcome.StallLimitHit" /> exists to distinguish from
    ///     a region that legitimately finished early (<see cref="MultiHartWarmupMeasureDriver.RunOutcome.AllHartsHalted" />).
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task MeasureLoopPointCheckpoints_LiveHartPermanentlyBlocked_ThrowsRatherThanExtrapolatingFromAStall() {
        await Task.Run(() => {
                var mem = new FlatMemory(0x1000);
                var ecallBytes = new byte[4];
                BitConverter.TryWriteBytes(ecallBytes.AsSpan(0), 0x0000_0073u); // ecall
                mem.Load(0x00, ecallBytes);

                var mech = new Rv32Mechanism();
                IArchState state = mech.CreateArchState();
                state.Pc = 0x00; // valid, in-range PC — passes the PC guard, then blocks forever

                using var ms = new MemoryStream();
                MultiHartCheckpoint.Save(ms, [state,], mem, null, 0);
                byte[] chkBytes = ms.ToArray();

                var captured = new LoopPointCheckpointSet(
                    new SimPointResult(1, 1, [0,], [new SimulationPoint(0, 0, 1.0),], 0),
                    new Dictionary<int, byte[]> { [0] = chkBytes, },
                    [1000L,],
                    new Dictionary<int, double> { [0] = 1.0, },
                    mem.BaseAddress, mem.SizeBytes,
                    new Dictionary<int, bool[]> { [0] = [true,], }
                );

                (IReadOnlyList<IMechanism> Mechanisms, ICheckpointableSyscallHandler? SyscallHandler) Factory() =>
                    ([new Rv32Mechanism(syscallHandler: new NeverClearingHandler()),], null);

                ISteppableTrain TrainFactory(IMechanism m, IMemory runMem, ulong pc, InstructionCounter c) =>
                    new FiveStageTrain(m, runMem, pc, commitObserver: c);

                Assert.Throws<InvalidOperationException>(() => MultiHartLoopPointExperiment.MeasureLoopPointCheckpoints(
                                                             captured, Factory, TrainFactory, 0
                                                         )
                );
            }
        );
    }

    private sealed class NeverClearingHandler : ISyscallHandler {
        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) =>
            new() { RequestBlock = true, };
    }
}