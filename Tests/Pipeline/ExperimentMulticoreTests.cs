#region

using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Spec;
using Pipeline;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Tests for the Face multi-hart demo program (a hand-encoded LR/SC atomic-increment loop on a
///     shared counter — see TODO.md's "multi-hart GUI support" entry) and for
///     <see cref="RiscV32.Analysis.Experiment.RunMulticore" />, which assembles a multi-hart run's
///     per-hart <c>RevolutionResult</c>s plus coherent/shared-cache stats into an
///     <see cref="RiscV32.Analysis.ExperimentResult" /> Face's existing chart/table rendering
///     already knows how to display.
/// </summary>
public class ExperimentMulticoreTests {
    // x5 = 0x100 (shared counter address); x6 = 20 (iterations); retry: lr.w x1,(x5); addi x1,x1,1;
    // sc.w x2,x1,(x5); bne x2,x0,retry; addi x6,x6,-1; bne x6,x0,retry; ebreak.
    // No stack, no function calls — only x1/x2/x5/x6 and one shared memory word, so it's safe to
    // run identically on every hart (see TODO.md for why that matters: real benchmark ELFs are NOT
    // safe to share across harts — they'd all write the same crt0 stack-top address into sp).
    private static readonly uint[] DemoWords = [
        0x10000293, 0x01400313, 0x1002A0AF, 0x00108093, 0x1812A12F,
        0xFE011AE3, 0xFFF30313, 0xFE0316E3, 0x00100073,
    ];

    private const ulong CounterAddress = 0x100;
    private const int IterationsPerHart = 20;

    private static byte[] EncodeDemo() {
        var bytes = new byte[DemoWords.Length * 4];
        for (var i = 0; i < DemoWords.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), DemoWords[i]);
        return bytes;
    }

    private static FlatMemory SharedMem(int sizeBytes = 0x1000) {
        var mem = new FlatMemory(sizeBytes);
        mem.Load(0x00, EncodeDemo());
        return mem;
    }

    // ── Smoke check: the hand-encoded program itself, single-hart, no multi-hart machinery ──────

    [Fact]
    public void DemoProgram_SingleHart_CounterReachesIterationCount() {
        FlatMemory mem = SharedMem();
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, 0);
        train.Run(10_000);
        Assert.Equal((ulong)IterationsPerHart, mem.Read(ExperimentMulticoreTests.CounterAddress, 4));
    }

    // ── Multi-hart via MulticoreSpec directly (no Experiment.RunMulticore yet) ──────────────────

    [Fact]
    public void DemoProgram_TwoHarts_NoCache_CounterEqualsTwentyTimesHartCount() {
        FlatMemory mem = SharedMem();
        var reservationTable = new ReservationTable();
        Func<IMechanism> mech(int hartId) => () => new Rv32Mechanism(reservationTable: reservationTable, hartId: hartId);

        new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), mech(0)),
                new HartSpec(new SingleCycleSpec(), mech(1)),
            ],
            ReservationTable: reservationTable
        ).Build(mem).Run(10_000);

        Assert.Equal((ulong)(IterationsPerHart * 2), mem.Read(ExperimentMulticoreTests.CounterAddress, 4));
    }

    // ── Experiment.RunMulticore ──────────────────────────────────────────────────

    [Fact]
    public void RunMulticore_TwoHarts_NoCache_CounterEqualsTwentyTimesHartCount() {
        ExperimentResult result = Experiment.RunMulticore(
            [(new TrainConfig(), null), (new TrainConfig(), null),]
        );

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("hart0", result.Runs[0].Name);
        Assert.Equal("hart1", result.Runs[1].Name);

        foreach (RunRecord run in result.Runs) {
            // Ordinary Train snapshots pass through untouched, not just the synthetic ones.
            DialBoardSnapshot? pipelineSnap = run.Result.Find("five_stage.pipeline");
            Assert.NotNull(pipelineSnap);
            Assert.True(pipelineSnap.Counters["retired"] > 0);

            long counter = run.Result.Find("multicore.demo")!.Counters["shared_counter"];
            Assert.Equal(IterationsPerHart * 2, counter);
        }
    }

    [Fact]
    public void RunMulticore_WithPrivateCacheAndSharedLlc_StatsAppearPerHartAndAsSharedRow() {
        var privateCache = new CacheLevelSpec(1024, 2, 64, 2);
        var sharedLlc = new CacheLevelSpec(4096, 4, 64, 10);

        ExperimentResult result = Experiment.RunMulticore(
            [(new TrainConfig(), privateCache), (new TrainConfig(), privateCache),],
            sharedLlc
        );

        // 2 hart rows + 1 shared_llc row.
        Assert.Equal(3, result.Runs.Count);

        foreach (RunRecord run in result.Runs.Where(r => r.Name.StartsWith("hart"))) {
            DialBoardSnapshot? cacheSnap = run.Result.Find("multicore.coherent_cache");
            Assert.NotNull(cacheSnap);
            Assert.True(cacheSnap.Counters["misses"] > 0);
        }

        RunRecord llcRun = result.Runs.Single(r => r.Name == "shared_llc");
        DialBoardSnapshot? llcSnap = llcRun.Result.Find("multicore.shared_llc");
        Assert.NotNull(llcSnap);
        Assert.True(llcSnap.Counters["hits"] + llcSnap.Counters["misses"] > 0);
    }
}
