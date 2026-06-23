using CommunityToolkit.Mvvm.ComponentModel;
using RiscV.Analysis;
using RiscV.Config;

namespace Face.ViewModels;

public partial class ConfigViewModel : ObservableObject {
    [ObservableProperty] private string _name = "config";
    [ObservableProperty] private string _pipeline = "five_stage";
    [ObservableProperty] private bool _forwardingEnabled = true;
    [ObservableProperty] private string _predictorType = "none";
    [ObservableProperty] private int _predictorTableSize = 1024;

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
    public bool HasTableSize => PredictorType is "one_bit" or "two_bit";

    partial void OnPipelineChanged(string value) {
        OnPropertyChanged(nameof(IsFiveStage));
        OnPropertyChanged(nameof(IsOoo));
        OnPropertyChanged(nameof(IsWidePipeline));
    }

    partial void OnPredictorTypeChanged(string value) =>
        OnPropertyChanged(nameof(HasTableSize));

    public string[] PipelineOptions { get; } = ["five_stage", "superscalar", "ooo",];
    public string[] PredictorOptions { get; } = ["none", "always_not_taken", "always_taken", "one_bit", "two_bit",];

    public NamedConfig ToNamedConfig() {
        BranchPredictorConfig? predictor = PredictorType switch {
            "always_not_taken" => BranchPredictorConfig.AlwaysNotTaken(),
            "always_taken"     => BranchPredictorConfig.AlwaysTaken(),
            "one_bit"          => BranchPredictorConfig.OneBit(PredictorTableSize),
            "two_bit"          => BranchPredictorConfig.TwoBit(PredictorTableSize),
            _                  => null,
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
                AlwaysNotTakenConfig => "always_not_taken",
                AlwaysTakenConfig    => "always_taken",
                OneBitConfig         => "one_bit",
                TwoBitConfig         => "two_bit",
                _                    => "none",
            },
            StoreBufferCapacity = nc.Config.StoreBufferCapacity,
            IssueWidth = nc.Config.IssueWidth,
            RobCapacity = nc.Config.RobCapacity,
            IqCapacity = nc.Config.IqCapacity,
            ExtraPhysRegs = nc.Config.ExtraPhysRegs,
            ICacheEnabled = nc.Config.ICache is not null,
            DCacheEnabled = nc.Config.DCache is not null,
        };

        if (nc.Config.Predictor is OneBitConfig ob)
            vm.PredictorTableSize = ob.TableSize;
        else if (nc.Config.Predictor is TwoBitConfig tb) vm.PredictorTableSize = tb.TableSize;

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