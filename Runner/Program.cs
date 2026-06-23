using Mechanism;
using RiscV;
using RiscV.Analysis;
using RiscV.Config;
using RiscV.Memory;

// ── Argument parsing ──────────────────────────────────────────────────────────

string? elfPath = null;
string? sweepPath = null;
long warmupTicks = 0;
long maxTicks = 1_000_000;
long snapshotInterval = 0; // 0 = off, -1 = auto, >0 = explicit ticks
var format = "md";         // md | csv | both | ts-csv

string[] argv = args; // top-level programs expose args implicitly
for (var i = 0; i < argv.Length; i++)
    switch (argv[i]) {
        case "--sweep":     sweepPath = argv[++i]; break;
        case "--warmup":    warmupTicks = long.Parse(argv[++i]); break;
        case "--max-ticks": maxTicks = long.Parse(argv[++i]); break;
        case "--snapshot-interval":
            snapshotInterval = argv[i + 1] == "auto" ? (++i, -1L).Item2 : long.Parse(argv[++i]);
            break;
        case "--format": format = argv[++i]; break;
        case "--help" or "-h":
            PrintUsage();
            return;
        default:
            if (!argv[i].StartsWith("--") && elfPath is null) { elfPath = argv[i]; }
            else {
                Console.Error.WriteLine($"Unknown argument: {argv[i]}");
                PrintUsage();
                return;
            }

            break;
    }

// ── Workload ──────────────────────────────────────────────────────────────────

IWorkload workload;
string workloadLabel;

if (elfPath is not null) {
    workload = new ElfWorkload(elfPath);
    workloadLabel = Path.GetFileName(elfPath);
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

    workload = new ByteArrayWorkload(bytes);
    workloadLabel = "built-in countdown loop (100 iterations)";
}

// ── Hardware configurations ───────────────────────────────────────────────────

IReadOnlyList<NamedConfig> configs = sweepPath is not null
    ? NamedConfig.LoadFile(sweepPath)
    : DefaultSweep();

// ── Run ───────────────────────────────────────────────────────────────────────

Console.Error.WriteLine($"Workload : {workloadLabel}");
Console.Error.WriteLine($"Configs  : {configs.Count} ({sweepPath ?? "default predictor sweep"})");
if (warmupTicks > 0) Console.Error.WriteLine($"Warmup   : {warmupTicks:N0} ticks");
Console.Error.WriteLine($"Max ticks: {maxTicks:N0}");
Console.Error.WriteLine();

ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism(), maxTicks, warmupTicks, snapshotInterval);

// ── Output ────────────────────────────────────────────────────────────────────

if (format is "md" or "both") Console.WriteLine(result.ToMarkdownTable());
if (format is "csv" or "both") Console.WriteLine(result.ToCsv());
if (format is "ts-csv") Console.WriteLine(result.ToTimeSeriesCsv());

// ── Helpers ───────────────────────────────────────────────────────────────────

static IReadOnlyList<NamedConfig> DefaultSweep() => [
    new("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
    new("always_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysTaken())),
    new("one_bit", new TrainConfig(Predictor: BranchPredictorConfig.OneBit())),
    new("two_bit", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
    new("no_forwarding", new TrainConfig(Predictor: BranchPredictorConfig.TwoBit(), ForwardingEnabled: false)),
];

static void PrintUsage() {
    Console.WriteLine(
        """
        Usage: runner [elf-path] [options]

          elf-path              ELF32 RISC-V binary to simulate (default: built-in demo)

        Options:
          --sweep <path>        JSON file with named hardware configurations to compare.
                                Default: compare four branch predictors + no-forwarding.
          --warmup <n>          Ticks to run before recording statistics (default: 0).
          --max-ticks <n>       Maximum measurement ticks per run (default: 1000000).
          --snapshot-interval <n|auto>  Ticks between time-series snapshots. Use 'auto' to
                                        estimate from binary size (default: off).
          --format md|csv|both|ts-csv   Output format (default: md). ts-csv emits
                                        time-series data (requires --snapshot-interval).
          --help                        Show this message.

        Sweep file format (JSON array):
          [
            {"name": "baseline", "config": {"predictor": {"type": "two_bit"}}},
            {"name": "no_cache", "config": {"forwarding_enabled": false}}
          ]

        Predictor types: always_not_taken, always_taken, one_bit, two_bit
        """
    );
}