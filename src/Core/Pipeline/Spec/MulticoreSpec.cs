using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Orrery.Train;

namespace Pipeline.Spec;

public enum CoherenceBusKind { Snooping, Directory, }

/// <summary>Per-hart configuration: pipeline topology, mechanism factory, entry point, and optional private coherent cache hierarchy.</summary>
public sealed record HartSpec(
    PipelineSpec Pipeline,
    Func<IMechanism> MechanismFactory,
    ulong EntryPoint = 0,
    CacheHierarchySpec? Cache = null
);

/// <summary>
/// The assembled multicore system produced by <see cref="MulticoreSpec.Build"/>.
/// <para>
/// Topology from backing outward: backing → [LLC] → bus → [per-hart private caches] → per-hart train.
/// Run all harts via <see cref="Run"/>; inspect per-hart trains through <see cref="Trains"/>
/// and the outermost coherent (bus-facing) cache per hart through <see cref="CoherentCaches"/>
/// (null entries mean the hart has no private cache).
/// </para>
/// </summary>
public sealed class MulticoreHandle {
    private readonly MultiHartPipeline _pipeline;
    private readonly DeferredBus[]? _deferredBuses;

    public IReadOnlyList<ISteppableTrain> Trains { get; }
    public IBus Bus { get; }

    /// <summary>Shared LLC above the coherence bus; null if <see cref="MulticoreSpec.SharedLlc"/> was not set.</summary>
    public SetAssociativeCache? SharedLlc { get; }

    /// <summary>
    /// Outermost coherent (bus-facing) private cache per hart; null means that hart has no private cache.
    /// For a single-L1 hart this is the L1. For an L1+L2 hart this is the L2.
    /// </summary>
    public IReadOnlyList<MoesifCache?> CoherentCaches { get; }

    internal MulticoreHandle(
        ISteppableTrain[] trains,
        IBus bus,
        SetAssociativeCache? sharedLlc,
        MoesifCache?[] coherentCaches,
        DeferredBus[]? deferredBuses = null
    ) {
        Trains = trains;
        Bus = bus;
        SharedLlc = sharedLlc;
        CoherentCaches = coherentCaches;
        _deferredBuses = deferredBuses;
        _pipeline = new MultiHartPipeline(trains);
    }

    /// <summary>Runs all harts sequentially (round-robin) for up to <paramref name="maxTicks"/> ticks.</summary>
    public RevolutionResult[] Run(long maxTicks = long.MaxValue) => _pipeline.Run(maxTicks);

    /// <summary>
    /// Runs all harts with two-phase parallelism: parallel tick then serial bus drain.
    /// Results are bit-identical to <see cref="Run"/> for well-synchronized programs.
    /// Requires <see cref="MulticoreSpec.ConcurrentMode"/> = true at build time.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the handle was built without <c>ConcurrentMode</c>.
    /// </exception>
    public RevolutionResult[] RunConcurrent(long maxTicks = long.MaxValue) {
        if (_deferredBuses is null)
            throw new InvalidOperationException(
                "RunConcurrent requires MulticoreSpec with ConcurrentMode = true and a snooping bus."
            );
        return _pipeline.RunConcurrent(_deferredBuses, maxTicks);
    }
}

/// <summary>
/// Structural description of an N-hart multicore machine: per-hart pipeline variant and
/// private cache hierarchy, optional shared LLC, and coherence bus topology.
/// <para>
/// Build order: backing → [LLC (<see cref="SetAssociativeCache"/>)] →
/// bus (<see cref="MoesifBus"/> or <see cref="DirectoryBus"/>) →
/// [per-hart private caches] → per-hart pipeline train.
/// The outermost private level (closest to the bus) is a <see cref="MoesifCache"/>;
/// inner private levels are non-coherent <see cref="SetAssociativeCache"/> filters.
/// When <see cref="HartSpec.Cache"/> is null the hart wires directly to the bus backing.
/// </para>
/// </summary>
public sealed record MulticoreSpec(
    IReadOnlyList<HartSpec> Harts,
    CacheLevelSpec? SharedLlc = null,
    CoherenceBusKind Bus = CoherenceBusKind.Snooping,
    bool ConcurrentMode = false
) {
    public MulticoreHandle Build(IMemory backing) {
        if (ConcurrentMode && Bus != CoherenceBusKind.Snooping)
            throw new InvalidOperationException(
                "ConcurrentMode requires a snooping bus (DeferredBus wraps MoesifBus only)."
            );

        // 1. Optional shared LLC wrapping raw backing.
        SetAssociativeCache? llc = SharedLlc is { } llcSpec
            ? new SetAssociativeCache(
                backing,
                llcSpec.CapacityBytes, llcSpec.Ways, llcSpec.BlockBytes,
                llcSpec.MissLatency, 0, llcSpec.ReplacementPolicy,
                llcSpec.TagLatency, llcSpec.DataLatency,
                llcSpec.WritePolicy, llcSpec.WriteMissPolicy, llcSpec.WbCapacity
            )
            : null;
        IMemory busBacking = llc ?? backing;

        // 2. Coherence bus behind the per-hart caches.
        IBus bus;
        DeferredBus[]? deferredBuses = null;
        if (Bus == CoherenceBusKind.Directory) { bus = new DirectoryBus(busBacking); }
        else {
            var moesifBus = new MoesifBus(busBacking);
            bus = moesifBus;
            if (ConcurrentMode) {
                deferredBuses = new DeferredBus[Harts.Count];
                for (var i = 0; i < Harts.Count; i++) deferredBuses[i] = new DeferredBus(moesifBus);
            }
        }

        // 3. Per-hart private cache stacks and pipeline trains.
        var trains = new ISteppableTrain[Harts.Count];
        var coherentCaches = new MoesifCache?[Harts.Count];
        for (var i = 0; i < Harts.Count; i++) {
            HartSpec hart = Harts[i];
            IBus hartBus = deferredBuses?[i] ?? bus;
            IMemory hartMemory;
            if (hart.Cache is { } cacheHierarchy) {
                // Private levels: D-path (innermost) followed by SharedLevels — all innermost first.
                CachePathSpec? dPath = cacheHierarchy.Paths?.GetValueOrDefault(CacheHierarchySpec.D)
                                    ?? cacheHierarchy.Paths?.Values.FirstOrDefault();
                IReadOnlyList<CacheLevelSpec>? dLevels = dPath?.Levels;
                IReadOnlyList<CacheLevelSpec>? sharedLvls = cacheHierarchy.SharedLevels;

                var allLevels = new List<CacheLevelSpec>(
                    (dLevels?.Count ?? 0) + (sharedLvls?.Count ?? 0)
                );
                if (dLevels is not null) allLevels.AddRange(dLevels);
                if (sharedLvls is not null) allLevels.AddRange(sharedLvls);

                if (allLevels.Count > 0) {
                    // Outermost level (last in list) → MoesifCache on the hart's (possibly deferred) bus.
                    CacheLevelSpec outerSpec = allLevels[^1];
                    var moesif = new MoesifCache(
                        hartBus, outerSpec.CapacityBytes, outerSpec.Ways, outerSpec.BlockBytes, outerSpec.MissLatency
                    );
                    coherentCaches[i] = moesif;
                    IMemory current = moesif;

                    // Inner levels (second-to-last down to first) → SetAssociativeCache filters.
                    for (int j = allLevels.Count - 2; j >= 0; j--) {
                        CacheLevelSpec lvl = allLevels[j];
                        current = new SetAssociativeCache(
                            current, lvl.CapacityBytes, lvl.Ways, lvl.BlockBytes,
                            lvl.MissLatency, 0, lvl.ReplacementPolicy,
                            lvl.TagLatency, lvl.DataLatency,
                            lvl.WritePolicy, lvl.WriteMissPolicy, lvl.WbCapacity
                        );
                    }

                    hartMemory = current;
                }
                else { hartMemory = busBacking; }
            }
            else { hartMemory = busBacking; }

            trains[i] = hart.Pipeline.Build(hart.MechanismFactory(), hartMemory, hart.EntryPoint);
        }

        return new MulticoreHandle(trains, bus, llc, coherentCaches, deferredBuses);
    }
}