#region

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Face.Models;
using Mechanism;
using Orrery.Observation;
using Orrery.Spec;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Config;
using RiscV32.Memory;
using RiscV64;
using RiscV64.Memory;

#endregion

// ReSharper disable UnusedParameterInPartialMethod

namespace Face.ViewModels;

public record WorkloadPreset(string Label, string? ElfFileName, int MemoryBytes = 0);

public partial class MainWindowViewModel : ObservableObject {
    private const int BenchmarkMemoryBytes = 4 * 1024 * 1024;

    // Matches RealLinkedBinaryTests' proven-safe margin for a real linked musl binary (stack +
    // TLS + a small static libc footprint) — larger than BenchmarkMemoryBytes, which is tuned for
    // bare-metal HTIF-style RV32 benchmark ELFs, a different memory-layout concern.
    private const int CompiledSourceMemoryBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     Default text for the Assembler tab's "UVE Kernel" sub-tab source editor — a real,
    ///     previously-validated UVE kernel (2 load streams + 1 store stream, <c>so.a.mac.fp</c>
    ///     reduction, <c>so.b.nc</c> loop) rather than an empty box, so a first-time user has
    ///     something that compiles and runs correctly to start from.
    /// </summary>
    private const string SampleUveKernel = """
                                           #include <stdio.h>
                                           #include <stdint.h>

                                           static float a[4] = {1.0f, 2.0f, 3.0f, 4.0f};
                                           static float b[4] = {10.0f, 20.0f, 30.0f, 40.0f};
                                           static float out;

                                           static void dot4(float *av, float *bv, float *outv, uint64_t n) {
                                               uint64_t one = 1;
                                               asm volatile(
                                                   "ss.sta.ld.w u1, %[a] \n"
                                                   "ss.end      u1, zero, %[n], %[one] \n"
                                                   "ss.sta.ld.w u2, %[b] \n"
                                                   "ss.end      u2, zero, %[n], %[one] \n"
                                                   "ss.sta.st.w u3, %[out] \n"
                                                   "ss.end      u3, zero, %[one], %[one] \n"
                                                   "so.v.dp.w   u4, zero, p0 \n"
                                                   ".Lloop: \n"
                                                   "so.a.mac.fp u4, u1, u2, p0 \n"
                                                   "so.b.nc     u1, .Lloop \n"
                                                   "so.a.adde.fp u3, u4, p0 \n"
                                                   :
                                                   : [a] "r"(av), [b] "r"(bv), [out] "r"(outv), [n] "r"(n), [one] "r"(one)
                                                   : "memory"
                                               );
                                           }

                                           int main() {
                                               dot4(a, b, &out, 4);
                                               printf("%f\n", out);
                                               return 0;
                                           }
                                           """;

    private static readonly string BenchmarksDir =
        Path.Combine(AppContext.BaseDirectory, "benchmarks");

    private ExperimentResult? _lastResult;

    public MainWindowViewModel() {
        foreach (NamedConfig nc in DefaultSweep()) {
            Configs.Add(ConfigViewModel.FromNamedConfig(nc));
            HartCaches.Add(new HartCacheViewModel());
        }

        SelectedConfig = Configs.FirstOrDefault();
        SelectedPreset = WorkloadPresets[0];
    }

    /// <summary>
    ///     Shared workload preset list — also consumed by <see cref="ConfiguratorViewModel" />
    ///     so both tabs offer the same benchmark set from one place.
    /// </summary>
    internal static IReadOnlyList<WorkloadPreset> DefaultWorkloadPresets { get; } = [
        new("Built-in demo  (100-iter countdown loop)", null),
        new("Benchmark — coremark", "coremark.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — dhrystone", "dhrystone.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — gcd", "gcd.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — median", "median.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — memcpy", "memcpy.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — mm", "mm.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — multiply", "multiply.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — pchase", "pchase.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — qsort", "qsort.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — rsort", "rsort.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — spmv", "spmv.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — towers", "towers.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — treesum", "treesum.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Benchmark — vvadd", "vvadd.elf", MainWindowViewModel.BenchmarkMemoryBytes),
        new("Custom ELF…", ""),
    ];

    public ObservableCollection<WorkloadPreset> WorkloadPresets { get; } =
        [..DefaultWorkloadPresets,];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowse))]
    public partial WorkloadPreset SelectedPreset { get; set; }

    [ObservableProperty] public partial string? WorkloadPath { get; set; } = null;

    /// <summary>
    ///     Source text for the Assembler tab's "UVE Kernel" sub-tab (see
    ///     <see cref="IsUveKernelSubTab" />), compiled via <see cref="Face.Models.CompiledSourceWorkload" />
    ///     when "Compile &amp; Run" is clicked.
    /// </summary>
    [ObservableProperty]
    public partial string SourceCode { get; set; } = MainWindowViewModel.SampleUveKernel;

    /// <summary>
    ///     Path to the UVE author's patched clang binary (github.com/lumicrespo/UVEcompiler) — not
    ///     part of <c>flake.nix</c>, so unlike every other toolchain Face shells out to, this one has
    ///     no PATH-search fallback and must be supplied by the user.
    /// </summary>
    [ObservableProperty]
    public partial string? UveClangPath { get; set; } = null;

    /// <summary>Captured stdout from the most recent "UVE Kernel" compile-and-run (see <see cref="IsUveKernelSubTab" />).</summary>
    [ObservableProperty]
    public partial string ConsoleOutput { get; set; } = "";

    /// <summary>
    ///     Selected index of the Assembler tab's own sub-tab strip (0 = CPU, 1 = UVE Kernel) — tracked
    ///     here, not in <see cref="AssemblerViewModel" />, because the UVE Kernel sub-tab's compile-
    ///     and-run state (<see cref="SourceCode" />/<see cref="UveClangPath" />/<see cref="ConsoleOutput" />/
    ///     <see cref="SelectedConfig" />) lives on this ViewModel, alongside the rest of the
    ///     Chart/Table machinery it reuses (a single <see cref="Configs" /> entry, not a parallel list).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUveKernelSubTab))]
    public partial int AssemblerSubTabIndex { get; set; }

    public bool IsUveKernelSubTab => AssemblerSubTabIndex == 1;

    /// <summary>
    ///     Selects both the ELF loader (<see cref="Rv32ElfWorkload" />/<see cref="Rv64ElfWorkload" />)
    ///     and the mechanism (<see cref="Rv32Mechanism" />/<see cref="Rv64Mechanism" />) for the whole
    ///     Run/Trace comparison, not per-config — <see cref="Experiment.Run" /> takes one shared
    ///     mechanism factory across every <see cref="TrainConfig" /> in the sweep, so ISA can't
    ///     meaningfully differ between configs being compared in the same run.
    /// </summary>
    [ObservableProperty]
    public partial string SelectedIsa { get; set; } = "rv32";

    public string[] IsaOptions { get; } = ["rv32", "rv64",];

    [ObservableProperty] public partial decimal MaxTicks { get; set; } = 1_000_000;

    [ObservableProperty] public partial decimal WarmupTicks { get; set; } = 0;

    [ObservableProperty] public partial decimal SnapshotInterval { get; set; } = 0;

    [ObservableProperty] public partial bool IsRunning { get; set; } = false;

    [ObservableProperty] public partial string StatusText { get; set; } = "Ready — configure and run an experiment.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedConfig), nameof(SelectedHartCache))]
    public partial ConfigViewModel? SelectedConfig { get; set; } = null;

    /// <summary>
    ///     <see cref="HartCaches" /> is kept parallel to <see cref="Configs" /> by index (see
    ///     <see cref="AddConfig" />/<see cref="DuplicateConfig" />/<see cref="RemoveConfig" />), so
    ///     this is always the private-cache config for whichever hart <see cref="SelectedConfig" />
    ///     is — used by multi-hart mode's cache editor.
    /// </summary>
    public HartCacheViewModel? SelectedHartCache =>
        SelectedConfig is null ? null : HartCaches.ElementAtOrDefault(Configs.IndexOf(SelectedConfig));

    [ObservableProperty] public partial string? SelectedMetric { get; set; } = null;

    private static bool HasResults { get; set; }

    [ObservableProperty] public partial decimal TraceMaxTicks { get; set; } = 2_000;

    [ObservableProperty] public partial bool IsTracing { get; set; } = false;

    [ObservableProperty] public partial WaterfallData? CurrentWaterfall { get; set; } = null;

    [ObservableProperty] public partial bool WaveformCumulative { get; set; } = false;

    [ObservableProperty]
    public partial string WaveformStatusText { get; set; } =
        "Run with Snapshot interval > 0 to record time-series signals.";

    public ObservableCollection<SignalToggle> AvailableSignals { get; } = [];

    [ObservableProperty]
    public partial string PEventStatusText { get; set; } = "Select a configuration and click Trace.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartTableTab), nameof(IsPEventsTab), nameof(IsAssemblerTab))]
    public partial int SelectedTabIndex { get; set; } = 0;

    public bool IsChartTableTab => SelectedTabIndex == 0;
    public bool IsPEventsTab => SelectedTabIndex == 1;
    public bool IsAssemblerTab => SelectedTabIndex == 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    public partial bool IsDarkTheme { get; set; } = true;

    public string ThemeLabel => IsDarkTheme ? "Dark" : "Light";

    public bool ShowBrowse => SelectedPreset.ElfFileName == "";

    public AssemblerViewModel Assembler { get; } = new();

    public ConfiguratorViewModel Configurator { get; } = new();

    public ObservableCollection<ConfigViewModel> Configs { get; } = [];

    /// <summary>
    ///     Multi-hart mode's per-hart private cache config, paired 1:1 by index with
    ///     <see cref="Configs" /> (which supplies each hart's pipeline/predictor config in this
    ///     mode — its own cache section is not used, see <see cref="HartCacheViewModel" />). Kept in
    ///     lockstep with <see cref="Configs" /> by <see cref="AddConfig" />/<see cref="DuplicateConfig" />/
    ///     <see cref="RemoveConfig" /> regardless of <see cref="IsMulticoreMode" />, so entering
    ///     multi-hart mode never finds a stale or mismatched list.
    /// </summary>
    private ObservableCollection<HartCacheViewModel> HartCaches { get; } = [];

    public MultiHartSettingsViewModel MultiHartSettings { get; } = new();

    [ObservableProperty] public partial bool IsMulticoreMode { get; set; }

    public ObservableCollection<string> AvailableMetrics { get; } = [];
    public ObservableCollection<Dictionary<string, string>> TableRows { get; } = [];
    public IReadOnlyList<string> TableHeaders { get; private set; } = [];

    public bool HasSelectedConfig => SelectedConfig is not null;

    /// <summary>
    ///     Entering the UVE Kernel sub-tab auto-selects an existing "ooo" config when one is
    ///     available — the only pipeline that steps the <c>StreamingEngine</c> UVE instructions need
    ///     (see <see cref="RunCompiledSource" />). Not "cpr": <c>CprTrain</c> throws outright on any
    ///     Vector/UVE instruction rather than merely failing to step the engine. Leaves the selection
    ///     alone otherwise; <see cref="RunCompiledSource" /> still validates and reports clearly if no
    ///     compatible config exists at all.
    /// </summary>
    partial void OnAssemblerSubTabIndexChanged(int value) {
        if (value != 1 || SelectedConfig?.Pipeline == "ooo") return;
        ConfigViewModel? candidate = Configs.FirstOrDefault(c => c.Pipeline == "ooo");
        if (candidate is not null) SelectedConfig = candidate;
    }

    /// <summary>
    ///     No RV64 benchmark ELFs exist in <c>TestBinaries/benchmarks/</c> today (only RV32 ones,
    ///     copied under <see cref="BenchmarksDir" />) — so under RV64, only the built-in demo and a
    ///     user-supplied custom ELF are offered; the RV32 benchmark presets are hidden rather than
    ///     left selectable-but-broken (they'd hit <see cref="Rv64ElfLoader" />'s clean
    ///     "only ELF64 is supported" rejection instead of running).
    /// </summary>
    private static IEnumerable<WorkloadPreset> PresetsForIsa(string isa) =>
        isa == "rv64" ? DefaultWorkloadPresets.Where(p => string.IsNullOrEmpty(p.ElfFileName)) : DefaultWorkloadPresets;

    partial void OnSelectedIsaChanged(string value) {
        WorkloadPreset previouslySelected = SelectedPreset;
        WorkloadPresets.Clear();
        foreach (WorkloadPreset p in PresetsForIsa(value)) WorkloadPresets.Add(p);
        SelectedPreset = WorkloadPresets.FirstOrDefault(p => p == previouslySelected) ?? WorkloadPresets[0];
    }

    public event Action? WaveformUpdated;

    partial void OnWaveformCumulativeChanged(bool value) => WaveformUpdated?.Invoke();

    partial void OnIsDarkThemeChanged(bool value) =>
        Application.Current!.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;

    public event Action? ResultsUpdated;

    // ReSharper disable once PartialMethodParameterNameMismatch
    partial void OnSelectedMetricChanged(string? value) {
        if (HasResults) ResultsUpdated?.Invoke();
    }

    [RelayCommand]
    private void AddConfig() {
        var vm = new ConfigViewModel { Name = $"config_{Configs.Count + 1}", };
        Configs.Add(vm);
        HartCaches.Add(new HartCacheViewModel());
        SelectedConfig = vm;
    }

    [RelayCommand]
    private void DuplicateConfig() {
        if (SelectedConfig is null) return;
        int idx = Configs.IndexOf(SelectedConfig);
        var nc = SelectedConfig.ToNamedConfig();
        ConfigViewModel dup = ConfigViewModel.FromNamedConfig(nc with { Name = nc.Name + "_2", });
        Configs.Add(dup);
        HartCacheViewModel srcCache = HartCaches[idx];
        HartCaches.Add(
            new HartCacheViewModel {
                Enabled = srcCache.Enabled,
                CapacityKb = srcCache.CapacityKb,
                Ways = srcCache.Ways,
                BlockBytes = srcCache.BlockBytes,
                MissLatency = srcCache.MissLatency,
                PoolId = srcCache.PoolId,
            }
        );
        SelectedConfig = dup;
    }

    [RelayCommand]
    private void RemoveConfig() {
        if (SelectedConfig is null) return;
        int idx = Configs.IndexOf(SelectedConfig);
        Configs.Remove(SelectedConfig);
        HartCaches.RemoveAt(idx);
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
            IWorkload workload = ResolveWorkload(SelectedPreset, WorkloadPath, SelectedIsa);

            List<NamedConfig> namedConfigs = Configs.Select(c => c.ToNamedConfig()).ToList();
            var maxTicks = (long)(MaxTicks > 0 ? MaxTicks : 1_000_000);
            var warmupTicks = (long)(WarmupTicks >= 0 ? WarmupTicks : 0);
            var snapshotInterval = (long)(SnapshotInterval >= 0 ? SnapshotInterval : 0);
            string isa = SelectedIsa;

            ExperimentResult result = await Task.Run(() =>
                                                         Experiment.Run(
                                                             workload, namedConfigs,
                                                             () => CreateMechanism(
                                                                 isa, workload.HtifTohostAddress
                                                             ),
                                                             maxTicks,
                                                             warmupTicks, snapshotInterval
                                                         )
            );

            _lastResult = result;
            UpdateMetrics(result);
            PopulateTable(result);
            UpdateSignals(result);
            HasResults = true;
            StatusText = $"Done — {result.Runs.Count} run(s), {maxTicks:N0} max ticks each.";
            ResultsUpdated?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsRunning = false; }
    }

    /// <summary>
    ///     Compiles <see cref="SourceCode" /> (via <see cref="Face.Models.CompiledSourceWorkload" />)
    ///     and runs it under <see cref="SelectedConfig" /> alone — a dedicated command, not routed
    ///     through <see cref="Run" />'s <see cref="Configs" /> sweep, since the only reason to restrict
    ///     this to a single config is also the reason a sweep would be broken here: of the default
    ///     sweep's 9 configs, only "ooo_2w" steps the <c>StreamingEngine</c> UVE instructions rely on
    ///     — the rest would either hang to the tick budget (pipelines that silently never step it) or
    ///     throw outright ("cpr", which rejects any Vector/UVE instruction). A real linked ELF also
    ///     needs a psABI initial stack and Linux syscall emulation for <c>printf</c> —
    ///     <see cref="Experiment.RunLinkedElf" /> provides both, unlike
    ///     <see cref="Experiment.RunOne" />'s bare-metal-entry assumption used by the rest of this tab.
    /// </summary>
    [RelayCommand]
    private async Task RunCompiledSource() {
        if (SelectedConfig is null) {
            StatusText = "Add at least one configuration.";
            return;
        }

        if (SelectedConfig.Pipeline is not "ooo") {
            StatusText = "Compiled UVE kernels need an 'ooo' pipeline config — "
                       + "the StreamingEngine only runs under that one (not even 'cpr').";
            return;
        }

        if (string.IsNullOrWhiteSpace(UveClangPath)) {
            StatusText = "Set the UVE clang path first.";
            return;
        }

        IsRunning = true;
        HasResults = false;
        ConsoleOutput = "";
        StatusText = "Compiling…";

        try {
            Rv64ElfWorkload workload = await CompiledSourceWorkload.CompileAsync(
                UveClangPath, SourceCode, MainWindowViewModel.CompiledSourceMemoryBytes
            );

            StatusText = "Running…";
            var maxTicks = (long)(MaxTicks > 0 ? MaxTicks : 1_000_000);
            var named = SelectedConfig.ToNamedConfig();

            (ExperimentResult result, string output, bool halted) = await Task.Run(() =>
                Experiment.RunLinkedElf(
                    workload, named, handler => new Rv64Mechanism(syscallHandler: handler), ["kernel",],
                    maxTicks
                )
            );

            _lastResult = result;
            UpdateMetrics(result);
            PopulateTable(result);
            UpdateSignals(result);
            ConsoleOutput = output;
            HasResults = true;
            StatusText = halted
                ? $"Done — {maxTicks:N0} max ticks."
                : $"Hit the {maxTicks:N0}-tick budget without the guest exiting.";
            ResultsUpdated?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsRunning = false; }
    }

    /// <summary>
    ///     Runs Face's fixed multi-hart demo (see <see cref="Experiment.RunMulticore" /> — there is
    ///     no workload picker in this mode, only one hand-verified LR/SC program every hart shares).
    ///     <see cref="Configs" /> supplies each hart's pipeline/predictor config; <see cref="HartCaches" />
    ///     (paired 1:1 by index) supplies its private cache. Results render in the same Chart/Table
    ///     views as <see cref="Run" /> — skips <see cref="UpdateSignals" /> since multi-hart results
    ///     never carry a time series.
    /// </summary>
    [RelayCommand]
    private async Task RunMulticore() {
        if (Configs.Count == 0) {
            StatusText = "Add at least one hart.";
            return;
        }

        IsRunning = true;
        HasResults = false;
        StatusText = $"Running {Configs.Count} hart(s)…";

        try {
            var hartConfigs = new List<(TrainConfig, CacheLevelSpec?, int)>(Configs.Count);
            hartConfigs.AddRange(
                Configs.Select((t, i) => (t.ToNamedConfig().Config, HartCaches[i].ToCacheLevelSpec(),
                                          HartCaches[i].PoolId)
                )
            );

            CacheLevelSpec? sharedLlc = MultiHartSettings.ToSharedLlcSpec();
            var bus = MultiHartSettings.ToCoherenceBusKind();
            bool concurrentMode = MultiHartSettings.ConcurrentMode;
            var maxTicks = (long)(MultiHartSettings.MaxTicks > 0 ? MultiHartSettings.MaxTicks : 100_000);

            ExperimentResult result
                = await Task.Run(() => Experiment.RunMulticore(hartConfigs, sharedLlc, bus, concurrentMode, maxTicks)
                );

            _lastResult = result;
            UpdateMetrics(result);
            PopulateTable(result);
            HasResults = true;
            StatusText = $"Done — {result.Runs.Count} row(s), {maxTicks:N0} max ticks.";
            ResultsUpdated?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task Trace() {
        if (SelectedConfig is null) {
            PEventStatusText = "Select a configuration to trace.";
            return;
        }

        var nc = SelectedConfig.ToNamedConfig();
        if (SelectedPreset.ElfFileName == "" && string.IsNullOrWhiteSpace(WorkloadPath)) {
            PEventStatusText = "Specify an ELF file or select a different workload.";
            return;
        }

        IsTracing = true;
        PEventStatusText = $"Tracing '{nc.Name}'…";

        try {
            IWorkload workload = ResolveWorkload(SelectedPreset, WorkloadPath, SelectedIsa);

            var maxTicks = (long)(TraceMaxTicks > 0 ? TraceMaxTicks : 2_000);
            string isa = SelectedIsa;
            PEventLog plog = await Task.Run(() =>
                                                Experiment.Trace(
                                                    workload, nc,
                                                    CreateMechanism(
                                                        isa, workload.HtifTohostAddress
                                                    ),
                                                    maxTicks
                                                )
            );

            if (plog.Events.Count == 0) {
                PEventStatusText = "No events recorded. The workload may not have executed any instructions.";
                CurrentWaterfall = null;
                return;
            }

            CurrentWaterfall = BuildWaterfall(plog);
            int instrCount = plog.Events.Select(e => e.InstrId).Distinct().Count();
            int shown = Math.Min(instrCount, 500);
            long minCy = CurrentWaterfall.MinCycle;
            long maxCy = CurrentWaterfall.MaxCycle;
            string extra = instrCount > 500 ? $", first {shown} shown" : "";
            PEventStatusText = $"{instrCount:N0} instructions traced{extra}, cycles {minCy}–{maxCy}.";
        }
        catch (Exception ex) { PEventStatusText = $"Error: {ex.Message}"; }
        finally { IsTracing = false; }
    }

    private static WaterfallData BuildWaterfall(PEventLog plog) {
        // Compute cycle-level maps first; SpecPc per row is derived from these.
        // instrId=0 is the sentinel used by FetchStall events — excluded from instruction rows.
        Dictionary<long, ulong> fetchPcPerCycle = plog.Events
                                                      .Where(e => e.Kind is PEventKind.Fetch or PEventKind.FetchStall)
                                                      .GroupBy(e => e.Cycle)
                                                      .ToDictionary(g => g.Key, g => g.Min(e => e.Pc));

        IReadOnlySet<long> flushCycles = plog.Events
                                             .Where(e => e.Kind == PEventKind.Flush)
                                             .Select(e => e.Cycle)
                                             .ToHashSet();

        IReadOnlySet<long> fetchStallCycles = plog.Events
                                                  .Where(e => e.Kind == PEventKind.FetchStall)
                                                  .Select(e => e.Cycle)
                                                  .ToHashSet();

        List<IGrouping<ulong, PEvent>> groups = plog.Events
                                                    .Where(e => e.InstrId != 0)
                                                    .GroupBy(e => e.InstrId)
                                                    .OrderBy(g => g.Key)
                                                    .Take(500)
                                                    .ToList();

        List<WaterfallRow> rows = groups.Select(g => {
                PEvent? fetchEv = g.Where(e => e.Kind == PEventKind.Fetch)
                                   .Select(e => (PEvent?)e)
                                   .FirstOrDefault();
                ulong pc = fetchEv?.Pc ?? g.First().Pc;
                ulong specPc = fetchEv is { } fe && fetchPcPerCycle.TryGetValue(fe.Cycle, out ulong fpc)
                    ? fpc
                    : pc;
                string disasm = plog.Disassembly.TryGetValue(g.Key, out string? d) ? d : $"0x{pc:X}";

                // Collapse multiple events at the same cycle (Flush wins, then by priority).
                var byKey = new Dictionary<long, PEventKind>();
                foreach (PEvent ev in g)
                    if (!byKey.TryGetValue(ev.Cycle, out PEventKind existing)
                     || Priority(ev.Kind) > Priority(existing))
                        byKey[ev.Cycle] = ev.Kind;

                // Build ordered spans. Flush terminates the instruction — drop anything after it.
                List<(long Cycle, PEventKind Kind)> ordered = byKey
                                                             .OrderBy(kv => kv.Key)
                                                             .Select(kv => (kv.Key, kv.Value))
                                                             .ToList();
                int flushIdx = ordered.FindIndex(t => t.Kind == PEventKind.Flush);
                if (flushIdx >= 0) ordered = ordered[..(flushIdx + 1)];

                var spans = new List<PSpan>(ordered.Count);
                for (var i = 0; i < ordered.Count; i++) {
                    long start = ordered[i].Cycle;
                    long end = i + 1 < ordered.Count ? ordered[i + 1].Cycle : start + 1;
                    spans.Add(new PSpan(ordered[i].Kind, start, end));
                }

                plog.TryGetSourceValues(g.Key, out IReadOnlyList<int> srcRegs, out IReadOnlyList<ulong> srcVals);
                plog.TryGetDestValue(g.Key, out int destReg, out ulong destVal);
                return new WaterfallRow(g.Key, pc, specPc, disasm, spans, srcRegs, srcVals, destReg, destVal);
            }
        ).ToList();

        if (rows.Count == 0) return new WaterfallData(rows, 0, 0, fetchPcPerCycle, flushCycles, fetchStallCycles, 0);

        long minCy = rows.SelectMany(r => r.Spans).Min(s => s.Start);
        long maxCy = rows.SelectMany(r => r.Spans).Max(s => s.End - 1);
        ulong basePc = rows.Min(r => Math.Min(r.Pc, r.SpecPc));

        return new WaterfallData(rows, minCy, maxCy, fetchPcPerCycle, flushCycles, fetchStallCycles, basePc);

        static int Priority(PEventKind k) => k switch {
            PEventKind.Flush      => 7,
            PEventKind.Retire     => 6,
            PEventKind.Execute    => 5,
            PEventKind.Issue      => 4,
            PEventKind.Dispatch   => 3,
            PEventKind.Rename     => 2,
            PEventKind.Decode     => 1,
            PEventKind.Fetch      => 0,
            PEventKind.FetchStall => -1,
            _                     => 0,
        };
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

    public IReadOnlyList<(string Label, double[] Ticks, double[] Values)> GetWaveformSeries() {
        if (_lastResult is null) return [];
        List<string> selected = AvailableSignals.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        if (selected.Count == 0) return [];

        bool multiRun = _lastResult.Runs.Count > 1;
        var series = new List<(string, double[], double[])>();
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (RunRecord run in _lastResult.Runs)
            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
        foreach (string name in selected) {
            Signal? sig = SignalExtractor.Extract(run.Result, name, WaveformCumulative);
            if (sig is null) continue;
            series.Add((multiRun ? $"{run.Name} · {name}" : name, sig.Ticks, sig.Values));
        }

        return series;
    }

    private void UpdateSignals(ExperimentResult result) {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (RunRecord run in result.Runs)
        foreach (string s in SignalExtractor.ListSignals(run.Result))
            names.Add(s);

        HashSet<string> previous = AvailableSignals.Where(s => s.IsSelected).Select(s => s.Name).ToHashSet();
        AvailableSignals.Clear();
        foreach (SignalToggle toggle in names.Select(name => new SignalToggle(name, previous.Contains(name)))) {
            toggle.PropertyChanged += (_, _) => WaveformUpdated?.Invoke();
            AvailableSignals.Add(toggle);
        }

        // Preselect windowed IPC on a fresh result so the tab isn't empty.
        if (AvailableSignals.Count > 0 && AvailableSignals.All(s => !s.IsSelected)) {
            SignalToggle def = AvailableSignals.FirstOrDefault(s => s.Name.EndsWith(".ipc (windowed)"))
                            ?? AvailableSignals[0];
            def.IsSelected = true;
        }

        WaveformStatusText = names.Count == 0
            ? "No time series recorded — set Snapshot interval > 0 and re-run."
            : $"{names.Count} signal(s) available across {result.Runs.Count} run(s).";
        WaveformUpdated?.Invoke();
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

    /// <summary>
    ///     Resolves a <see cref="WorkloadPreset" /> selection to a runnable <see cref="IWorkload" />,
    ///     loaded with the matching ISA's ELF loader. <paramref name="isa" /> defaults to
    ///     <c>"rv32"</c> for callers (e.g. <see cref="ConfiguratorViewModel" />) that don't offer an
    ///     ISA selector of their own — the Configurator tab's `.csx` script already picks its own
    ///     mechanism regardless of this method, so an RV64-targeting script there still needs a
    ///     matching RV64 workload; that pairing isn't wired up yet.
    /// </summary>
    internal static IWorkload ResolveWorkload(WorkloadPreset preset, string? workloadPath, string isa = "rv32") =>
        (preset.ElfFileName, isa) switch {
            (null, _)    => CreateBuiltInWorkload(),
            ("", "rv64") => new Rv64ElfWorkload(workloadPath!),
            ("", _)      => new Rv32ElfWorkload(workloadPath!),
            (var fn, "rv64") => new Rv64ElfWorkload(
                Path.Combine(MainWindowViewModel.BenchmarksDir, fn),
                preset.MemoryBytes
            ),
            var (fn, _) => new Rv32ElfWorkload(
                Path.Combine(MainWindowViewModel.BenchmarksDir, fn),
                preset.MemoryBytes
            ),
        };

    /// <summary>Builds the mechanism matching <paramref name="isa" /> (<c>"rv32"</c> or <c>"rv64"</c>).</summary>
    private static IMechanism CreateMechanism(string isa, ulong? htifTohost) =>
        isa == "rv64" ? new Rv64Mechanism(htifTohost) : new Rv32Mechanism(htifTohost);

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
        new("superscalar_2w", new TrainConfig("superscalar", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 2)),
        new("ooo_2w", new TrainConfig("ooo", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 2)),
        new("cpr_2w", new TrainConfig("cpr", Predictor: BranchPredictorConfig.NBit(), IssueWidth: 2)),
        new("dae", new TrainConfig("dae")),
    ];
}