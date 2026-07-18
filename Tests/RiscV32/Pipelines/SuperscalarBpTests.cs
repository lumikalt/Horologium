using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Branch-prediction semantics and PEvent tracing on <see cref="SuperscalarTrain" />:
///     with a predictor attached, a correctly predicted branch no longer cuts the issue
///     group (it continues at the predicted target within the same cycle) and a mispredict
///     pays a fixed frontend-redirect penalty; without one, the legacy cut-at-every-branch
///     behavior is unchanged.
/// </summary>
public class SuperscalarBpTests {
    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    // 200-iteration countdown loop with a 3-instruction body — short enough that a
    // 4-wide group can span the backward branch into the next iteration when predicted.
    //   addi x1, x0, 200
    //   loop: addi x2, x2, 1
    //         addi x1, x1, -1
    //         bne  x1, x0, loop
    //   ebreak
    private static readonly uint[] LoopProgram = [
        0x0C800093, // addi x1, x0, 200
        0x00110113, // addi x2, x2, 1
        0xFFF08093, // addi x1, x1, -1
        0xFE009CE3, // bne x1, x0, -8
        0x00100073, // ebreak
    ];

    private static (long Cycles, long BranchMisses, uint X2) RunLoop(
        IBranchPredictor? predictor,
        PEventLog? plog = null
    ) {
        var mem = new FlatMemory(4096);
        Load(mem, LoopProgram);
        var train = new SuperscalarTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, predictor: predictor, pEventLog: plog
        );
        train.Run();
        DialBoardSnapshot snap = train.SnapshotPipeline();
        return (snap.Counters["cycles"], snap.Counters["branch_misses"],
                (uint)train.ArchState.IntegerRegisters.Read(2));
    }

    [Fact]
    public void NoPredictor_LegacyBehavior_NoMisses() {
        (long cycles, long misses, uint x2) = RunLoop(null);
        Assert.Equal(200u, x2);
        Assert.Equal(0, misses);
        // Every iteration is exactly one group (cut at the branch): ≥ 200 cycles.
        Assert.True(cycles >= 200, $"cycles {cycles}");
    }

    [Fact]
    public void Predictor_LearnsLoop_GroupsSpanTheBranch() {
        (long cyclesWithout, _, _) = RunLoop(null);
        (long cyclesWith, long misses, uint x2) = RunLoop(new NBitPredictor(2, 1024));

        Assert.Equal(200u, x2); // architectural result unchanged
        // A trained predicted-taken branch lets 4-wide groups span iterations:
        // 3 instructions/iteration at width 4 beats one group per iteration.
        Assert.True(
            cyclesWith < cyclesWithout,
            $"with predictor {cyclesWith} should beat without {cyclesWithout}"
        );
        // The 2-bit counter warms up and mispredicts once more at loop exit.
        Assert.InRange(misses, 1, 5);
    }

    [Fact]
    public void AlwaysNotTaken_MispredictsEveryTakenIteration() {
        (long cyclesWithout, _, _) = RunLoop(null);
        (long cyclesWith, long misses, _) = RunLoop(new AlwaysNotTakenPredictor());

        // The backward branch is taken 199 times, each one a mispredict; the final
        // not-taken exit is predicted correctly.
        Assert.Equal(199, misses);
        // Each mispredict pays the redirect penalty on top of the legacy group cut.
        Assert.True(cyclesWith > cyclesWithout);
    }

    [Fact]
    public void PEventLog_RecordsFetchExecuteRetirePerInstruction() {
        var plog = new PEventLog();
        (_, _, uint x2) = RunLoop(new NBitPredictor(2, 1024), plog);
        Assert.Equal(200u, x2);

        Assert.NotEmpty(plog.Events);
        // Every instruction gets Fetch, Execute and Retire events in its issue cycle.
        List<IGrouping<ulong, PEvent>> byInstr = plog.Events.GroupBy(e => e.InstrId).ToList();
        Assert.Equal(602, byInstr.Count); // 1 setup + 200×3 loop + 1 ebreak
        foreach (IGrouping<ulong, PEvent> g in byInstr.Take(50)) {
            Assert.Contains(g, e => e.Kind == PEventKind.Fetch);
            Assert.Contains(g, e => e.Kind == PEventKind.Execute);
            Assert.Contains(g, e => e.Kind == PEventKind.Retire);
        }
    }

    [Fact]
    public void Trace_SuperscalarConfig_ProducesEvents() {
        var bytes = new byte[LoopProgram.Length * 4];
        for (var i = 0; i < LoopProgram.Length; i++) {
            bytes[i * 4 + 0] = (byte)LoopProgram[i];
            bytes[i * 4 + 1] = (byte)(LoopProgram[i] >> 8);
            bytes[i * 4 + 2] = (byte)(LoopProgram[i] >> 16);
            bytes[i * 4 + 3] = (byte)(LoopProgram[i] >> 24);
        }

        var config = new NamedConfig(
            "ss", new TrainConfig("superscalar", IssueWidth: 2, Predictor: BranchPredictorConfig.NBit())
        );
        PEventLog plog = Experiment.Trace(new ByteArrayWorkload(bytes), config, new Rv32Mechanism());
        Assert.NotEmpty(plog.Events);
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Fetch);
    }

    [Fact]
    public void ExperimentRun_DaeAndCprConfigs_ExecuteTheWorkload() {
        var bytes = new byte[LoopProgram.Length * 4];
        for (var i = 0; i < LoopProgram.Length; i++) {
            bytes[i * 4 + 0] = (byte)LoopProgram[i];
            bytes[i * 4 + 1] = (byte)(LoopProgram[i] >> 8);
            bytes[i * 4 + 2] = (byte)(LoopProgram[i] >> 16);
            bytes[i * 4 + 3] = (byte)(LoopProgram[i] >> 24);
        }

        NamedConfig[] configs = [
            new("dae", new TrainConfig("dae")),
            new("cpr", new TrainConfig("cpr", Predictor: BranchPredictorConfig.NBit())),
        ];
        ExperimentResult result = Experiment.Run(
            new ByteArrayWorkload(bytes), configs, () => new Rv32Mechanism()
        );

        Assert.Equal(2, result.Runs.Count);
        foreach (RunRecord run in result.Runs) {
            long retired = run.Result.Snapshots.Sum(s => s.Counters.GetValueOrDefault("retired"));
            Assert.True(retired >= 601, $"{run.Name}: retired {retired}");
        }
    }
}
