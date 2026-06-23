using System.Text.Json;
using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using RiscV;
using RiscV.Analysis;
using RiscV.Config;
using RiscV.Memory;

namespace Tests.RiscV;

public class ExperimentTests {
    // ── BranchPredictorConfig JSON round-trips ────────────────────────────────

    [Fact]
    public void BranchPredictorConfig_AlwaysNotTaken_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.AlwaysNotTaken();
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<AlwaysNotTakenConfig>(result);
    }

    [Fact]
    public void BranchPredictorConfig_AlwaysTaken_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.AlwaysTaken();
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        Assert.IsType<AlwaysTakenConfig>(result);
    }

    [Fact]
    public void BranchPredictorConfig_OneBit_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.OneBit(512);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<OneBitConfig>(result);
        Assert.Equal(512, typed.TableSize);
    }

    [Fact]
    public void BranchPredictorConfig_TwoBit_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.TwoBit(256);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<TwoBitConfig>(result);
        Assert.Equal(256, typed.TableSize);
    }

    // ── TrainConfig JSON round-trips ──────────────────────────────────────────

    [Fact]
    public void TrainConfig_DefaultValues_RoundTrip() {
        var cfg = new TrainConfig();
        string json = cfg.ToJson();
        TrainConfig result = TrainConfig.FromJson(json);
        Assert.True(result.ForwardingEnabled);
        Assert.Null(result.Predictor);
        Assert.Null(result.ICache);
        Assert.Null(result.DCache);
    }

    [Fact]
    public void TrainConfig_WithPredictor_RoundTrip() {
        var cfg = new TrainConfig(ForwardingEnabled: false, Predictor: BranchPredictorConfig.TwoBit(512));
        string json = cfg.ToJson();
        TrainConfig result = TrainConfig.FromJson(json);
        Assert.False(result.ForwardingEnabled);
        var pred = Assert.IsType<TwoBitConfig>(result.Predictor);
        Assert.Equal(512, pred.TableSize);
    }

    [Fact]
    public void TrainConfig_WithCache_RoundTrip() {
        var cfg = new TrainConfig(
            ICache: new CacheHardwareConfig(4096, 2, 64, 5),
            DCache: new CacheHardwareConfig(8192)
        );
        string json = cfg.ToJson();
        TrainConfig result = TrainConfig.FromJson(json);
        Assert.NotNull(result.ICache);
        Assert.Equal(4096, result.ICache.CapacityBytes);
        Assert.Equal(2, result.ICache.Ways);
        Assert.Equal(64, result.ICache.BlockBytes);
        Assert.Equal(5, result.ICache.MissLatency);
        Assert.NotNull(result.DCache);
        Assert.Equal(8192, result.DCache.CapacityBytes);
    }

    [Fact]
    public void TrainConfig_ToMemoryConfig_MapsCorrectly() {
        var cfg = new TrainConfig(
            ICache: new CacheHardwareConfig(4096, 2, 64, 8),
            ITlb: new TlbHardwareConfig(16, 4096, 15)
        );
        MemoryConfig iMem = cfg.ToIMemoryConfig();
        Assert.Equal(4096, iMem.CacheCapacityBytes);
        Assert.Equal(2, iMem.CacheWays);
        Assert.Equal(64, iMem.CacheBlockBytes);
        Assert.Equal(8, iMem.CacheMissLatency);
        Assert.Equal(16, iMem.TlbEntries);
        Assert.Equal(4096, iMem.TlbPageBytes);
        Assert.Equal(15, iMem.TlbMissLatency);

        MemoryConfig dMem = cfg.ToDMemoryConfig();
        Assert.Equal(0, dMem.CacheCapacityBytes); // no DCache configured
    }

    // ── NamedConfig JSON round-trips ──────────────────────────────────────────

    [Fact]
    public void NamedConfig_SweepRoundTrip() {
        var sweep = new[] {
            new NamedConfig("ant", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            new NamedConfig("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit(512))),
        };
        string json = NamedConfig.ToJson(sweep);
        IReadOnlyList<NamedConfig> result = NamedConfig.FromJson(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("ant", result[0].Name);
        Assert.IsType<AlwaysNotTakenConfig>(result[0].Config.Predictor);
        Assert.Equal("two_bit", result[1].Name);
        var pred = Assert.IsType<TwoBitConfig>(result[1].Config.Predictor);
        Assert.Equal(512, pred.TableSize);
    }

    // ── ByteArrayWorkload ─────────────────────────────────────────────────────

    [Fact]
    public void ByteArrayWorkload_LoadsAtCorrectAddress() {
        var program = new byte[] { 0x01, 0x02, 0x03, 0x04, };
        var workload = new ByteArrayWorkload(program, 0x100, 0x100);
        var memory = new FlatMemory(workload.MemorySize);
        workload.Load(memory);
        Assert.Equal(0x04030201UL, memory.Read(0x100, 4));
    }

    [Fact]
    public void ByteArrayWorkload_DefaultEntryPointIsLoadAddress() {
        var workload = new ByteArrayWorkload(new byte[4], 0x200);
        Assert.Equal(0x200UL, workload.EntryPoint);
    }

    // ── ElfWorkload ───────────────────────────────────────────────────────────

    private static string TestElfPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test.elf");

    [Fact]
    public void ElfWorkload_EntryPointMatchesElfLoader() {
        var mem = new FlatMemory(1 << 20);
        ulong expected = ElfLoader.LoadFile(mem, TestElfPath);
        var workload = new ElfWorkload(TestElfPath);
        Assert.Equal(expected, workload.EntryPoint);
    }

    [Fact]
    public void ElfWorkload_ComputesNonZeroMemorySize() {
        var workload = new ElfWorkload(TestElfPath);
        Assert.True(workload.MemorySize > 0);
    }

    [Fact]
    public void ElfWorkload_RunsViaExperiment() {
        var workload = new ElfWorkload(TestElfPath);
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        DialBoardSnapshot? snap = result.Runs[0].Result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters["retired"] > 0);
    }

    // ── Experiment integration ────────────────────────────────────────────────

    // A tight loop: x1 = 10, loop: beq x1,x0 → exit; addi x1,x1,-1; j loop; ebreak
    // Encoding:
    //   0x00A00093  addi x1, x0, 10
    //   0x00008663  beq  x1, x0, +12   (branch to ebreak at addr 16 when x1==0)
    //   0xFFF08093  addi x1, x1, -1
    //   0xFF9FF06F  jal  x0, -8        (back to beq at addr 4)
    //   0x00100073  ebreak
    private static byte[] MakeCountdownProgram() {
        uint[] words = [
            0x00A00093,
            0x00008663,
            0xFFF08093,
            0xFF9FF06F,
            0x00100073,
        ];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        return bytes;
    }

    [Fact]
    public void Experiment_Run_ProducesResultsForEachConfig() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [
            new("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
        ];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("always_not_taken", result.Runs[0].Name);
        Assert.Equal("two_bit", result.Runs[1].Name);

        foreach (RunRecord run in result.Runs) {
            DialBoardSnapshot? snap = run.Result.Find("five_stage.pipeline");
            Assert.NotNull(snap);
            Assert.True(snap.Counters["retired"] > 0);
        }
    }

    [Fact]
    public void Experiment_TwoBit_HasFewerMisses_ThanAlwaysNotTaken() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [
            new("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
        ];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        long antMisses = result.Runs[0].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];
        long tbMisses = result.Runs[1].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];

        Assert.True(
            tbMisses < antMisses,
            $"Expected TwoBit ({tbMisses}) < AlwaysNotTaken ({antMisses})"
        );
    }

    [Fact]
    public void Experiment_Warmup_ReducesMeasuredCycles() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("test", new TrainConfig()),];

        ExperimentResult noWarmup = Experiment.Run(workload, configs, new RvMechanism());
        ExperimentResult withWarmup = Experiment.Run(workload, configs, new RvMechanism(), warmupTicks: 50);

        long totalCycles = noWarmup.Runs[0].Result.Find("five_stage.pipeline")!.Counters["cycles"];
        long measuredCycles = withWarmup.Runs[0].Result.Find("five_stage.pipeline")!.Counters["cycles"];

        Assert.True(
            measuredCycles < totalCycles,
            $"Warmup should reduce measured cycle count: {measuredCycles} >= {totalCycles}"
        );
        Assert.True(measuredCycles > 0, "Some cycles should remain after 50-tick warmup");
    }

    [Fact]
    public void ExperimentResult_ToCsv_ContainsAllRunNames() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [
            new("config_a", new TrainConfig()),
            new("config_b", new TrainConfig(ForwardingEnabled: false)),
        ];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());
        string csv = result.ToCsv();

        Assert.Contains("config_a", csv);
        Assert.Contains("config_b", csv);
        Assert.Contains("pipeline.retired", csv);
        Assert.Contains("pipeline.cycles", csv);
    }

    [Fact]
    public void ExperimentResult_ToMarkdownTable_ContainsHeaderRow() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());
        string md = result.ToMarkdownTable();

        Assert.Contains("|---|", md);
        Assert.Contains("baseline", md);
    }

    // ── Time-series snapshots ─────────────────────────────────────────────────

    [Fact]
    public void TimeSeries_NoInterval_IsNull() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        Assert.Null(result.Runs[0].Result.TimeSeries);
    }

    [Fact]
    public void TimeSeries_WithInterval_CapturesMultiplePoints() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(
            workload, configs, new RvMechanism(),
            snapshotInterval: 5
        );

        IReadOnlyList<TimeSeriesPoint>? ts = result.Runs[0].Result.TimeSeries;
        Assert.NotNull(ts);
        Assert.True(ts.Count > 1, $"Expected multiple time-series points, got {ts.Count}");
    }

    [Fact]
    public void TimeSeries_CountersAreMonotonicallyIncreasing() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(
            workload, configs, new RvMechanism(),
            snapshotInterval: 5
        );

        IReadOnlyList<TimeSeriesPoint>? ts = result.Runs[0].Result.TimeSeries!;
        for (var i = 1; i < ts.Count; i++) {
            long prev = ts[i - 1].Snapshots.Sum(s => s.Counters.GetValueOrDefault("cycles"));
            long curr = ts[i].Snapshots.Sum(s => s.Counters.GetValueOrDefault("cycles"));
            Assert.True(
                curr >= prev,
                $"cycles at point {i} ({curr}) < point {i - 1} ({prev})"
            );
        }
    }

    [Fact]
    public void TimeSeries_AutoInterval_ProducesReasonableCount() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(
            workload, configs, new RvMechanism(),
            snapshotInterval: -1
        );

        IReadOnlyList<TimeSeriesPoint>? ts = result.Runs[0].Result.TimeSeries;
        Assert.NotNull(ts);
        // Auto-interval: max(10, codeSize/200) = max(10, 20/200) = 10
        // Program runs ~60 cycles, so we expect at least 1 data point and at most a small number.
        Assert.True(ts.Count >= 1, "Expected at least one auto-interval snapshot");
    }

    [Fact]
    public void ToTimeSeriesCsv_EmptyWhenNoTimeSeries() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());
        string csv = result.ToTimeSeriesCsv();

        Assert.Equal(string.Empty, csv);
    }

    [Fact]
    public void ToTimeSeriesCsv_ContainsRunNamesAndTick() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [
            new("run_a", new TrainConfig()),
            new("run_b", new TrainConfig(ForwardingEnabled: false)),
        ];

        ExperimentResult result = Experiment.Run(
            workload, configs, new RvMechanism(),
            snapshotInterval: 5
        );
        string csv = result.ToTimeSeriesCsv();

        Assert.Contains("run_a", csv);
        Assert.Contains("run_b", csv);
        Assert.Contains("tick", csv);
        Assert.Contains("pipeline.cycles", csv);
        Assert.Contains("pipeline.retired", csv);
    }

    [Fact]
    public void IWorkload_CodeSize_ByteArray() {
        var program = new byte[20];
        var workload = new ByteArrayWorkload(program);
        Assert.Equal(20, workload.CodeSize);
    }
}