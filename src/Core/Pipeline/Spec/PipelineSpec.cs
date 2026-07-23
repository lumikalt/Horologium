#region

using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline.Ooo;

#endregion

namespace Pipeline.Spec;

/// <summary>
///     Structural description of a pipeline topology.
///     Use one of the concrete subtypes: <see cref="SingleCycleSpec" />,
///     <see cref="FiveStageSpec" />, <see cref="SuperscalarSpec" />,
///     or <see cref="OutOfOrderSpec" />. Each carries only the parameters
///     meaningful to its variant — no OoO fields on a single-cycle spec.
///     <para>
///         Every subtype's <c>BranchPredictorFactory</c> is a bare <c>Func&lt;IBranchPredictor&gt;</c>
///         with no way to supply a mechanism/workload — those don't exist yet when a <c>.csx</c>
///         script builds its spec. Predictors needing a functional pre-pass
///         (<c>RiscV32.Config.OracleConfig</c>/<c>BranchNetConfig</c>/<c>TeaConfig</c>, whose
///         <c>Build(IMechanism, IWorkload)</c> override needs both) are therefore unusable from
///         scripts today; <c>TrainConfig.ToPipelineSpec</c> only works around this because it runs
///         after mechanism/workload are already known.
///     </para>
/// </summary>
public abstract record PipelineSpec {
    /// <param name="fdipBackingMemory">
    ///     Overrides what FDIP (fetch-directed instruction prefetch, on the subtypes that support it)
    ///     reads its own decode-ahead lookahead from — deliberately distinct from <paramref name="backing" />
    ///     /the eventual fetch-translator memory, since FDIP's reads should bypass the very I-cache it
    ///     warms (see <c>FdipPrefetcher</c>'s doc comment). Defaults to <paramref name="backing" /> when
    ///     null, matching every existing caller's behavior.
    /// </param>
    public abstract ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    );

    /// <param name="fdipBackingMemory">
    ///     See the other <see cref="Build(IMechanism,IMemory,ulong,MemoryConfig?,MemoryConfig?,IMemory?)" />
    ///     overload. Defaults to <paramref name="iLayers" />'s accessor (today's behavior) when null.
    /// </param>
    public virtual ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
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
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new SingleCycleTrain(mechanism, backing, entryPoint, iMemConfig, dMemConfig, CommitObserver);

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new SingleCycleTrain(mechanism, iLayers, dLayers, entryPoint, CommitObserver);
}

/// <summary>Classic five-stage in-order pipeline with optional forwarding and store buffer.</summary>
public sealed record FiveStageSpec(
    bool ForwardingEnabled = true,
    int StoreBufferCapacity = 0,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    PEventLog? PEventLog = null,
    ICommitObserver? CommitObserver = null,
    int FdipFtqCapacity = 0,
    bool Rdip = false
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new FiveStageTrain(
        mechanism, backing, entryPoint,
        ForwardingEnabled,
        BranchPredictorFactory?.Invoke(),
        iMemConfig,
        dMemConfig,
        StoreBufferCapacity,
        PEventLog,
        CommitObserver,
        FdipFtqCapacity,
        Rdip,
        fdipBackingMemory
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new FiveStageTrain(
        mechanism, iLayers, dLayers, entryPoint,
        ForwardingEnabled,
        BranchPredictorFactory?.Invoke(),
        StoreBufferCapacity,
        PEventLog,
        CommitObserver,
        FdipFtqCapacity,
        Rdip,
        fdipBackingMemory
    );
}

/// <summary>
///     Superscalar in-order: issues up to <see cref="IssueWidth" /> instructions per cycle
///     behind a scoreboard, with speculative fetch through <see cref="BranchPredictorFactory" />
///     (always-not-taken when null) and per-class FU timing from <see cref="FuLatency" />.
/// </summary>
public sealed record SuperscalarSpec(
    int IssueWidth = 2,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    FuLatencyConfig? FuLatency = null,
    int FrontendDepth = 2,
    PEventLog? PEventLog = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new SuperscalarTrain(
        mechanism, backing, entryPoint, IssueWidth, iMemConfig, dMemConfig,
        BranchPredictorFactory?.Invoke(), PEventLog, FuLatency, FrontendDepth
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new SuperscalarTrain(
        mechanism, iLayers, dLayers, entryPoint, IssueWidth,
        BranchPredictorFactory?.Invoke(), PEventLog, FuLatency, FrontendDepth
    );
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
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new SmtTrain([mechanism,], [backing,], [entryPoint,], IssueWidth);

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new SmtTrain([mechanism,], [iLayers.Accessor,], [entryPoint,], IssueWidth);

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
    int StreamMaxCount = 8,
    FuLatencyConfig? FuLatency = null,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    PEventLog? PEventLog = null,
    ICommitObserver? CommitObserver = null,
    int FdipFtqCapacity = 0,
    bool Rdip = false,
    bool EnableStoreSets = false
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new OooeTrain(
        mechanism, backing, entryPoint,
        IssueWidth, RobCapacity, IqCapacity, ExtraPhysRegs,
        BranchPredictorFactory?.Invoke(),
        iMemConfig, dMemConfig,
        FuLatency,
        PEventLog,
        StreamPrefetchDepth,
        StreamMaxCount,
        CommitObserver,
        LqCapacity, SqCapacity, WriteBufferCapacity, MshrCapacity,
        FlatIq,
        FdipFtqCapacity,
        Rdip,
        EnableStoreSets,
        fdipBackingMemory: fdipBackingMemory
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new OooeTrain(
        mechanism, iLayers, dLayers, entryPoint,
        IssueWidth, RobCapacity, IqCapacity, ExtraPhysRegs,
        BranchPredictorFactory?.Invoke(),
        FuLatency,
        PEventLog,
        StreamPrefetchDepth,
        StreamMaxCount,
        CommitObserver,
        LqCapacity, SqCapacity, WriteBufferCapacity, MshrCapacity,
        FlatIq,
        FdipFtqCapacity,
        Rdip,
        EnableStoreSets,
        fdipBackingMemory: fdipBackingMemory
    );
}

/// <summary>
///     Checkpoint Processing and Recovery: a ROB-free OoO pipeline that speculates past
///     memory-dependence violations and recovers by replaying from a checkpoint rather than
///     flushing from a ROB head.
/// </summary>
public sealed record CprSpec(
    int IssueWidth = 2,
    int IqCapacity = 8,
    int ExtraPhysRegs = 32,
    Func<IBranchPredictor>? BranchPredictorFactory = null,
    FuLatencyConfig? FuLatency = null,
    PEventLog? PEventLog = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new CprTrain(
        mechanism, backing, entryPoint,
        IssueWidth, IqCapacity, ExtraPhysRegs,
        predictor: BranchPredictorFactory?.Invoke(),
        iMemConfig: iMemConfig,
        dMemConfig: dMemConfig,
        fuLatency: FuLatency,
        pEventLog: PEventLog
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new CprTrain(
        mechanism, iLayers, dLayers, entryPoint,
        IssueWidth, IqCapacity, ExtraPhysRegs,
        predictor: BranchPredictorFactory?.Invoke(),
        fuLatency: FuLatency,
        pEventLog: PEventLog
    );
}

/// <summary>
///     Decoupled Access-Execute: splits the pipeline into an Access lane (address generation,
///     memory) and an Execute lane, connected by per-lane instruction queues.
/// </summary>
public sealed record DaeSpec(
    int DaeLaneQueueDepth = 8,
    PEventLog? PEventLog = null
) : PipelineSpec {
    public override ISteppableTrain Build(
        IMechanism mechanism,
        IMemory backing,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IMemory? fdipBackingMemory = null
    ) => new DaeTrain(
        mechanism, backing, entryPoint,
        DaeLaneQueueDepth,
        iMemConfig,
        dMemConfig,
        PEventLog
    );

    public override ISteppableTrain Build(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint = 0,
        IMemory? fdipBackingMemory = null
    ) => new DaeTrain(mechanism, iLayers, dLayers, entryPoint, DaeLaneQueueDepth, PEventLog);
}