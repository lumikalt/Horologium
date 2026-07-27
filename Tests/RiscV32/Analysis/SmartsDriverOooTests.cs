#region

using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable AccessToModifiedClosure -- trainRef is a deliberate forward reference: the
// commitObserver closure is only ever invoked from trainRef.Run(), which runs after trainRef is
// assigned.

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     SMARTS systematic sampling against <see cref="OooTrain" />, using the shared workload in
///     <see cref="SmartsTestWorkload" /> — the out-of-order counterpart to
///     <see cref="SmartsDriverTests" />. Exercises the drain-before-handoff path
///     (<see cref="OooTrain.Drain" />) that a store buffer-free in-order train never needs: an OoO
///     train can still have in-flight, not-yet-retired instructions in its ROB/IQ/LQ/SQ at the
///     exact tick a sampling unit's measured window ends, and those must be drained (retired to
///     the shared memory) before the next functional-warming leg reads memory or the next detailed
///     leg inherits architectural state — otherwise a store made "during" this unit could still be
///     invisible to what comes after it.
///     <para>
///         Drain's extra retirements mean the run's true final instruction count can exceed the
///         nominal <c>J + (N-1)*K + U</c> by a few — <see cref="SmartsResult.FinalPosition" />
///         reports the real value, and every accumulator oracle here is built from it, not the
///         nominal formula (which <see cref="SmartsDriverTests" />'s no-drain FiveStage case can
///         use directly).
///     </para>
///     <c>W</c> is set well above the ROB capacity so the detailed-warming window has room to
///     refill the ROB/IQ before the measured window starts, per <see cref="SmartsDriver.Ooo" />'s
///     doc comment.
/// </summary>
public class SmartsDriverOooTests {
    private const int RobCapacity = 32;
    private static SmartsParameters Parameters() => new(120, 80, 1200, 120, 15);

    // The oracle for "did the handoff preserve state" is a continuous OooTrain run of the same
    // total instruction count, not a functional SingleCycleTrain reference — see
    // SmartsTestWorkload.RunContinuousDetailedAccumulator's doc comment for why.
    private static int ExpectedAccumulator(long totalInstructions) =>
        SmartsTestWorkload.RunContinuousDetailedAccumulator(
            totalInstructions,
            (mechanism, mem, counter) => new OooTrain(
                mechanism, mem, robCapacity: SmartsDriverOooTests.RobCapacity, predictor: new NBitBp(),
                dMemConfig: SmartsTestWorkload.DCache(),
                commitObserver: counter
            )
        );

    private static double RunFullDetailed() {
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        var counter = new InstructionCounter();
        var train = new OooTrain(
            mechanism, mem, robCapacity: SmartsDriverOooTests.RobCapacity, predictor: new NBitBp(),
            dMemConfig: SmartsTestWorkload.DCache(),
            commitObserver: counter
        );
        RevolutionResult result = train.Run();
        return result.TotalTicks / (double)counter.Count;
    }

    private static (SmartsResult Result, FlatMemory Memory) RunSmarts(
        IBranchPredictor? sharedPredictor,
        Action<int, long, IArchState>? onUnitEntry = null
    ) {
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        var iLayers = MemoryLayers.Build(mem, MemoryConfig.None);
        var dLayers = MemoryLayers.Build(mem, SmartsTestWorkload.DCache());

        SmartsResult result = SmartsDriver.Run(
            mechanism, 0, iLayers, dLayers, sharedPredictor, Parameters(),
            SmartsDriver.Ooo(robCapacity: SmartsDriverOooTests.RobCapacity), onUnitEntry
        );
        return (result, mem);
    }

    [Fact]
    public void WarmPredictor_TracksTrueCpi() {
        double trueCpi = RunFullDetailed();
        (SmartsResult warm, FlatMemory mem) = RunSmarts(new NBitBp());

        Assert.False(warm.Halted);
        Assert.Equal(15, warm.Units.Count);

        Assert.Equal(ExpectedAccumulator(warm.FinalPosition), (int)mem.Read(SmartsTestWorkload.AccumulatorAddress, 4));

        double bias = Math.Abs(warm.MeanCpi - trueCpi) / trueCpi;
        Assert.True(bias < 0.1, $"warm SMARTS CPI {warm.MeanCpi:F4} vs true {trueCpi:F4} (bias {bias:P1})");
    }

    // Regression test: SmartsDriver used to call OooTrain.Drain() unconditionally after every
    // measured window, which throws InvalidOperationException when the workload halts (program
    // exit) mid-window rather than completing normally — a real Runner CLI run against a real ELF
    // hit exactly this (Drain() throwing "the train halted before the pipeline reached a drained
    // boundary") before any unit test exercised it, since every other test here picks parameters
    // that never overrun the workload's end. K*N here deliberately overruns
    // SmartsTestWorkload's total instruction count (~24003).
    [Fact]
    public void HaltingMidWindow_DoesNotThrowAndReportsHalted() {
        var parameters = new SmartsParameters(200, 80, 2000, 200, 30);
        FlatMemory mem = SmartsTestWorkload.BuildProgram();
        var mechanism = new Rv32Mechanism();
        var iLayers = MemoryLayers.Build(mem, MemoryConfig.None);
        var dLayers = MemoryLayers.Build(mem, SmartsTestWorkload.DCache());

        SmartsResult result = SmartsDriver.Run(
            mechanism, 0, iLayers, dLayers, new NBitBp(), parameters,
            SmartsDriver.Ooo(robCapacity: SmartsDriverOooTests.RobCapacity)
        );

        Assert.True(result.Halted);
        Assert.True(result.Units.Count < parameters.N);
    }

    // The decisive handoff-fidelity check for the OoO case specifically: the drain-before-handoff
    // path must leave the exact same architectural state a plain functional trace would show at
    // the same absolute instruction count — proving Drain() correctly retires everything in
    // flight (and its stores land in the shared memory) before ArchStateTransfer runs.
    [Fact]
    public void UnitEntryState_MatchesReferenceFunctionalTrace() {
        var capturedPositions = new List<long>();
        var captured = new Dictionary<long, (ulong Pc, ulong[] Regs)>();

        (SmartsResult result, FlatMemory mem) = RunSmarts(
            new NBitBp(),
            (_, position, state) => {
                capturedPositions.Add(position);
                captured[position] = (
                    state.Pc, [..Enumerable.Range(0, 32).Select(r => state.IntegerRegisters.Read(r)),]);
            }
        );

        Assert.False(result.Halted);
        Assert.NotEmpty(capturedPositions);

        Assert.Equal(
            ExpectedAccumulator(result.FinalPosition), (int)mem.Read(SmartsTestWorkload.AccumulatorAddress, 4)
        );

        FlatMemory refMem = SmartsTestWorkload.BuildProgram();
        var refMechanism = new Rv32Mechanism();
        var reference = new Dictionary<long, (ulong Pc, ulong[] Regs)>();
        SingleCycleTrain? trainRef = null;
        var counter = new InstructionCounter(
            capturedPositions,
            idx => reference[capturedPositions[idx]] = (
                trainRef!.ArchState.Pc,
                [..Enumerable.Range(0, 32).Select(r => trainRef.ArchState.IntegerRegisters.Read(r)),]
            )
        );
        trainRef = new SingleCycleTrain(refMechanism, refMem, commitObserver: counter);
        trainRef.Run();

        foreach (long position in capturedPositions) {
            (ulong Pc, ulong[] Regs) expected = reference[position];
            (ulong Pc, ulong[] Regs) actual = captured[position];
            Assert.True(
                expected.Pc == actual.Pc, $"position {position}: expected Pc=0x{expected.Pc:X}, got 0x{actual.Pc:X}"
            );
            Assert.True(
                expected.Regs.SequenceEqual(actual.Regs),
                $"position {position}: register mismatch — expected [{string.Join(",", expected.Regs)}], " +
                $"got [{string.Join(",", actual.Regs)}]"
            );
        }
    }
}