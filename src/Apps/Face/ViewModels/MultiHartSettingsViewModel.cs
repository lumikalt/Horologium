#region

using CommunityToolkit.Mvvm.ComponentModel;
using Orrery.Spec;
using Pipeline.Spec;

#endregion

namespace Face.ViewModels;

/// <summary>
///     Global (not-per-hart) settings for Face's multi-hart mode: the shared LLC
///     (same 4 fields as <see cref="HartCacheViewModel" /> — the coherent cache path only honors
///     those regardless of level), the coherence bus, and whether to run round-robin or with
///     <see cref="MulticoreSpec.ConcurrentMode" />. Hart count itself is not here — it's implicit
///     in how many rows the user has added to the shared <c>Configs</c>/<c>HartCaches</c>
///     collections, the same Add/Remove flow the single-hart sweep already uses.
/// </summary>
public partial class MultiHartSettingsViewModel : ObservableObject {
    [ObservableProperty] public partial bool SharedLlcEnabled { get; set; }
    [ObservableProperty] public partial int SharedLlcCapacityKb { get; set; } = 256;
    [ObservableProperty] public partial int SharedLlcWays { get; set; } = 8;
    [ObservableProperty] public partial int SharedLlcBlockBytes { get; set; } = 64;
    [ObservableProperty] public partial int SharedLlcMissLatency { get; set; } = 20;

    [ObservableProperty] public partial string BusKind { get; set; } = "snooping";
    [ObservableProperty] public partial bool ConcurrentMode { get; set; }
    [ObservableProperty] public partial decimal MaxTicks { get; set; } = 100_000;

    public static string[] BusKindOptions { get; } = ["snooping", "directory",];

    public CacheLevelSpec? ToSharedLlcSpec() =>
        SharedLlcEnabled
            ? new CacheLevelSpec(SharedLlcCapacityKb * 1024, SharedLlcWays, SharedLlcBlockBytes, SharedLlcMissLatency)
            : null;

    public CoherenceBusKind ToCoherenceBusKind() =>
        BusKind == "directory" ? CoherenceBusKind.Directory : CoherenceBusKind.Snooping;
}
