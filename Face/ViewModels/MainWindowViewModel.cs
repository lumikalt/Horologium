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

public partial class MainWindowViewModel : ObservableObject {
    [ObservableProperty] private string? _workloadPath = null;
    [ObservableProperty] private bool _useBuiltInDemo = true;
    [ObservableProperty] private decimal _maxTicks = 1_000_000;
    [ObservableProperty] private decimal _warmupTicks = 0;
    [ObservableProperty] private decimal _snapshotInterval = 0;
    [ObservableProperty] private bool _isRunning = false;
    [ObservableProperty] private string _statusText = "Ready — configure and run an experiment.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedConfig))]
    private ConfigViewModel? _selectedConfig = null;

    [ObservableProperty] private string? _selectedMetric = null;
    [ObservableProperty] private bool _hasResults = false;
    [ObservableProperty] private bool _canBrowse = false;

    public ObservableCollection<ConfigViewModel>            Configs          { get; } = [];
    public ObservableCollection<string>                     AvailableMetrics { get; } = [];
    public ObservableCollection<Dictionary<string, string>> TableRows        { get; } = [];
    public IReadOnlyList<string>                            TableHeaders     { get; private set; } = [];

    public bool HasSelectedConfig => SelectedConfig is not null;

    public event Action? ResultsUpdated;

    private ExperimentResult? _lastResult;

    public MainWindowViewModel() {
        foreach (var nc in DefaultSweep())
            Configs.Add(ConfigViewModel.FromNamedConfig(nc));
        SelectedConfig = Configs.FirstOrDefault();
    }

    partial void OnUseBuiltInDemoChanged(bool value) => CanBrowse = !value;

    partial void OnSelectedMetricChanged(string? value) {
        if (HasResults) ResultsUpdated?.Invoke();
    }

    [RelayCommand]
    private void AddConfig() {
        var vm = new ConfigViewModel { Name = $"config_{Configs.Count + 1}" };
        Configs.Add(vm);
        SelectedConfig = vm;
    }

    [RelayCommand]
    private void DuplicateConfig() {
        if (SelectedConfig is null) return;
        var nc  = SelectedConfig.ToNamedConfig();
        var dup = ConfigViewModel.FromNamedConfig(nc with { Name = nc.Name + "_2" });
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

        if (!UseBuiltInDemo && string.IsNullOrWhiteSpace(WorkloadPath)) {
            StatusText = "Specify an ELF file or switch to the built-in demo.";
            return;
        }

        IsRunning  = true;
        HasResults = false;
        StatusText = $"Running {Configs.Count} configuration(s)…";

        try {
            IWorkload workload = UseBuiltInDemo
                ? CreateBuiltInWorkload()
                : new ElfWorkload(WorkloadPath!);

            var namedConfigs     = Configs.Select(c => c.ToNamedConfig()).ToList();
            long maxTicks        = (long)(MaxTicks > 0 ? MaxTicks : 1_000_000);
            long warmupTicks     = (long)(WarmupTicks >= 0 ? WarmupTicks : 0);
            long snapshotInterval = (long)(SnapshotInterval >= 0 ? SnapshotInterval : 0);

            ExperimentResult result = await Task.Run(() =>
                Experiment.Run(workload, namedConfigs, new RvMechanism(), maxTicks, warmupTicks, snapshotInterval)
            );

            _lastResult = result;
            UpdateMetrics(result);
            PopulateTable(result);
            HasResults = true;
            StatusText = $"Done — {result.Runs.Count} run(s), {maxTicks:N0} max ticks each.";
            ResultsUpdated?.Invoke();
        }
        catch (Exception ex) {
            StatusText = $"Error: {ex.Message}";
        }
        finally {
            IsRunning = false;
        }
    }

    public void SetWorkloadPath(string path) {
        WorkloadPath    = path;
        UseBuiltInDemo  = false;
    }

    public (string[] names, double[] values) GetChartData() {
        if (_lastResult is null || SelectedMetric is null) return ([], []);

        var names  = new List<string>();
        var values = new List<double>();

        foreach (var run in _lastResult.Runs) {
            names.Add(run.Name);
            var counters = MergeCounters(run.Result.Snapshots);
            var dials    = MergeDials(run.Result.Snapshots);

            double value = 0;
            if (counters.TryGetValue(SelectedMetric, out long cv)) value = cv;
            else if (dials.TryGetValue(SelectedMetric, out double dv)) value = dv;
            values.Add(value);
        }

        return ([..names], [..values]);
    }

    private void UpdateMetrics(ExperimentResult result) {
        var counterCols = new SortedSet<string>();
        var dialCols    = new SortedSet<string>();

        foreach (var run in result.Runs)
        foreach (var snap in run.Result.Snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach (string k in snap.Counters.Keys) counterCols.Add($"{prefix}.{k}");
            foreach (string k in snap.Dials.Keys)    dialCols.Add($"{prefix}.{k}");
        }

        AvailableMetrics.Clear();
        foreach (string m in counterCols) AvailableMetrics.Add(m);
        foreach (string m in dialCols)    AvailableMetrics.Add(m);

        SelectedMetric = AvailableMetrics.FirstOrDefault(m => m.EndsWith(".ipc"))
                      ?? AvailableMetrics.FirstOrDefault(m => m.EndsWith(".cycles"))
                      ?? AvailableMetrics.FirstOrDefault();
    }

    private void PopulateTable(ExperimentResult result) {
        var counterCols = new SortedSet<string>();
        var dialCols    = new SortedSet<string>();

        foreach (var run in result.Runs)
        foreach (var snap in run.Result.Snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach (string k in snap.Counters.Keys) counterCols.Add($"{prefix}.{k}");
            foreach (string k in snap.Dials.Keys)    dialCols.Add($"{prefix}.{k}");
        }

        var headers = new List<string> { "name" };
        headers.AddRange(counterCols);
        headers.AddRange(dialCols);
        TableHeaders = headers;

        TableRows.Clear();
        foreach (var run in result.Runs) {
            var counters = MergeCounters(run.Result.Snapshots);
            var dials    = MergeDials(run.Result.Snapshots);
            var row      = new Dictionary<string, string> { ["name"] = run.Name };
            foreach (string col in counterCols)
                row[col] = counters.TryGetValue(col, out long lv) ? lv.ToString("N0") : "—";
            foreach (string col in dialCols)
                row[col] = dials.TryGetValue(col, out double dv) ? dv.ToString("G4") : "—";
            TableRows.Add(row);
        }
    }

    private static string GearRelPath(string ownerPath) {
        int dot = ownerPath.IndexOf('.');
        return dot < 0 ? ownerPath : ownerPath[(dot + 1)..];
    }

    private static Dictionary<string, long> MergeCounters(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, long>();
        foreach (var snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, long value) in snap.Counters) {
                string col = $"{prefix}.{key}";
                merged[col] = merged.GetValueOrDefault(col) + value;
            }
        }
        return merged;
    }

    private static Dictionary<string, double> MergeDials(IReadOnlyList<DialBoardSnapshot> snapshots) {
        var merged = new Dictionary<string, double>();
        foreach (var snap in snapshots) {
            string prefix = GearRelPath(snap.OwnerPath);
            foreach ((string key, double value) in snap.Dials)
                if (value != 0.0)
                    merged[$"{prefix}.{key}"] = value;
        }
        return merged;
    }

    private static IWorkload CreateBuiltInWorkload() {
        // Built-in demo: 100-iteration countdown loop
        uint[] words = [0x06400093, 0x00008663, 0xFFF08093, 0xFF9FF06F, 0x00100073];
        var bytes    = new byte[words.Length * 4];
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
        new("always_taken",     new TrainConfig(Predictor: BranchPredictorConfig.AlwaysTaken())),
        new("one_bit",          new TrainConfig(Predictor: BranchPredictorConfig.OneBit())),
        new("two_bit",          new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
        new("superscalar_2w",   new TrainConfig("superscalar", IssueWidth: 2)),
        new("ooo_2w",           new TrainConfig("ooo", Predictor: BranchPredictorConfig.TwoBit(), IssueWidth: 2)),
    ];
}
