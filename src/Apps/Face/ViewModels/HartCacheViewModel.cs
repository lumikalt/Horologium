#region

using CommunityToolkit.Mvvm.ComponentModel;
using Orrery.Spec;

#endregion

namespace Face.ViewModels;

/// <summary>
///     One hart's per-hart multi-hart settings: its private coherent cache and its memory pool.
///     Deliberately NOT <see cref="ConfigViewModel" />'s I/D/L2 cache section — a hart's Train never
///     receives <c>iMemConfig</c>/<c>dMemConfig</c> in the multicore path (<c>MulticoreSpec.Build</c>
///     calls <c>hart.Pipeline.Build(mechanism, hartMemory, entryPoint)</c> with only 3 args), so
///     <see cref="ConfigViewModel" />'s cache knobs would be silently dead weight here. Only these 4
///     cache fields are honored by the coherent (bus-facing) cache level — richer knobs (replacement
///     policy, prefetcher, banks, sectors, victim cache, inclusion policy, write policy, tag/data
///     latency) are dropped by <c>MulticoreSpec.Build</c>'s <c>MoesifCache</c> construction.
///     <para>
///         <see cref="PoolId" /> is unrelated to the cache fields — it's which memory/coherence
///         domain this hart belongs to (see <c>HartSpec.PoolId</c>). Harts sharing a pool id share
///         one backing memory, bus, and shared LLC; different pools are fully isolated. It lives here
///         rather than a dedicated ViewModel because this is already the per-hart-parallel-to-Configs
///         collection Face maintains.
///     </para>
/// </summary>
public partial class HartCacheViewModel : ObservableObject {
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial int CapacityKb { get; set; } = 4;
    [ObservableProperty] public partial int Ways { get; set; } = 4;
    [ObservableProperty] public partial int BlockBytes { get; set; } = 32;
    [ObservableProperty] public partial int MissLatency { get; set; } = 10;
    [ObservableProperty] public partial int PoolId { get; set; }

    public CacheLevelSpec? ToCacheLevelSpec() =>
        Enabled ? new CacheLevelSpec(CapacityKb * 1024, Ways, BlockBytes, MissLatency) : null;
}