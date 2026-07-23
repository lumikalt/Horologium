#region

using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Orrery.Train;

#endregion

namespace Pipeline.Spec;

public enum CoherenceBusKind { Snooping, Directory, }

/// <summary>
///     Per-hart configuration: pipeline topology, mechanism factory, entry point, and optional private coherent cache
///     hierarchy.
/// </summary>
public sealed record HartSpec(
    PipelineSpec Pipeline,
    Func<IMechanism> MechanismFactory,
    ulong EntryPoint = 0,
    CacheHierarchySpec? Cache = null
);

/// <summary>
///     The assembled multicore system produced by <see cref="MulticoreSpec.Build" />.
///     <para>
///         Topology from backing outward: backing → [LLC] → bus → [per-hart private caches] → per-hart train.
///         Run all harts via <see cref="Run" />; inspect per-hart trains through <see cref="Trains" />
///         and the outermost coherent (bus-facing) cache per hart through <see cref="CoherentCaches" />
///         (null entries mean the hart has no private cache).
///     </para>
/// </summary>
public sealed class MulticoreHandle {
    private readonly DeferredBus[]? _deferredBuses;
    private readonly MultiHartPipeline _pipeline;

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

    public IReadOnlyList<ISteppableTrain> Trains { get; }
    public IBus Bus { get; }

    /// <summary>Shared LLC above the coherence bus; null if <see cref="MulticoreSpec.SharedLlc" /> was not set.</summary>
    public SetAssociativeCache? SharedLlc { get; }

    /// <summary>
    ///     Outermost coherent (bus-facing) private cache per hart; null means that hart has no private cache.
    ///     For a single-L1 hart this is the L1. For an L1+L2 hart this is the L2.
    /// </summary>
    public IReadOnlyList<MoesifCache?> CoherentCaches { get; }

    /// <summary>Runs all harts sequentially (round-robin) for up to <paramref name="maxTicks" /> ticks.</summary>
    public RevolutionResult[] Run(long maxTicks = long.MaxValue) => _pipeline.Run(maxTicks);

    /// <summary>
    ///     Runs all harts with two-phase parallelism: parallel tick then serial bus drain.
    ///     Results are bit-identical to <see cref="Run" /> for well-synchronized programs.
    ///     Requires <see cref="MulticoreSpec.ConcurrentMode" /> = true at build time.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the handle was built without <c>ConcurrentMode</c>.
    /// </exception>
    public RevolutionResult[] RunConcurrent(long maxTicks = long.MaxValue) {
        if (_deferredBuses is null)
            throw new InvalidOperationException(
                "RunConcurrent requires MulticoreSpec with ConcurrentMode = true and a snooping bus."
            );
        return _pipeline.RunConcurrent(_deferredBuses, maxTicks);
    }

    /// <summary>
    ///     Writes every dirty line in the shared LLC and every hart's outermost (coherent)
    ///     private cache down to backing memory, without evicting or changing coherence state.
    ///     Call this after <see cref="Run" />/<see cref="RunConcurrent" /> before inspecting final
    ///     memory state directly (e.g. through the <see cref="IMemory" /> originally passed to
    ///     <see cref="MulticoreSpec.Build" />) — a write-back cache's freshest data otherwise stays
    ///     uncommitted indefinitely if its line is never evicted.
    ///     <para>
    ///         Does not walk inner (non-coherent) private levels below the outermost per hart —
    ///         today's only caller (<c>Experiment.RunMulticore</c>) and the Face GUI never
    ///         configure more than one private level per hart, so there is nothing to flush there
    ///         yet; a multi-level private stack would need this extended.
    ///     </para>
    /// </summary>
    public void FlushAllToBacking() {
        SharedLlc?.FlushAllToBacking();
        foreach (MoesifCache? cc in CoherentCaches) cc?.FlushToBacking();
    }
}

/// <summary>
///     Structural description of an N-hart multicore machine: per-hart pipeline variant and
///     private cache hierarchy, optional shared LLC, and coherence bus topology.
///     <para>
///         Build order: backing → [LLC (<see cref="SetAssociativeCache" />)] →
///         bus (<see cref="MoesifBus" /> or <see cref="DirectoryBus" />) →
///         [per-hart private caches] → per-hart pipeline train.
///         The outermost private level (closest to the bus) is a <see cref="MoesifCache" />;
///         inner private levels are non-coherent <see cref="SetAssociativeCache" /> filters.
///         When <see cref="HartSpec.Cache" /> is null the hart wires directly to the bus backing.
///     </para>
/// </summary>
public sealed record MulticoreSpec(
    IReadOnlyList<HartSpec> Harts,
    CacheLevelSpec? SharedLlc = null,
    CoherenceBusKind Bus = CoherenceBusKind.Snooping,
    bool ConcurrentMode = false,
    ReservationTable? ReservationTable = null
) {
    public MulticoreHandle Build(IMemory backing) {
        if (ConcurrentMode && Bus != CoherenceBusKind.Snooping)
            throw new InvalidOperationException(
                "ConcurrentMode requires a snooping bus (DeferredBus wraps MoesifBus only)."
            );

        // Cross-hart LR/SC: without this, writes never invalidate another hart's reservation
        // (harts sharing this ReservationTable only get correct Set/TryConsume bookkeeping, not
        // actual cross-hart invalidation), so every SC silently "succeeds" against a stale read —
        // a lost-update race, not a correctness error the caller would otherwise see. Must sit
        // immediately above the raw backing, below the shared LLC/bus/per-hart caches, so it sees
        // every write regardless of which hart or cache level it penetrates from.
        IMemory reservationAwareBacking = ReservationTable is { } table
            ? new ReservationAwareMemory(backing, table)
            : backing;

        // 1. Optional shared LLC wrapping raw backing.
        SetAssociativeCache? llc = SharedLlc is { } llcSpec
            ? new SetAssociativeCache(
                reservationAwareBacking,
                llcSpec.CapacityBytes, llcSpec.Ways, llcSpec.BlockBytes,
                llcSpec.MissLatency, 0, llcSpec.ReplacementPolicy,
                llcSpec.TagLatency, llcSpec.DataLatency,
                llcSpec.WritePolicy, llcSpec.WriteMissPolicy, llcSpec.WbCapacity
            )
            : null;
        IMemory busBacking = llc ?? reservationAwareBacking;

        // 2. Coherence bus behind the per-hart caches. A write a hart's private cache absorbs
        // (Modified state) never reaches reservationAwareBacking at all until eviction/writeback —
        // ReservationAwareMemory alone only catches writes that reach the shared backing directly
        // (the no-private-cache case). Coherent invalidation for cached harts happens here instead:
        // MoesifBus/DirectoryBus call ReservationTable.InvalidateAt themselves from their own
        // snoop/invalidate paths (BusReadInvalidate, BusReadForOwnership, BusSilentUpgrade), so the
        // same table must also be handed to whichever bus gets built. A hart with no private cache
        // wires to BusCoherentMemory (below), not straight to busBacking: that hart's own reads/writes
        // must still go through the same snoop paths, or a cached peer's dirty line is silently read
        // past (stale data) or clobbered without invalidation (lost update) — the bus is the only
        // thing every hart shares, cached or not.
        IBus bus;
        DeferredBus[]? deferredBuses = null;
        if (Bus == CoherenceBusKind.Directory) { bus = new DirectoryBus(busBacking, ReservationTable); }
        else {
            var moesifBus = new MoesifBus(busBacking, ReservationTable);
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
                else { hartMemory = new BusCoherentMemory(hartBus); }
            }
            else { hartMemory = new BusCoherentMemory(hartBus); }

            trains[i] = hart.Pipeline.Build(hart.MechanismFactory(), hartMemory, hart.EntryPoint);
        }

        return new MulticoreHandle(trains, bus, llc, coherentCaches, deferredBuses);
    }
}