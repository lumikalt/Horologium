#region

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mechanism;
using Orrery.Observation;
using Pipeline.Spec;
using Script;

#endregion

// ReSharper disable UnusedParameterInPartialMethod

namespace Face.ViewModels;

public sealed record StatRow(string Name, string Value);

/// <summary>
///     Backs the Configurator tab: edits a `.csx` architecture script, hot-reloads
///     it (debounced in-app edits and external saves via <see cref="FileSystemWatcher" /> both
///     converge on <see cref="ConfiguratorEngine.BuildAsync" />), and drives/observes the
///     resulting <see cref="MachineHandle" />.
/// </summary>
public partial class ConfiguratorViewModel : ObservableObject, IDisposable {
    private const int RunDelayMs = 50;

    private const string DefaultScript =
        """
        // Returns a MachineSpec — the last expression is the script's result.
        // Available: SingleCycleSpec, FiveStageSpec, OutOfOrderSpec, CacheHierarchySpec,
        // CachePathSpec, CacheLevelSpec, Rv32Mechanism, and the rest of Pipeline.Spec/Orrery.Spec.
        new MachineSpec(
            new FiveStageSpec(),
            () => new Rv32Mechanism(),
            CacheHierarchySpec.Unified(new CachePathSpec([new CacheLevelSpec(4096, 4, 64),]))
        )
        """;

    private MachineHandle? _handle;
    private CancellationTokenSource? _rebuildCts;
    private CancellationTokenSource? _runCts;
    private FileSystemWatcher? _watcher;
    private Timer? _watcherDebounce;

    public ConfiguratorViewModel() =>
        SelectedPreset = WorkloadPresets[0]; // triggers the first RebuildAsync via OnSelectedPresetChanged

    public ObservableCollection<WorkloadPreset> WorkloadPresets { get; } =
        [..MainWindowViewModel.DefaultWorkloadPresets,];

    public ObservableCollection<StatRow> Stats { get; } = [];

    [ObservableProperty] public partial string ScriptText { get; set; } = ConfiguratorViewModel.DefaultScript;

    [ObservableProperty] public partial string? ScriptFilePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowse))]
    public partial WorkloadPreset SelectedPreset { get; set; }

    [ObservableProperty] public partial string? WorkloadPath { get; set; }

    [ObservableProperty] public partial bool HasError { get; set; }

    [ObservableProperty] public partial string ErrorText { get; set; } = "";

    [ObservableProperty] public partial string StatusText { get; set; } = "Building…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool CanRun { get; set; }

    [ObservableProperty] public partial bool IsRunning { get; set; }

    public bool ShowBrowse => SelectedPreset.ElfFileName == "";

    public string? ScriptFileName => ScriptFilePath is { } p ? Path.GetFileName(p) : null;

    public string StatusLine => ScriptFileName is { } name ? $"{StatusText} — {name}" : StatusText;

    public void Dispose() {
        _rebuildCts?.Cancel();
        _runCts?.Cancel();
        _watcherDebounce?.Dispose();
        _watcher?.Dispose();
    }

    partial void OnSelectedPresetChanged(WorkloadPreset value) => _ = RebuildAsync();

    partial void OnWorkloadPathChanged(string? value) => _ = RebuildAsync();

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    public void SetWorkloadPath(string path) {
        WorkloadPath = path;
        SelectedPreset = WorkloadPresets.First(p => p.ElfFileName == "");
    }

    /// <summary>
    ///     Points hot-reload at a backing file: writes the current buffer there and starts watching it for external
    ///     edits.
    /// </summary>
    public async Task SetScriptFilePathAsync(string path) {
        await File.WriteAllTextAsync(path, ScriptText);
        ScriptFilePath = path;
        OnPropertyChanged(nameof(ScriptFileName));
        OnPropertyChanged(nameof(StatusLine));

        _watcher?.Dispose();
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) {
            NotifyFilter = NotifyFilters.LastWrite,
        };
        watcher.Changed += OnScriptFileChanged;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void OnScriptFileChanged(object? sender, FileSystemEventArgs e) {
        _watcherDebounce?.Dispose();
        _watcherDebounce = new Timer(_ => Dispatcher.UIThread.Post(TriggerReloadFromDisk), null, 300, Timeout.Infinite);
    }

    // The Timer callback needs a fire-and-forget entry point; naming it (rather than an inline
    // async lambda) lets it actually catch its own exceptions instead of crashing the dispatcher.
    private async void TriggerReloadFromDisk() {
        try { await ReloadFromDiskAndRebuildAsync(); }
        catch (Exception ex) {
            HasError = true;
            ErrorText = $"Reload from disk failed: {ex.Message}";
        }
    }

    private async Task ReloadFromDiskAndRebuildAsync() {
        if (ScriptFilePath is not { } path || !File.Exists(path)) return;
        try { ScriptText = await File.ReadAllTextAsync(path); }
        catch (IOException) { return; } // file mid-write; the next Changed event will retry

        await RebuildAsync();
    }

    [RelayCommand]
    public async Task RebuildAsync() {
        _rebuildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rebuildCts = cts;

        _runCts?.Cancel();
        IsRunning = false;

        if (SelectedPreset.ElfFileName == "" && string.IsNullOrWhiteSpace(WorkloadPath)) {
            HasError = true;
            ErrorText = "Specify an ELF file or select a different workload.";
            CanRun = false;
            return;
        }

        IWorkload workload = MainWindowViewModel.ResolveWorkload(SelectedPreset, WorkloadPath);
        ConfiguratorBuildResult result = await ConfiguratorEngine.BuildAsync(ScriptText, workload, cts.Token);
        if (cts.Token.IsCancellationRequested) return;

        if (!result.Success) {
            HasError = true;
            ErrorText = result.Error ?? "Unknown error.";
            StatusText = "Build failed — showing the last-good machine below.";
            CanRun = _handle is not null;
            return;
        }

        HasError = false;
        ErrorText = "";
        _handle = result.Handle;
        _handle!.Train.BeginStepping();
        CanRun = true;
        StatusText = "Ready.";
        RefreshStats();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Step() {
        if (_handle is null) return;
        bool running = _handle.Train.StepCycle();
        RefreshStats();
        StatusText = running ? StatusText : "Halted.";
        if (!running) CanRun = false;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run() {
        if (_handle is null) return;
        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;

        try {
            var stepsSinceRefresh = 0;
            while (CanRun && !cts.Token.IsCancellationRequested) {
                bool running = _handle.Train.StepCycle();
                if (!running) {
                    CanRun = false;
                    StatusText = "Halted.";
                    break;
                }

                // Refresh the stat panel every 10 steps rather than every step — SnapshotDials()
                // walks every Gear's DialBoard, too expensive to pay on each individual cycle.
                if (++stepsSinceRefresh >= 10) {
                    RefreshStats();
                    stepsSinceRefresh = 0;
                }

                await Task.Delay(ConfiguratorViewModel.RunDelayMs, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally {
            RefreshStats();
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private async Task Reset() => await RebuildAsync();

    private void RefreshStats() {
        if (_handle is null) return;
        ConfiguratorStats stats = ConfiguratorEngine.SnapshotStats(_handle);
        Stats.Clear();
        if (stats.L1Hits is { } l1H) Stats.Add(new StatRow("L1 hits / misses", $"{l1H:N0} / {stats.L1Misses:N0}"));
        if (stats.L2Hits is { } l2H) Stats.Add(new StatRow("L2 hits / misses", $"{l2H:N0} / {stats.L2Misses:N0}"));
        if (stats.L3Hits is { } l3H) Stats.Add(new StatRow("L3 hits / misses", $"{l3H:N0} / {stats.L3Misses:N0}"));
        if (stats.TlbHits is { } tH) Stats.Add(new StatRow("TLB hits / misses", $"{tH:N0} / {stats.TlbMisses:N0}"));
        foreach (DialBoardSnapshot snap in stats.Dials) {
            foreach ((string key, long value) in snap.Counters)
                Stats.Add(new StatRow($"{snap.OwnerPath}.{key}", $"{value:N0}"));
            foreach ((string key, double value) in snap.Dials)
                if (value != 0.0)
                    Stats.Add(new StatRow($"{snap.OwnerPath}.{key}", value.ToString("G4")));
        }
    }
}