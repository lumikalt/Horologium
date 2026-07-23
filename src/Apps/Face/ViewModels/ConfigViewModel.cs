#region

using CommunityToolkit.Mvvm.ComponentModel;
using Orrery.Cache;
using RiscV32.Analysis;
using RiscV32.Config;

#endregion

// ReSharper disable UnusedParameterInPartialMethod

namespace Face.ViewModels;

public partial class ConfigViewModel : ObservableObject {
    [ObservableProperty] public partial string Name { get; set; } = "config";

    [ObservableProperty] public partial string Pipeline { get; set; } = "five_stage";

    [ObservableProperty] public partial bool ForwardingEnabled { get; set; } = true;

    [ObservableProperty] public partial string PredictorType { get; set; } = "none";

    [ObservableProperty] public partial int PredictorBits { get; set; } = 2;

    [ObservableProperty] public partial int PredictorTableSize { get; set; } = 1024;

    [ObservableProperty] public partial int PredictorHistoryBits { get; set; } = 8;

    [ObservableProperty] public partial int PredictorPcBits { get; set; } = 4;

    [ObservableProperty] public partial int CorrelatedM { get; set; } = 2;

    [ObservableProperty] public partial int CorrelatedN { get; set; } = 2;

    [ObservableProperty] public partial int CorrelatedBhtSize { get; set; } = 1024;

    [ObservableProperty] public partial int PerceptronHistoryLength { get; set; } = 24;

    [ObservableProperty] public partial int PerceptronTableSize { get; set; } = 256;

    [ObservableProperty] public partial int HashedPerceptronTableSize { get; set; } = 512;

    [ObservableProperty] public partial int TournamentLocalHistoryBits { get; set; } = 10;

    [ObservableProperty] public partial int TournamentLocalTableSize { get; set; } = 1024;

    [ObservableProperty] public partial int TournamentGlobalHistoryBits { get; set; } = 12;

    [ObservableProperty] public partial int ImliPhtSize { get; set; } = 65536;

    [ObservableProperty] public partial int ImliBtbSize { get; set; } = 1024;

    [ObservableProperty] public partial string CbpLibraryPath { get; set; } = "";

    [ObservableProperty] public partial string CbpNgLibraryPath { get; set; } = "";

    [ObservableProperty] public partial bool ICacheEnabled { get; set; } = false;

    [ObservableProperty] public partial int ICacheCapacityKb { get; set; } = 32;

    [ObservableProperty] public partial int ICacheWays { get; set; } = 4;

    [ObservableProperty] public partial int ICacheBlockBytes { get; set; } = 32;

    [ObservableProperty] public partial int ICacheMissLatency { get; set; } = 10;

    [ObservableProperty] public partial int ICacheTagLatency { get; set; } = 0;

    [ObservableProperty] public partial int ICacheDataLatency { get; set; } = 0;

    [ObservableProperty] public partial string ICacheWritePolicy { get; set; } = "write_through";

    [ObservableProperty] public partial string ICacheWriteMissPolicy { get; set; } = "no_write_allocate";

    [ObservableProperty] public partial int ICacheWbCapacity { get; set; } = 0;

    [ObservableProperty] public partial int ICacheBankCount { get; set; } = 1;

    [ObservableProperty] public partial int ICacheReadPorts { get; set; } = 0;

    [ObservableProperty] public partial int ICacheWritePorts { get; set; } = 0;

    [ObservableProperty] public partial int ICacheSectorBytes { get; set; } = 0;

    [ObservableProperty] public partial int ICacheVictimCacheEntries { get; set; } = 0;

    [ObservableProperty] public partial int ICacheVictimCacheHitLatency { get; set; } = 1;

    [ObservableProperty] public partial bool DCacheEnabled { get; set; } = false;

    [ObservableProperty] public partial int DCacheCapacityKb { get; set; } = 32;

    [ObservableProperty] public partial int DCacheWays { get; set; } = 4;

    [ObservableProperty] public partial int DCacheBlockBytes { get; set; } = 32;

    [ObservableProperty] public partial int DCacheMissLatency { get; set; } = 10;

    [ObservableProperty] public partial int DCacheTagLatency { get; set; } = 0;

    [ObservableProperty] public partial int DCacheDataLatency { get; set; } = 0;

    [ObservableProperty] public partial string DCacheWritePolicy { get; set; } = "write_through";

    [ObservableProperty] public partial string DCacheWriteMissPolicy { get; set; } = "no_write_allocate";

    [ObservableProperty] public partial int DCacheWbCapacity { get; set; } = 0;

    [ObservableProperty] public partial int DCacheBankCount { get; set; } = 1;

    [ObservableProperty] public partial int DCacheReadPorts { get; set; } = 0;

    [ObservableProperty] public partial int DCacheWritePorts { get; set; } = 0;

    [ObservableProperty] public partial int DCacheSectorBytes { get; set; } = 0;

    [ObservableProperty] public partial int DCacheVictimCacheEntries { get; set; } = 0;

    [ObservableProperty] public partial int DCacheVictimCacheHitLatency { get; set; } = 1;

    [ObservableProperty] public partial bool L2CacheEnabled { get; set; } = false;

    [ObservableProperty] public partial int L2CacheCapacityKb { get; set; } = 256;

    [ObservableProperty] public partial int L2CacheWays { get; set; } = 8;

    [ObservableProperty] public partial int L2CacheBlockBytes { get; set; } = 64;

    [ObservableProperty] public partial int L2CacheMissLatency { get; set; } = 20;

    [ObservableProperty] public partial int L2CacheTagLatency { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheDataLatency { get; set; } = 0;

    [ObservableProperty] public partial string L2CacheWritePolicy { get; set; } = "write_through";

    [ObservableProperty] public partial string L2CacheWriteMissPolicy { get; set; } = "no_write_allocate";

    [ObservableProperty] public partial int L2CacheWbCapacity { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheBankCount { get; set; } = 1;

    [ObservableProperty] public partial int L2CacheReadPorts { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheWritePorts { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheSectorBytes { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheVictimCacheEntries { get; set; } = 0;

    [ObservableProperty] public partial int L2CacheVictimCacheHitLatency { get; set; } = 1;

    [ObservableProperty] public partial string L2CacheInclusionPolicy { get; set; } = "nine";

    [ObservableProperty] public partial string CacheReplacementPolicy { get; set; } = "lru";

    [ObservableProperty] public partial string DPrefetcher { get; set; } = "none";
    [ObservableProperty] public partial int DPrefetcherTableSize { get; set; } = 64;
    [ObservableProperty] public partial int DPrefetcherDepth { get; set; } = 8;
    [ObservableProperty] public partial int DPrefetchLatency { get; set; } = 0;

    [ObservableProperty] public partial int StoreBufferCapacity { get; set; } = 0;

    [ObservableProperty] public partial int IssueWidth { get; set; } = 2;

    [ObservableProperty] public partial int RobCapacity { get; set; } = 32;

    [ObservableProperty] public partial int IqCapacity { get; set; } = 8;

    [ObservableProperty] public partial int ExtraPhysRegs { get; set; } = 32;

    [ObservableProperty] public partial bool FlatIq { get; set; } = false;

    [ObservableProperty] public partial int MshrCapacity { get; set; } = 0;

    [ObservableProperty] public partial bool FdipEnabled { get; set; }
    [ObservableProperty] public partial int FdipFtqCapacity { get; set; } = 32;
    [ObservableProperty] public partial bool RdipEnabled { get; set; }

    [ObservableProperty] public partial int DaeLaneQueueDepth { get; set; } = 8;

    public bool HasFdipFtqCapacity => FdipEnabled;

    public bool IsSingleCycle => Pipeline == "single_cycle";
    public bool IsFiveStage => Pipeline == "five_stage";
    public bool IsOoo => Pipeline == "ooo";
    public bool IsCpr => Pipeline == "cpr";
    public bool IsDae => Pipeline == "dae";
    public bool IsWidePipeline => Pipeline is "superscalar" or "ooo" or "cpr";
    public bool HasPredictorConfig => Pipeline is "five_stage" or "superscalar" or "ooo" or "cpr";
    public bool HasNBitParams => PredictorType == "n_bit";
    public bool HasGshareParams => PredictorType is "gshare" or "gselect";
    public bool HasGselectParams => PredictorType == "gselect";
    public bool HasCorrelatedParams => PredictorType == "correlated";
    public bool HasPerceptronParams => PredictorType == "perceptron";
    public bool HasHashedPerceptronParams => PredictorType == "hashed_perceptron";
    public bool HasTournamentParams => PredictorType == "tournament";
    public bool HasImliParams => PredictorType == "imli";
    public bool HasCbpPluginParams => PredictorType == "cbp_plugin";
    public bool HasCbpNgPluginParams => PredictorType is "cbp_ng_plugin" or "cbp_ng_ooo_plugin";

    public bool HasDPrefetcherTableSize => DPrefetcher is "stride" or "stream";
    public bool HasDPrefetcherDepth => DPrefetcher == "stream";
    public bool HasDPrefetcherParams => DPrefetcher != "none";

    public static string[] CacheReplacementPolicyOptions { get; } =
        ["lru", "mru", "clock", "fifo", "plru", "random", "srrip", "brrip", "drrip", "ship", "ship_pc", "hawkeye",];

    public static string[] DPrefetcherOptions { get; } =
        ["none", "next_line", "stride", "stream", "ipcp", "berti", "pythia", "sms", "bop", "spp", "ppf", "stems",];

    public static string[] WritePolicyOptions { get; } = ["write_through", "write_back",];
    public static string[] WriteMissPolicyOptions { get; } = ["no_write_allocate", "write_allocate",];
    public static string[] InclusionPolicyOptions { get; } = ["nine", "inclusive", "exclusive",];

    public string[] PipelineOptions { get; } = ["single_cycle", "five_stage", "superscalar", "ooo", "cpr", "dae",];

    public string[] PredictorOptions { get; } = [
        "none",
        "always_not_taken",
        "always_taken",
        "always_backward_not_forwards",
        "n_bit",
        "correlated",
        "gselect",
        "gshare",
        "l_tage",
        "perceptron",
        "hashed_perceptron",
        "tournament",
        "tage_sc_l",
        "ittage",
        "batage",
        "imli",
        "llbp",
        "llbp_x",
        "vla_tage",
        "true_oracle",
        "runlts",
        "lvcp",
        "branchnet",
        "tea",
        "cbp_plugin",
        "cbp_ng_plugin",
        "cbp_ng_ooo_plugin",
    ];

    // ReSharper disable once PartialMethodParameterNameMismatch
    partial void OnFdipEnabledChanged(bool value) => OnPropertyChanged(nameof(HasFdipFtqCapacity));

    // ReSharper disable once PartialMethodParameterNameMismatch
    partial void OnPipelineChanged(string value) {
        OnPropertyChanged(nameof(IsFiveStage));
        OnPropertyChanged(nameof(IsOoo));
        OnPropertyChanged(nameof(IsCpr));
        OnPropertyChanged(nameof(IsDae));
        OnPropertyChanged(nameof(IsWidePipeline));
        OnPropertyChanged(nameof(HasPredictorConfig));
    }

    // ReSharper disable once PartialMethodParameterNameMismatch
    partial void OnDPrefetcherChanged(string value) {
        OnPropertyChanged(nameof(HasDPrefetcherTableSize));
        OnPropertyChanged(nameof(HasDPrefetcherDepth));
        OnPropertyChanged(nameof(HasDPrefetcherParams));
    }

    // ReSharper disable once PartialMethodParameterNameMismatch
    partial void OnPredictorTypeChanged(string value) {
        OnPropertyChanged(nameof(HasNBitParams));
        OnPropertyChanged(nameof(HasGshareParams));
        OnPropertyChanged(nameof(HasGselectParams));
        OnPropertyChanged(nameof(HasCorrelatedParams));
        OnPropertyChanged(nameof(HasPerceptronParams));
        OnPropertyChanged(nameof(HasHashedPerceptronParams));
        OnPropertyChanged(nameof(HasTournamentParams));
        OnPropertyChanged(nameof(HasImliParams));
        OnPropertyChanged(nameof(HasCbpPluginParams));
        OnPropertyChanged(nameof(HasCbpNgPluginParams));
    }

    private static WritePolicyKind ParseWritePolicy(string s) =>
        s == "write_back" ? WritePolicyKind.WriteBack : WritePolicyKind.WriteThrough;

    private static WriteMissPolicyKind ParseWriteMissPolicy(string s) =>
        s == "write_allocate" ? WriteMissPolicyKind.WriteAllocate : WriteMissPolicyKind.NoWriteAllocate;

    private static InclusionPolicyKind ParseInclusionPolicy(string s) => s switch {
        "inclusive" => InclusionPolicyKind.Inclusive,
        "exclusive" => InclusionPolicyKind.Exclusive,
        _           => InclusionPolicyKind.Nine,
    };

    private static string FormatInclusionPolicy(InclusionPolicyKind k) => k switch {
        InclusionPolicyKind.Inclusive => "inclusive",
        InclusionPolicyKind.Exclusive => "exclusive",
        _                              => "nine",
    };

    public NamedConfig ToNamedConfig() {
        BranchPredictorConfig? predictor = PredictorType switch {
            "always_not_taken" => BranchPredictorConfig.AlwaysNotTaken(),
            "always_taken" => BranchPredictorConfig.AlwaysTaken(),
            "always_backward_not_forwards" => BranchPredictorConfig.AlwaysBackwardNotForwards(),
            "n_bit" => BranchPredictorConfig.NBit(PredictorBits, PredictorTableSize),
            "correlated" => BranchPredictorConfig.Correlated(CorrelatedM, CorrelatedN, CorrelatedBhtSize),
            "gselect" => BranchPredictorConfig.Gselect(PredictorHistoryBits, PredictorPcBits),
            "gshare" => BranchPredictorConfig.Gshare(PredictorHistoryBits),
            "l_tage" => BranchPredictorConfig.LTage(),
            "perceptron" => BranchPredictorConfig.Perceptron(PerceptronHistoryLength, PerceptronTableSize),
            "hashed_perceptron" => BranchPredictorConfig.HashedPerceptron(HashedPerceptronTableSize),
            "tournament" => BranchPredictorConfig.Tournament(
                TournamentLocalHistoryBits, TournamentLocalTableSize, TournamentGlobalHistoryBits
            ),
            "tage_sc_l"         => BranchPredictorConfig.TageScL(),
            "ittage"            => BranchPredictorConfig.Ittage(),
            "batage"            => BranchPredictorConfig.Batage(),
            "imli"              => BranchPredictorConfig.Imli(ImliPhtSize, ImliBtbSize),
            "llbp"              => BranchPredictorConfig.Llbp(),
            "llbp_x"            => BranchPredictorConfig.LlbpX(),
            "vla_tage"          => BranchPredictorConfig.VlaTage(),
            "oracle"            => BranchPredictorConfig.Oracle(),
            "runlts"            => BranchPredictorConfig.Runlts(),
            "lvcp"              => BranchPredictorConfig.Lvcp(),
            "branchnet"         => BranchPredictorConfig.BranchNet(),
            "tea"               => BranchPredictorConfig.Tea(),
            "cbp_plugin"        => BranchPredictorConfig.CbpPlugin(CbpLibraryPath),
            "cbp_ng_plugin"     => BranchPredictorConfig.CbpNgPlugin(CbpNgLibraryPath),
            "cbp_ng_ooo_plugin" => BranchPredictorConfig.CbpNgOoOePlugin(CbpNgLibraryPath),
            _                   => null,
        };

        CacheHardwareConfig? iCache = ICacheEnabled
            ? new CacheHardwareConfig(
                ICacheCapacityKb * 1024, ICacheWays, ICacheBlockBytes, ICacheMissLatency,
                ICacheTagLatency, ICacheDataLatency,
                ParseWritePolicy(ICacheWritePolicy), ParseWriteMissPolicy(ICacheWriteMissPolicy),
                ICacheWbCapacity, ICacheBankCount, ICacheReadPorts, ICacheWritePorts, ICacheSectorBytes,
                ICacheVictimCacheEntries, ICacheVictimCacheHitLatency
            )
            : null;
        CacheHardwareConfig? dCache = DCacheEnabled
            ? new CacheHardwareConfig(
                DCacheCapacityKb * 1024, DCacheWays, DCacheBlockBytes, DCacheMissLatency,
                DCacheTagLatency, DCacheDataLatency,
                ParseWritePolicy(DCacheWritePolicy), ParseWriteMissPolicy(DCacheWriteMissPolicy),
                DCacheWbCapacity, DCacheBankCount, DCacheReadPorts, DCacheWritePorts, DCacheSectorBytes,
                DCacheVictimCacheEntries, DCacheVictimCacheHitLatency
            )
            : null;
        CacheHardwareConfig? l2Cache = L2CacheEnabled
            ? new CacheHardwareConfig(
                L2CacheCapacityKb * 1024, L2CacheWays, L2CacheBlockBytes, L2CacheMissLatency,
                L2CacheTagLatency, L2CacheDataLatency,
                ParseWritePolicy(L2CacheWritePolicy), ParseWriteMissPolicy(L2CacheWriteMissPolicy),
                L2CacheWbCapacity, L2CacheBankCount, L2CacheReadPorts, L2CacheWritePorts, L2CacheSectorBytes,
                L2CacheVictimCacheEntries, L2CacheVictimCacheHitLatency,
                ParseInclusionPolicy(L2CacheInclusionPolicy)
            )
            : null;

        return new NamedConfig(
            Name, new TrainConfig(
                Pipeline,
                ForwardingEnabled,
                predictor,
                iCache,
                dCache,
                L2Cache: l2Cache,
                StoreBufferCapacity: StoreBufferCapacity,
                IssueWidth: IssueWidth,
                RobCapacity: RobCapacity,
                IqCapacity: IqCapacity,
                ExtraPhysRegs: ExtraPhysRegs,
                FlatIq: FlatIq,
                MshrCapacity: MshrCapacity,
                DPrefetcher: DPrefetcher == "none" ? null : DPrefetcher,
                DPrefetcherTableSize: DPrefetcherTableSize,
                DPrefetcherDepth: DPrefetcherDepth,
                DPrefetchLatency: DPrefetchLatency,
                CacheReplacementPolicy: CacheReplacementPolicy == "lru" ? null : CacheReplacementPolicy,
                DaeLaneQueueDepth: DaeLaneQueueDepth,
                FdipFtqCapacity: ICacheEnabled && FdipEnabled ? FdipFtqCapacity : 0,
                Rdip: ICacheEnabled && RdipEnabled
            )
        );
    }

    public static ConfigViewModel FromNamedConfig(NamedConfig nc) {
        var vm = new ConfigViewModel {
            Name = nc.Name,
            Pipeline = nc.Config.Pipeline,
            ForwardingEnabled = nc.Config.ForwardingEnabled,
            PredictorType = nc.Config.Predictor switch {
                AlwaysNotTakenConfig            => "always_not_taken",
                AlwaysTakenConfig               => "always_taken",
                AlwaysBackwardNotForwardsConfig => "always_backward_not_forwards",
                NBitConfig                      => "n_bit",
                CorrelatedConfig                => "correlated",
                GselectConfig                   => "gselect",
                GshareConfig                    => "gshare",
                LTageConfig                     => "l_tage",
                PerceptronConfig                => "perceptron",
                HashedPerceptronConfig          => "hashed_perceptron",
                TournamentConfig                => "tournament",
                TageScLConfig                   => "tage_sc_l",
                IttageConfig                    => "ittage",
                BatageConfig                    => "batage",
                ImliConfig                      => "imli",
                LlbpConfig                      => "llbp",
                LlbpXConfig                     => "llbp_x",
                VlaTageConfig                   => "vla_tage",
                OracleConfig                    => "true_oracle",
                RunltsConfig                    => "runlts",
                LvcpConfig                      => "lvcp",
                BranchNetConfig                 => "branchnet",
                TeaConfig                       => "tea",
                CbpPluginConfig                 => "cbp_plugin",
                CbpNgPluginConfig               => "cbp_ng_plugin",
                CbpNgOoOePluginConfig           => "cbp_ng_ooo_plugin",
                _                               => "none",
            },
            StoreBufferCapacity = nc.Config.StoreBufferCapacity,
            IssueWidth = nc.Config.IssueWidth,
            RobCapacity = nc.Config.RobCapacity,
            IqCapacity = nc.Config.IqCapacity,
            ExtraPhysRegs = nc.Config.ExtraPhysRegs,
            FlatIq = nc.Config.FlatIq,
            MshrCapacity = nc.Config.MshrCapacity,
            DaeLaneQueueDepth = nc.Config.DaeLaneQueueDepth,
            ICacheEnabled = nc.Config.ICache is not null,
            DCacheEnabled = nc.Config.DCache is not null,
            L2CacheEnabled = nc.Config.L2Cache is not null,
            CacheReplacementPolicy = nc.Config.CacheReplacementPolicy ?? "lru",
            DPrefetcher = nc.Config.DPrefetcher ?? "none",
            DPrefetcherTableSize = nc.Config.DPrefetcherTableSize,
            DPrefetcherDepth = nc.Config.DPrefetcherDepth,
            DPrefetchLatency = nc.Config.DPrefetchLatency,
            FdipEnabled = nc.Config.FdipFtqCapacity > 0,
            FdipFtqCapacity = nc.Config.FdipFtqCapacity > 0 ? nc.Config.FdipFtqCapacity : 32,
            RdipEnabled = nc.Config.Rdip,
        };

        switch (nc.Config.Predictor) {
            case NBitConfig nb:
                vm.PredictorBits = nb.Bits;
                vm.PredictorTableSize = nb.TableSize;
                break;
            case GselectConfig gsel:
                vm.PredictorHistoryBits = gsel.HistoryBits;
                vm.PredictorPcBits = gsel.PcBits;
                break;
            case GshareConfig gsh: vm.PredictorHistoryBits = gsh.HistoryBits; break;
            case CorrelatedConfig cor:
                vm.CorrelatedM = cor.M;
                vm.CorrelatedN = cor.N;
                vm.CorrelatedBhtSize = cor.BhtSize;
                break;
            case PerceptronConfig perc:
                vm.PerceptronHistoryLength = perc.HistoryLength;
                vm.PerceptronTableSize = perc.TableSize;
                break;
            case HashedPerceptronConfig hp: vm.HashedPerceptronTableSize = hp.TableSize; break;
            case TournamentConfig tour:
                vm.TournamentLocalHistoryBits = tour.LocalHistoryBits;
                vm.TournamentLocalTableSize = tour.LocalTableSize;
                vm.TournamentGlobalHistoryBits = tour.GlobalHistoryBits;
                break;
            case ImliConfig imli:
                vm.ImliPhtSize = imli.PhtSize;
                vm.ImliBtbSize = imli.BtbSize;
                break;
            case CbpPluginConfig cbp:          vm.CbpLibraryPath = cbp.LibraryPath; break;
            case CbpNgPluginConfig cbpNg:      vm.CbpNgLibraryPath = cbpNg.LibraryPath; break;
            case CbpNgOoOePluginConfig cbpNgO: vm.CbpNgLibraryPath = cbpNgO.LibraryPath; break;
        }

        if (nc.Config.ICache is { } ic) {
            vm.ICacheCapacityKb = ic.CapacityBytes / 1024;
            vm.ICacheWays = ic.Ways;
            vm.ICacheBlockBytes = ic.BlockBytes;
            vm.ICacheMissLatency = ic.MissLatency;
            vm.ICacheTagLatency = ic.TagLatency;
            vm.ICacheDataLatency = ic.DataLatency;
            vm.ICacheWritePolicy = ic.WritePolicy == WritePolicyKind.WriteBack ? "write_back" : "write_through";
            vm.ICacheWriteMissPolicy = ic.WriteMissPolicy == WriteMissPolicyKind.WriteAllocate
                ? "write_allocate"
                : "no_write_allocate";
            vm.ICacheWbCapacity = ic.WbCapacity;
            vm.ICacheBankCount = ic.BankCount;
            vm.ICacheReadPorts = ic.ReadPorts;
            vm.ICacheWritePorts = ic.WritePorts;
            vm.ICacheSectorBytes = ic.SectorBytes;
            vm.ICacheVictimCacheEntries = ic.VictimCacheEntries;
            vm.ICacheVictimCacheHitLatency = ic.VictimCacheHitLatency;
        }

        if (nc.Config.DCache is { } dc) {
            vm.DCacheCapacityKb = dc.CapacityBytes / 1024;
            vm.DCacheWays = dc.Ways;
            vm.DCacheBlockBytes = dc.BlockBytes;
            vm.DCacheMissLatency = dc.MissLatency;
            vm.DCacheTagLatency = dc.TagLatency;
            vm.DCacheDataLatency = dc.DataLatency;
            vm.DCacheWritePolicy = dc.WritePolicy == WritePolicyKind.WriteBack ? "write_back" : "write_through";
            vm.DCacheWriteMissPolicy = dc.WriteMissPolicy == WriteMissPolicyKind.WriteAllocate
                ? "write_allocate"
                : "no_write_allocate";
            vm.DCacheWbCapacity = dc.WbCapacity;
            vm.DCacheBankCount = dc.BankCount;
            vm.DCacheReadPorts = dc.ReadPorts;
            vm.DCacheWritePorts = dc.WritePorts;
            vm.DCacheSectorBytes = dc.SectorBytes;
            vm.DCacheVictimCacheEntries = dc.VictimCacheEntries;
            vm.DCacheVictimCacheHitLatency = dc.VictimCacheHitLatency;
        }

        if (nc.Config.L2Cache is { } l2) {
            vm.L2CacheCapacityKb = l2.CapacityBytes / 1024;
            vm.L2CacheWays = l2.Ways;
            vm.L2CacheBlockBytes = l2.BlockBytes;
            vm.L2CacheMissLatency = l2.MissLatency;
            vm.L2CacheTagLatency = l2.TagLatency;
            vm.L2CacheDataLatency = l2.DataLatency;
            vm.L2CacheWritePolicy = l2.WritePolicy == WritePolicyKind.WriteBack ? "write_back" : "write_through";
            vm.L2CacheWriteMissPolicy = l2.WriteMissPolicy == WriteMissPolicyKind.WriteAllocate
                ? "write_allocate"
                : "no_write_allocate";
            vm.L2CacheWbCapacity = l2.WbCapacity;
            vm.L2CacheBankCount = l2.BankCount;
            vm.L2CacheReadPorts = l2.ReadPorts;
            vm.L2CacheWritePorts = l2.WritePorts;
            vm.L2CacheSectorBytes = l2.SectorBytes;
            vm.L2CacheVictimCacheEntries = l2.VictimCacheEntries;
            vm.L2CacheVictimCacheHitLatency = l2.VictimCacheHitLatency;
            vm.L2CacheInclusionPolicy = FormatInclusionPolicy(l2.InclusionPolicy);
        }

        return vm;
    }
}