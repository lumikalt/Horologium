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
///     shared counter) and for
///     <see cref="Experiment.RunMulticore" />, which assembles a multi-hart run's
///     per-hart <c>RevolutionResult</c>s plus coherent/shared-cache stats into an
///     <see cref="ExperimentResult" /> Face's existing chart/table rendering
///     already knows how to display.
/// </summary>
public class ExperimentMulticoreTests {
    private const ulong CounterAddress = 0x100;

    private const int IterationsPerHart = 20;

    // x5 = 0x100 (shared counter address); x6 = 20 (iterations); retry: lr.w x1,(x5); addi x1,x1,1;
    // sc.w x2,x1,(x5); bne x2,x0,retry; addi x6,x6,-1; bne x6,x0,retry; ebreak.
    // No stack, no function calls — only x1/x2/x5/x6 and one shared memory word, so it's safe to
    // run identically on every hart (real benchmark ELFs are NOT
    // safe to share across harts — they'd all write the same crt0 stack-top address into sp).
    private static readonly uint[] DemoWords = [
        0x10000293, 0x01400313, 0x1002A0AF, 0x00108093, 0x1812A12F,
        0xFE011AE3, 0xFFF30313, 0xFE0316E3, 0x00100073,
    ];

    private static byte[] EncodeDemo() {
        var bytes = new byte[ExperimentMulticoreTests.DemoWords.Length * 4];
        for (var i = 0; i < ExperimentMulticoreTests.DemoWords.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), ExperimentMulticoreTests.DemoWords[i]);
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
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem);
        train.Run(10_000);
        Assert.Equal(
            (ulong)ExperimentMulticoreTests.IterationsPerHart, mem.Read(ExperimentMulticoreTests.CounterAddress, 4)
        );
    }

    // ── Multi-hart via MulticoreSpec directly (no Experiment.RunMulticore yet) ──────────────────

    [Fact]
    public void DemoProgram_TwoHarts_NoCache_CounterEqualsTwentyTimesHartCount() {
        FlatMemory mem = SharedMem();
        var reservationTable = new ReservationTable();

        new MulticoreSpec(
            [
                new HartSpec(new SingleCycleSpec(), Mech(0)),
                new HartSpec(new SingleCycleSpec(), Mech(1)),
            ],
            ReservationTable: reservationTable
        ).Build(mem).Run(10_000);

        Assert.Equal(
            (ulong)(ExperimentMulticoreTests.IterationsPerHart * 2),
            mem.Read(ExperimentMulticoreTests.CounterAddress, 4)
        );
        return;

        Func<IMechanism> Mech(int hartId) =>
            () => new Rv32Mechanism(reservationTable: reservationTable, hartId: hartId);
    }

    [Fact]
    public void DemoProgram_ThreeHarts_FiveStageAllWithSameCache_CounterEqualsTwentyTimesHartCount() {
        // Regression coverage for two real bugs found while verifying this exact configuration:
        // (1) MulticoreSpec.Build never wired ReservationTable into MoesifBus/DirectoryBus, so a
        // write a peer's private cache absorbed (Modified state, never reaching
        // ReservationAwareMemory) failed to invalidate another hart's reservation; (2) MoesifCache
        // is write-back, and Run() (unlike RunConcurrent's per-tick DeferredBus.Drain) never
        // flushed dirty lines to backing, so the final direct-backing read could observe a stale
        // value even though every hart's SC sequence completed correctly.
        FlatMemory mem = SharedMem();
        var reservationTable = new ReservationTable();
        var l1Spec = new CacheLevelSpec(4096);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));

        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new FiveStageSpec(), Mech(0), Cache: cacheSpec),
                new HartSpec(new FiveStageSpec(), Mech(1), Cache: cacheSpec),
                new HartSpec(new FiveStageSpec(), Mech(2), Cache: cacheSpec),
            ],
            ReservationTable: reservationTable
        ).Build(mem);
        handle.Run(100_000);
        handle.FlushAllToBacking();

        Assert.Equal(
            (ulong)(ExperimentMulticoreTests.IterationsPerHart * 3),
            mem.Read(ExperimentMulticoreTests.CounterAddress, 4)
        );
        return;

        Func<IMechanism> Mech(int hartId) =>
            () => new Rv32Mechanism(reservationTable: reservationTable, hartId: hartId);
    }

    [Fact]
    public void DemoProgram_TwoHarts_FiveStageOneWithCache_CounterEqualsTwentyTimesHartCount() {
        // Regression coverage for the uncached-hart coherence hole: a hart with no private cache
        // used to wire straight to the bus's shared backing, so a peer's dirty (Modified) cached
        // line was invisible to it — reads saw stale data and writes silently clobbered the peer's
        // copy without invalidating it. Fixed via BusCoherentMemory routing every uncached access
        // through the bus's snoop paths (BusSyncToBacking / BusReadInvalidate).
        FlatMemory mem = SharedMem();
        var reservationTable = new ReservationTable();
        var l1Spec = new CacheLevelSpec(4096);
        CacheHierarchySpec cacheSpec = CacheHierarchySpec.Unified(new CachePathSpec([l1Spec,]));

        MulticoreHandle handle = new MulticoreSpec(
            [
                new HartSpec(new FiveStageSpec(), Mech(0), Cache: cacheSpec),
                new HartSpec(new FiveStageSpec(), Mech(1)),
            ],
            ReservationTable: reservationTable
        ).Build(mem);
        handle.Run(100_000);
        handle.FlushAllToBacking();

        Assert.Equal(
            (ulong)(ExperimentMulticoreTests.IterationsPerHart * 2),
            mem.Read(ExperimentMulticoreTests.CounterAddress, 4)
        );
        return;

        Func<IMechanism> Mech(int hartId) =>
            () => new Rv32Mechanism(reservationTable: reservationTable, hartId: hartId);
    }

    // ── Experiment.RunMulticore ──────────────────────────────────────────────────

    [Fact]
    public void RunMulticore_TwoHarts_NoCache_CounterEqualsTwentyTimesHartCount() {
        ExperimentResult result = Experiment.RunMulticore(
            [(new TrainConfig(), null, 0), (new TrainConfig(), null, 0),]
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
            Assert.Equal(ExperimentMulticoreTests.IterationsPerHart * 2, counter);
        }
    }

    [Fact]
    public void RunMulticore_WithPrivateCacheAndSharedLlc_StatsAppearPerHartAndAsSharedRow() {
        var privateCache = new CacheLevelSpec(1024, 2, 64, 2);
        var sharedLlc = new CacheLevelSpec(4096, 4, 64);

        ExperimentResult result = Experiment.RunMulticore(
            [(new TrainConfig(), privateCache, 0), (new TrainConfig(), privateCache, 0),],
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

        // The independent correctness signal, not just "stats exist": both harts' coherent
        // caches are write-back, so without RunMulticore flushing dirty lines to backing before
        // returning, the final counter can read stale (a real bug this exact configuration hit —
        // see the MulticoreSpec.Build history).
        long counter = result.Runs[0].Result.Find("multicore.demo")!.Counters["shared_counter"];
        Assert.Equal(ExperimentMulticoreTests.IterationsPerHart * 2, counter);
    }

    // ── Heterogeneous cache population: one hart cached, one not ────────────────────────────────

    [Fact]
    public void RunMulticore_OneHartCachedOneUncached_CounterEqualsFortyOnBothPaths() {
        var privateCache = new CacheLevelSpec(4096);

        ExperimentResult result = Experiment.RunMulticore(
            [(new TrainConfig(), privateCache, 0), (new TrainConfig(), null, 0),]
        );

        long counter = result.Runs[0].Result.Find("multicore.demo")!.Counters["shared_counter"];
        Assert.Equal(ExperimentMulticoreTests.IterationsPerHart * 2, counter);
    }

    // ── Multi-pool: independent memory/coherence domains ────────────────────────────────────────

    [Fact]
    public void RunMulticore_TwoPools_TwoHartsEach_CountersIndependentlyReachForty() {
        // Deliberately the same demo image (same 0x100 counter address) in both pools: if pool
        // isolation were broken (e.g. one ReservationTable or one backing accidentally reused
        // across pools), one pool's count would run ahead of the other's or exceed 40 outright,
        // instead of each independently landing on exactly IterationsPerHart * 2.
        ExperimentResult result = Experiment.RunMulticore(
            [
                (new TrainConfig(), null, 0), (new TrainConfig(), null, 0),
                (new TrainConfig(), null, 1), (new TrainConfig(), null, 1),
            ]
        );

        Assert.Equal(4, result.Runs.Count);
        foreach (RunRecord run in result.Runs) {
            long counter = run.Result.Find("multicore.demo")!.Counters["shared_counter"];
            Assert.Equal(ExperimentMulticoreTests.IterationsPerHart * 2, counter);
        }
    }
}