using System.Text.Json;
using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
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
        var cfg = new TrainConfig(
            false,
            BranchPredictorConfig.TwoBit(512)
        );
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

    // ── Experiment integration ────────────────────────────────────────────────

    // A tight loop: x1 = 10, loop: beq x1,x0 → exit; addi x1,x1,-1; j loop; ebreak
    // Encoding:
    //   0x00A00093  addi x1, x0, 10
    //   0x00008463  beq  x1, x0, +8     (branch to ebreak at offset 12 when x1==0)
    //   0xFFF08093  addi x1, x1, -1
    //   0xFF9FF06F  jal  x0, -8         (back to beq)
    //   0x00100073  ebreak
    private static byte[] MakeCountdownProgram() {
        uint[] words = [
            0x00A00093,
            0x00008463,
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
        var configs = new[] {
            ("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            ("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
        };

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("always_not_taken", result.Runs[0].Name);
        Assert.Equal("two_bit", result.Runs[1].Name);

        // Both runs should retire instructions
        foreach (RunRecord run in result.Runs) {
            DialBoardSnapshot? snap = run.Result.Find("five_stage.pipeline");
            Assert.NotNull(snap);
            Assert.True(snap.Counters["retired"] > 0);
        }
    }

    [Fact]
    public void Experiment_TwoBit_HasFewerMisses_ThanAlwaysNotTaken() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        var configs = new[] {
            ("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            ("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
        };

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

        long antMisses = result.Runs[0].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];
        long tbMisses = result.Runs[1].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];

        // Loop body is taken 10 times + final not-taken exit.
        // ANT: misses all 10 taken iterations. TwoBit: learns quickly, fewer misses.
        Assert.True(
            tbMisses < antMisses,
            $"Expected TwoBit ({tbMisses}) < AlwaysNotTaken ({antMisses})"
        );
    }

    [Fact]
    public void ExperimentResult_ToCsv_ContainsAllRunNames() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        var configs = new[] {
            ("config_a", new TrainConfig()),
            ("config_b", new TrainConfig(false)),
        };

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());
        string csv = result.ToCsv();

        Assert.Contains("config_a", csv);
        Assert.Contains("config_b", csv);
        Assert.Contains("retired", csv);
        Assert.Contains("cycles", csv);
    }

    [Fact]
    public void ExperimentResult_ToMarkdownTable_ContainsHeaderRow() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        var configs = new[] {
            ("baseline", new TrainConfig()),
        };

        ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());
        string md = result.ToMarkdownTable();

        // Markdown tables have a separator row with |---|
        Assert.Contains("|---|", md);
        Assert.Contains("baseline", md);
    }
}