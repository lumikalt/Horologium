using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Timing semantics of the in-order <see cref="SuperscalarTrain" />: scoreboarded issue
///     (RAW/WAW interlocks with full bypass), per-class FU ports and latencies, a pipelined
///     frontend whose refill is the emergent misprediction penalty, and PEvent tracing.
/// </summary>
public class SuperscalarBpTests {
    private const uint Ebreak = 0x00100073;

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

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static (long Cycles, long BranchMisses, uint X2) RunLoop(
        IBranchPredictor? predictor,
        PEventLog? plog = null
    ) {
        var mem = new FlatMemory(4096);
        Load(mem, SuperscalarBpTests.LoopProgram);
        var train = new SuperscalarTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, predictor: predictor, pEventLog: plog
        );
        train.Run();
        DialBoardSnapshot snap = train.SnapshotPipeline();
        return (snap.Counters["cycles"], snap.Counters["branch_misses"],
                (uint)train.ArchState.IntegerRegisters.Read(2));
    }

    private static (DialBoardSnapshot Snap, PEventLog Plog, SuperscalarTrain Train) RunProgram(
        uint[] program,
        int issueWidth = 4,
        FuLatencyConfig? fuLatency = null
    ) {
        var mem = new FlatMemory(4096);
        Load(mem, program);
        var plog = new PEventLog();
        var train = new SuperscalarTrain(
            new Rv32Mechanism(), mem, issueWidth: issueWidth, fuLatency: fuLatency, pEventLog: plog
        );
        train.Run();
        return (train.SnapshotPipeline(), plog, train);
    }

    // ── Branch prediction ──────────────────────────────────────────────────────

    [Fact]
    public void DefaultPredictor_IsAlwaysNotTaken_TakenBranchesPayRefill() {
        (long cycles, long misses, uint x2) = RunLoop(null);
        Assert.Equal(200u, x2);
        // The backward branch is taken 199 times, each a mispredict under the
        // always-not-taken default; the final not-taken exit predicts correctly.
        Assert.Equal(199, misses);
        // Each iteration pays the frontend refill: ≥ 2 cycles per iteration.
        Assert.True(cycles >= 2 * 199, $"cycles {cycles}");
    }

    [Fact]
    public void Predictor_LearnsLoop_GroupsSpanTheBranch() {
        (long cyclesNotTaken, _, _) = RunLoop(null);
        (long cyclesNBit, long misses, uint x2) = RunLoop(new NBitPredictor());

        Assert.Equal(200u, x2); // architectural result unchanged
        // A trained predicted-taken branch keeps the frontend on the loop path: no refill
        // bubbles, and 4-wide groups span iterations.
        Assert.True(
            cyclesNBit < cyclesNotTaken / 2,
            $"nbit {cyclesNBit} should beat not-taken {cyclesNotTaken} decisively"
        );
        // The 2-bit counter warms up and mispredicts once more at loop exit.
        Assert.InRange(misses, 1, 5);
    }

    // ── Scoreboard and structural timing ───────────────────────────────────────

    [Fact]
    public void DependentChain_IssuesOnePerCycle() {
        // 32 chained addis on x1: RAW interlock limits issue to one per cycle even at
        // width 4 (a 1-cycle producer feeds a consumer issuing the next cycle).
        var program = new uint[34];
        program[0] = Addi(1, 0, 1);
        for (var i = 1; i < 33; i++) program[i] = Addi(1, 1, 1);
        program[33] = SuperscalarBpTests.Ebreak;

        (DialBoardSnapshot snap, _, SuperscalarTrain train) = RunProgram(program);
        Assert.Equal(33u, (uint)train.ArchState.IntegerRegisters.Read(1));
        // 33 dependent instructions ≈ 33 issue cycles + frontend fill; well below width×speedup.
        Assert.InRange(snap.Counters["cycles"], 33, 40);
    }

    [Fact]
    public void IndependentAlu_IssuesFullWidth() {
        // 40 independent addis at width 4 with 4 ALU ports → ~10 issue cycles.
        var program = new uint[41];
        for (var i = 0; i < 40; i++) program[i] = Addi(1 + i % 8, 0, i);
        program[40] = SuperscalarBpTests.Ebreak;

        (DialBoardSnapshot snap, _, _) = RunProgram(
            program, fuLatency: new FuLatencyConfig(4)
        );
        Assert.InRange(snap.Counters["cycles"], 10, 18);
    }

    [Fact]
    public void MulDivPort_LimitsIssueToOnePerCycle() {
        // 16 independent muls: MulDivCount = 1 makes the single multiplier port the
        // bottleneck regardless of the 4-wide issue width.
        var program = new uint[19];
        program[0] = Addi(1, 0, 7);
        program[1] = Addi(2, 0, 9);
        for (var i = 0; i < 16; i++)
            program[2 + i] = (uint)((0b0000001 << 25) | (2 << 20) | (1 << 15) | (0b000 << 12)
                                  | ((3 + i % 8) << 7) | 0b0110011); // mul x{3+i%8}, x1, x2
        program[18] = SuperscalarBpTests.Ebreak;

        (DialBoardSnapshot snap, _, _) = RunProgram(program);
        Assert.True(snap.Counters["cycles"] >= 16, $"cycles {snap.Counters["cycles"]}");
    }

    [Fact]
    public void LoadUse_WaitsForLoadLatency() {
        // lw x2, 0(x1) with LoadHitLatency 3, then a dependent add: the consumer's issue
        // cycle must trail the load's by exactly the load latency (stall-on-use).
        uint[] program = [
            Addi(1, 0, 0x100), // addi x1, x0, 0x100
            0x0000A103,        // lw x2, 0(x1)
            0x002101B3,        // add x3, x2, x2
            SuperscalarBpTests.Ebreak,
        ];

        (_, PEventLog plog, _) = RunProgram(program, fuLatency: new FuLatencyConfig(LoadHitLatency: 3));
        long loadIssue = plog.Events.Single(e => e is { Pc: 0x04, Kind: PEventKind.Execute, }).Cycle;
        long addIssue = plog.Events.Single(e => e is { Pc: 0x08, Kind: PEventKind.Execute, }).Cycle;
        Assert.Equal(3, addIssue - loadIssue);
    }

    // ── PEvents and integration ────────────────────────────────────────────────

    [Fact]
    public void PEventLog_RecordsLifecycle_AndWrongPathFlushes() {
        var plog = new PEventLog();
        (_, _, uint x2) = RunLoop(new NBitPredictor(), plog);
        Assert.Equal(200u, x2);

        // Every correct-path instruction carries Fetch → Execute → Retire.
        List<IGrouping<ulong, PEvent>> retired = plog.Events
                                                     .GroupBy(e => e.InstrId)
                                                     .Where(g => g.Any(e => e.Kind == PEventKind.Retire))
                                                     .ToList();
        Assert.Equal(602, retired.Count); // 1 setup + 200×3 loop + 1 ebreak
        foreach (IGrouping<ulong, PEvent> g in retired.Take(50)) {
            Assert.Contains(g, e => e.Kind == PEventKind.Fetch);
            Assert.Contains(g, e => e.Kind == PEventKind.Execute);
        }

        // Speculative fetch produces wrong-path instructions that flush without retiring.
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Flush);
    }

    [Fact]
    public void Trace_SuperscalarConfig_ProducesEvents() {
        var bytes = new byte[SuperscalarBpTests.LoopProgram.Length * 4];
        for (var i = 0; i < SuperscalarBpTests.LoopProgram.Length; i++) {
            bytes[i * 4 + 0] = (byte)SuperscalarBpTests.LoopProgram[i];
            bytes[i * 4 + 1] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 8);
            bytes[i * 4 + 2] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 16);
            bytes[i * 4 + 3] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 24);
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
        var bytes = new byte[SuperscalarBpTests.LoopProgram.Length * 4];
        for (var i = 0; i < SuperscalarBpTests.LoopProgram.Length; i++) {
            bytes[i * 4 + 0] = (byte)SuperscalarBpTests.LoopProgram[i];
            bytes[i * 4 + 1] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 8);
            bytes[i * 4 + 2] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 16);
            bytes[i * 4 + 3] = (byte)(SuperscalarBpTests.LoopProgram[i] >> 24);
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