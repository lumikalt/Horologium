using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline.Ooo;

namespace Pipeline.Spec;

/// <summary>
///     Structural description of a pipeline topology.
///     Use one of the concrete subtypes: <see cref="SingleCycleSpec" />,
///     <see cref="FiveStageSpec" />, <see cref="SuperscalarSpec" />,
///     or <see cref="OutOfOrderSpec" />. Each carries only the parameters
///     meaningful to its variant — no OoO fields on a single-cycle spec.
/// </summary>
public abstract record PipelineSpec {
    public abstract ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    );

    public virtual ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0
    ) => throw new NotSupportedException($"{GetType().Name} does not support pre-built MemoryLayers.");
}

/// <summary>Single-cycle fetch-decode-execute-writeback; no pipeline, no hazards.</summary>
public sealed record SingleCycleSpec(
    ICommitObserver? CommitObserver = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) => new SingleCycleTrain(mechanism, backing, entryPoint, iMemConfig, dMemConfig, CommitObserver);

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0
    ) => new SingleCycleTrain(mechanism, iLayers, dLayers, entryPoint, CommitObserver);
}

/// <summary>Classic five-stage in-order pipeline with optional forwarding and store buffer.</summary>
public sealed record FiveStageSpec(
    bool ForwardingEnabled = true,
    int StoreBufferCapacity = 0,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    PEventLog? PEventLog = null,
    ICommitObserver? CommitObserver = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) => new FiveStageTrain(
        mechanism, backing, entryPoint,
        ForwardingEnabled,
        BranchPredictorFactory?.Invoke(),
        iMemConfig,
        dMemConfig,
        StoreBufferCapacity,
        PEventLog,
        CommitObserver
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0
    ) => new FiveStageTrain(
        mechanism, iLayers, dLayers, entryPoint,
        ForwardingEnabled,
        BranchPredictorFactory?.Invoke(),
        StoreBufferCapacity,
        PEventLog,
        CommitObserver
    );
}

/// <summary>Superscalar in-order: issues up to <see cref="IssueWidth" /> instructions per cycle.</summary>
public sealed record SuperscalarSpec(
    int IssueWidth = 2
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) => new SuperscalarTrain(mechanism, backing, entryPoint, IssueWidth, iMemConfig, dMemConfig);

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0
    ) => new SuperscalarTrain(mechanism, iLayers, dLayers, entryPoint, IssueWidth);
}

/// <summary>
///     Simultaneous multi-threading: N hart contexts share a single issue window.
///     <para>
///         The base <see cref="Build(IMechanism,IMemory,ulong,MemoryConfig?,MemoryConfig?)" /> satisfies the
///         <see cref="PipelineSpec" /> contract by building a 1-hart SMT. The <c>MemoryConfig</c> parameters
///         are not forwarded — <see cref="SmtTrain" /> expects pre-configured <see cref="IMemory" /> objects
///         (callers wrap caches into the memory before passing). For multi-hart construction use
///         <see cref="Build(IMechanism[],IMemory[],ulong[])" /> which returns the concrete
///         <see cref="SmtTrain" /> so callers can access per-hart state via <c>StateOf(i)</c>.
///     </para>
/// </summary>
public sealed record SmtSpec(
    int IssueWidth = 2
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) => new SmtTrain([mechanism,], [backing,], [entryPoint,], IssueWidth);

    public SmtTrain Build(IMechanism[] mechanisms, IMemory[] memories, ulong[]? entryPoints = null) =>
        new(mechanisms, memories, entryPoints, IssueWidth);
}

/// <summary>Out-of-order pipeline with ROB, issue queue, physical register file, and optional LSQ.</summary>
public sealed record OutOfOrderSpec(
    int IssueWidth = 2,
    int RobCapacity = 32,
    int IqCapacity = 8,
    bool FlatIq = false,
    int ExtraPhysRegs = 32,
    int LqCapacity = 0,
    int SqCapacity = 0,
    int WriteBufferCapacity = 0,
    int MshrCapacity = 0,
    int StreamPrefetchDepth = 4,
    FuLatencyConfig? FuLatency = null,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    PEventLog? PEventLog = null,
    ICommitObserver? CommitObserver = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) => new OooeTrain(
        mechanism, backing, entryPoint,
        IssueWidth, RobCapacity, IqCapacity, ExtraPhysRegs,
        BranchPredictorFactory?.Invoke(),
        iMemConfig, dMemConfig,
        FuLatency,
        PEventLog,
        StreamPrefetchDepth,
        CommitObserver,
        LqCapacity, SqCapacity, WriteBufferCapacity, MshrCapacity,
        FlatIq
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0
    ) => new OooeTrain(
        mechanism, iLayers, dLayers, entryPoint,
        IssueWidth, RobCapacity, IqCapacity, ExtraPhysRegs,
        BranchPredictorFactory?.Invoke(),
        FuLatency,
        PEventLog,
        StreamPrefetchDepth,
        CommitObserver,
        LqCapacity, SqCapacity, WriteBufferCapacity, MshrCapacity,
        FlatIq
    );
}