#region

using System.Text.Json;
using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;
using RiscV64;
using OooTrain = global::Pipeline.OooTrain;
using PipelineSpec = global::Pipeline.Spec.PipelineSpec;
using OutOfOrderSpec = global::Pipeline.Spec.OutOfOrderSpec;

#endregion

namespace Tests.RiscV32.Analysis;

public class ExperimentTests {
    // ── Rv32ElfWorkload ───────────────────────────────────────────────────────────

    private static string TestElfPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test.elf");
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
    public void BranchPredictorConfig_NBit_OneBit_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.NBit(1, 512);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<NBitConfig>(result);
        Assert.Equal(1, typed.Bits);
        Assert.Equal(512, typed.TableSize);
    }

    [Fact]
    public void BranchPredictorConfig_NBit_TwoBit_RoundTrip() {
        BranchPredictorConfig cfg = BranchPredictorConfig.NBit(2, 256);
        string json = JsonSerializer.Serialize(cfg);
        var result = JsonSerializer.Deserialize<BranchPredictorConfig>(json);
        var typed = Assert.IsType<NBitConfig>(result);
        Assert.Equal(2, typed.Bits);
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
        var cfg = new TrainConfig(ForwardingEnabled: false, Predictor: BranchPredictorConfig.NBit(2, 512));
        string json = cfg.ToJson();
        TrainConfig result = TrainConfig.FromJson(json);
        Assert.False(result.ForwardingEnabled);
        var pred = Assert.IsType<NBitConfig>(result.Predictor);
        Assert.Equal(2, pred.Bits);
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
    public void TrainConfig_DPrefetcherMlopString_ReachesMlopPrefetcherInstance() {
        // Regression guard for the "mlop" string → PrefetcherKind.Mlop → MlopPrefetcher wiring
        // added across TrainConfig/MemoryConfig/AssemblerViewModel — a typo in any of those three
        // hand-edited switches would silently fall through to PrefetcherKind.None and no unit test
        // on MlopPrefetcher itself would ever catch it, since none of them go through this string path.
        var cfg = new TrainConfig(DCache: new CacheHardwareConfig(4096, 2, 32, 8), DPrefetcher: "mlop");
        MemoryConfig dMem = cfg.ToDMemoryConfig();
        Assert.Equal(PrefetcherKind.Mlop, dMem.Prefetcher);

        var dLayers = MemoryLayers.Build(new FlatMemory(0x10000), dMem);
        Assert.IsType<MlopPrefetcher>(dLayers.Prefetcher);
    }

    [Fact]
    public void TrainConfig_L2CompressionBdi_ReachesBdiCacheInstance() {
        // Regression guard for the CacheHardwareConfig.Compression -> MemoryConfig.L2Compression ->
        // BdiCache wiring in MemoryLayers.Build: a config-level typo or a dropped field would
        // silently fall back to a plain SetAssociativeCache, and no BdiCache/BdiCompressor unit
        // test would ever catch it, since none of them go through this config path.
        var cfg = new TrainConfig(
            DCache: new CacheHardwareConfig(4096, 2, 32, 8),
            L2Cache: new CacheHardwareConfig(65536, 2, 32, 20, Compression: CompressionKind.Bdi)
        );
        MemoryConfig dMem = cfg.ToDMemoryConfig();
        Assert.Equal(CompressionKind.Bdi, dMem.L2Compression);

        var dLayers = MemoryLayers.Build(new FlatMemory(0x20000), dMem);
        Assert.Null(dLayers.L2Cache); // compressed level is not a SetAssociativeCache
        Assert.NotNull(dLayers.L2Bdi);
    }

    [Fact]
    public void TrainConfig_EnableSttExpOnly_ReachesOooTrainSttMachinery() {
        // Regression guard for TrainConfig.EnableSttExpOnly -> OutOfOrderSpec.EnableSttExpOnly ->
        // OooTrain(enableSttExpOnly:) wiring: a dropped parameter anywhere along that chain would
        // silently build a plain OooTrain with the defense never active, and no OooTrain-direct
        // test (SttExpOnlyTests) goes through this config/spec path, so none of them would catch it.
        var cfg = new TrainConfig(Pipeline: "ooo", EnableSttExpOnly: true);
        var mech = new Rv32Mechanism();
        var mem = new FlatMemory(0x10000);
        uint[] program = [0x00100073]; // ebreak
        var bytes = new byte[program.Length * 4];
        for (var i = 0; i < program.Length; i++) {
            bytes[i * 4 + 0] = (byte)program[i];
            bytes[i * 4 + 1] = (byte)(program[i] >> 8);
            bytes[i * 4 + 2] = (byte)(program[i] >> 16);
            bytes[i * 4 + 3] = (byte)(program[i] >> 24);
        }

        var workload = new ByteArrayWorkload(bytes, entryPoint: 0);
        workload.Load(mem);

        PipelineSpec spec = cfg.ToPipelineSpec(mech, workload);
        Assert.IsType<OutOfOrderSpec>(spec);
        ISteppableTrain train = spec.Build(mech, mem, workload.EntryPoint, cfg.ToIMemoryConfig(), cfg.ToDMemoryConfig());
        var ooo = Assert.IsType<OooTrain>(train);

        RevolutionResult result = ooo.Run();
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.ContainsKey("stt_load_issue_stalls"),
            "the stt_load_issue_stalls counter is only registered when enableSttExpOnly reaches OooTrain"
        );
    }

    [Fact]
    public void TrainConfig_EnableInvisiSpec_ReachesOooTrainInvisiSpecMachinery() {
        // Regression guard for TrainConfig.EnableInvisiSpec -> OutOfOrderSpec.EnableInvisiSpec ->
        // OooTrain(enableInvisiSpec:) wiring, mirroring TrainConfig_EnableSttExpOnly_... above —
        // no OooTrain-direct test (InvisiSpecTests) goes through this config/spec path.
        var cfg = new TrainConfig(Pipeline: "ooo", EnableInvisiSpec: true);
        var mech = new Rv32Mechanism();
        var mem = new FlatMemory(0x10000);
        uint[] program = [0x00100073]; // ebreak
        var bytes = new byte[program.Length * 4];
        for (var i = 0; i < program.Length; i++) {
            bytes[i * 4 + 0] = (byte)program[i];
            bytes[i * 4 + 1] = (byte)(program[i] >> 8);
            bytes[i * 4 + 2] = (byte)(program[i] >> 16);
            bytes[i * 4 + 3] = (byte)(program[i] >> 24);
        }

        var workload = new ByteArrayWorkload(bytes, entryPoint: 0);
        workload.Load(mem);

        PipelineSpec spec = cfg.ToPipelineSpec(mech, workload);
        Assert.IsType<OutOfOrderSpec>(spec);
        ISteppableTrain train = spec.Build(mech, mem, workload.EntryPoint, cfg.ToIMemoryConfig(), cfg.ToDMemoryConfig());
        var ooo = Assert.IsType<OooTrain>(train);

        RevolutionResult result = ooo.Run();
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.ContainsKey("invisispec_exposures") && snap.Counters.ContainsKey("invisispec_validations"),
            "the invisispec_* counters are only registered when enableInvisiSpec reaches OooTrain"
        );
    }

    [Fact]
    public void TrainConfig_EnableSttImplicitBranches_ReachesOooTrainSttMachinery() {
        // Regression guard for TrainConfig.EnableSttImplicitBranches -> OutOfOrderSpec.EnableSttImplicitBranches
        // -> OooTrain(enableSttImplicitBranches:) wiring, mirroring the two guards above — no
        // OooTrain-direct test (SttImplicitBranchTests) goes through this config/spec path.
        var cfg = new TrainConfig(Pipeline: "ooo", EnableSttImplicitBranches: true);
        var mech = new Rv32Mechanism();
        var mem = new FlatMemory(0x10000);
        uint[] program = [0x00100073]; // ebreak
        var bytes = new byte[program.Length * 4];
        for (var i = 0; i < program.Length; i++) {
            bytes[i * 4 + 0] = (byte)program[i];
            bytes[i * 4 + 1] = (byte)(program[i] >> 8);
            bytes[i * 4 + 2] = (byte)(program[i] >> 16);
            bytes[i * 4 + 3] = (byte)(program[i] >> 24);
        }

        var workload = new ByteArrayWorkload(bytes, entryPoint: 0);
        workload.Load(mem);

        PipelineSpec spec = cfg.ToPipelineSpec(mech, workload);
        Assert.IsType<OutOfOrderSpec>(spec);
        ISteppableTrain train = spec.Build(mech, mem, workload.EntryPoint, cfg.ToIMemoryConfig(), cfg.ToDMemoryConfig());
        var ooo = Assert.IsType<OooTrain>(train);

        RevolutionResult result = ooo.Run();
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters.ContainsKey("stt_mispredict_deferrals"),
            "the stt_mispredict_deferrals counter is only registered when enableSttImplicitBranches reaches OooTrain"
        );
    }

    [Fact]
    public void CompressedL2_MissStallsAreActuallyDrainedIntoCycleAccounting() {
        // MemoryLayers.ConsumeAllStalls() is what every pipeline train calls each cycle to charge
        // miss latency — it originally summed only the typed Cache/L2Cache/L3Cache
        // (SetAssociativeCache?) properties, which are null whenever that level is compressed
        // (the value lives on L2Bdi/L3Bdi instead). Before wiring L2Bdi/L3Bdi into that sum, a
        // compressed level's miss latency was silently dropped from every train's cycle count —
        // the cache would still work correctly but be timed as if every access were free. No L1
        // here, so the L2 (compressed) miss latency is the only thing that can produce a stall.
        var cfg = new TrainConfig(
            L2Cache: new CacheHardwareConfig(65536, 2, 32, 25, Compression: CompressionKind.Bdi)
        );
        MemoryConfig dMem = cfg.ToDMemoryConfig();
        var dLayers = MemoryLayers.Build(new FlatMemory(0x20000), dMem);

        dLayers.Accessor.Read(0x1000, 4); // first touch of this line: a guaranteed L2 miss
        Assert.Equal(25, dLayers.ConsumeAllStalls());
    }

    [Fact]
    public void FullChain_L1PlusCompressedL2_ReadsAndWritesCorrectly() {
        // Beyond type-checking the wiring: drive an actual multi-level chain (L1 SetAssociativeCache
        // -> L2 BdiCache -> backing) through real reads/writes and confirm values still round-trip
        // and an L1 miss genuinely reaches (and fills from) the compressed L2 level.
        var cfg = new TrainConfig(
            DCache: new CacheHardwareConfig(1024, 2, 32, 5),
            L2Cache: new CacheHardwareConfig(65536, 2, 32, 20, Compression: CompressionKind.Bdi)
        );
        MemoryConfig dMem = cfg.ToDMemoryConfig();
        var dLayers = MemoryLayers.Build(new FlatMemory(0x20000), dMem);

        dLayers.Accessor.Write(0x1000, 0xCAFEF00D, 4);
        Assert.Equal(0xCAFEF00DUL, dLayers.Accessor.Read(0x1000, 4));
        Assert.True(dLayers.L2Bdi!.Hits + dLayers.L2Bdi.Misses > 0); // the compressed L2 was actually exercised
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

    // ── CacheHardwareConfig richer knobs (bank/port/sector/victim-cache/inclusion) ────

    [Fact]
    public void CacheHardwareConfig_RicherKnobs_RoundTrip() {
        var cfg = new TrainConfig(
            ICache: new CacheHardwareConfig(
                4096, 2, 64, 8,
                BankCount: 4, ReadPorts: 2, WritePorts: 1, SectorBytes: 16,
                VictimCacheEntries: 4, VictimCacheHitLatency: 3
            ),
            L2Cache: new CacheHardwareConfig(65536, InclusionPolicy: InclusionPolicyKind.Inclusive)
        );
        string json = cfg.ToJson();
        TrainConfig result = TrainConfig.FromJson(json);

        Assert.NotNull(result.ICache);
        Assert.Equal(4, result.ICache.BankCount);
        Assert.Equal(2, result.ICache.ReadPorts);
        Assert.Equal(1, result.ICache.WritePorts);
        Assert.Equal(16, result.ICache.SectorBytes);
        Assert.Equal(4, result.ICache.VictimCacheEntries);
        Assert.Equal(3, result.ICache.VictimCacheHitLatency);

        Assert.NotNull(result.L2Cache);
        Assert.Equal(InclusionPolicyKind.Inclusive, result.L2Cache.InclusionPolicy);
    }

    [Fact]
    public void CacheHardwareConfig_RicherKnobs_ThreadThroughToMemoryConfigAndCache() {
        // sectorBytes and victimCacheEntries are mutually exclusive on SetAssociativeCache itself
        // (see its ctor), so bank/ports/victim-cache go on ICache and sector size goes on DCache.
        var cfg = new TrainConfig(
            ICache: new CacheHardwareConfig(
                4096, 2, 64, 8,
                BankCount: 4, ReadPorts: 2, WritePorts: 1, VictimCacheEntries: 4, VictimCacheHitLatency: 3
            ),
            DCache: new CacheHardwareConfig(4096, 2, 64, 8, SectorBytes: 16),
            L2Cache: new CacheHardwareConfig(65536, InclusionPolicy: InclusionPolicyKind.Inclusive)
        );

        MemoryConfig iMem = cfg.ToIMemoryConfig();
        Assert.Equal(4, iMem.CacheBankCount);
        Assert.Equal(2, iMem.CacheReadPorts);
        Assert.Equal(1, iMem.CacheWritePorts);
        Assert.Equal(4, iMem.CacheVictimCacheEntries);
        Assert.Equal(3, iMem.CacheVictimCacheHitLatency);
        Assert.Equal(InclusionPolicyKind.Inclusive, iMem.L2InclusionPolicy);

        MemoryConfig dMem = cfg.ToDMemoryConfig();
        Assert.Equal(16, dMem.CacheSectorBytes);

        // One hop further: confirm the values actually reach the constructed SetAssociativeCache,
        // not just the MemoryConfig record (InclusionPolicy and VictimCacheHitLatency are private on
        // the cache, so they aren't re-asserted here — the AttachInner-driven copy-down/eviction-
        // handoff and victim-hit-latency behavior they control are already covered directly against
        // SetAssociativeCache in Tests/Orrery/CacheTests.cs).
        var iLayers = MemoryLayers.Build(new FlatMemory(0x10000), iMem);
        Assert.NotNull(iLayers.Cache);
        Assert.Equal(4, iLayers.Cache.BankCount);
        Assert.Equal(2, iLayers.Cache.ReadPorts);
        Assert.Equal(1, iLayers.Cache.WritePorts);
        Assert.Equal(4, iLayers.Cache.VictimCacheEntries);

        var dLayers = MemoryLayers.Build(new FlatMemory(0x10000), dMem);
        Assert.NotNull(dLayers.Cache);
        Assert.Equal(16, dLayers.Cache.SectorBytes);
    }

    // ── NamedConfig JSON round-trips ──────────────────────────────────────────

    [Fact]
    public void NamedConfig_SweepRoundTrip() {
        var sweep = new[] {
            new NamedConfig("ant", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            new NamedConfig("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit(2, 512))),
        };
        string json = NamedConfig.ToJson(sweep);
        IReadOnlyList<NamedConfig> result = NamedConfig.FromJson(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("ant", result[0].Name);
        Assert.IsType<AlwaysNotTakenConfig>(result[0].Config.Predictor);
        Assert.Equal("two_bit", result[1].Name);
        var pred = Assert.IsType<NBitConfig>(result[1].Config.Predictor);
        Assert.Equal(2, pred.Bits);
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

    [Fact]
    public void ElfWorkload_EntryPointMatchesElfLoader() {
        var workload = new Rv32ElfWorkload(TestElfPath);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        ulong expected = Rv32ElfLoader.LoadFile(mem, TestElfPath);
        Assert.Equal(expected, workload.EntryPoint);
    }

    [Fact]
    public void ElfWorkload_ComputesNonZeroMemorySize() {
        var workload = new Rv32ElfWorkload(TestElfPath);
        Assert.True(workload.MemorySize > 0);
    }

    [Fact]
    public void ElfWorkload_RunsViaExperiment() {
        var workload = new Rv32ElfWorkload(TestElfPath);
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());

        DialBoardSnapshot? snap = result.Runs[0].Result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters["retired"] > 0);
    }

    // ── Experiment integration ────────────────────────────────────────────────

    // A tight loop counting x1 down from 20 with a backward *conditional* loop branch.
    // The bne is taken 19× and not-taken once, so a learning predictor (2-bit) beats
    // always-not-taken; there is no unconditional jump to skew the comparison.
    // Encoding:
    //   0x01400093  addi x1, x0, 20
    //   0xFFF08093  loop: addi x1, x1, -1
    //   0xFE009EE3  bne  x1, x0, -4    (back to loop while x1 != 0)
    //   0x00100073  ebreak
    //   0x00000013  nop                (padding; keeps CodeSize == 20 bytes)
    private static byte[] MakeCountdownProgram() {
        uint[] words = [
            0x01400093,
            0xFFF08093,
            0xFE009EE3,
            0x00100073,
            0x00000013,
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
            new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit())),
        ];

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());

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
            new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit())),
        ];

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());

        long antMisses = result.Runs[0].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];
        long tbMisses = result.Runs[1].Result.Find("five_stage.pipeline")!.Counters["branch_misses"];

        Assert.True(
            tbMisses < antMisses,
            $"Expected NBit(2) ({tbMisses}) < AlwaysNotTaken ({antMisses})"
        );
    }

    // ── Experiment against a non-RiscV32 mechanism (RV64) ─────────────────────

    /// <summary>
    ///     Confirms <see cref="Experiment.Run" /> works against an <c>Rv64Mechanism</c> factory, not
    ///     just the <c>Rv32Mechanism</c> every other test in this file uses — the mechanism-agnostic
    ///     paths inside <see cref="Experiment.RunOne" /> (<c>mechanism is Rv32Mechanism</c> soft
    ///     checks for RTL-unit wrapping and kernel-only-IPC symbol lookup) should degrade gracefully
    ///     rather than break when given a different <see cref="IMechanism" /> implementation. This is
    ///     the part of Face's RV64-selector support that's actually new and
    ///     unverified elsewhere — the ISA-branching glue in Face's own
    ///     <c>MainWindowViewModel.ResolveWorkload</c>/<c>CreateMechanism</c> is a few lines of
    ///     <c>isa == "rv64" ? ... : ...</c> not covered by a dedicated test, consistent with the rest
    ///     of that view model's data-mapping code (e.g. <c>ConfigViewModel.ToNamedConfig</c>) never
    ///     being unit tested directly.
    /// </summary>
    [Fact]
    public void Experiment_Run_WorksAgainstRv64Mechanism() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv64Mechanism());

        DialBoardSnapshot? snap = result.Runs[0].Result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters["retired"] > 0);
    }

    [Fact]
    public void Experiment_Warmup_ReducesMeasuredCycles() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("test", new TrainConfig()),];

        ExperimentResult noWarmup = Experiment.Run(workload, configs, () => new Rv32Mechanism());
        ExperimentResult withWarmup = Experiment.Run(workload, configs, () => new Rv32Mechanism(), warmupTicks: 50);

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

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());
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

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());
        string md = result.ToMarkdownTable();

        Assert.Contains("|---|", md);
        Assert.Contains("baseline", md);
    }

    // ── Time-series snapshots ─────────────────────────────────────────────────

    [Fact]
    public void TimeSeries_NoInterval_IsNull() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());

        Assert.Null(result.Runs[0].Result.TimeSeries);
    }

    [Fact]
    public void TimeSeries_WithInterval_CapturesMultiplePoints() {
        var workload = new ByteArrayWorkload(MakeCountdownProgram());
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult result = Experiment.Run(
            workload, configs, () => new Rv32Mechanism(),
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
            workload, configs, () => new Rv32Mechanism(),
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
            workload, configs, () => new Rv32Mechanism(),
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

        ExperimentResult result = Experiment.Run(workload, configs, () => new Rv32Mechanism());
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
            workload, configs, () => new Rv32Mechanism(),
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

    // ── RunMany: multi-workload parallel sweeps ───────────────────────────────

    [Fact]
    public void RunMany_ProducesOneResultPerWorkload() {
        byte[] program = MakeCountdownProgram();
        (string, IWorkload)[] workloads = [
            ("alpha", new ByteArrayWorkload(program)),
            ("beta", new ByteArrayWorkload(program)),
        ];
        NamedConfig[] configs = [
            new("ant", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
            new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit())),
        ];

        IReadOnlyList<(string Label, ExperimentResult Result)> results =
            Experiment.RunMany(workloads, configs, _ => new Rv32Mechanism());

        Assert.Equal(2, results.Count);
        Assert.Equal("alpha", results[0].Label);
        Assert.Equal("beta", results[1].Label);
        foreach ((string _, ExperimentResult result) in results) {
            Assert.Equal(2, result.Runs.Count);
            Assert.Equal("ant", result.Runs[0].Name);
            Assert.Equal("two_bit", result.Runs[1].Name);
            foreach (RunRecord run in result.Runs)
                Assert.True(run.Result.Find("five_stage.pipeline")!.Counters["retired"] > 0);
        }
    }

    [Fact]
    public void RunMany_MatchesIndependentRunCalls() {
        byte[] program = MakeCountdownProgram();
        var workload = new ByteArrayWorkload(program);
        NamedConfig[] configs = [new("baseline", new TrainConfig()),];

        ExperimentResult single = Experiment.Run(workload, configs, () => new Rv32Mechanism());
        IReadOnlyList<(string Label, ExperimentResult Result)> multi =
            Experiment.RunMany([("w", workload),], configs, _ => new Rv32Mechanism());

        long singleRetired = single.Runs[0].Result.Find("five_stage.pipeline")!.Counters["retired"];
        long multiRetired = multi[0].Result.Runs[0].Result.Find("five_stage.pipeline")!.Counters["retired"];
        Assert.Equal(singleRetired, multiRetired);
    }

    // ── MMIO must bypass the cache (regression) ───────────────────────────────

    [Theory]
    [InlineData("ooo")]
    [InlineData("five_stage")]
    public void CachedRun_OnHtifBenchmark_Terminates(string pipeline) {
        // The HTIF tohost/fromhost registers are memory-mapped I/O. Before the
        // UncacheableMemory fix, an L1 cache held a stale fromhost (HtifMemory
        // ACKs by writing it to the backing below the cache), so printstr's poll
        // loop spun to maxTicks. With the fix the run terminates at its true
        // length (towers ≈ 10k instructions).
        var workload = new Rv32ElfWorkload(
            Path.Combine(AppContext.BaseDirectory, "benchmarks", "towers.elf"), 4 * 1024 * 1024
        );
        var config = new NamedConfig(
            "cached",
            new TrainConfig(
                pipeline,
                Predictor: BranchPredictorConfig.NBit(),
                DCache: new CacheHardwareConfig(16384, 4, 64)
            )
        );
        const long maxTicks = 400_000;

        ExperimentResult result = Experiment.Run(
            workload, [config,], () => new Rv32Mechanism(workload.HtifTohostAddress), maxTicks
        );

        RevolutionResult r = result.Runs[0].Result;
        long retired = r.Find($"{pipeline}.pipeline")!.Counters["retired"];
        Assert.True(r.TotalTicks < maxTicks, $"{pipeline} hit maxTicks ({r.TotalTicks}) — MMIO not bypassing cache");
        // OOO: kernel-only count (setStats window); other pipelines: full-run count.
        long retiredLo = pipeline == "ooo" ? 500L : 9_000L;
        long retiredHi = pipeline == "ooo" ? 10_000L : 20_000L;
        Assert.InRange(retired, retiredLo, retiredHi);
    }
}