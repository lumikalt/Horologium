using Mechanism;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;

// ── Argument parsing ──────────────────────────────────────────────────────────

List<string> elfPaths = [];
string? sweepPath = null;
long warmupTicks = 0;
long maxTicks = 1_000_000;
long snapshotInterval = 0;    // 0 = off, -1 = auto, >0 = explicit ticks
var format = "md";            // md | csv | both | ts-csv
int? memorySizeBytes = null;  // null → default to 4 MB for ELF workloads
string? traceJsonPath = null; // --trace-json <path>: emit an Olympia JSON trace and exit

for (var i = 0; i < args.Length; i++)
    switch (args[i]) {
        case "--sweep":     sweepPath = args[++i]; break;
        case "--warmup":    warmupTicks = long.Parse(args[++i]); break;
        case "--max-ticks": maxTicks = long.Parse(args[++i]); break;
        case "--memory":    memorySizeBytes = int.Parse(args[++i]); break;
        case "--snapshot-interval":
            snapshotInterval = args[i + 1] == "auto" ? (++i, -1L).Item2 : long.Parse(args[++i]);
            break;
        case "--format":     format = args[++i]; break;
        case "--trace-json": traceJsonPath = args[++i]; break;
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