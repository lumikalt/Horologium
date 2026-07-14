using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Train;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;
using RiscV32.Trace;
using Script;

// ── Argument parsing ──────────────────────────────────────────────────────────

List<string> elfPaths = [];
string? sweepPath = null;
long warmupTicks = 0;
long maxTicks = 1_000_000;
long snapshotInterval = 0;         // 0 = off, -1 = auto, >0 = explicit ticks
var format = "md";                 // md | csv | both | ts-csv
int? memorySizeBytes = null;       // null → default to 4 MB for ELF workloads
string? traceJsonPath = null;      // --trace-json <path>: emit an Olympia JSON trace and exit
string? scriptPath = null;         // --script <file.csx>: evaluate script → MachineSpec → run
string? checkpointSavePath = null; // --checkpoint-save <path>: save arch checkpoint after run
string? checkpointLoadPath = null; // --checkpoint-load <path>: restore arch checkpoint before run
string? roiStartSymbol = null;     // --roi-start <symbol>: fast-forward to this ELF symbol, then measure
string? roiEndSymbol = null;       // --roi-end <symbol>: stop measuring when PC reaches this symbol
string? elasticRecordPath = null;  // --elastic-record <path>: record DDG trace and exit
string? elasticReplayPath = null;  // --elastic-replay <path>: replay DDG trace and print IPC
string? elasticToGem5In = null;    // --elastic-to-gem5 <in> <out>: translate HELF → gem5 inst_dep_record proto
string? elasticToGem5Out = null;
string? fetchToGem5In = null; // --fetch-to-gem5 <in> <out>: translate HELF → gem5 packet (fetch) proto
string? fetchToGem5Out = null;
string? stfRecordPath = null;     // --stf-record <path>: record STF binary trace and exit
string? champsimTracePath = null; // --champsim-trace <path>: replay a ChampSim binary trace and exit
var champsimPredictor = "n_bit";  // --champsim-predictor <name>: built-in predictor to evaluate
string? champsimCbpLib = null;    // --champsim-cbp-lib <path>: evaluate a CBP-3/5 native plugin instead
var champsimCachePolicy = "Lru";  // --champsim-cache-policy <name|none>: replacement policy to evaluate
var champsimCacheSets = 2048;     // --champsim-cache-sets <n>
var champsimCacheWays = 16;       // --champsim-cache-ways <n>
var champsimCacheBlock = 64;      // --champsim-cache-block <bytes>

for (var i = 0; i < args.Length; i++)
    switch (args[i]) {
        case "--script":    scriptPath = args[++i]; break;
        case "--sweep":     sweepPath = args[++i]; break;
        case "--warmup":    warmupTicks = long.Parse(args[++i]); break;
        case "--max-ticks": maxTicks = long.Parse(args[++i]); break;
        case "--memory":    memorySizeBytes = int.Parse(args[++i]); break;
        case "--snapshot-interval":
            snapshotInterval = args[i + 1] == "auto" ? (++i, -1L).Item2 : long.Parse(args[++i]);
            break;
        case "--format":          format = args[++i]; break;
        case "--trace-json":      traceJsonPath = args[++i]; break;
        case "--checkpoint-save": checkpointSavePath = args[++i]; break;
        case "--checkpoint-load": checkpointLoadPath = args[++i]; break;
        case "--roi-start":       roiStartSymbol = args[++i]; break;
        case "--roi-end":         roiEndSymbol = args[++i]; break;
        case "--elastic-record":  elasticRecordPath = args[++i]; break;
        case "--elastic-replay":  elasticReplayPath = args[++i]; break;
        case "--elastic-to-gem5":
            elasticToGem5In = args[++i];
            elasticToGem5Out = args[++i];
            break;
        case "--fetch-to-gem5":
            fetchToGem5In = args[++i];
            fetchToGem5Out = args[++i];
            break;
        case "--stf-record":            stfRecordPath = args[++i]; break;
        case "--champsim-trace":        champsimTracePath = args[++i]; break;
        case "--champsim-predictor":    champsimPredictor = args[++i]; break;
        case "--champsim-cbp-lib":      champsimCbpLib = args[++i]; break;
        case "--champsim-cache-policy": champsimCachePolicy = args[++i]; break;
        case "--champsim-cache-sets":   champsimCacheSets = int.Parse(args[++i]); break;
        case "--champsim-cache-ways":   champsimCacheWays = int.Parse(args[++i]); break;
        case "--champsim-cache-block":  champsimCacheBlock = int.Parse(args[++i]); break;
        case "--help" or "-h":
            PrintUsage();
            return;
        default:
            if (!args[i].StartsWith("--")) { elfPaths.Add(args[i]); }
            else {
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                PrintUsage();
                return;
            }

            break;
    }

// ── Elastic trace → gem5 Protobuf translation (standalone) ───────────────────

if (elasticToGem5In is not null) {
    await using var inFs = new FileStream(elasticToGem5In, FileMode.Open, FileAccess.Read);
    await using var outFs = new FileStream(elasticToGem5Out!, FileMode.Create, FileAccess.Write);
    long converted = Gem5ElasticTraceConverter.Convert(inFs, outFs);
    Console.Error.WriteLine($"Converted {converted:N0} records: {elasticToGem5In} → {elasticToGem5Out}");
    return;
}

if (fetchToGem5In is not null) {
    await using var inFs = new FileStream(fetchToGem5In, FileMode.Open, FileAccess.Read);
    await using var outFs = new FileStream(fetchToGem5Out!, FileMode.Create, FileAccess.Write);
    long converted = Gem5FetchTraceConverter.Convert(inFs, outFs);
    Console.Error.WriteLine($"Converted {converted:N0} fetch records: {fetchToGem5In} → {fetchToGem5Out}");
    return;
}

// ── Elastic trace replay (standalone — no workload needed) ───────────────────

if (elasticReplayPath is not null) {
    await using var fs = new FileStream(elasticReplayPath, FileMode.Open, FileAccess.Read);
    using var reader = new ElasticTraceReader(fs);
    ReplayResult replayResult = ElasticTraceReplayer.Replay(reader.ReadAll());
    Console.Error.WriteLine(
        $"Elastic replay: {replayResult.InstructionCount:N0} instructions, " +
        $"{replayResult.TotalCycles:N0} cycles (critical path), " +
        $"IPC upper bound = {replayResult.Ipc:F3}"
    );
    return;
}

// ── ChampSim trace replay (standalone — no workload needed) ──────────────────

if (champsimTracePath is not null) {
    IBranchPredictor predictor = champsimCbpLib is not null
        ? new CbpFfiPredictor(champsimCbpLib)
        : ResolveChampSimPredictor(champsimPredictor);

    SetAssociativeCache? cache = null;
    if (!string.Equals(champsimCachePolicy, "none", StringComparison.OrdinalIgnoreCase)) {
        if (!Enum.TryParse(champsimCachePolicy, true, out ReplacementPolicyKind policyKind)) {
            Console.Error.WriteLine($"Unknown --champsim-cache-policy '{champsimCachePolicy}'.");
            return;
        }

        int capacityBytes = champsimCacheSets * champsimCacheWays * champsimCacheBlock;
        cache = new SetAssociativeCache(
            new ChampSimBackingMemory(), capacityBytes, champsimCacheWays, champsimCacheBlock, 1,
            replacementPolicy: policyKind
        );
    }

    await using var champsimFs = new FileStream(champsimTracePath, FileMode.Open, FileAccess.Read);
    using var champsimReader = new ChampSimTraceReader(champsimFs);
    ChampSimReplayResult replay = ChampSimTraceReplayer.Replay(champsimReader.ReadAll(), predictor, cache);

    Console.Error.WriteLine($"ChampSim replay: {replay.Instructions:N0} instructions");
    if (replay.Branches > 0)
        Console.Error.WriteLine(
            $"  Predictor ({(champsimCbpLib is not null ? $"cbp:{champsimCbpLib}" : champsimPredictor)}) — " +
            $"{replay.Branches:N0} branches, {replay.Mispredictions:N0} mispredicts " +
            $"({replay.MispredictionRate:P2}), MPKI = {replay.Mpki:F3}"
        );
    if (cache is not null)
        Console.Error.WriteLine(
            $"  Cache ({champsimCachePolicy}, {champsimCacheSets}x{champsimCacheWays}x{champsimCacheBlock}B) — " +
            $"{replay.Loads:N0} loads, {replay.Stores:N0} stores, " +
            $"{replay.CacheHits:N0} hits, {replay.CacheMisses:N0} misses ({replay.CacheHitRate:P2} hit rate)"
        );

    return;
}

// ── Workload(s) ───────────────────────────────────────────────────────────────

List<(string Label, IWorkload Workload)> workloads;

if (elfPaths.Count > 0) {
    int memSize = memorySizeBytes ?? 4 * 1024 * 1024;
    workloads = elfPaths.Select(p => (Path.GetFileName(p), (IWorkload)new Rv32ElfWorkload(p, memSize))).ToList();
}
else {
    // Built-in demo: 100-iteration countdown loop
    //   addi  x1, x0, 100
    //   beq   x1, x0, +12    ← exit when x1 == 0
    //   addi  x1, x1, -1
    //   jal   x0, -8          ← back to beq
    //   ebreak
    uint[] words = [0x06400093, 0x00008663, 0xFFF08093, 0xFF9FF06F, 0x00100073,];
    var bytes = new byte[words.Length * 4];
    for (var i = 0; i < words.Length; i++) {
        bytes[i * 4 + 0] = (byte)words[i];
        bytes[i * 4 + 1] = (byte)(words[i] >> 8);
        bytes[i * 4 + 2] = (byte)(words[i] >> 16);
        bytes[i * 4 + 3] = (byte)(words[i] >> 24);
    }

    workloads = [("built-in countdown loop (100 iterations)", new ByteArrayWorkload(bytes)),];
}

// ── Olympia JSON trace output ─────────────────────────────────────────────────

if (traceJsonPath is not null) {
    if (workloads.Count > 1) {
        Console.Error.WriteLine("--trace-json supports only a single workload.");
        return;
    }

    IWorkload traceWorkload = workloads[0].Workload;
    await using var sw = new StreamWriter(traceJsonPath);
    int written = Experiment.WriteOlympiaTrace(
        traceWorkload, new Rv32Mechanism(traceWorkload.HtifTohostAddress), sw, maxTicks
    );
    Console.Error.WriteLine($"Wrote {written} instructions to {traceJsonPath}");
    return;
}

// ── Elastic DDG trace recording ───────────────────────────────────────────────

if (elasticRecordPath is not null) {
    if (workloads.Count > 1) {
        Console.Error.WriteLine("--elastic-record supports only a single workload.");
        return;
    }

    IWorkload elasticWorkload = workloads[0].Workload;
    await using var fs = new FileStream(elasticRecordPath, FileMode.Create, FileAccess.Write);
    int written = Experiment.WriteElasticTrace(
        elasticWorkload, new Rv32Mechanism(elasticWorkload.HtifTohostAddress), fs, maxTicks
    );
    Console.Error.WriteLine($"Recorded {written:N0} instructions to {elasticRecordPath}");
    return;
}

// ── STF binary trace recording ────────────────────────────────────────────────

if (stfRecordPath is not null) {
    if (workloads.Count > 1) {
        Console.Error.WriteLine("--stf-record supports only a single workload.");
        return;
    }

    IWorkload stfWorkload = workloads[0].Workload;
    await using var fs = new FileStream(stfRecordPath, FileMode.Create, FileAccess.Write);
    int written = Experiment.WriteStfTrace(
        stfWorkload, new Rv32Mechanism(stfWorkload.HtifTohostAddress), fs, maxTicks
    );
    Console.Error.WriteLine($"Recorded {written:N0} instructions to {stfRecordPath}");
    return;
}

// ── Script mode ──────────────────────────────────────────────────────────────

if (scriptPath is not null) {
    if (workloads.Count > 1) {
        Console.Error.WriteLine("--script supports only a single workload.");
        return;
    }

    MachineSpec spec;
    try {
        Console.Error.WriteLine($"Evaluating {scriptPath} …");
        spec = await ScriptHost.EvaluateFileAsync(scriptPath);
    }
    catch (Exception ex) {
        Console.Error.WriteLine($"Script error: {ex.Message}");
        return;
    }

    if (roiStartSymbol is not null) {
        // ── Region-of-Interest mode ──────────────────────────────────────────
        // Phase 1: fast-forward with SingleCycleTrain until ArchState.Pc == roiStartPc.
        // Phase 2: rebuild with the script's pipeline, restore state, run to roiEnd or maxTicks.

        if (workloads[0].Workload is not Rv32ElfWorkload elfWorkload) {
            Console.Error.WriteLine("--roi-start requires an ELF workload.");
            return;
        }

        ulong roiStartPc;
        try { roiStartPc = elfWorkload.FindSymbol(roiStartSymbol); }
        catch (Exception ex) {
            Console.Error.WriteLine($"Symbol '{roiStartSymbol}' not found: {ex.Message}");
            return;
        }

        ulong? roiEndPc = null;
        if (roiEndSymbol is not null)
            try { roiEndPc = elfWorkload.FindSymbol(roiEndSymbol); }
            catch (Exception ex) {
                Console.Error.WriteLine($"Symbol '{roiEndSymbol}' not found: {ex.Message}");
                return;
            }

        var flatMem = new FlatMemory(elfWorkload.MemorySize, elfWorkload.BaseAddress);
        elfWorkload.Load(flatMem);
        IWorkload elfAsWorkload = elfWorkload;
        IMemory backing = elfWorkload.WrapMemory(flatMem);
        ulong entryPoint = elfWorkload.EntryPoint;
        (ulong Base, ulong Size)? mmio = elfAsWorkload.MmioRegion;

        // Phase 1 — fast-forward
        Console.Error.WriteLine($"Fast-forwarding to {roiStartSymbol} (0x{roiStartPc:X}) …");
        MachineSpec ffSpec = spec with { Pipeline = new SingleCycleSpec(), Cache = null, };
        MachineHandle ffHandle = ffSpec.Build(backing, entryPoint, mmio);

        ffHandle.Train.BeginStepping();
        long ffTicks = 0;
        bool roiReached = ffHandle.Train.ArchState!.Pc == roiStartPc; // true if entry == roi start
        while (!roiReached && ffHandle.Train.StepCycle()) {
            ffTicks++;
            roiReached = ffHandle.Train.ArchState!.Pc == roiStartPc;
        }

        ffHandle.Train.FinishStepping();

        if (!roiReached) {
            Console.Error.WriteLine(
                $"Symbol '{roiStartSymbol}' (0x{roiStartPc:X}) was never reached after {ffTicks:N0} ticks."
            );
            return;
        }

        Console.Error.WriteLine($"Fast-forward done — {ffTicks:N0} ticks");

        // Capture arch state at ROI start via in-memory checkpoint
        using var chkStream = new MemoryStream();
        ArchitecturalCheckpoint.Save(chkStream, ffHandle.Train.ArchState!, flatMem, (ulong)ffTicks);
        chkStream.Position = 0;
        ArchitecturalCheckpoint roiChk = ArchitecturalCheckpoint.Load(chkStream);

        // Phase 2 — detailed simulation
        Console.Error.WriteLine($"Starting detailed simulation from {roiStartSymbol} …");
        MachineHandle detHandle = spec.Build(backing, roiStartPc, mmio);
        roiChk.RestoreInto(detHandle.Train.ArchState!, flatMem);

        long roiTicks;
        if (roiEndPc is { } endPc) {
            detHandle.Train.BeginStepping();
            roiTicks = 0;
            var endReached = false;
            while (roiTicks < maxTicks && detHandle.Train.StepCycle()) {
                roiTicks++;
                if (detHandle.Train.ArchState!.Pc == endPc) {
                    endReached = true;
                    break;
                }
            }

            detHandle.Train.FinishStepping();
            Console.Error.WriteLine(
                endReached
                    ? $"ROI done — {roiTicks:N0} ticks (reached {roiEndSymbol})"
                    : $"ROI done — {roiTicks:N0} ticks (maxTicks reached; {roiEndSymbol} not seen)"
            );
        }
        else {
            RevolutionResult roiResult = detHandle.Run(maxTicks, warmupTicks);
            roiTicks = roiResult.TotalTicks;
            Console.Error.WriteLine($"ROI done — {roiTicks:N0} ticks");
        }

        PrintLayerStats(detHandle);

        if (checkpointSavePath is not null) {
            Task checkpointSave = ArchitecturalCheckpoint.SaveAsync(
                checkpointSavePath, detHandle.Train.ArchState!, flatMem, (ulong)(ffTicks + roiTicks)
            );
            Console.Error.WriteLine($"Checkpoint saved → {checkpointSavePath}");
            await checkpointSave;
        }
    }
    else if (checkpointLoadPath is not null) {
        // ── Checkpoint-load mode ─────────────────────────────────────────────
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(checkpointLoadPath);
        Console.Error.WriteLine(
            $"Loaded checkpoint — tick={chk.Tick:N0} pc=0x{chk.Pc:X} mem={chk.MemorySizeBytes:N0} bytes"
        );
        var scriptMem = new FlatMemory(chk.MemorySizeBytes, chk.MemoryBaseAddress);

        MachineHandle handle = spec.Build(scriptMem, chk.Pc);
        chk.RestoreInto(handle.ArchState!, scriptMem);

        RevolutionResult result = handle.Run(maxTicks, warmupTicks);
        Console.Error.WriteLine($"Done — {result.TotalTicks:N0} ticks");
        PrintLayerStats(handle);

        if (checkpointSavePath is not null) {
            Task checkpointSave =
                ArchitecturalCheckpoint.SaveAsync(
                    checkpointSavePath, handle.ArchState!, scriptMem, (ulong)result.TotalTicks
                );
            Console.Error.WriteLine($"Checkpoint saved → {checkpointSavePath}");
            await checkpointSave;
        }
    }
    else {
        // ── Normal script mode ───────────────────────────────────────────────
        IWorkload scriptWorkload = workloads[0].Workload;
        var scriptMem = new FlatMemory(scriptWorkload.MemorySize, scriptWorkload.BaseAddress);
        scriptWorkload.Load(scriptMem);
        IMemory scriptBacking = scriptWorkload.WrapMemory(scriptMem);

        MachineHandle handle = spec.Build(scriptBacking, scriptWorkload.EntryPoint, scriptWorkload.MmioRegion);
        RevolutionResult result = handle.Run(maxTicks, warmupTicks);
        Console.Error.WriteLine($"Done — {result.TotalTicks:N0} ticks");
        PrintLayerStats(handle);

        if (checkpointSavePath is not null) {
            Task checkpointSave =
                ArchitecturalCheckpoint.SaveAsync(
                    checkpointSavePath, handle.ArchState!, scriptMem, (ulong)result.TotalTicks
                );
            Console.Error.WriteLine($"Checkpoint saved → {checkpointSavePath}");
            await checkpointSave;
        }
    }

    return;

    static void PrintLayerStats(MachineHandle h) {
        if (h.Layers is not { } layers) return;
        if (layers.Cache is { } l1) Console.Error.WriteLine($"  L1  — misses: {l1.Misses:N0}  hits: {l1.Hits:N0}");
        if (layers.L2Cache is { } l2) Console.Error.WriteLine($"  L2  — misses: {l2.Misses:N0}  hits: {l2.Hits:N0}");
        if (layers.Tlb is { } tlb) Console.Error.WriteLine($"  TLB — misses: {tlb.Misses:N0}  hits: {tlb.Hits:N0}");
    }
}

// ── Hardware configurations ───────────────────────────────────────────────────

IReadOnlyList<NamedConfig> configs = sweepPath is not null
    ? NamedConfig.LoadFile(sweepPath)
    : DefaultSweep();

// ── Run ───────────────────────────────────────────────────────────────────────

Console.Error.WriteLine($"Workloads: {workloads.Count} ({string.Join(", ", workloads.Select(w => w.Label))})");
Console.Error.WriteLine($"Configs  : {configs.Count} ({sweepPath ?? "default predictor sweep"})");
if (warmupTicks > 0) Console.Error.WriteLine($"Warmup   : {warmupTicks:N0} ticks");
Console.Error.WriteLine($"Max ticks: {maxTicks:N0}");
Console.Error.WriteLine();

if (workloads.Count == 1) {
    IWorkload workload = workloads[0].Workload;
    ExperimentResult result = Experiment.Run(
        workload, configs, () => new Rv32Mechanism(workload.HtifTohostAddress),
        maxTicks, warmupTicks, snapshotInterval
    );

    if (format is "md" or "both") Console.WriteLine(result.ToMarkdownTable());
    switch (format) {
        case "csv" or "both": Console.WriteLine(result.ToCsv()); break;
        case "ts-csv":        Console.WriteLine(result.ToTimeSeriesCsv()); break;
    }
}
else {
    IReadOnlyList<(string Label, ExperimentResult Result)> results = Experiment.RunMany(
        workloads, configs, w => new Rv32Mechanism(w.HtifTohostAddress),
        maxTicks, warmupTicks, snapshotInterval
    );

    foreach ((string label, ExperimentResult result) in results) {
        if (format is "md" or "both") {
            Console.WriteLine($"## {label}");
            Console.WriteLine(result.ToMarkdownTable());
        }

        switch (format) {
            case "csv" or "both":
                Console.WriteLine($"## {label}");
                Console.WriteLine(result.ToCsv());
                break;
            case "ts-csv":
                Console.WriteLine($"## {label}");
                Console.WriteLine(result.ToTimeSeriesCsv());
                break;
        }
    }
}

return;

// ── Helpers ───────────────────────────────────────────────────────────────────

static IBranchPredictor ResolveChampSimPredictor(string name) => (name switch {
    "always_taken"      => BranchPredictorConfig.AlwaysTaken(),
    "always_not_taken"  => BranchPredictorConfig.AlwaysNotTaken(),
    "n_bit"             => BranchPredictorConfig.NBit(),
    "correlated"        => BranchPredictorConfig.Correlated(),
    "gselect"           => BranchPredictorConfig.Gselect(),
    "gshare"            => BranchPredictorConfig.Gshare(),
    "l_tage"            => BranchPredictorConfig.LTage(),
    "perceptron"        => BranchPredictorConfig.Perceptron(),
    "tournament"        => BranchPredictorConfig.Tournament(),
    "tage_sc_l"         => BranchPredictorConfig.TageScL(),
    "hashed_perceptron" => BranchPredictorConfig.HashedPerceptron(),
    "ittage"            => BranchPredictorConfig.Ittage(),
    "batage"            => BranchPredictorConfig.Batage(),
    "imli"              => BranchPredictorConfig.Imli(),
    _ => throw new ArgumentException(
        $"Unknown --champsim-predictor '{name}'. Use --champsim-cbp-lib for a native CBP plugin instead."
    ),
}).Build();

static IReadOnlyList<NamedConfig> DefaultSweep() => [
    new("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
    new("always_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysTaken())),
    new("1_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit(1))),
    new("2_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit())),
    new("3_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit(3))),
    new("no_forwarding", new TrainConfig(Predictor: BranchPredictorConfig.NBit(), ForwardingEnabled: false)),
    new("scalar_2wide", new TrainConfig("superscalar", IssueWidth: 2)),
    new("scalar_4wide", new TrainConfig("superscalar", IssueWidth: 4)),
    new("ooo_2wide", new TrainConfig("ooo", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 2)),
    new(
        "ooo_4wide",
        new TrainConfig(
            "ooo", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 4, RobCapacity: 64, IqCapacity: 32
        )
    ),
];

static void PrintUsage() {
    Console.WriteLine(
        """
        Usage: runner [elf-path ...] [options]

          elf-path              One or more ELF32 RISC-V binaries to simulate in parallel.
                                Omit to run the built-in 100-iteration countdown loop.

        Options:
          --sweep <path>        JSON file with named hardware configurations to compare.
                                Default: branch-predictor sweep + OoO 2-wide and 4-wide.
          --memory <bytes>      Physical memory size in bytes (default: 4194304 = 4 MB).
          --warmup <n>          Ticks to run before recording statistics (default: 0).
          --max-ticks <n>       Maximum measurement ticks per run (default: 1000000).
          --snapshot-interval <n|auto>  Ticks between time-series snapshots. Use 'auto' to
                                        estimate from binary size (default: off).
          --format md|csv|both|ts-csv   Output format (default: md). ts-csv emits
                                        time-series data (requires --snapshot-interval).
          --trace-json <path>   Emit an Olympia-compatible JSON instruction trace
                                (functional single-cycle run) to <path> and exit.
                                Single workload only.
          --script <path>       Evaluate a .csx/.fsx file returning a MachineSpec and run
                                the selected workload on it. All Spec types and RiscV32
                                are pre-imported; no #r or using needed.
                                Single workload only.
          --checkpoint-save <path>  After run completes, save an architectural checkpoint
                                    (PC, registers, CSRs, VRF, memory) to <path>.
                                    Requires --script. Combine with --checkpoint-load for
                                    fast-forward → detailed handoffs.
          --checkpoint-load <path>  Before run, restore state from a checkpoint saved by
                                    --checkpoint-save. Ignores the workload's memory and
                                    entry point; uses the checkpoint's. Requires --script.
          --roi-start <symbol>      Fast-forward functionally (single-cycle) until the named
                                    ELF symbol is reached, then run the script's pipeline for
                                    detailed timing from that point. Requires --script + ELF.
          --roi-end <symbol>        Stop the detailed phase when ArchState.Pc reaches this
                                    ELF symbol. If omitted, runs until --max-ticks.
                                    Requires --roi-start.
          --elastic-record <path>   Record a Horologium elastic DDG trace (HELF binary) by
                                    running the workload on a single-cycle functional model.
                                    Captures per-instruction register and memory RAW edges.
                                    Single workload only.
          --elastic-replay <path>   Replay a HELF trace and print the dataflow critical-path
                                    cycle count and IPC upper bound (infinite issue width,
                                    no structural hazards). No workload needed.
          --elastic-to-gem5 <in> <out>  Translate a HELF trace to a gem5 inst_dep_record.proto
                                        binary stream (LE magic + varint32 length per message,
                                        InstDepRecordHeader + InstDepRecord messages). No workload needed.
          --fetch-to-gem5 <in> <out>    Translate a HELF trace to a gem5 packet.proto fetch-trace
                                        binary stream (PacketHeader + Packet messages). This is the
                                        instTraceFile companion to --elastic-to-gem5's dataTraceFile.
                                        No workload needed.
          --stf-record <path>           Record an STF binary trace (Sparcians stf_lib format,
                                        version 1.5) by running the workload on a single-cycle
                                        functional model. Includes operand values (integer and FP
                                        registers), memory addresses and data, and taken-branch
                                        targets. Replay with Olympia or any stf_lib-based tool.
                                        Single workload only.
          --champsim-trace <path>       Replay a ChampSim binary trace (raw or gzip; `input_instr`
                                        records — see inc/trace_instruction.h in ChampSim) through
                                        a branch predictor and/or cache replacement policy and print
                                        misprediction/hit-rate stats. No workload needed.
          --champsim-predictor <name>   Built-in predictor to evaluate (default: n_bit). One of:
                                        always_taken, always_not_taken, n_bit, correlated, gselect,
                                        gshare, l_tage, perceptron, tournament, tage_sc_l,
                                        hashed_perceptron, ittage, batage, imli.
          --champsim-cbp-lib <path>     Evaluate a CBP-3/5 native plugin (native/CbpShim) instead of
                                        a built-in predictor. Overrides --champsim-predictor.
          --champsim-cache-policy <name|none>
                                        Replacement policy to evaluate (default: Lru; case-
                                        insensitive Orrery.Cache.ReplacementPolicyKind name — Lru,
                                        Srrip, Brrip, Drrip, Ship, ShipPc, Random, Fifo, Plru, Mru,
                                        Clock, Hawkeye). "none" skips cache evaluation.
          --champsim-cache-sets <n>     Cache set count (default: 2048).
          --champsim-cache-ways <n>     Cache associativity (default: 16).
          --champsim-cache-block <n>    Cache line size in bytes (default: 64).
          --help                        Show this message.

        Sweep file format (JSON array):
          [
            {"name": "baseline",  "config": {"predictor": {"type": "n_bit", "bits": 2}}},
            {"name": "no_cache",  "config": {"forwarding_enabled": false}},
            {"name": "ooo_2wide", "config": {"pipeline": "ooo", "issue_width": 2}},
            {"name": "ooo_4wide", "config": {"pipeline": "ooo", "issue_width": 4, "rob_capacity": 64}}
          ]

        Pipeline types  : five_stage (default), superscalar, ooo
        Predictor types : always_not_taken, always_taken, n_bit (params: bits, table_size)
        OoO parameters  : issue_width (default 2), rob_capacity (32), iq_capacity (8, per-class), extra_phys_regs (32)
        """
    );
}