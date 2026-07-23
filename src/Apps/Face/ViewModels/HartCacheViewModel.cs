#region

using CommunityToolkit.Mvvm.ComponentModel;
using Orrery.Spec;

#endregion

namespace Face.ViewModels;

/// <summary>
///     One hart's private coherent cache in multi-hart mode. Deliberately NOT
///     <see cref="ConfigViewModel" />'s I/D/L2 cache section — a hart's Train never receives
///     <c>iMemConfig</c>/<c>dMemConfig</c> in the multicore path (<c>MulticoreSpec.Build</c> calls
///     <c>hart.Pipeline.Build(mechanism, hartMemory, entryPoint)</c> with only 3 args), so
///     <see cref="ConfigViewModel" />'s cache knobs would be silently dead weight here. Only these 4
///     fields are honored by the coherent (bus-facing) cache level — richer knobs (replacement
///     policy, prefetcher, banks, sectors, victim cache, inclusion policy, write policy, tag/data
///     latency) are dropped by <c>MulticoreSpec.Build</c>'s <c>MoesifCache</c> construction.
/// </summary>
public partial class HartCacheViewModel : ObservableObject {
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial int CapacityKb { get; set; } = 4;
    [ObservableProperty] public partial int Ways { get; set; } = 4;
    [ObservableProperty] public partial int BlockBytes { get; set; } = 32;
    [ObservableProperty] public partial int MissLatency { get; set; } = 10;

    public CacheLevelSpec? ToCacheLevelSpec() =>
        Enabled ? new CacheLevelSpec(CapacityKb * 1024, Ways, BlockBytes, MissLatency) : null;
}
