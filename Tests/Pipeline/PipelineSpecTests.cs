using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Observation;
using Orrery.Train;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

namespace Tests.Pipeline;

/// <summary>
/// Tests for <see cref="PipelineSpec"/> and its concrete subtypes.
/// </summary>
public class PipelineSpecTests {
    // addi x1,x0,10 / addi x2,x0,32 / add x3,x1,x2 / sw x3,128(x0) / ebreak
    private static readonly uint[] AddProgram = [
        0x00a00093, // addi x1, x0, 10
        0x02000113, // addi x2, x0, 32
        0x002081b3, // add  x3, x1, x2
        0x08302023, // sw   x3, 128(x0)
        0x00100073, // ebreak
    ];

    private static readonly uint[] EbreakOnly = [0x00100073,];

    private static FlatMemory MakeMemory(params uint[] program) {
        var mem = new FlatMemory(4096);
        var bytes = new byte[program.Length * 4];
        for (var i = 0; i < program.Length; i++) {
            bytes[i * 4 + 0] = (byte)program[i];
            bytes[i * 4 + 1] = (byte)(program[i] >> 8);
            bytes[i * 4 + 2] = (byte)(program[i] >> 16);
            bytes[i * 4 + 3] = (byte)(program[i] >> 24);
        }

        mem.Load(0, bytes);
        return mem;
    }

    private static RevolutionResult Run(ISteppableTrain train, long maxTicks = 10_000) {
        train.BeginStepping();
        long ticks = 0;
        while (ticks++ < maxTicks && train.StepCycle()) { }

        return train.FinishStepping();
    }

    private sealed class TrackingObserver : ICommitObserver {
        public int Commits;
        public void OnCommit(ulong pc, uint encoding, IArchState state) => Commits++;
    }

    // ── Per-variant smoke tests ────────────────────────────────────────────────

    [Fact]
    public void SingleCycle_RunsAddProgram() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new SingleCycleSpec().Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void FiveStage_RunsAddProgram() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new FiveStageSpec().Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void Superscalar_RunsAddProgram() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new SuperscalarSpec(4).Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void OutOfOrder_RunsAddProgram() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new OutOfOrderSpec(4, 16).Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    // ── FiveStage-specific parameters ─────────────────────────────────────────

    [Fact]
    public void FiveStage_ForwardingDisabled_ProducesCorrectResult() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new FiveStageSpec(false).Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void FiveStage_WithForwarding_TakesFewerCycles_ThanWithout() {
        uint[] rawProgram = [
            0x00a00093, // addi x1, x0, 10
            0x00108133, // add  x2, x1, x1   (RAW on x1)
            0x00100073, // ebreak
        ];
        long ticksWith = Run(
            new FiveStageSpec(true)
               .Build(new Rv32Mechanism(), MakeMemory(rawProgram))
        ).TotalTicks;
        long ticksWithout = Run(
            new FiveStageSpec(false)
               .Build(new Rv32Mechanism(), MakeMemory(rawProgram))
        ).TotalTicks;
        Assert.True(
            ticksWith < ticksWithout,
            $"Forwarding should reduce cycle count ({ticksWith} vs {ticksWithout})"
        );
    }

    [Fact]
    public void FiveStage_BranchPredictorFactory_IsInvokedOnBuild() {
        var invoked = false;
        new FiveStageSpec(
            BranchPredictorFactory: () => {
                invoked = true;
                return new AlwaysNotTakenPredictor();
            }
        ).Build(new Rv32Mechanism(), MakeMemory(PipelineSpecTests.EbreakOnly));
        Assert.True(invoked);
    }

    [Fact]
    public void FiveStage_CommitObserver_ReceivesCommitsForEveryRetiredInstruction() {
        var observer = new TrackingObserver();
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new FiveStageSpec(CommitObserver: observer).Build(new Rv32Mechanism(), mem));
        // AddProgram retires 4 instructions (ebreak is the halt signal, not a commit).
        Assert.Equal(4, observer.Commits);
    }

    [Fact]
    public void FiveStage_PEventLog_RecordsEvents() {
        var log = new PEventLog();
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new FiveStageSpec(PEventLog: log).Build(new Rv32Mechanism(), mem));
        Assert.NotEmpty(log.Events);
    }

    // ── OutOfOrder-specific parameters ────────────────────────────────────────

    [Fact]
    public void OutOfOrder_SmallRob_ProducesCorrectResult() {
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new OutOfOrderSpec(RobCapacity: 4, IqCapacity: 4).Build(new Rv32Mechanism(), mem));
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void OutOfOrder_BranchPredictorFactory_IsInvokedOnBuild() {
        var invoked = false;
        new OutOfOrderSpec(
            BranchPredictorFactory: () => {
                invoked = true;
                return new AlwaysNotTakenPredictor();
            }
        ).Build(new Rv32Mechanism(), MakeMemory(PipelineSpecTests.EbreakOnly));
        Assert.True(invoked);
    }

    [Fact]
    public void OutOfOrder_CommitObserver_ReceivesCommits() {
        var observer = new TrackingObserver();
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new OutOfOrderSpec(CommitObserver: observer).Build(new Rv32Mechanism(), mem));
        Assert.True(observer.Commits > 0);
    }

    [Fact]
    public void OutOfOrder_PEventLog_RecordsEvents() {
        var log = new PEventLog();
        FlatMemory mem = MakeMemory(PipelineSpecTests.AddProgram);
        Run(new OutOfOrderSpec(PEventLog: log).Build(new Rv32Mechanism(), mem));
        Assert.NotEmpty(log.Events);
    }
}