using CommunityToolkit.Mvvm.ComponentModel;
using RiscV.Analysis;
using RiscV.Config;

namespace Face.ViewModels;

public partial class ConfigViewModel : ObservableObject {
    [ObservableProperty] private string _name = "config";
    [ObservableProperty] private string _pipeline = "five_stage";
    [ObservableProperty] private bool _forwardingEnabled = true;
    [ObservableProperty] private string _predictorType = "none";
    [ObservableProperty] private int _predictorBits = 2;
    [ObservableProperty] private int _predictorTableSize = 1024;
    [ObservableProperty] private int _predictorHistoryBits = 8;
    [ObservableProperty] private int _predictorPcBits = 4;
    [ObservableProperty] private int _correlatedM = 2;
    [ObservableProperty] private int _correlatedN = 2;
    [ObservableProperty] private int _correlatedBhtSize = 1024;
    [ObservableProperty] private int _perceptronHistoryLength = 24;
    [ObservableProperty] private int _perceptronTableSize = 256;
    [ObservableProperty] private int _hashedPerceptronTableSize = 512;
    [ObservableProperty] private int _tournamentLocalHistoryBits = 10;
    [ObservableProperty] private int _tournamentLocalTableSize = 1024;
    [ObservableProperty] private int _tournamentGlobalHistoryBits = 12;

    [ObservableProperty] private bool _iCacheEnabled = false;
    [ObservableProperty] private int _iCacheCapacityKb = 32;
    [ObservableProperty] private int _iCacheWays = 4;
    [ObservableProperty] private int _iCacheBlockBytes = 32;
    [ObservableProperty] private int _iCacheMissLatency = 10;

    [ObservableProperty] private bool _dCacheEnabled = false;
    [ObservableProperty] private int _dCacheCapacityKb = 32;
    [ObservableProperty] private int _dCacheWays = 4;
    [ObservableProperty] private int _dCacheBlockBytes = 32;
    [ObservableProperty] private int _dCacheMissLatency = 10;

    [ObservableProperty] private int _storeBufferCapacity = 0;
    [ObservableProperty] private int _issueWidth = 2;
    [ObservableProperty] private int _robCapacity = 32;
    [ObservableProperty] private int _iqCapacity = 16;
    [ObservableProperty] private int _extraPhysRegs = 32;

    public bool IsFiveStage => Pipeline == "five_stage";
    public bool IsOoo => Pipeline == "ooo";
    public bool IsWidePipeline => Pipeline is "superscalar" or "ooo";
    public bool HasNBitParams => PredictorType == "n_bit";
    public bool HasGshareParams => PredictorType is "gshare" or "gselect";
    public bool HasGselectParams => PredictorType == "gselect";
    public bool HasCorrelatedParams => PredictorType == "correlated";
    public bool HasPerceptronParams => PredictorType == "perceptron";
    public bool HasHashedPerceptronParams => PredictorType == "hashed_perceptron";
    public bool HasTournamentParams => PredictorType == "tournament";

    partial void OnPipelineChanged(string value) {
        OnPropertyChanged(nameof(IsFiveStage));
        OnPropertyChanged(nameof(IsOoo));
        OnPropertyChanged(nameof(IsWidePipeline));
    }

    partial void OnPredictorTypeChanged(string value) {
        OnPropertyChanged(nameof(HasNBitParams));
        OnPropertyChanged(nameof(HasGshareParams));
        OnPropertyChanged(nameof(HasGselectParams));
        OnPropertyChanged(nameof(HasCorrelatedParams));
        OnPropertyChanged(nameof(HasPerceptronParams));
        OnPropertyChanged(nameof(HasHashedPerceptronParams));
        OnPropertyChanged(nameof(HasTournamentParams));
    }

    public string[] PipelineOptions { get; } = ["five_stage", "superscalar", "ooo",];

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
    ];

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
            "tage_sc_l" => BranchPredictorConfig.TageScL(),
            _           => null,
        };

        CacheHardwareConfig? iCache = ICacheEnabled
            ? new CacheHardwareConfig(ICacheCapacityKb * 1024, ICacheWays, ICacheBlockBytes, ICacheMissLatency)
            : null;
        CacheHardwareConfig? dCache = DCacheEnabled
            ? new CacheHardwareConfig(DCacheCapacityKb * 1024, DCacheWays, DCacheBlockBytes, DCacheMissLatency)
            : null;

        return new NamedConfig(
            Name, new TrainConfig(
                Pipeline,
                ForwardingEnabled,
                predictor,
                iCache,
                dCache,
                StoreBufferCapacity: StoreBufferCapacity,
                IssueWidth: IssueWidth,
                RobCapacity: RobCapacity,
                IqCapacity: IqCapacity,
                ExtraPhysRegs: ExtraPhysRegs
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
                _                               => "none",
            },
            StoreBufferCapacity = nc.Config.StoreBufferCapacity,
            IssueWidth = nc.Config.IssueWidth,
            RobCapacity = nc.Config.RobCapacity,
            IqCapacity = nc.Config.IqCapacity,
            ExtraPhysRegs = nc.Config.ExtraPhysRegs,
            ICacheEnabled = nc.Config.ICache is not null,
            DCacheEnabled = nc.Config.DCache is not null,
        };

        if (nc.Config.Predictor is NBitConfig nb) {
            vm.PredictorBits = nb.Bits;
            vm.PredictorTableSize = nb.TableSize;
        }

        if (nc.Config.Predictor is GselectConfig gsel) {
            vm.PredictorHistoryBits = gsel.HistoryBits;
            vm.PredictorPcBits = gsel.PcBits;
        }

        if (nc.Config.Predictor is GshareConfig gsh) vm.PredictorHistoryBits = gsh.HistoryBits;

        if (nc.Config.Predictor is CorrelatedConfig cor) {
            vm.CorrelatedM = cor.M;
            vm.CorrelatedN = cor.N;
            vm.CorrelatedBhtSize = cor.BhtSize;
        }

        if (nc.Config.Predictor is PerceptronConfig perc) {
            vm.PerceptronHistoryLength = perc.HistoryLength;
            vm.PerceptronTableSize = perc.TableSize;
        }

        if (nc.Config.Predictor is HashedPerceptronConfig hp) vm.HashedPerceptronTableSize = hp.TableSize;

        if (nc.Config.Predictor is TournamentConfig tour) {
            vm.TournamentLocalHistoryBits = tour.LocalHistoryBits;
            vm.TournamentLocalTableSize = tour.LocalTableSize;
            vm.TournamentGlobalHistoryBits = tour.GlobalHistoryBits;
        }

        if (nc.Config.ICache is { } ic) {
            vm.ICacheCapacityKb = ic.CapacityBytes / 1024;
            vm.ICacheWays = ic.Ways;
            vm.ICacheBlockBytes = ic.BlockBytes;
            vm.ICacheMissLatency = ic.MissLatency;
        }

        if (nc.Config.DCache is { } dc) {
            vm.DCacheCapacityKb = dc.CapacityBytes / 1024;
            vm.DCacheWays = dc.Ways;
            vm.DCacheBlockBytes = dc.BlockBytes;
            vm.DCacheMissLatency = dc.MissLatency;
        }

        return vm;
    }
}