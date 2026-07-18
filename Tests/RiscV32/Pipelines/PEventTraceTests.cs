using Mechanism;
using Orrery.Observation;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     <see cref="Experiment.Trace" /> coverage: every single-hart train records a PEvent
///     lifecycle usable by the Face waterfall — Fetch/Execute/Retire per correct-path
///     instruction, plus Flush markers where the train speculates.
/// </summary>
public class PEventTraceTests {
    // Countdown loop (mispredicts under always-not-taken defaults) + ebreak.
    private static readonly uint[] LoopProgram = [
        0x0C800093, // addi x1, x0, 200
        0x00110113, // addi x2, x2, 1
        0xFFF08093, // addi x1, x1, -1
        0xFE009CE3, // bne x1, x0, -8
        0x00100073, // ebreak
    ];

    private static ByteArrayWorkload Workload() {
        var bytes = new byte[PEventTraceTests.LoopProgram.Length * 4];
        for (var i = 0; i < PEventTraceTests.LoopProgram.Length; i++) {
            bytes[i * 4 + 0] = (byte)PEventTraceTests.LoopProgram[i];
            bytes[i * 4 + 1] = (byte)(PEventTraceTests.LoopProgram[i] >> 8);
            bytes[i * 4 + 2] = (byte)(PEventTraceTests.LoopProgram[i] >> 16);
            bytes[i * 4 + 3] = (byte)(PEventTraceTests.LoopProgram[i] >> 24);
        }

        return new ByteArrayWorkload(bytes);
    }

    [Theory]
    [InlineData("five_stage")]
    [InlineData("superscalar")]
    [InlineData("ooo")]
    [InlineData("cpr")]
    [InlineData("dae")]
    public void Trace_RecordsLifecycleEvents(string pipeline) {
        var config = new NamedConfig(pipeline, new TrainConfig(pipeline, IssueWidth: 2));
        PEventLog plog = Experiment.Trace(Workload(), config, new Rv32Mechanism(), 20_000);

        Assert.NotEmpty(plog.Events);
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Fetch);
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Execute);
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Retire);

        // Retired instructions carry a full lifecycle: their Fetch precedes (or shares)
        // their Retire cycle, under one stable InstrId.
        List<IGrouping<ulong, PEvent>> retired = plog.Events
                                                     .GroupBy(e => e.InstrId)
                                                     .Where(g => g.Any(e => e.Kind == PEventKind.Retire))
                                                     .ToList();
        Assert.True(retired.Count >= 600, $"{pipeline}: {retired.Count} retired instructions traced");
        foreach (IGrouping<ulong, PEvent> g in retired.Take(30)) {
            long fetch = g.First(e => e.Kind == PEventKind.Fetch).Cycle;
            long retire = g.Where(e => e.Kind == PEventKind.Retire).Max(e => e.Cycle);
            Assert.True(fetch <= retire);
        }
    }

    [Theory]
    [InlineData("superscalar")]
    [InlineData("ooo")]
    [InlineData("cpr")]
    public void Trace_SpeculativeTrains_RecordWrongPathFlushes(string pipeline) {
        // The taken back-edge mispredicts every iteration under the always-not-taken
        // default, so speculatively fetched wrong-path instructions must flush.
        var config = new NamedConfig(pipeline, new TrainConfig(pipeline, IssueWidth: 2));
        PEventLog plog = Experiment.Trace(Workload(), config, new Rv32Mechanism(), 20_000);
        Assert.Contains(plog.Events, e => e.Kind == PEventKind.Flush);
    }
}