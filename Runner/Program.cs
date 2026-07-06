using Mechanism;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;
using Script;

// ── Argument parsing ──────────────────────────────────────────────────────────

List<string> elfPaths = [];
string? sweepPath = null;
long warmupTicks = 0;
long maxTicks = 1_000_000;
long snapshotInterval = 0;    // 0 = off, -1 = auto, >0 = explicit ticks
var format = "md";            // md | csv | both | ts-csv
int? memorySizeBytes = null;  // null → default to 4 MB for ELF workloads
string? traceJsonPath = null;      // --trace-json <path>: emit an Olympia JSON trace and exit
string? scriptPath = null;         // --script <file.csx>: evaluate script → MachineSpec → run
string? checkpointSavePath = null; // --checkpoint-save <path>: save arch checkpoint after run
string? checkpointLoadPath = null; // --checkpoint-load <path>: restore arch checkpoint before run

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
        case "--format":     format = args[++i]; break;
        case "--trace-json":       traceJsonPath = args[++i]; break;
        case "--checkpoint-save":  checkpointSavePath = args[++i]; break;
        case "--checkpoint-load":  checkpointLoadPath = args[++i]; break;
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
    using var sw = new StreamWriter(traceJsonPath);
    int written = Experiment.WriteOlympiaTrace(
        traceWorkload, new Rv32Mechanism(traceWorkload.HtifTohostAddress), sw, maxTicks
    );
    Console.Error.WriteLine($"Wrote {written} instructions to {traceJsonPath}");
    return;
}

// ── Script mode ──────────────────────────────────────────────────────────────

if (scriptPath is not null) {
    if (workloads.Count > 1) {
        Console.Error.WriteLine("--script supports only a single workload.");
        return;
    }

    Pipeline.Spec.MachineSpec spec;
    try {
        Console.Error.WriteLine($"Evaluating {scriptPath} …");
        spec = await ScriptHost.EvaluateFileAsync(scriptPath);
    } catch (Exception ex) {
        Console.Error.WriteLine($"Script error: {ex.Message}");
        return;
    }

    FlatMemory scriptMem;
    ulong scriptEntryPoint;
    (ulong Base, ulong Size)? scriptMmio;

    if (checkpointLoadPath is not null) {
        // Restore memory geometry and arch state from checkpoint; workload just provides pipeline.
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(checkpointLoadPath);
        Console.Error.WriteLine(
            $"Loaded checkpoint — tick={chk.Tick:N0} pc=0x{chk.Pc:X} mem={chk.MemorySizeBytes:N0} bytes"
        );
        scriptMem = new FlatMemory(chk.MemorySizeBytes, chk.MemoryBaseAddress);
        scriptEntryPoint = chk.Pc;
        scriptMmio = null;

        Pipeline.Spec.MachineHandle handle = spec.Build(scriptMem, scriptEntryPoint, scriptMmio);
        chk.RestoreInto(handle.ArchState!, scriptMem);

        Orrery.Train.RevolutionResult result = handle.Run(maxTicks, warmupTicks);
        Console.Error.WriteLine($"Done — {result.TotalTicks:N0} ticks");
        PrintLayerStats(handle);

        if (checkpointSavePath is not null) {
            ArchitecturalCheckpoint.Save(checkpointSavePath, handle.ArchState!, scriptMem, (ulong)result.TotalTicks);
            Console.Error.WriteLine($"Checkpoint saved → {checkpointSavePath}");
        }
    } else {
        IWorkload scriptWorkload = workloads[0].Workload;
        scriptMem = new FlatMemory(scriptWorkload.MemorySize, scriptWorkload.BaseAddress);
        scriptWorkload.Load(scriptMem);
        IMemory scriptBacking = scriptWorkload.WrapMemory(scriptMem);
        scriptEntryPoint = scriptWorkload.EntryPoint;
        scriptMmio = scriptWorkload.MmioRegion;

        Pipeline.Spec.MachineHandle handle = spec.Build(scriptBacking, scriptEntryPoint, scriptMmio);
        Orrery.Train.RevolutionResult result = handle.Run(maxTicks, warmupTicks);
        Console.Error.WriteLine($"Done — {result.TotalTicks:N0} ticks");
        PrintLayerStats(handle);

        if (checkpointSavePath is not null) {
            ArchitecturalCheckpoint.Save(checkpointSavePath, handle.ArchState!, scriptMem, (ulong)result.TotalTicks);
            Console.Error.WriteLine($"Checkpoint saved → {checkpointSavePath}");
        }
    }

    return;

    static void PrintLayerStats(Pipeline.Spec.MachineHandle h) {
        if (h.Layers is not { } layers) return;
        if (layers.Cache is { } l1)
            Console.Error.WriteLine($"  L1  — misses: {l1.Misses:N0}  hits: {l1.Hits:N0}");
        if (layers.L2Cache is { } l2)
            Console.Error.WriteLine($"  L2  — misses: {l2.Misses:N0}  hits: {l2.Hits:N0}");
        if (layers.Tlb is { } tlb)
            Console.Error.WriteLine($"  TLB — misses: {tlb.Misses:N0}  hits: {tlb.Hits:N0}");
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