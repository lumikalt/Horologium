using Mechanism;
using Orrery.Cache;
using Orrery.Spec;
using Orrery.Train;

namespace Pipeline.Spec;

/// <summary>
/// The assembled single-hart simulation produced by <see cref="MachineSpec.Build"/>.
/// <para>
/// Topology from backing outward: backing → [private cache stack] → pipeline train.
/// Run the pipeline via <see cref="Run"/>; inspect cache statistics through <see cref="Layers"/>
/// (null when no cache was configured).
/// </para>
/// </summary>
public sealed class MachineHandle {
    private readonly ISteppableTrain _train;

    /// <summary>The pipeline train; implement <see cref="ISteppableTrain"/> for cycle-level stepping.</summary>
    public ISteppableTrain Train => _train;

    /// <summary>
    /// The I-path cache layer stack. Null when no cache hierarchy was configured.
    /// For unified I/D this is the same object as <see cref="DLayers"/>.
    /// </summary>
    public MemoryLayers? ILayers { get; }

    /// <summary>
    /// The D-path cache layer stack. Null when no cache hierarchy was configured.
    /// For unified I/D this is the same object as <see cref="ILayers"/>.
    /// Cache statistics (misses, hits, …) are on <see cref="MemoryLayers.Cache"/>,
    /// <see cref="MemoryLayers.L2Cache"/>, etc.
    /// </summary>
    public MemoryLayers? DLayers { get; }

    /// <summary>Alias for <see cref="DLayers"/>. Null when no cache hierarchy was configured.</summary>
    public MemoryLayers? Layers => DLayers;

    internal MachineHandle(ISteppableTrain train, MemoryLayers? iLayers, MemoryLayers? dLayers) {
        _train = train;
        ILayers = iLayers;
        DLayers = dLayers;
    }

    internal MachineHandle(ISteppableTrain train, MemoryLayers? layers = null)
        : this(train, layers, layers) { }

    /// <summary>
    /// The hart's committed architectural state. Null for multi-hart trains.
    /// Use with <see cref="Mechanism.ArchitecturalCheckpoint"/> to save and restore state.
    /// </summary>
    public Mechanism.IArchState? ArchState => _train.ArchState;

    /// <summary>Runs the train for up to <paramref name="maxTicks"/> ticks.</summary>
    public RevolutionResult Run(long maxTicks = long.MaxValue, long warmupTicks = 0, long snapshotInterval = 0)
        => _train.Run(maxTicks, warmupTicks, snapshotInterval);
}

/// <summary>
/// Structural description of a single-hart machine: pipeline topology, mechanism factory,
/// and optional private cache hierarchy.
/// <para>
/// Build order: backing → [cache stack from <see cref="Cache"/>] → pipeline train.
/// When <see cref="Cache"/> is non-null the full hierarchy is built externally via
/// <see cref="CacheHierarchySpec.BuildDLayers"/>; the accessor top is passed as unified (I = D)
/// backing to the train with null iMemConfig/dMemConfig. This mirrors the <see cref="MulticoreSpec"/>
/// approach and preserves per-level replacement policies and arbitrary hierarchy depth.
/// </para>
/// <para>
/// Rigidities: I and D paths share one cache chain (no split I/D); TLB configuration is not
/// supported; the train's internal ICache/DCache stat fields are null — use
/// <see cref="MachineHandle.Layers"/> for cache statistics.
/// </para>
/// </summary>
public sealed record MachineSpec(
    PipelineSpec Pipeline,
    Func<IMechanism> MechanismFactory,
    CacheHierarchySpec? Cache = null
) {
    /// <summary>
    /// Builds and wires the machine.
    /// </summary>
    /// <param name="backing">The raw backing memory (FlatMemory, shared IMemory, etc.).</param>
    /// <param name="entryPoint">Program entry point address.</param>
    /// <param name="mmioRegion">
    /// Optional MMIO region to bypass the cache. Addresses in this range route directly to
    /// <paramref name="backing"/>, preventing stale cached device register reads.
    /// </param>
    public MachineHandle Build(
        IMemory backing,
        ulong entryPoint = 0,
        (ulong Base, ulong Size)? mmioRegion = null
    ) {
        IMechanism mechanism = MechanismFactory();

        if (Cache is { } cache) {
            ulong mmioBase = mmioRegion?.Base ?? 0;
            ulong mmioSize = mmioRegion?.Size ?? 0;

            bool splitId = cache.Paths is { } paths &&
                           paths.TryGetValue(CacheHierarchySpec.I, out var iPath) &&
                           paths.TryGetValue(CacheHierarchySpec.D, out var dPath) &&
                           !ReferenceEquals(iPath, dPath);

            if (splitId) {
                MemoryLayers iLayers = cache.BuildILayers(backing, mmioBase, mmioSize);
                MemoryLayers dLayers = cache.BuildDLayers(backing, mmioBase, mmioSize);
                ISteppableTrain train = Pipeline.Build(mechanism, iLayers, dLayers, entryPoint);
                return new MachineHandle(train, iLayers, dLayers);
            } else {
                MemoryLayers layers = cache.BuildDLayers(backing, mmioBase, mmioSize);
                ISteppableTrain train = Pipeline.Build(mechanism, layers.Accessor, entryPoint);
                return new MachineHandle(train, layers);
            }
        }

        ISteppableTrain trainNc = Pipeline.Build(mechanism, backing, entryPoint);
        return new MachineHandle(trainNc);
    }
}
