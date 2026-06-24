using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mechanism;
using Orrery.Observation;
using RiscV;
using RiscV.Analysis;
using RiscV.Config;
using RiscV.Memory;

namespace Face.ViewModels;

public record WorkloadPreset(string Label, string? ElfFileName, int MemoryBytes = 0);

public partial class MainWindowViewModel : ObservableObject {
    private static readonly string BenchmarksDir =
        Path.Combine(AppContext.BaseDirectory, "benchmarks");

    private const int BenchmarkMemoryBytes = 4 * 1024 * 1024;

    public ObservableCollection<WorkloadPreset> WorkloadPresets { get; } = [
        new("Built-in demo  (100-iter countdown loop)", null),
        new("Benchmark — median", "median.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — memcpy", "memcpy.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — multiply", "multiply.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — qsort", "qsort.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — rsort", "rsort.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — towers", "towers.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — vvadd", "vvadd.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Custom ELF…", ""),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowse))]
    public partial WorkloadPreset SelectedPreset { get; set; }

    [ObservableProperty] public partial string? WorkloadPath { get; set; } = null;

    [ObservableProperty] public partial decimal MaxTicks { get; set; } = 1_000_000;

    [ObservableProperty] public partial decimal WarmupTicks { get; set; } = 0;

    [ObservableProperty] public partial decimal SnapshotInterval { get; set; } = 0;

    [ObservableProperty] public partial bool IsRunning { get; set; } = false;

    [ObservableProperty] public partial string StatusText { get; set; } = "Ready — configure and run an experiment.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedConfig))]
    public partial ConfigViewModel? SelectedConfig { get; set; } = null;

    [ObservableProperty] public partial string? SelectedMetric { get; set; } = null;

    [ObservableProperty] public partial bool HasResults { get; set; } = false;

    public bool ShowBrowse => SelectedPreset.ElfFileName == "";

    public ObservableCollection<ConfigViewModel> Configs { get; } = [];
    public ObservableCollection<string> AvailableMetrics { get; } = [];
    public ObservableCollection<Dictionary<string, string>> TableRows { get; } = [];
    public IReadOnlyList<string> TableHeaders { get; private set; } = [];

    public bool HasSelectedConfig => SelectedConfig is not null;

    public event Action? ResultsUpdated;

    private ExperimentResult? _lastResult;

    public MainWindowViewModel() {
        foreach (NamedConfig nc in DefaultSweep()) Configs.Add(ConfigViewModel.FromNamedConfig(nc));
        SelectedConfig = Configs.FirstOrDefault();
        SelectedPreset = WorkloadPresets[0];
    }

    partial void OnSelectedMetricChanged(string? value) {
        if (HasResults) ResultsUpdated?.Invoke();
    }

    [RelayCommand]
    private void AddConfig() {
        var vm = new ConfigViewModel { Name = $"config_{Configs.Count + 1}", };
        Configs.Add(vm);
        SelectedConfig = vm;
    }

    [RelayCommand]
    private void DuplicateConfig() {
        if (SelectedConfig is null) return;
        var nc = SelectedConfig.ToNamedConfig();
        ConfigViewModel dup = ConfigViewModel.FromNamedConfig(nc with { Name = nc.Name + "_2", });
        Configs.Add(dup);
        SelectedConfig = dup;
    }

    [RelayCommand]
    private void RemoveConfig() {
        if (SelectedConfig is null) return;
        int idx = Configs.IndexOf(SelectedConfig);
        Configs.Remove(SelectedConfig);
        SelectedConfig = Configs.ElementAtOrDefault(Math.Max(0, idx - 1));
    }

    [RelayCommand]
    private async Task Run() {
        if (Configs.Count == 0) {
            StatusText = "Add at least one configuration.";
            return;
        }

        if (SelectedPreset.ElfFileName == "" && string.IsNullOrWhiteSpace(WorkloadPath)) {
            StatusText = "Specify an ELF file or select a different workload.";
            return;
        }

        IsRunning = true;
        HasResults = false;
        StatusText = $"Running {Configs.Count} configuration(s)…";

        try {
            IWorkload workload = SelectedPreset.ElfFileName switch {
                null => CreateBuiltInWorkload(),
                ""   => new ElfWorkload(WorkloadPath!),
                var fn => new ElfWorkload(
                    Path.Combine(MainWindowViewModel.BenchmarksDir, fn),
                    SelectedPreset.MemoryBytes
                ),
            };

            List<NamedConfig> namedConfigs = Configs.Select(c => c.ToNamedConfig()).ToList();
            var maxTicks = (long)(MaxTicks > 0 ? MaxTicks : 1_000_000);
            var warmupTicks = (long)(WarmupTicks >= 0 ? WarmupTicks : 0);
            var snapshotInterval = (long)(SnapshotInterval >= 0 ? SnapshotInterval : 0);

            ExperimentResult result = await Task.Run(() =>
                                                         Experiment.Run(
                                                             workload, namedConfigs, new RvMechanism(), maxTicks,
                                                             warmupTicks, snapshotInterval
                                                         )
            );

            _lastResult = result;
            UpdateMetrics(result);
            PopulateTable(result);
            HasResults = true;
            StatusText = $"Done — {result.Runs.Count} run(s), {maxTicks:N0} max ticks each.";
            ResultsUpdated?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsRunning = false; }
    }

    public void SetWorkloadPath(string path) {
        WorkloadPath = path;
        SelectedPreset = WorkloadPresets.First(p => p.ElfFileName == "");
    }

    public (string[] names, double[] values) GetChartData() {
        if (_lastResult is null || SelectedMetric is null) return ([], []);

        var names = new List<string>();
        var values = new List<double>();

        foreach (RunRecord run in _lastResult.Runs) {
            names.Add(run.Name);
            Dictionary<string, long> counters = MergeCounters(run.Result.Snapshots);
            Dictionary<string, double> dials = MergeDials(run.Result.Snapshots);

            double value = 0;
            if (counters.TryGetValue(SelectedMetric, out long cv))
                value = cv;
            else if (dials.TryGetValue(SelectedMetric, out double dv)) value = dv;
            values.Add(value);
        }

        return ([..names,], [..values,]);
    }

    private void UpdateMetrics(ExperimentResult result) {
        var counterCols = new SortedSet<string>();
        var dialCols = new SortedSet<string>();

        foreach (RunRecord run in result.Runs)
        foreach (DialBoardSnapshot snap in run.Result.Snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach (string k in snap.Counters.Keys) counterCols.Add($"{prefix}.{k}");
            foreach (string k in snap.Dials.Keys) dialCols.Add($"{prefix}.{k}");
        }

        AvailableMetrics.Clear();
        foreach (string m in counterCols) AvailableMetrics.Add(m);
        foreach (string m in dialCols) AvailableMetrics.Add(m);

        SelectedMetric = AvailableMetrics.FirstOrDefault(m => m.EndsWith(".ipc"))
                      ?? AvailableMetrics.FirstOrDefault(m => m.EndsWith(".cycles"))
                      ?? AvailableMetrics.FirstOrDefault();
    }

    private void PopulateTable(ExperimentResult result) {
        var counterCols = new SortedSet<string>();
        var dialCols = new SortedSet<string>();

        foreach (RunRecord run in result.Runs)
        foreach (DialBoardSnapshot snap in run.Result.Snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach (string k in snap.Counters.Keys) counterCols.Add($"{prefix}.{k}");
            foreach (string k in snap.Dials.Keys) dialCols.Add($"{prefix}.{k}");
        }

        var headers = new List<string> { "name", };
        headers.AddRange(counterCols);
        headers.AddRange(dialCols);
        TableHeaders = headers;

        TableRows.Clear();
        foreach (RunRecord run in result.Runs) {
            Dictionary<string, long> counters = MergeCounters(run.Result.Snapshots);
            Dictionary<string, double> dials = MergeDials(run.Result.Snapshots);
            var row = new Dictionary<string, string> { ["name"] = run.Name, };
            foreach (string col in counterCols)
                row[col] = counters.TryGetValue(col, out long lv) ? lv.ToString("N0") : "—";
            foreach (string col in dialCols) row[col] = dials.TryGetValue(col, out double dv) ? dv.ToString("G4") : "—";
            TableRows.Add(row);
        }
    }

    private static string GearRelPath(string ownerPath) {
        int dot = ownerPath.IndexOf('.');
        return dot < 0 ? ownerPath : ownerPath[(dot + 1)..];
    }

    private static Dictionary<string, long> MergeCounters(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, long>();
        foreach (DialBoardSnapshot snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, long value) in snap.Counters) {
                var col = $"{prefix}.{key}";
                merged[col] = merged.GetValueOrDefault(col) + value;
            }
        }

        return merged;
    }

    private static Dictionary<string, double> MergeDials(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, double>();
        foreach (DialBoardSnapshot snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, double value) in snap.Dials)
                if (value != 0.0)
                    merged[$"{prefix}.{key}"] = value;
        }

        return merged;
    }

    private static ByteArrayWorkload CreateBuiltInWorkload() {
        // Built-in demo: 100-iteration countdown loop
        uint[] words = [0x06400093, 0x00008663, 0xFFF08093, 0xFF9FF06F, 0x00100073,];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        return new ByteArrayWorkload(bytes);
    }

    private static IReadOnlyList<NamedConfig> DefaultSweep() => [
        new("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
        new("always_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysTaken())),
        new("1_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit(1))),
        new("2_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit())),
        new("3_bit", new TrainConfig(Predictor: BranchPredictorConfig.NBit(3))),
        new("superscalar_2w", new TrainConfig("superscalar", IssueWidth: 2)),
        new("ooo_2w", new TrainConfig("ooo", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 2)),
    ];
}