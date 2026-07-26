#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     Regression test for a finding made (and initially misdiagnosed) while validating
///     <see cref="SmartsDriver" />: a continuous <see cref="FiveStageTrain" /> run of
///     <see cref="SmartsTestWorkload" /> stopped mid-stream at a given committed-instruction count
///     shows a memory accumulator exactly one loop iteration's store ahead of a
///     <see cref="SingleCycleTrain" /> reference stopped at the same count. This is not a pipeline
///     correctness bug — it is a measurement-boundary artifact: the commit-count observer
///     (<see cref="InstructionCounter" />) fires in <c>WriteBack</c>, one stage after
///     <c>MemoryStage</c> has already written a trailing store's data, so the one store still in
///     flight past the stop point is already visible in memory a cycle before it is counted.
///     <see cref="FiveStageVsSingleCycle_AtNaturalCompletion_Match" /> and
///     <see cref="OooVsSingleCycle_AtNaturalCompletion_Match" /> prove this for both detailed
///     trains: with no in-flight shadow left to read ahead of (every train drained to its own
///     <c>ebreak</c>), all three models agree exactly.
/// </summary>
public class SmartsPipelineShadowDecisiveTest {
    [Fact]
    public void FiveStageVsSingleCycle_AtNaturalCompletion_Match() {
        FlatMemory memFs = SmartsTestWorkload.BuildProgram();
        var mechFs = new Rv32Mechanism();
        var counterFs = new InstructionCounter();
        var trainFs = new FiveStageTrain(mechFs, memFs, commitObserver: counterFs);
        trainFs.Run();
        int accFs = (int)memFs.Read(SmartsTestWorkload.AccumulatorAddress, 4);

        FlatMemory memSc = SmartsTestWorkload.BuildProgram();
        var mechSc = new Rv32Mechanism();
        var counterSc = new InstructionCounter();
        var trainSc = new SingleCycleTrain(mechSc, memSc, commitObserver: counterSc);
        trainSc.Run();
        int accSc = (int)memSc.Read(SmartsTestWorkload.AccumulatorAddress, 4);

        Assert.Equal(counterSc.Count, counterFs.Count);
        Assert.Equal(accSc, accFs);
    }

    [Fact]
    public void FiveStageVsSingleCycle_MidStream_AccumulatorRunsAheadBecauseOfMemWbShadow() {
        long stop = SmartsTestWorkload.Iters * 6L / 2;

        int accFs = SmartsTestWorkload.RunContinuousDetailedAccumulator(
            stop, (mechanism, mem, counter) => new FiveStageTrain(mechanism, mem, commitObserver: counter)
        );
        int accSc = SmartsTestWorkload.RunFunctionalReferenceAccumulator(stop);

        Assert.Equal(accSc + 1, accFs);
    }

    [Fact]
    public void OooVsSingleCycle_AtNaturalCompletion_Match() {
        FlatMemory memOoo = SmartsTestWorkload.BuildProgram();
        var mechOoo = new Rv32Mechanism();
        var counterOoo = new InstructionCounter();
        var trainOoo = new OooTrain(mechOoo, memOoo, commitObserver: counterOoo);
        trainOoo.Run();
        int accOoo = (int)memOoo.Read(SmartsTestWorkload.AccumulatorAddress, 4);

        FlatMemory memSc = SmartsTestWorkload.BuildProgram();
        var mechSc = new Rv32Mechanism();
        var counterSc = new InstructionCounter();
        var trainSc = new SingleCycleTrain(mechSc, memSc, commitObserver: counterSc);
        trainSc.Run();
        int accSc = (int)memSc.Read(SmartsTestWorkload.AccumulatorAddress, 4);

        Assert.Equal(counterSc.Count, counterOoo.Count);
        Assert.Equal(accSc, accOoo);
    }
}
