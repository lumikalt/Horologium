#region

using System.Diagnostics;
using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Streaming;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Ooo;

#endregion

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

public sealed partial class OooTrain : ISteppableTrain {
    private readonly OoOPipelineCore _core;
    private readonly Train _train;

    public OooTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 8,
        int extraPhysRegs = 32,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        PEventLog? pEventLog = null,
        int streamPrefetchDepth = 4,
        int streamMaxCount = 8,
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false,
        int fdipFtqCapacity = 0,
        bool rdip = false,
        bool enableStoreSets = false,
        bool enableCriticalityPrediction = false,
        bool enableSmbBypass = false,
        bool enableRunahead = false,
        int runaheadBudget = 200,
        bool enableVectorRunahead = false,
        int runaheadVectorWidth = 8,
        int runaheadUnrollLength = 8,
        int runaheadPipelineDepth = 1,
        IValuePredictor? valuePredictor = null,
        bool enableEoleLateExec = false,
        bool enableEoleEarlyExec = false,
        IMemory? fdipBackingMemory = null,
        bool enableSttExpOnly = false,
        bool enableInvisiSpec = false,
        bool enableSttImplicitBranches = false,
        bool enableSttMemDepGating = false,
        bool enableInvisiSpecLlcSb = false,
        int llcSbCapacity = 16,
        int llcSbHitLatency = 1,
        bool sttFuturisticModel = false,
        bool enableEarlyStoreAddress = false
    ) {
        var esc = new Escapement();
        _train = new Train("ooo", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new OoOPipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, iLayers, dLayers, entryPoint,
                issueWidth, robCapacity, iqCapacity, extraPhysRegs,
                predictor ?? new AlwaysNotTakenPredictor(),
                fuLatency ?? FuLatencyConfig.Default,
                pEventLog,
                streamPrefetchDepth,
                streamMaxCount,
                commitObserver,
                lqCapacity,
                sqCapacity,
                writeBufferCapacity,
                mshrCapacity,
                flatIq,
                fdipBackingMemory ?? memory,
                fdipFtqCapacity,
                rdip,
                enableStoreSets,
                enableCriticalityPrediction,
                enableSmbBypass,
                enableRunahead,
                runaheadBudget,
                enableVectorRunahead,
                runaheadVectorWidth,
                runaheadUnrollLength,
                runaheadPipelineDepth,
                valuePredictor,
                enableEoleLateExec,
                enableEoleEarlyExec,
                enableSttExpOnly: enableSttExpOnly,
                enableInvisiSpec: enableInvisiSpec,
                enableSttImplicitBranches: enableSttImplicitBranches,
                enableSttMemDepGating: enableSttMemDepGating,
                enableInvisiSpecLlcSb: enableInvisiSpecLlcSb,
                llcSbCapacity: llcSbCapacity,
                llcSbHitLatency: llcSbHitLatency,
                sttFuturisticModel: sttFuturisticModel,
                enableEarlyStoreAddress: enableEarlyStoreAddress
            )
        );
        _train.Build();
    }

    internal OooTrain(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 8,
        int extraPhysRegs = 32,
        IBranchPredictor? predictor = null,
        FuLatencyConfig? fuLatency = null,
        PEventLog? pEventLog = null,
        int streamPrefetchDepth = 4,
        int streamMaxCount = 8,
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false,
        int fdipFtqCapacity = 0,
        bool rdip = false,
        bool enableStoreSets = false,
        IValuePredictor? valuePredictor = null,
        bool enableEoleLateExec = false,
        bool enableEoleEarlyExec = false,
        IMemory? fdipBackingMemory = null,
        bool enableSttExpOnly = false,
        bool enableInvisiSpec = false,
        bool enableSttImplicitBranches = false,
        bool enableSttMemDepGating = false,
        bool enableInvisiSpecLlcSb = false,
        int llcSbCapacity = 16,
        int llcSbHitLatency = 1,
        bool sttFuturisticModel = false,
        bool enableEarlyStoreAddress = false
    ) {
        var esc = new Escapement();
        _train = new Train("ooo", esc);
        _core = _train.AddGear(
            new OoOPipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, iLayers, dLayers, entryPoint,
                issueWidth, robCapacity, iqCapacity, extraPhysRegs,
                predictor ?? new AlwaysNotTakenPredictor(),
                fuLatency ?? FuLatencyConfig.Default,
                pEventLog,
                streamPrefetchDepth,
                streamMaxCount,
                commitObserver,
                lqCapacity,
                sqCapacity,
                writeBufferCapacity,
                mshrCapacity,
                flatIq,
                fdipBackingMemory ?? iLayers.Accessor,
                fdipFtqCapacity,
                rdip,
                enableStoreSets,
                valuePredictor: valuePredictor,
                enableEoleLateExec: enableEoleLateExec,
                enableEoleEarlyExec: enableEoleEarlyExec,
                enableSttExpOnly: enableSttExpOnly,
                enableInvisiSpec: enableInvisiSpec,
                enableSttImplicitBranches: enableSttImplicitBranches,
                enableSttMemDepGating: enableSttMemDepGating,
                enableInvisiSpecLlcSb: enableInvisiSpecLlcSb,
                llcSbCapacity: llcSbCapacity,
                llcSbHitLatency: llcSbHitLatency,
                sttFuturisticModel: sttFuturisticModel,
                enableEarlyStoreAddress: enableEarlyStoreAddress
            )
        );
        _train.Build();
    }

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public RdipPrefetcher? Rdip => _core.Rdip;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public BdiCache? L2Bdi => _core.ILayers.L2Bdi;
    public BdiCache? L3Bdi => _core.ILayers.L3Bdi;
    public CeaserCache? L2Ceaser => _core.ILayers.L2Ceaser;
    public CeaserCache? L3Ceaser => _core.ILayers.L3Ceaser;
    public ScatterCache? L2Scatter => _core.ILayers.L2Scatter;
    public ScatterCache? L3Scatter => _core.ILayers.L3Scatter;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;
    public PEventLog? PEventLog => _core.PEventLog;
    public StreamingEngine StreamingEngine => _core.StreamingEngine;

    public long CurrentTick => _train.CurrentTick;
    public bool IsIdle => _train.IsIdle;

    public IArchState ArchState => _core.State;

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();
    public IReadOnlyList<DialBoardSnapshot> SnapshotDials() => _train.SnapshotDials();

    public RevolutionResult FinishStepping(IReadOnlyList<DialBoardSnapshot> baseline) =>
        _train.FinishStepping(baseline);

    public DialBoardSnapshot SnapshotPipeline() => _core.Dials.Snapshot();
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
///     Superscalar out-of-order pipeline Gear using Tomasulo's algorithm.
///     <para>
///         Pipeline stages (cross-tick latches connect them):
///         Fetch → [decodeQueue] → Dispatch → [IssueQueue] → Issue
///         → [_execBuffer] → Execute → [_cdbBuffer] → Complete → [ROB] → Commit
///     </para>
///     <para>
///         All six logical stages execute within a single RunCycle tick, reading from
///         the latch populated by the previous tick. Minimum end-to-end latency for
///         an independent instruction is ~4 ticks (Fetch, Dispatch, Execute, Complete+Commit).
///     </para>
/// </summary>
internal sealed partial class OoOPipelineCore : Gear {
    // IQ index 0=INT(Alu/MulDiv/Sys/Fence/Halt), 1=FP, 2=BR, 3=VEC(Vector/UVE), 4=LSU
    private const int IqCount = 5;

    // ── Runahead execution (Mutlu et al., HPCA 2003; Naithani et al., HPCA 2020 / ISCA 2021) ──
    //
    // A self-contained shadow execution lane entered only when dispatch is stalled behind a
    // full ROB whose head is an incomplete load. Real commit/dispatch are already frozen in
    // that state (commit can't retire past an incomplete head; dispatch can't allocate into a
    // full ROB), so the shadow lane can safely draw fresh physical registers from the same live
    // RenameMap free list with zero collision risk, and undo everything on exit by restoring a
    // RAT snapshot. It never touches the real ROB/IQ/LQ/SQ/RAS/predictor history — only the
    // RAT, the PRF, and the real data-cache accessor, which is what delivers the prefetch
    // benefit (Horologium's cache installs data synchronously on every access, hit or miss).

    private static readonly HashSet<ToothClass> RunaheadSupportedClasses = [
        ToothClass.IntegerAlu, ToothClass.IntegerMulDiv, ToothClass.Load, ToothClass.Store,
        ToothClass.Branch, ToothClass.ConditionalBranch,
    ];

    private readonly int _activeIqCount; // 1 when flat, IqCount when per-class
    private readonly CapturingMemory _capMem;
    private readonly List<ExecResult> _cdbBuffer = [];
    private readonly ICommitObserver? _commitObserver;

    // Architectural RAS shadow: updated only when a call/return actually retires.
    // On a flush the speculative _ras is restored from this, discarding the wrong-path
    // push/pop corruption that would otherwise cascade into return mispredictions.
    private readonly ReturnAddressStack _committedRas = new();
    private readonly ICriticalityPredictor? _criticalityPredictor;

    // Cross-tick latches
    private readonly Queue<FetchedInstr> _decodeQueue = new();

    // ISA services
    private readonly IDecoder _decoder;

    // EOLE Early Execution (Perais & Seznec, ISCA 2014, §3.2): physical registers written by an
    // Early-Execution computation during the current StepRename() call. Cleared at the top of
    // every call — see StepRename for why this must never persist across ticks.
    private readonly HashSet<int> _eeWrittenThisTick = [];
    private readonly bool _enableEoleEarlyExec;
    private readonly bool _enableEoleLateExec;

    // STT-ExpOnly (Yu et al., MICRO 2019): loads are treated as the only "transmitter" class
    // (explicit-channel-only, per the paper's own DelayExecute+STT-ExpOnly evaluated variant).
    // A load whose address depends on not-yet-visible data (RobEntry.SourceYrot) is held at
    // Issue until the shared visibility point clears it — see TryIssueSlot.
    private readonly bool _enableSttExpOnly;

    // Store address/data decomposition: gives a store's address-operand readiness independent
    // effect on StoreSetStallLoad/CheckLoadViolations/forwarding-candidate matching, ahead of
    // its data operand — see StepEarlyStoreAddressResolution.
    private readonly bool _enableEarlyStoreAddress;

    // Shared visibility-point tracker, selectable between the Spectre model
    // (SpectreVisibilityTracker: safe once all older branches resolve) and the Futuristic model
    // (FuturisticVisibilityTracker: safe once at the ROB head, or once no older in-flight
    // instruction of ANY squash-source type is unresolved), via sttFuturisticModel. Every gating
    // call site below is written against the IVisibilityTracker interface and is oblivious to
    // which model is active. _futuristicTracker aliases the same instance, narrowed to the
    // concrete type, only where Futuristic-specific registration/resolution calls are needed —
    // it is null in Spectre mode (and whenever the shared tracker itself is null).
    private readonly IVisibilityTracker? _vpTracker;
    private readonly FuturisticVisibilityTracker? _futuristicTracker;
    private Counter? _sttLoadIssueStallsCounter;

    // InvisiSpec (Yan et al., MICRO 2018 + 2019 Corrigendum): an unsafe speculative load (USL)
    // peeks its data at Execute via IMemory.PeekRead (no cache-state mutation) and is queued here;
    // its deferred real access (expose or validate) fires once the Spectre-model visibility point
    // (shared _vpTracker) clears it, in StepUslResolution. The register value reaches dependents
    // immediately via the ordinary Complete/CDB broadcast — only retirement waits on this queue
    // (RobEntry.PendingUslAccess), per the corrigendum's fix (never delay data propagation to the
    // visibility point; the paper's own simulator bug that did so inflated overhead substantially).
    private readonly bool _enableInvisiSpec;
    private readonly List<PendingUsl> _pendingUsls = [];
    private Counter? _invisispecExposuresCounter;
    private Counter? _invisispecValidationsCounter;

    private readonly record struct PendingUsl(int RobIdx, ulong InstrId, ulong Address, int Bytes, bool NeedsValidation);

    // Once a USL's real access fires (StepUslResolution) and misses, its miss latency drains here
    // as a per-entry countdown — the same MLP-overlap shape StepExecute gives ordinary load misses
    // via _inFlight — rather than a lump-sum stall charge, so multiple USLs resolving in the same
    // cycle overlap their miss penalties instead of paying them additively. No CDB broadcast is
    // involved (the USL's data already reached dependents at its original speculative Execute);
    // reaching zero here only clears RobEntry.PendingUslAccess so Commit can retire it. No squash
    // cleanup beyond StepFlush's full clear is needed: an entry only lands here once its visibility
    // point has already cleared, and once IsSafe(instrId) is true for an instruction it stays true
    // (dispatch order is InstrId order, so no older blocking entry can ever be registered after a
    // younger one has already resolved) — so no later partial squash can retroactively invalidate
    // an entry already in this list, only a full flush (e.g. an older trap) discards it.
    private readonly List<(int RobIdx, int Countdown)> _pendingUslLatency = [];

    // InvisiSpec's optional Per-Core Speculative Buffer in the LLC (Yan et al., MICRO 2018, §VI-C):
    // when enabled, a USL's speculative peek records the LLC-level line it touched here; if that
    // USL's own later deferred access (or a different USL's) lands on the same line while the
    // record is still resident, the deferred access is charged a cheap buffer-hit latency instead
    // of paying the full miss-and-refetch cost again — avoiding the double payment the base design
    // (see StepUslResolution's own docs) accepts as its cost of doing nothing extra. This is a
    // cost-only model: the real access below always still fires for real (so hit/miss stats and
    // cache-line installation stay correct); only the *charged stall* is overridden on a hit. The
    // paper's LLC-SB additionally has to enforce a security rule (§VII) that a squashed USL's
    // buffered *data* is never allowed to serve a later, unrelated request — that rule doesn't
    // apply here, because this model never serves data out of the buffer at all (the real
    // DLayers.Accessor.Read always runs and returns the true bytes); the buffer only ever
    // influences timing. Squash cleanup (StepFlush/StepPartialSquash) still discards entries for
    // instructions that never actually reach their deferred access, matching a real squash
    // cancelling the outstanding speculative fetch.
    private readonly bool _enableInvisiSpecLlcSb;
    private readonly int _llcSbCapacity;
    private readonly int _llcSbHitLatency;
    private readonly List<(ulong LineBase, ulong InstrId)> _llcSb = [];
    private Counter? _llcSbHitsCounter;

    private int? LlcBlockBytes => DLayers.L3Cache?.BlockBytes ?? DLayers.L2Cache?.BlockBytes ?? DLayers.Cache?.BlockBytes;

    // STT full DelayExecute+STT, explicit-branch slice only (Yu et al., MICRO 2019, Section 6.4.1):
    // closes the resolution-based implicit channel through explicit branches. Predictor training
    // (_predictor.Update) and the value/bypass-mispredict squashes already fire only at commit —
    // strictly later than any instruction's visibility point — so they are already safe by
    // construction and untouched here (verified by inspection, not assumed; the load-bearing
    // "ROB head implies already safe" invariant this relies on is asserted at the commit-time
    // mispredict site in StepCommit, not just trusted silently). The one real hole is the execute-time
    // speculative squash (see StepComplete/StepPartialSquash): armed the instant a branch resolves
    // mispredicted, with no taint check, so a squash whose timing depends on tainted data is itself
    // an implicit covert channel. When enabled, a mispredicted branch's own SourceYrot must be safe
    // (no older unresolved branch, via the shared _vpTracker) before its squash may be armed;
    // otherwise it is queued here and re-checked every cycle (StepSttMispredictResolution) — the
    // branch keeps executing/resolving normally (paper: "STT lets the instructions execute, and
    // only increases the latency of recovering from a tainted branch misprediction"), only the
    // *observable squash effect* is delayed. StoreSetPredictor's only persistent-state writer,
    // RecordViolation, already fires exclusively at commit (same as _predictor.Update) — no new gate
    // needed there. See _enableSttMemDepGating below for the one genuinely open training site.
    private readonly bool _enableSttImplicitBranches;
    private readonly List<ulong> _pendingTaintedMispredicts = [];
    private Counter? _sttMispredictDeferralsCounter;

    // STT full DelayExecute+STT, prediction-based implicit-channel slice (Yu et al., MICRO 2019,
    // §6.4.2 "Implicit branch with prediction"): "the relevant predictor ... [must] be updated only
    // by untainted data, i.e., only after the implicit branch predicate becomes untainted" — for
    // memory-dependence speculation that predicate is a function of the PRODUCING STORE's own
    // address (whether it aliases the load), not the load's own address/value. SmbPredictor.Train's
    // cold-start/ongoing-seeding call in StepComplete (an ordinary forwarded load teaching the
    // predictor a fresh SSN distance) is the one call site not already commit-time-safe: the other
    // three Train/TrainNoBypass calls all fire inside StepCommit's retire loop, strictly later than
    // any visibility point, same as _predictor.Update. When the producing store's own SourceYrot
    // isn't safe yet, the training is queued here and re-checked every cycle
    // (StepSmbTrainingResolution) rather than applied immediately.
    private readonly bool _enableSttMemDepGating;
    private readonly List<(ulong Pc, ulong Distance, ulong StoreYrot)> _pendingSmbTraining = [];
    private Counter? _sttMemDepTrainingDeferralsCounter;

    // Runahead execution (Mutlu et al., HPCA 2003; Naithani et al., HPCA 2020 / ISCA 2021):
    // a self-contained shadow execution lane entered when dispatch is stalled behind a full
    // ROB whose head is an incomplete load. Never touches the real ROB/IQ/LQ/SQ/RAS/predictor
    // history — only the RAT (snapshotted and restored), the PRF, and the real data-cache
    // accessor (which is what actually delivers the prefetch benefit).
    private readonly bool _enableRunahead;

    // Vector Runahead (Naithani, Ainsworth, Jones & Eeckhout, ISCA 2021): extends the scalar
    // shadow lane above with (1) a termination-condition change that keeps the episode running
    // until a detected dependent-load chain fully issues, not just until the real blocking load
    // resolves, and (2) N-wide lane replication of the shadow body once a load's PC is confirmed
    // striding by a small RPT-style stride table. No real VRF/vector-rename state is used —
    // "vectorization" is modeled purely as running the existing scalar shadow body N times with
    // different per-lane values, tracked by _runaheadVectorLanes (a physical register carries a
    // vector-lane set iff it's a key in that dictionary). Vector unrolling (repeated N-wide
    // rounds, bounded by _runaheadUnrollLength) is implemented below, as is vector pipelining
    // (§III-G, "P overlapped in-flight rounds", bounded by _runaheadPipelineDepth): instead of
    // requiring one full loop-body walk per round, a single origin-load vectorization event packs
    // up to _runaheadPipelineDepth rounds' worth of N-wide lanes at once (see
    // PipelineRoundsThisVisit), needing only ⌈U/P⌉ walks to reach U total rounds — no VRAT is
    // needed for this because a vectorized physical register's lane array is already width-generic
    // (see VectorizeByReplay), unlike the paper's fixed 512-bit AVX vector registers. Physical-
    // register pressure from deep unrolling/pipelining is instead managed by freeing a shadow-
    // allocated physical register immediately when its architectural register is re-renamed (see
    // the free-on-rename comment at the Rename() call sites) — a simplification of the paper's
    // register-deallocation queue that is exact, not approximate, because the shadow lane issues
    // strictly in program order.
    private readonly bool _enableVectorRunahead;
    private readonly List<IssuedInstr> _execBuffer = [];
    private readonly IExecutor _executor;
    private readonly FdipPrefetcher? _fdip;
    private readonly IMacroFuser? _macroFuser;
    private readonly IFetchTranslator? _fetchTranslator;

    // In per-class mode each IQ has iqCapacity slots; in flat mode all instructions
    // go to IQ[0] which has IqCount×iqCapacity slots so total capacity is the same.
    private readonly bool _flatIq;
    private readonly FuLatencyConfig _fuConfig;
    private readonly List<(int Countdown, ExecResult Result, bool HoldsMshr)> _inFlight = [];
    private readonly IssueQueue[] _iqs;
    private readonly int _issueWidth;
    private readonly LoadQueue _lq;
    private readonly int _maxDecodeDepth;

    // MSHR (Miss Status Holding Register) capacity: limits the number of simultaneously
    // outstanding load/atomic cache misses. Capacity 0 means unlimited (old behavior).
    private readonly int _mshrCapacity;
    private readonly IBranchPredictor _predictor;

    // OoOE structures
    private readonly PhysicalRegisterFile _prf;

    private readonly ReturnAddressStack _ras = new();
    private readonly RenameMap _rat;

    // True when a D-prefetcher is configured with PrefetchLatency > 0: prefetched lines
    // arrive after a countdown instead of instantly (realistic prefetch latency model).
    private readonly bool _realisticPrefetch;
    private readonly Queue<RenameEntry> _renameQueue = new();
    private readonly ReorderBuffer _rob;
    private readonly int _runaheadBudget;

    // Origin PCs whose unroll-round budget (_runaheadUnrollLength) was fully spent this episode —
    // see TerminateOrUnroll. Without this, the very next shadow-lane pass through the same origin
    // (the common case: a tight loop revisiting it) would re-enter the "start new chain" branch in
    // TryVectorizeTaintedLoad/TryVectorizeShadowStep and silently reset the round counter, so the
    // cap would never actually stick.
    private readonly HashSet<ulong> _runaheadCappedOrigins = [];

    // Physical registers allocated by THIS shadow episode's own Rename() calls — distinguishes
    // them from pre-episode (real) registers, which must never be freed mid-episode. See
    // FreeShadowRename.
    private readonly HashSet<int> _runaheadOwnedPhys = [];

    // Vector pipelining (Naithani et al., ISCA 2021 §III-G, "P overlapped in-flight rounds"):
    // the number of unroll rounds packed into a single origin-load vectorization event, instead
    // of requiring a separate full loop-body walk per round. See PipelineRoundsThisVisit.
    private readonly int _runaheadPipelineDepth;
    private readonly Dictionary<ulong, (ulong Value, int Bytes)> _runaheadStoreBuffer = [];
    private readonly HashSet<int> _runaheadTainted = [];
    private readonly int _runaheadUnrollLength;
    private readonly Dictionary<int, ulong[]> _runaheadVectorLanes = [];
    private readonly int _runaheadVectorWidth;
    private readonly SmbPredictor? _smbPredictor;
    private readonly StoreQueue _sq;
    private readonly StoreSetPredictor? _storeSets;
    private readonly ITrapController _trapController;
    private readonly IValuePredictor? _valuePredictor;
    private readonly VrStrideEntry[] _vrStrideTable;

    // Write buffer: absorbs post-commit store write-miss stalls so the pipeline
    // doesn't freeze for them. Each slot holds a countdown (in cycles) until the
    // corresponding write bus penalty expires. Capacity 0 disables the feature
    // and falls back to lump-sum charging (old behavior).
    private readonly int _wbCapacity;
    private readonly int[] _wbSlots; // per-slot miss countdown

    private bool _anyCache;
    private Counter _branchMissCounter = null!;
    private Counter? _cacheMissStallsCounter;

    // ── CPI-stack accounting (Eyerman et al., ASPLOS 2006); see CpiStack for the model ──
    private Counter _cpiBpredCounter = null!;

    // Post-mispredict refill: charge dispatch-empty cycles to the branch misprediction
    // component until the first correct-path instruction dispatches (the paper's "global
    // branch misprediction cycle counter is incremented every cycle until new instructions
    // enter the ROB").
    private bool _cpiBpredRefill;
    private Counter _cpiDTlbCounter = null!;
    private Counter _cpiITlbCounter = null!;
    private Counter _cpiL1DCounter = null!;
    private Counter _cpiL1ICounter = null!;
    private Counter _cpiL2DCounter = null!;
    private Counter _cpiL2ICounter = null!;
    private Counter _cpiL3DCounter = null!;
    private Counter _cpiL3ICounter = null!;

    // Provisional I-side miss cycles (the sFMT local counters): accumulated when fetch-stall
    // cycles drain, posted to the global counters when a miss-flagged instruction retires
    // (proving the stalled fetch was correct-path), discarded on any flush/squash.
    private long _cpiPendingItlb, _cpiPendingL1I, _cpiPendingL2I, _cpiPendingL3I;
    private Counter _cpiResourceCounter = null!;

    // Cumulative cycles classified to backend/store components; branch penalty windows
    // subtract the delta of this across the branch's ROB residency (the paper's rule of not
    // counting full-ROB cycles in the FMT branch penalty counters).
    private long _cpiStolenCycles;
    private Counter _cpiStoreCounter = null!;

    // Counters (initialized in Initialize)
    private Counter _cyclesCounter = null!;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _dcacheLatePrefetchHitsCounter;
    private Counter? _dcachePrefetchesCounter;

    // Critical-path prediction (Fields, Rubin & Bodík, ISCA 2001): D-source bookkeeping.
    // _dispatchStalledPrevCycle mirrors the condition already used for _stallsCounter (CD edge).
    // _pendingRedirectInstrId is latched by a branch misprediction (execute-time partial squash
    // or commit-time flush) and consumed by the very next dispatched instruction (ED edge) —
    // only branch mispredictions set it; traps/halts are not modeled by the paper's ED rule.
    private bool _dispatchStalledPrevCycle;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;
    private Counter? _eoleEarlyExecCounter;
    private Counter? _eoleLateExecCounter;

    // TMA ExecutionStalls classification (Table 1) is finalized at the end of StepDispatch,
    // not StepExecute, so that EOLE Late/Early Execution bypasses dispatched later in the
    // same tick (see StepDispatch) can be folded in. This field carries StepExecute's
    // IQ-issued count across that gap.
    private int _execCountThisTick;
    private bool _fetchFaulted; // suppress repeated fault entries until flush clears

    // Set by Drain() to stop admitting new instructions while the back-end empties out ahead of
    // a microarchitectural checkpoint. See OooTrain.Checkpoint.cs.
    private bool _fetchInhibited;

    // Runtime state
    private ulong _fetchPc;

    private Counter _flushesCounter = null!;
    private bool _flushPending;
    private ulong _flushTarget;
    private bool _halted;
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;
    private long _lastDPrefetches, _lastDLatePrefetchHits;

    // Delta tracking for hit/miss/prefetch counters
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastITlbHits, _lastITlbMisses, _lastDTlbHits, _lastDTlbMisses;
    private Counter _memViolationsCounter = null!;
    private Counter? _mshrStallsCounter;
    private int _mshrUsed; // MSHR slots currently occupied

    // PEvent recording
    private ulong _nextInstrId = 1;

    // Monotonically increasing sequence number assigned at dispatch to each
    // load/store/atomic. Shared between LQ and SQ so that program-order comparisons
    // across the two queues don't rely on ROB index arithmetic (which wraps).
    // Not reset on flush — entries are discarded by Flush(), the counter climbs.
    private ulong _nextMemSeqNo;
    private bool _pendingRedirect;
    private ulong _pendingRedirectInstrId;
    private int _pendingRollbackAbandonedPhys = -1;

    // Pending rename rollback for a trap/return-from-trap instruction that retires (leaves the
    // ROB) as part of raising the flush itself. StepFlush's walk-back only sees entries still in
    // the ROB, so this instruction's own rename must be undone separately — and specifically
    // *after* that walk-back, since younger (already-flushed) entries may have chained their
    // PrevPhysDestination through this instruction's PhysDestination, and undoing them first
    // would overwrite this rollback if applied before them. Set to -1 when nothing is pending.
    private int _pendingRollbackArch = -1;
    private int _pendingRollbackPrevPhys = -1;
    private Counter _retiredCounter = null!;
    private Counter _macroFusionsCounter = null!;
    private Counter _microFusionsCounter = null!;
    private bool _runaheadActive;
    private bool _runaheadChainActive;
    private ulong _runaheadChainOrigin;
    private int _runaheadChainRound; // rounds completed so far within the active chain (vector unrolling/pipelining)
    private ulong _runaheadChainTerminator;
    private Counter? _runaheadEpisodesCounter, _runaheadInstructionsCounter;
    private int _runaheadInstrCount;
    private RenameMapSnapshot _runaheadRatSnapshot;
    private ulong _runaheadRoundBaseAddr; // current unroll round's base address for stride-lane computation
    private Counter? _runaheadVectorChainsCounter, _runaheadVectorLaneAccessesCounter;

    // ── Main driver ────────────────────────────────────────────────────────────

    // Cached to avoid a fresh Action allocation per simulated cycle.
    private Action? _runCycle;
    private ulong _shadowPc;
    private Counter? _smbBypassesCounter, _smbMispredictsCounter;
    private ulong _squashInstrId;

    // Execute-time partial squash (branch mispredict resolved before the branch reaches the ROB
    // head). Detected in StepComplete, applied at the flush-check like a full flush but preserving
    // the redirecting branch and every older in-flight instruction. gem5 O3CPU redirects fetch at
    // execute (iew) the same way; this is the microarchitectural analogue.
    private bool _squashPending;
    private bool _squashTaken;
    private ulong _squashTarget;
    private Counter _stallsCounter = null!;

    // Top-Down Microarchitecture Analysis slot accounting (Yasin, ISPASS 2014); see
    // TopDownBreakdown for the metric formulas these feed.
    private Counter _tdExecStallCyclesCounter = null!;
    private Counter _tdFetchBubblesCounter = null!;
    private Counter _tdFetchLatencyCyclesCounter = null!;
    private Counter _tdMemStallLoadCyclesCounter = null!;
    private Counter _tdMemStallStoreCyclesCounter = null!;
    private Counter _tdRecoveryBubblesCounter = null!;
    private Counter _tdSlotsIssuedCounter = null!;
    private Counter _tdSlotsRetiredCounter = null!;
    private Counter _tdTotalSlotsCounter = null!;
    private Counter? _vpPredictionsCounter, _vpCorrectCounter, _vpMispredictsCounter;
    private Counter? _wbAbsorbedStallsCounter;
    private int _wbOccupied; // number of slots currently counting down

    public OoOPipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        int issueWidth,
        int robCapacity,
        int iqCapacity,
        int extraPhysRegs,
        IBranchPredictor predictor,
        FuLatencyConfig fuConfig,
        PEventLog? pEventLog = null,
        int streamPrefetchDepth = 4,
        int streamMaxCount = 8,
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false,
        IMemory? fdipBackingMemory = null,
        int fdipFtqCapacity = 0,
        bool rdipEnabled = false,
        bool enableStoreSets = false,
        bool enableCriticalityPrediction = false,
        bool enableSmbBypass = false,
        bool enableRunahead = false,
        int runaheadBudget = 200,
        bool enableVectorRunahead = false,
        int runaheadVectorWidth = 8,
        int runaheadUnrollLength = 8,
        int runaheadPipelineDepth = 1,
        IValuePredictor? valuePredictor = null,
        bool enableEoleLateExec = false,
        bool enableEoleEarlyExec = false,
        bool enableSttExpOnly = false,
        bool enableInvisiSpec = false,
        bool enableSttImplicitBranches = false,
        bool enableSttMemDepGating = false,
        bool enableInvisiSpecLlcSb = false,
        int llcSbCapacity = 16,
        int llcSbHitLatency = 1,
        bool sttFuturisticModel = false,
        bool enableEarlyStoreAddress = false
    ) : base(name, parent, esc) {
        PEventLog = pEventLog;
        _commitObserver = commitObserver;
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
        _macroFuser = mechanism.MacroFuser;
        _predictor = predictor;
        _fuConfig = fuConfig;
        ILayers = iLayers;
        DLayers = dLayers;
        _realisticPrefetch = DLayers is { Prefetcher: not null, Cache.PrefetchLatency: > 0, };
        _capMem = new CapturingMemory(DLayers.Accessor);
        _issueWidth = issueWidth;
        _maxDecodeDepth = issueWidth * 4;
        _fetchPc = entryPoint;
        _flatIq = flatIq;
        _activeIqCount = flatIq ? 1 : OoOPipelineCore.IqCount;

        State = mechanism.CreateArchState();
        State.Pc = entryPoint;
        _fetchTranslator = mechanism.CreateFetchTranslator(State, ILayers.Accessor);

        _fdip = fdipFtqCapacity > 0 && fdipBackingMemory is not null && iLayers.Cache is not null
            ? new FdipPrefetcher(predictor, _decoder, fdipBackingMemory, iLayers.Cache, entryPoint, fdipFtqCapacity)
            : null;

        Rdip = rdipEnabled && iLayers.Cache is not null
            ? new RdipPrefetcher(iLayers.Cache, _decoder)
            : null;
        _storeSets = enableStoreSets ? new StoreSetPredictor() : null;
        _criticalityPredictor = enableCriticalityPrediction
            ? new TokenPassingCriticalityPredictor(robCapacity)
            : null;
        _smbPredictor = enableSmbBypass ? new SmbPredictor() : null;
        _valuePredictor = valuePredictor;
        _enableEoleLateExec = enableEoleLateExec && valuePredictor is not null;
        _enableEoleEarlyExec = enableEoleEarlyExec;
        _enableRunahead = enableRunahead;
        _runaheadBudget = runaheadBudget;
        _enableVectorRunahead = enableRunahead && enableVectorRunahead;
        _runaheadVectorWidth = runaheadVectorWidth;
        _runaheadUnrollLength = runaheadUnrollLength;
        _runaheadPipelineDepth = Math.Max(1, runaheadPipelineDepth);
        _vrStrideTable = _enableVectorRunahead ? new VrStrideEntry[256] : [];
        _enableSttExpOnly = enableSttExpOnly;
        _enableEarlyStoreAddress = enableEarlyStoreAddress;
        _enableInvisiSpec = enableInvisiSpec;
        _enableSttImplicitBranches = enableSttImplicitBranches;
        _enableSttMemDepGating = enableSttMemDepGating;
        _vpTracker = enableSttExpOnly || enableInvisiSpec || enableSttImplicitBranches || enableSttMemDepGating
            ? sttFuturisticModel ? new FuturisticVisibilityTracker() : new SpectreVisibilityTracker()
            : null;
        _futuristicTracker = _vpTracker as FuturisticVisibilityTracker;
        _enableInvisiSpecLlcSb = enableInvisiSpec && enableInvisiSpecLlcSb;
        _llcSbCapacity = llcSbCapacity;
        _llcSbHitLatency = llcSbHitLatency;

        int archRegs = State.IntegerRegisters.Count;
        int physRegs = archRegs + extraPhysRegs;
        _prf = new PhysicalRegisterFile(physRegs);
        _rat = new RenameMap(archRegs, physRegs);
        _rob = new ReorderBuffer(robCapacity);
        _iqs = new IssueQueue[OoOPipelineCore.IqCount];
        if (flatIq) {
            // Flat mode: one unified IQ with IqCount× capacity; dummy 1-slot IQs for the rest
            // (they receive no instructions since IqIndex always returns 0).
            _iqs[0] = new IssueQueue(OoOPipelineCore.IqCount * iqCapacity);
            for (var i = 1; i < OoOPipelineCore.IqCount; i++) _iqs[i] = new IssueQueue(1);
        }
        else {
            for (var i = 0; i < OoOPipelineCore.IqCount; i++) _iqs[i] = new IssueQueue(iqCapacity);
        }

        _lq = new LoadQueue(lqCapacity > 0 ? lqCapacity : robCapacity);
        _sq = new StoreQueue(sqCapacity > 0 ? sqCapacity : robCapacity);
        StreamingEngine = new StreamingEngine(streamPrefetchDepth, streamMaxCount);
        _wbCapacity = writeBufferCapacity;
        _wbSlots = writeBufferCapacity > 0 ? new int[writeBufferCapacity] : [];
        _mshrCapacity = mshrCapacity;
    }

    // Streaming engine (architectural; survives pipeline flushes)
    public StreamingEngine StreamingEngine { get; }

    // Memory hierarchy layers
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }

    // In-flight prefetches also hold miss-tracking slots. A demand hit on an in-flight
    // line retires the prefetch entry and transfers its remaining latency (and MSHR slot)
    // to the load itself, so the two counts never overlap.
    private int InFlightPrefetches => _realisticPrefetch ? DLayers.Cache!.InFlightPrefetchCount : 0;
    public PEventLog? PEventLog { get; }
    public RdipPrefetcher? Rdip { get; }

    public IArchState State { get; }

    private int IqIndex(ToothClass cls) => _flatIq
        ? 0
        : cls switch {
            ToothClass.FloatingPoint or ToothClass.FloatDivSqrt      => 1,
            ToothClass.Branch or ToothClass.ConditionalBranch        => 2,
            ToothClass.Vector or ToothClass.Uve                      => 3,
            ToothClass.Load or ToothClass.Store or ToothClass.Atomic => 4,
            _                                                        => 0,
        };

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _macroFusionsCounter = Dials.AddCounter(
            "macro_fusions", "Instruction pairs renamed as a single macro-fused ROB/IQ entry (compare+branch)"
        );
        _microFusionsCounter = Dials.AddCounter(
            "micro_fusions", "Instruction pairs renamed as a single micro-fused ROB/IQ entry (load+ALU)"
        );
        _flushesCounter = Dials.AddCounter("flushes", "Pipeline flushes (branch + trap)");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions");
        _stallsCounter = Dials.AddCounter(
            "stalls", "Dispatch-stall cycles (ROB/IQ/LQ/SQ full) + cache miss penalties"
        );
        _memViolationsCounter = Dials.AddCounter(
            "mem_order_violations", "Memory-order violations: speculative load read stale data"
        );
        if (_smbPredictor is not null) {
            _smbBypassesCounter = Dials.AddCounter(
                "smb_bypasses", "NoSQ speculative memory bypasses attempted at dispatch"
            );
            _smbMispredictsCounter = Dials.AddCounter(
                "smb_mispredicts", "NoSQ speculative memory bypasses that mispredicted"
            );
        }

        if (_valuePredictor is not null) {
            _vpPredictionsCounter = Dials.AddCounter(
                "vp_predictions", "Value predictions supplied speculatively at rename"
            );
            _vpCorrectCounter = Dials.AddCounter(
                "vp_correct", "Value predictions that verified correct at execute"
            );
            _vpMispredictsCounter = Dials.AddCounter(
                "vp_mispredicts", "Value predictions that mispredicted and were squashed at commit"
            );
        }

        if (_enableSttExpOnly)
            _sttLoadIssueStallsCounter = Dials.AddCounter(
                "stt_load_issue_stalls",
                "Cycles a load was held at Issue by STT-ExpOnly pending its address operands' visibility point"
            );

        if (_enableInvisiSpec) {
            _invisispecExposuresCounter = Dials.AddCounter(
                "invisispec_exposures", "USLs resolved via a cheap exposure (no older load/fence at execute time)"
            );
            _invisispecValidationsCounter = Dials.AddCounter(
                "invisispec_validations", "USLs resolved via validation (an older load/fence was in the ROB)"
            );
        }

        if (_enableInvisiSpecLlcSb)
            _llcSbHitsCounter = Dials.AddCounter(
                "invisispec_llc_sb_hits",
                "USL deferred accesses served from the Per-Core LLC-SB instead of paying a fresh miss (§VI-C)"
            );

        if (_enableSttImplicitBranches)
            _sttMispredictDeferralsCounter = Dials.AddCounter(
                "stt_mispredict_deferrals",
                "Cycles a mispredicted branch's squash was held pending its own visibility point (STT explicit-branch protection)"
            );

        if (_enableSttMemDepGating)
            _sttMemDepTrainingDeferralsCounter = Dials.AddCounter(
                "stt_memdep_training_deferrals",
                "Cycles a queued SmbPredictor training update was held pending the producing store's own visibility point"
            );

        if (_enableEoleLateExec)
            _eoleLateExecCounter = Dials.AddCounter(
                "eole_late_exec",
                "Instructions retired via EOLE Late Execution (bypassed IQ/Issue/Execute entirely)"
            );

        if (_enableEoleEarlyExec)
            _eoleEarlyExecCounter = Dials.AddCounter(
                "eole_early_exec",
                "Instructions computed via EOLE Early Execution at Rename (bypassed IQ/Issue/Execute entirely)"
            );

        if (_enableRunahead) {
            _runaheadEpisodesCounter = Dials.AddCounter(
                "runahead_episodes", "Runahead shadow-execution episodes entered"
            );
            _runaheadInstructionsCounter = Dials.AddCounter(
                "runahead_instructions", "Shadow instructions executed across all runahead episodes"
            );
        }

        if (_enableVectorRunahead) {
            _runaheadVectorChainsCounter = Dials.AddCounter(
                "runahead_vector_chains", "Vector Runahead dependent-load chains vectorized"
            );
            _runaheadVectorLaneAccessesCounter = Dials.AddCounter(
                "runahead_vector_lane_accesses",
                "Vector Runahead shadow lane memory/ALU accesses (scalar-equivalent units)"
            );
        }

        Dials.AddDial(
            "cpi",
            () => _retiredCounter.Value == 0 ? 0.0 : _cyclesCounter.Value / (double)_retiredCounter.Value,
            "Cycles per instruction"
        );
        Dials.AddDial(
            "ipc",
            () => _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
            "Instructions per cycle"
        );

        // ── Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) ──────────────
        // Slot accounting at the dispatch stage, this machine's frontend/backend border.
        TopDownCounters td = TopDownBreakdown.RegisterCounters(Dials, ComputeTopDown);
        _tdTotalSlotsCounter = td.TotalSlots;
        _tdSlotsIssuedCounter = td.SlotsIssued;
        _tdSlotsRetiredCounter = td.SlotsRetired;
        _tdFetchBubblesCounter = td.FetchBubbles;
        _tdRecoveryBubblesCounter = td.RecoveryBubbles;
        _tdFetchLatencyCyclesCounter = td.FetchLatencyCycles;
        _tdExecStallCyclesCounter = td.ExecStallCycles;
        _tdMemStallLoadCyclesCounter = td.MemStallLoadCycles;
        _tdMemStallStoreCyclesCounter = td.MemStallStoreCycles;

        // ── CPI stack via interval analysis (Eyerman et al., ASPLOS 2006) ──────────
        CpiStackCounters cpi = CpiStack.RegisterCounters(Dials, ComputeCpiStack);
        _cpiL1ICounter = cpi.L1I;
        _cpiL2ICounter = cpi.L2I;
        _cpiL3ICounter = cpi.L3I;
        _cpiITlbCounter = cpi.ITlb;
        _cpiBpredCounter = cpi.Bpred;
        _cpiL1DCounter = cpi.L1D;
        _cpiL2DCounter = cpi.L2D;
        _cpiL3DCounter = cpi.L3D;
        _cpiDTlbCounter = cpi.DTlb;
        _cpiStoreCounter = cpi.Store;
        _cpiResourceCounter = cpi.Resource;

        _anyCache = ILayers.Cache is not null || DLayers.Cache is not null
                                              || ILayers.L2Cache is not null || DLayers.L2Cache is not null
                                              || ILayers.L3Cache is not null || DLayers.L3Cache is not null
                                              || ILayers.L2Bdi is not null || DLayers.L2Bdi is not null
                                              || ILayers.L3Bdi is not null || DLayers.L3Bdi is not null
                                              || ILayers.L2Ceaser is not null || DLayers.L2Ceaser is not null
                                              || ILayers.L3Ceaser is not null || DLayers.L3Ceaser is not null
                                              || ILayers.L2Scatter is not null || DLayers.L2Scatter is not null
                                              || ILayers.L3Scatter is not null || DLayers.L3Scatter is not null
                                              || ILayers.Tlb is not null || DLayers.Tlb is not null;
        if (_anyCache) {
            // Lump-sum model: penalty cycles are appended per cycle, not overlapped.
            // This overestimates stalls relative to real out-of-order memory-level parallelism.
            _cacheMissStallsCounter = Dials.AddCounter(
                "cache_miss_stalls", "Stall cycles from memory hierarchy misses"
            );
            if (_wbCapacity > 0)
                _wbAbsorbedStallsCounter = Dials.AddCounter(
                    "wb_absorbed_stalls", "Store write-miss cycles absorbed by the write buffer"
                );
            if (_mshrCapacity > 0)
                _mshrStallsCounter = Dials.AddCounter(
                    "mshr_stalls", "Cycles a load was held at issue waiting for a free MSHR slot"
                );
        }

        if (ILayers.Cache is not null) {
            _icacheHitsCounter = Dials.AddCounter("icache_hits", "L1 I-cache hits");
            _icacheMissesCounter = Dials.AddCounter("icache_misses", "L1 I-cache misses");
        }

        if (ILayers.L2Cache is not null || ILayers.L2Bdi is not null || ILayers.L2Ceaser is not null
                                         || ILayers.L2Scatter is not null) {
            _l2IcacheHitsCounter = Dials.AddCounter("l2_icache_hits", "L2 I-cache hits");
            _l2IcacheMissesCounter = Dials.AddCounter("l2_icache_misses", "L2 I-cache misses");
        }

        if (ILayers.L3Cache is not null || ILayers.L3Bdi is not null || ILayers.L3Ceaser is not null
                                         || ILayers.L3Scatter is not null) {
            _l3IcacheHitsCounter = Dials.AddCounter("l3_icache_hits", "L3 I-cache hits");
            _l3IcacheMissesCounter = Dials.AddCounter("l3_icache_misses", "L3 I-cache misses");
        }

        if (DLayers.Cache is not null) {
            _dcacheHitsCounter = Dials.AddCounter("dcache_hits", "L1 D-cache hits");
            _dcacheMissesCounter = Dials.AddCounter("dcache_misses", "L1 D-cache misses");
            if (DLayers.Prefetcher is not null) {
                _dcachePrefetchesCounter = Dials.AddCounter("dcache_prefetches", "L1 D-cache prefetch fills");
                if (_realisticPrefetch)
                    _dcacheLatePrefetchHitsCounter = Dials.AddCounter(
                        "dcache_late_prefetch_hits",
                        "Demand hits on lines whose prefetch was still in flight (paid the remaining countdown)"
                    );
            }
        }

        if (DLayers.L2Cache is not null || DLayers.L2Bdi is not null || DLayers.L2Ceaser is not null
                                         || DLayers.L2Scatter is not null) {
            _l2DcacheHitsCounter = Dials.AddCounter("l2_dcache_hits", "L2 D-cache hits");
            _l2DcacheMissesCounter = Dials.AddCounter("l2_dcache_misses", "L2 D-cache misses");
        }

        if (DLayers.L3Cache is not null || DLayers.L3Bdi is not null || DLayers.L3Ceaser is not null
                                         || DLayers.L3Scatter is not null) {
            _l3DcacheHitsCounter = Dials.AddCounter("l3_dcache_hits", "L3 D-cache hits");
            _l3DcacheMissesCounter = Dials.AddCounter("l3_dcache_misses", "L3 D-cache misses");
        }

        if (ILayers.Tlb is not null) {
            _itlbHitsCounter = Dials.AddCounter("itlb_hits", "I-TLB hits");
            _itlbMissesCounter = Dials.AddCounter("itlb_misses", "I-TLB misses");
        }

        if (DLayers.Tlb is not null) {
            _dtlbHitsCounter = Dials.AddCounter("dtlb_hits", "D-TLB hits");
            _dtlbMissesCounter = Dials.AddCounter("dtlb_misses", "D-TLB misses");
        }
    }

    public override void Wind() {
        // Seed the PRF's identity-mapped architectural registers from State.IntegerRegisters —
        // the PRF starts zeroed at construction (see PhysicalRegisterFile ctor) and execution
        // reads register operands from the PRF, never from State.IntegerRegisters directly. A
        // caller that writes State.IntegerRegisters between construction and Run()/BeginStepping()
        // (e.g. ArchitecturalCheckpoint.RestoreInto) would otherwise be silently invisible to the
        // OoO pipeline. On a fresh (never-written) ArchState this seeds zeros — no behavior change.
        for (var i = 0; i < State.IntegerRegisters.Count; i++) _prf.Write(i, State.IntegerRegisters.Read(i));
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    private void RunCycle() {
        if (_halted) return;

        // Advance all active streams one prefetch step. Streams are architectural state
        // and run every cycle, independent of pipeline flush/stall.
        StreamingEngine.Step(DLayers.Accessor, State.UveScalars?.VectorLength ?? 1);

        // Charge the previous cycle's instruction-fetch (and any store-commit) stall
        // penalties. Load-miss penalties are NOT lump-summed here — StepExecute gives
        // each load its own in-flight latency so independent misses overlap
        // (memory-level parallelism); see StepExecute.
        if (_anyCache) {
            (long iStalls, long dStalls) = DrainStalls();
            ChargeStallCycles(iStalls + dStalls);
            // TMA: an I-fetch miss freezes the whole machine with no uop delivery and no
            // backend stall — whole-cycle fetch starvation (Frontend Latency Bound). The
            // leftover D-side stalls here are store-commit write misses: execution stalls
            // pending on stores (Backend Memory Bound).
            if (iStalls > 0) {
                _tdFetchBubblesCounter.IncrementBy(iStalls * _issueWidth);
                _tdFetchLatencyCyclesCounter.IncrementBy(iStalls);
            }

            if (dStalls > 0) {
                _tdExecStallCyclesCounter.IncrementBy(dStalls);
                _tdMemStallStoreCyclesCounter.IncrementBy(dStalls);
                _cpiStoreCounter.IncrementBy(dStalls);
                _cpiStolenCycles += dStalls;
            }
        }

        _cyclesCounter.Increment();
        _tdTotalSlotsCounter.IncrementBy(_issueWidth);
        State.OnCycle();

        // CPI stack (ASPLOS 2006, sections 4.2/4.3): a cycle where the backend is exerting
        // backpressure (dispatch structurally blocked — ROB/IQ/LQ/SQ full — or the ROB
        // outright full) while an incomplete instruction blocks the ROB head is a backend
        // completion stall, classified by the deepest cache level the blocking load missed —
        // or as a long-latency/dependence resource stall for non-loads and loads that hit.
        // The paper uses "ROB full" alone; in this machine the per-class issue queues are
        // the binding window resource for serialized chains, so IQ/LQ/SQ backpressure
        // (_dispatchStalledPrevCycle) must count as well. These cycles are excluded from any
        // in-flight branch's misprediction penalty window via _cpiStolenCycles.
        if ((_rob.IsFull || _dispatchStalledPrevCycle)
         && _rob is { IsEmpty: false, Head: { IsComplete: false, } blockedHead, }) {
            Counter blocked = blockedHead.IsLoad
                ? blockedHead.DMissClass switch {
                    CpiMissClass.L1D  => _cpiL1DCounter,
                    CpiMissClass.L2D  => _cpiL2DCounter,
                    CpiMissClass.L3D  => _cpiL3DCounter,
                    CpiMissClass.DTlb => _cpiDTlbCounter,
                    _                 => _cpiResourceCounter,
                }
                : _cpiResourceCounter;
            blocked.Increment();
            _cpiStolenCycles++;
        }

        // Futuristic model, condition (i): the current ROB head is safe regardless of any still-
        // pending squash source (see FuturisticVisibilityTracker's class doc) — force it resolved
        // before anything below reads IsSafe this cycle, so a USL/load sitting at the head isn't
        // stuck waiting on a squash source (e.g. an EOLE Late-Execution verification) that can
        // only ever be settled at Commit, which would otherwise never arrive since retirement
        // itself is what's gated on IsSafe becoming true.
        if (_futuristicTracker is not null && !_rob.IsEmpty) _futuristicTracker.ForceResolve(_rob.Head.InstrId);

        // Complete: broadcast last tick's execution results onto CDB.
        StepComplete();

        // STT full DelayExecute+STT, explicit-branch slice: re-check any mispredicted branch whose
        // squash StepComplete deferred because its own resolution was still tainted.
        if (_enableSttImplicitBranches) StepSttMispredictResolution();

        // STT full DelayExecute+STT, prediction-based implicit-channel slice: re-check any queued
        // SmbPredictor training update whose producing store was still tainted.
        if (_enableSttMemDepGating) StepSmbTrainingResolution();

        // InvisiSpec: resolve any USL whose visibility point cleared this cycle (fire its
        // deferred real cache access) before Commit checks RobEntry.PendingUslAccess below.
        if (_enableInvisiSpec) StepUslResolution();

        // Commit: retire completed ROB heads in program order.
        StepCommit();

        // Tick down write-buffer miss countdowns. Runs every cycle (including halt/flush cycles)
        // because the write buffer holds committed architectural state, not speculative state.
        StepWriteBuffer();

        // Tick down in-flight prefetch countdowns. Like the write buffer, prefetched lines
        // are non-speculative cache state, so they keep arriving through halt/flush cycles.
        if (_realisticPrefetch) DLayers.Cache!.TickPrefetch();
        // Drain one write-back buffer entry per cycle (asynchronous background drain).
        if (_anyCache) {
            ILayers.TickWb();
            DLayers.TickWb();
            ILayers.TickMshr();
            DLayers.TickMshr();
            ILayers.TickPorts();
            DLayers.TickPorts();
        }

        if (_halted || _flushPending || _squashPending) {
            // TMA RecoveryBubbles: the issue pipeline delivers nothing this cycle because the
            // machine is recovering from a flush/squash (Bad Speculation, Table 1).
            if (_flushPending || _squashPending) _tdRecoveryBubblesCounter.IncrementBy(_issueWidth);
            // CPI stack: discard provisional I-side miss cycles — the flush proves the stalled
            // fetches were wrong-path. Their frozen cycles stay inside the mispredicted
            // branch's penalty window (the ASPLOS 2006 policy of absorbing wrong-path
            // frontend misses into the branch misprediction component).
            if (_flushPending || _squashPending) _cpiPendingL1I = _cpiPendingL2I = _cpiPendingL3I = _cpiPendingItlb = 0;
            // Restore the RAT to its pre-episode baseline before the real flush/squash's own
            // walk-back runs, so the walk-back's absolute writes start from a correct base
            // regardless of how far the shadow lane had progressed.
            if (_runaheadActive) ExitRunahead();
            if (_flushPending)
                StepFlush();
            else if (_squashPending) StepPartialSquash();
            if (!_halted) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
            return;
        }

        // Execute: run last tick's issued instructions.
        StepExecute();

        // Early store-address resolution: any store whose address operand became ready this
        // tick (via StepComplete's broadcast, above) gets a provisional Address/AddressKnown
        // now, ahead of its own Issue/Execute — so StoreSetStallLoad/CheckLoadViolations/
        // forwarding-candidate matching (all address-only) can react before this store's data
        // operand arrives.
        if (_enableEarlyStoreAddress) StepEarlyStoreAddressResolution();

        // Issue: select up to issueWidth ready IQ entries.
        StepIssue();

        // Dispatch: allocate ROB + IQ slots from the rename queue.
        StepDispatch();

        // Rename: drain decoded instructions through the RAT/PRF rename stage.
        StepRename();

        // Fetch: fill the decode queue with new speculative instructions.
        if (!_fetchInhibited) StepFetch();

        // Runahead: on a full-window stall behind an incomplete load, pre-execute past it in
        // a self-contained shadow lane to generate prefetches (Mutlu et al., HPCA 2003).
        if (_enableRunahead) {
            if (!_runaheadActive && NeedsRunahead()) EnterRunahead();
            if (_runaheadActive) RunaheadStep();
        }

        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // ── Pipeline stages ────────────────────────────────────────────────────────

    /// <summary>CDB broadcast: apply Execute T-1 results to PRF + IQ + ROB.</summary>
    private void StepComplete() {
        // Track the oldest branch that resolves mispredicted this tick, to redirect fetch at
        // execute rather than deferring the flush to the ROB head (see StepPartialSquash).
        var haveMispredict = false;
        ulong oldestMispredId = 0;
        RobEntry? oldestMispredRob = null;

        foreach (ExecResult r in _cdbBuffer) {
            RobEntry rob = _rob.At(r.RobIdx);
            rob.IsComplete = true;
            rob.CompletedTick = (ulong)Escapement.CurrentTick;
            rob.ResolvedNextPc = r.ResolvedNextPc;
            rob.HasTrap = r.Trap is not null;
            rob.Trap = r.Trap;
            rob.IsReturnFromTrap = r.IsReturnFromTrap;
            rob.ReturnPrivilege = r.ReturnPrivilege;
            rob.RequestHalt = r.RequestHalt;
            rob.RequestBlock = r.RequestBlock;
            rob.SideEffect = r.SideEffect;

            // Futuristic model: this instruction's own trap status is now known. Only resolve the
            // bit when it did NOT trap — a trapping instruction stays an unresolved squash source
            // until its squash actually fires at commit, exactly like a mispredicted branch below.
            if (_futuristicTracker is not null && !rob.HasTrap) _futuristicTracker.ResolveTrap(r.InstrId);

            // A branch (only branches set ResolvedNextPc) that resolved off its predicted path.
            bool thisMispredicted = rob.ResolvedNextPc is { HasValue: true, Value: var resolvedPc, }
                                  && resolvedPc != rob.PredictedNextPc;

            // Only remove a branch from the shared visibility tracker once it is confirmed
            // correctly predicted. A mispredicted branch must stay "unresolved" until the squash
            // that discards its younger instructions actually takes effect (StepPartialSquash/
            // StepFlush explicitly resolve/clear it there) — otherwise, when this branch happens
            // to already be the ROB head (so no execute-time partial squash is armed here; the
            // mispredict is instead only detected later, at commit), there is a one-cycle window
            // where StepUslResolution/STT's IsSafe checks would wrongly treat younger USLs/loads
            // as already safe before the squash is even flagged.
            if (_vpTracker is not null && rob.ResolvedNextPc.HasValue && !thisMispredicted)
                _vpTracker.OnBranchResolved(r.InstrId);

            if (thisMispredicted && (!haveMispredict || r.InstrId < oldestMispredId)) {
                haveMispredict = true;
                oldestMispredId = r.InstrId;
                oldestMispredRob = rob;
            }

            if (r.HasStoreCapture) {
                // Update the SQ entry with the resolved store address and value.
                SqEntry sq = _sq.At(rob.SqIdx);
                sq.AddressKnown = true;
                sq.DataKnown = true;
                sq.Address = r.StoreAddr;
                sq.Value = r.StoreVal;
                sq.Width = r.StoreBytes;
                // A store's address just became known: check whether any younger speculative
                // load has already executed against the same address with a stale value.
                CheckLoadViolations(sq.SeqNo, r.StoreAddr, r.StoreBytes, sq.Pc);
                _storeSets?.OnStoreIssued(sq.Pc, sq.SeqNo);
                if (_smbPredictor is not null) CheckBypassLoads(sq);
                // Futuristic model: an older store's address being unknown is exactly what lets a
                // younger load alias/violate it (Table I's "address alias between a load and an
                // earlier store") — resolving this bit is what stops this store from blocking
                // every younger instruction's safety, regardless of whether the store itself later
                // traps (if it does, the Trap bit above still keeps it — and everything younger,
                // via the eventual full flush — correctly blocked).
                _futuristicTracker?.ResolveStoreAddr(r.InstrId);
            }

            // Load disambiguation state (LQ.Executed/Address + violation check) is
            // registered at EXECUTE time in StepExecute, not here — see the comment there.
            // Registering at broadcast time opened a window, under memory-level
            // parallelism, where a missed load sat invisible in _inFlight while an older
            // store resolved and committed, so the load broadcast a stale value.

            if (!r.RegValue.HasValue || r.PhysDest < 0) continue;

            // SMB (NoSQ) verification: this load's own (shadow) execution just produced the
            // ground-truth value. Compare it against whatever the early bypass broadcast
            // already wrote — a mismatch is caught here and squashed at commit (StepCommit),
            // same recovery path as an ordinary memory-order Violated load.
            if (rob is { IsLoad: true, LqIdx: >= 0, }) {
                LqEntry lq = _lq.At(rob.LqIdx);
                if (lq.SpeculativelyCompleted) {
                    if (_prf.Read(r.PhysDest) != r.RegValue.Value) {
                        lq.BypassMispredicted = true;
                        // Capture the true producer now — by the time this load reaches commit,
                        // the actual producing store may have already retired out of the SQ.
                        ulong? actualProducer = FindForwardingProducerSeqNo(lq.SeqNo, lq.Address, lq.Bytes);
                        lq.HasActualProducer = actualProducer.HasValue;
                        lq.ActualProducerSeqNo = actualProducer ?? 0;
                    }
                    else {
                        // Futuristic model: verified correct — this load can stop blocking younger
                        // instructions on the SMB-bypass squash source.
                        _futuristicTracker?.ResolveSmb(r.InstrId);
                    }
                }
                else if (_smbPredictor is not null && !lq.Bypassed) {
                    // Cold-start / ongoing seeding: TryPredict never fires until Train has
                    // been called once for a PC, and Train was otherwise only reachable via
                    // an already-successful bypass. An ordinary load that forwarded from an
                    // in-flight store here teaches SmbPredictor the observed SSN distance so
                    // later dynamic instances of this load's PC can bypass.
                    ulong? producer = FindForwardingProducerSeqNo(lq.SeqNo, lq.Address, lq.Bytes);
                    if (producer.HasValue && lq.SeqNo > producer.Value) {
                        ulong distance = lq.SeqNo - producer.Value;
                        // STT (Yu et al., MICRO 2019, §6.4.2 "Implicit branch with prediction"): "the
                        // relevant predictor ... [must] be updated only by untainted data, i.e., only
                        // after the implicit branch predicate becomes untainted" — for memory-dependence
                        // speculation that predicate is a function of the PRODUCING STORE's address
                        // (whether it aliases the load), not the load's own address or loaded value. If
                        // that store's own SourceYrot isn't safe yet, defer training rather than let a
                        // still-tainted store address influence when/what SmbPredictor learns.
                        if (_enableSttMemDepGating
                         && FindSqBySeqNo(producer.Value) is { } producerSq
                         && _rob.At(producerSq.RobIdx).SourceYrot is { } storeYrot
                         && !_vpTracker!.IsSafe(storeYrot))
                            _pendingSmbTraining.Add((rob.Pc, distance, storeYrot));
                        else
                            _smbPredictor.Train(rob.Pc, distance);
                    }
                }
            }

            // Value prediction verification: the real execution just produced the ground-truth
            // value. Compare against the speculative value already written at rename — a
            // mismatch is caught here and squashed at commit (StepCommit), never here (the
            // unconditional PRF write below self-heals the value regardless of the outcome).
            if (rob.WasValuePredicted) {
                if (_prf.Read(r.PhysDest) == r.RegValue.Value) {
                    _vpCorrectCounter?.Increment();
                    // Futuristic model: verified correct — stop blocking younger instructions on
                    // the value-prediction squash source. (EOLE Late Execution's own verification,
                    // which never reaches here, resolves this bit at Commit instead — see StepCommit.)
                    _futuristicTracker?.ResolveVp(r.InstrId);
                }
                else
                    rob.ValuePredMispredicted = true;
            }

            _prf.Write(r.PhysDest, r.RegValue.Value);
            foreach (IssueQueue iq in _iqs) iq.Broadcast(r.PhysDest, r.RegValue.Value, r.InstrId);
        }

        _cdbBuffer.Clear();

        // Arm an execute-time partial squash on the oldest branch that mispredicted this tick —
        // unless STT explicit-branch protection is on and this branch's own resolution is still
        // tainted (SourceYrot not yet safe): squashing on it right now would make the squash's
        // timing a function of tainted data (the resolution-based implicit channel, §6.4.1). Such
        // a branch is queued instead and re-checked every cycle by StepSttMispredictResolution —
        // it keeps executing/resolving normally, only this observable effect is delayed.
        if (haveMispredict) {
            if (_enableSttImplicitBranches && oldestMispredRob!.SourceYrot is { } yrot && !_vpTracker!.IsSafe(yrot))
                _pendingTaintedMispredicts.Add(oldestMispredId);
            else
                ArmMispredictSquash(oldestMispredId, oldestMispredRob!);
        }
    }

    /// <summary>
    ///     Arms an execute-time partial squash for a mispredicted branch, unless it is already the
    ///     ROB head (the commit-time path flushes it — an identical outcome, nothing older to
    ///     overlap) or an older in-flight halt/trap will redirect or stop the machine at commit
    ///     (making this branch definitively wrong-path; the commit-time model never counts such a
    ///     mispredict because the older halt/trap retires first). Called either directly from
    ///     <see cref="StepComplete" /> (the common case) or later from
    ///     <see cref="StepSttMispredictResolution" />, once a deferred tainted branch's own
    ///     visibility point finally clears.
    /// </summary>
    private void ArmMispredictSquash(ulong branchInstrId, RobEntry b) {
        if (_rob.Head.InstrId == branchInstrId || AnyOlderHaltOrTrap(branchInstrId)) return;
        ulong resolvedPc = b.ResolvedNextPc!.Value;
        _squashPending = true;
        _squashInstrId = branchInstrId;
        _squashTarget = resolvedPc;
        _squashTaken = resolvedPc != b.Pc + (ulong)(b.Instruction?.SizeBytes ?? 4);
    }

    /// <summary>
    ///     STT full DelayExecute+STT, explicit-branch slice (Yu et al., MICRO 2019, §6.4.1): re-checks
    ///     every branch whose squash was deferred by <see cref="StepComplete" /> because its own
    ///     resolution was still tainted. Arms the squash for the single oldest now-safe candidate (an
    ///     older squash, once it fires, discards every younger entry anyway — including any other
    ///     now-safe candidate still in this list, cleaned up by <see cref="StepPartialSquash" />/
    ///     <see cref="StepFlush" />). Skips entirely if a squash was already armed this same cycle
    ///     (by an ordinary untainted mispredict in <see cref="StepComplete" />) rather than risk
    ///     overwriting it incorrectly — deferred one extra cycle in that rare collision, which only
    ///     makes the defense more conservative, never less safe.
    /// </summary>
    private void StepSttMispredictResolution() {
        if (_pendingTaintedMispredicts.Count == 0) return;
        _sttMispredictDeferralsCounter?.Increment();

        if (_flushPending || _squashPending) return;

        ulong best = ulong.MaxValue;
        var bestIdx = -1;
        for (var i = 0; i < _pendingTaintedMispredicts.Count; i++) {
            ulong instrId = _pendingTaintedMispredicts[i];
            RobEntry b = FindRobByInstrId(instrId);
            if (b.SourceYrot is { } yrot && !_vpTracker!.IsSafe(yrot)) continue;
            if (instrId < best) {
                best = instrId;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) return;

        _pendingTaintedMispredicts.RemoveAt(bestIdx);
        ArmMispredictSquash(best, FindRobByInstrId(best));
    }

    /// <summary>
    ///     STT full DelayExecute+STT, prediction-based implicit-channel slice (Yu et al., MICRO 2019,
    ///     §6.4.2): re-checks every queued <see cref="SmbPredictor" /> training update whose producing
    ///     store was still tainted when an ordinary forwarded load tried to teach it a fresh SSN
    ///     distance. Unlike <see cref="_pendingTaintedMispredicts" />/<see cref="_pendingUsls" />, this
    ///     list is not tied to a live ROB entry that a later squash could invalidate — it only holds a
    ///     PC, a distance, and a taint-root value already captured by value — so no squash/flush
    ///     cleanup is needed: applying a queued training update once it's safe is correct regardless of
    ///     whether the load that originally observed it later turns out to be wrong-path (this
    ///     simulator's existing, non-STT training call already had that same property — a squashed
    ///     load's observed distance still trains the predictor today, matching how real hardware
    ///     routinely lets wrong-path speculation shape predictor state).
    /// </summary>
    private void StepSmbTrainingResolution() {
        if (_pendingSmbTraining.Count == 0) return;
        _sttMemDepTrainingDeferralsCounter?.Increment();

        for (int i = _pendingSmbTraining.Count - 1; i >= 0; i--) {
            (ulong pc, ulong distance, ulong storeYrot) = _pendingSmbTraining[i];
            if (!_vpTracker!.IsSafe(storeYrot)) continue;
            _smbPredictor!.Train(pc, distance);
            _pendingSmbTraining.RemoveAt(i);
        }
    }

    /// <summary>
    ///     InvisiSpec (Yan et al., MICRO 2018): fires the deferred real cache access for any queued
    ///     USL whose visibility point has cleared, clearing <see cref="RobEntry.PendingUslAccess" />
    ///     so Commit can retire it. Entries doomed by a squash detected earlier this same cycle are
    ///     left queued rather than resolved — firing a real access for a load about to be discarded
    ///     would pollute the cache with wrong-path data, defeating the entire mechanism; squash
    ///     cleanup (StepFlush/StepPartialSquash) removes them from the queue once it actually runs.
    ///     Single-core scope: the real access always simply completes (exposure or validation cost
    ///     the same — a full non-speculative memory access, matching the paper's own description of
    ///     validation as "more like exposure but with a comparison"); no compare-and-squash-on-mismatch
    ///     is implemented, since a same-core mismatch would already have been caught by the existing
    ///     memory-order-violation path (LqEntry.Violated) — a genuine cross-core coherence mismatch,
    ///     which validation exists to catch, cannot arise without multi-hart coherence-squash plumbing
    ///     this slice deliberately does not build (see TODO.md).
    ///     A miss on the real access does not freeze the shared clock: its latency is drained through
    ///     <see cref="_pendingUslLatency" />'s own per-entry countdown (see that field's docs), the same
    ///     MLP-overlapped shape ordinary load misses get from <see cref="_inFlight" /> in
    ///     <see cref="StepExecute" />, instead of the cache's lump-sum stall accumulator that store-commit
    ///     misses use. When the optional LLC-SB (§VI-C) is enabled and this line is still recorded from
    ///     an earlier speculative peek, the miss latency the real access would otherwise pay is capped
    ///     at the buffer-hit latency instead — see the field docs next to <c>_llcSb</c>.
    /// </summary>
    private void StepUslResolution() {
        // Drain previously-fired USLs' own miss-latency countdowns first (mirrors StepExecute's
        // drain-then-issue ordering for _inFlight) — a hit fired this same cycle clears its gate
        // below without ever passing through here.
        for (int i = _pendingUslLatency.Count - 1; i >= 0; i--) {
            (int robIdx, int countdown) = _pendingUslLatency[i];
            if (--countdown <= 0) {
                _rob.At(robIdx).PendingUslAccess = false;
                _pendingUslLatency.RemoveAt(i);
            }
            else { _pendingUslLatency[i] = (robIdx, countdown); }
        }

        for (int i = _pendingUsls.Count - 1; i >= 0; i--) {
            PendingUsl p = _pendingUsls[i];
            if (_flushPending || (_squashPending && p.InstrId > _squashInstrId)) continue;
            if (!_vpTracker!.IsSafe(p.InstrId)) continue;

            bool sbHit = false;
            if (_enableInvisiSpecLlcSb && LlcBlockBytes is { } bb) {
                ulong lineBase = p.Address & ~(ulong)(bb - 1);
                sbHit = _llcSb.Exists(e => e.LineBase == lineBase);
            }

            _ = DLayers.Accessor.Read(p.Address, p.Bytes);
            if (p.NeedsValidation) _invisispecValidationsCounter?.Increment();
            else _invisispecExposuresCounter?.Increment();
            _pendingUsls.RemoveAt(i);
            if (_enableInvisiSpecLlcSb) _llcSb.RemoveAll(e => e.InstrId == p.InstrId);

            long stalls = _anyCache ? DLayers.ConsumeAllStalls() : 0;
            if (sbHit) {
                _llcSbHitsCounter?.Increment();
                stalls = Math.Min(stalls, _llcSbHitLatency);
            }

            if (stalls > 0) _pendingUslLatency.Add((p.RobIdx, (int)stalls));
            else _rob.At(p.RobIdx).PendingUslAccess = false;
        }
    }

    /// <summary>
    ///     Retires <paramref name="head" /> from the ROB, scaling the retired-instruction
    ///     counter and <see cref="IArchState.OnRetire" /> (instret) by
    ///     <see cref="ITooth.ArchInstructionCount" /> — 1 for an ordinary instruction, 2 for a
    ///     macro-fused ROB entry — rather than assuming a 1:1 correspondence with ROB entries.
    ///     <see cref="TopDownBreakdown.SlotsRetiredCounter" /> instead increments by exactly one
    ///     per call: a fused ROB entry retires through one slot, same as it dispatched through
    ///     one, regardless of how many architectural instructions it represents.
    ///     Must read <c>head.Instruction</c> before <see cref="ReorderBuffer.Retire" />: the ROB
    ///     is a pooled ring buffer, and <c>Retire()</c> clears the slot (including
    ///     <c>Instruction</c>) for reuse before returning.
    /// </summary>
    private void FinishRetire(RobEntry head) {
        int archCount = head.Instruction?.ArchInstructionCount ?? 1;
        _rob.Retire();
        _retiredCounter.IncrementBy(archCount);
        _tdSlotsRetiredCounter.Increment();
        for (var i = 0; i < archCount; i++) State.OnRetire();
    }

    /// <summary>In-order retirement from the ROB head.</summary>
    private void StepCommit() {
        var committed = 0;
        var dcachePortUsed = false;
        while (_rob is { IsEmpty: false, Head: { IsComplete: true, PendingUslAccess: false, }, }
             && committed < _issueWidth) {
            RobEntry head = _rob.Head;

            // Futuristic model, condition (i): the RunCycle-level ForceResolve above only covers
            // whichever entry was the ROB head at the START of this tick — but this loop can retire
            // more than one entry per tick (up to _issueWidth), and every subsequent head examined
            // here only becomes head mid-tick, after this loop's own retirements advance it. Without
            // this second call, such an entry could retire (leaving the ROB) with a still-pending
            // bit that nothing will ever clear again — a permanent phantom blocker for anything
            // younger. Idempotent with the RunCycle-level call for the first iteration.
            _futuristicTracker?.ForceResolve(head.InstrId);

            switch (head) {
                case { IsHalt: true, }: {
                    CommitRegisters(head);
                    TrainCriticality(head);
                    RecordRetire(head);
                    if (head.IcacheMiss) PostIcachePendings();
                    RetireMemQueues(head);
                    FinishRetire(head);
                    _halted = true;
                    return;
                }
                case { HasTrap: true, Trap: not null, }: {
                    ulong target = _trapController.RaiseTrap(head.Trap, State);
                    // A trapping instruction's destination write must never reach
                    // architectural state (traps are precise), and its physical
                    // destination was never broadcast on the CDB (StepComplete skips
                    // that for trap results), so it can never become ready either.
                    // Roll back the Dispatch-time rename instead of committing it —
                    // this entry has already left the ROB by the time StepFlush's
                    // walk-back runs, so nothing else will undo it. Deferred until
                    // after that walk-back (see _pendingRollback* fields).
                    if (head is { PhysDestination: >= 0, ArchDestination: > 0, }) {
                        _pendingRollbackArch = head.ArchDestination;
                        _pendingRollbackPrevPhys = head.PrevPhysDestination;
                        _pendingRollbackAbandonedPhys = head.PhysDestination;
                    }

                    TrainCriticality(head);
                    RecordRetire(head);
                    if (head.IcacheMiss) PostIcachePendings();
                    RetireMemQueues(head);
                    FinishRetire(head);
                    SetFlush(target);
                    return;
                }
                case { IsReturnFromTrap: true, ReturnPrivilege: not null, }: {
                    ulong target = _trapController.ReturnFromTrap(head.ReturnPrivilege.Value, State);
                    CommitRegisters(head);
                    TrainCriticality(head);
                    RecordRetire(head);
                    if (head.IcacheMiss) PostIcachePendings();
                    RetireMemQueues(head);
                    FinishRetire(head);
                    SetFlush(target);
                    return;
                }
                case { RequestBlock: true, }: {
                    // A still-blocked syscall (e.g. futex(FUTEX_WAIT) that hasn't cleared):
                    // it never carried a register value onto the CDB (StepComplete's
                    // `!r.RegValue.HasValue` guard skips the PRF write for it, same as a
                    // trap), so it contributes no committed work — no CommitRegisters, no
                    // retired-count increment, no State.OnRetire(). Unlike Trap/Halt/
                    // ReturnFromTrap, this entry is NOT removed from the ROB here: it stays
                    // at the head so StepFlush's own walk-back (over the still-present ROB)
                    // un-renames it exactly like a load-violation's re-executed load, so no
                    // _pendingRollback* bookkeeping is needed either. Flushing to its own Pc
                    // re-fetches and re-executes it from scratch, mirroring
                    // MultiHartKernel's functional retry-in-place.
                    SetFlush(head.Pc);
                    return;
                }
            }

            // A load that executed speculatively may have read a stale value if a
            // conflicting store resolved after it. By the time the load reaches the
            // ROB head, all older instructions (including the store) have committed
            // and written memory, so re-executing the load from its own PC is safe.
            // Check LQ violation before the store-write below (matters for atomics).
            if (head is { IsLoad: true, LqIdx: >= 0, }) {
                LqEntry lq = _lq.At(head.LqIdx);
                if (lq.Violated) {
                    // STT (Yu et al., MICRO 2019, §6.4.2/6.5): a memory-order-violation squash is
                    // structurally commit-time only — CheckLoadViolations (called from StepComplete
                    // the instant the violating store's address resolves) only sets this flag; the
                    // squash itself fires here, at the ROB head, unconditionally, regardless of any
                    // STT flag. So unlike the branch-mispredict deferral above, there was never a
                    // flag-dependent immediate-vs-deferred choice to make here — this squash was
                    // already safe by construction. Assert the same underlying invariant anyway: if
                    // this load ever reaches the ROB head with a still-unsafe SourceYrot, the whole
                    // "ROB head implies already safe" foundation this feature leans on has broken.
                    Debug.Assert(
                        !(_enableSttExpOnly || _enableSttImplicitBranches || _enableSttMemDepGating)
                     || head.SourceYrot is not { } loadYrot || _vpTracker!.IsSafe(loadYrot),
                        "STT: a load reached the ROB head (memory-order violation) with an unsafe " +
                        "SourceYrot — retirement should be strictly later than any visibility point."
                    );
                    _memViolationsCounter.Increment();
                    _storeSets?.RecordViolation(lq.ViolatingStorePc, head.Pc);
                    SetFlush(head.Pc); // re-executes from the load's PC; flush clears the ROB+LQ+SQ
                    return;
                }

                // SMB (NoSQ) bypass verification failed during the load's shadow execution —
                // same recovery path as an ordinary violation, but retrains SmbPredictor with
                // the real producer distance found during verification (or decays confidence
                // if no in-flight store produced the value at all).
                if (lq.BypassMispredicted) {
                    _smbMispredictsCounter?.Increment();
                    if (lq.HasActualProducer && lq.SeqNo > lq.ActualProducerSeqNo)
                        _smbPredictor?.Train(head.Pc, lq.SeqNo - lq.ActualProducerSeqNo);
                    else
                        _smbPredictor?.TrainNoBypass(head.Pc);
                    SetFlush(head.Pc);
                    return;
                }

                // A bypass that reached commit unmispredicted was correct — reinforce.
                if (lq.SpeculativelyCompleted) _smbPredictor?.Train(head.Pc, lq.SeqNo - lq.PredictedProducerSeqNo);
            }

            // Value prediction: train on every eligible instruction's real committed value,
            // whether or not it was predicted (Perais & Seznec, HPCA 2014 — predictors must be
            // trained even when unused). A misprediction is the only trigger for a full pipeline
            // squash from value prediction — recovery is always a full re-fetch from this PC,
            // never a partial squash, matching the paper's squash-at-commit recovery model.
            // EOLE Late Execution (Perais & Seznec, ISCA 2014): this entry skipped
            // Issue/Execute entirely — its "execution" happens right here, in-order, as pure
            // verification of the value already speculatively written to the PRF at rename.
            // Mutually exclusive with the IsVpEligible branch below: an LE entry never
            // traverses StepComplete, so ValuePredMispredicted (which that branch relies on
            // to decide whether to squash) was never set for it — verification has to happen
            // here instead, not just be read out of a flag.
            if (head.IsLateExecEligible) {
                ulong src1 = head.P1 >= 0 ? _prf.Read(head.P1) : 0;
                ulong src2 = head.P2 >= 0 ? _prf.Read(head.P2) : 0;
                var issued = new IssuedInstr(
                    0, head.PhysDestination, head.Instruction!, head.Pc, src1, src2, 0, head.InstrId
                );
                ExecResult er = ExecuteOne(issued, 0);
                _eoleLateExecCounter?.Increment();
                head.SideEffect = er.SideEffect;
                ulong actual = er.RegValue.Value;
                if (_prf.Read(head.PhysDestination) == actual) {
                    _vpCorrectCounter?.Increment();
                    _valuePredictor?.Update(head.Pc, head.VpHistCheckpoint, actual);
                }
                else {
                    _prf.Write(head.PhysDestination, actual); // self-heal before squash/refetch
                    _vpMispredictsCounter?.Increment();
                    _valuePredictor?.Update(head.Pc, head.VpHistCheckpoint, actual);
                    // STT: value-prediction training/squash was never flag-dependently deferred (unlike
                    // the branch-mispredict/memdep-training slices) — Update and the squash below are
                    // both unconditionally commit-time by construction, same as the memory-order-violation
                    // squash. Assert the same underlying invariant anyway (see that site's own comment).
                    Debug.Assert(
                        !(_enableSttExpOnly || _enableSttImplicitBranches || _enableSttMemDepGating)
                     || head.SourceYrot is not { } leYrot || _vpTracker!.IsSafe(leYrot),
                        "STT: an EOLE Late-Execution entry reached the ROB head (value-prediction " +
                        "squash) with an unsafe SourceYrot."
                    );
                    SetFlush(head.Pc);
                    return;
                }
            }
            else if (head.IsVpEligible) {
                _valuePredictor?.Update(head.Pc, head.VpHistCheckpoint, _prf.Read(head.PhysDestination));
                if (head.ValuePredMispredicted) {
                    _vpMispredictsCounter?.Increment();
                    Debug.Assert(
                        !(_enableSttExpOnly || _enableSttImplicitBranches || _enableSttMemDepGating)
                     || head.SourceYrot is not { } vpYrot || _vpTracker!.IsSafe(vpYrot),
                        "STT: a value-predicted instruction reached the ROB head (value-prediction " +
                        "squash) with an unsafe SourceYrot."
                    );
                    SetFlush(head.Pc);
                    return;
                }
            }

            // Write deferred store/atomic data to memory at commit time.
            // Real hardware has one D-cache write port: break if already used this cycle.
            if (head.SqIdx >= 0) {
                SqEntry sq = _sq.At(head.SqIdx);
                if (sq.AddressKnown) {
                    Debug.Assert(
                        sq.DataKnown,
                        "A store reached commit with its address known but data still unknown — " +
                        "retirement is gated on RobEntry.IsComplete, which only follows the store's " +
                        "own full Execute (the same event that sets DataKnown), so this should be " +
                        "unreachable regardless of early address resolution."
                    );
                    if (dcachePortUsed) break;
                    CommitStore(sq.Address, sq.Value, sq.Width);
                    dcachePortUsed = true;
                }
            }

            CommitRegisters(head);

            // Co-sim notification. Reaching here guarantees a real commit: halt,
            // trap, return-from-trap, and load-violation cases all returned above.
            // Fires for both the normal and branch-mispredict retire paths below.
            if (head.Instruction is not null) {
                _commitObserver?.OnCommit(head.Pc, head.Instruction.RawEncoding, State);
                Rdip?.OnCommit(head.Pc, head.Instruction.RawEncoding);

                // A fused entry (see IMacroFuser) commits two real architectural instructions
                // through one ROB slot — head.Pc/RawEncoding describe only the compare half.
                // Without this, a co-sim trace replay sees one commit where two really
                // happened, a divergence source if fusion and co-sim are both enabled.
                if (head.FusedSecondInstrId is not null) {
                    ITooth branch = head.Instruction.BranchComponent;
                    _commitObserver?.OnCommit(branch.Pc, branch.RawEncoding, State);
                    Rdip?.OnCommit(branch.Pc, branch.RawEncoding);
                }
            }

            // Advance the architectural RAS shadow for a retiring call/return. Only jumps
            // (ToothClass.Branch) touch the RAS; the shadow sees the correct committed path
            // only, so it never suffers the wrong-path corruption the speculative _ras can.
            // Order (push-then-pop) mirrors the fetch path for the rare call+return jalr.
            if (head.Instruction is { Class: ToothClass.Branch, }) {
                FetchHint hint = _decoder.GetFetchHint(head.Pc, head.Instruction.RawEncoding);
                if (hint.IsCall) _committedRas.Push(head.Pc + (ulong)head.Instruction.SizeBytes);
                if (hint.IsReturn) _committedRas.TryPop(out _);
            }

            // First-class HTIF tohost exit: the store flagged a post-commit halt.
            // It has committed (memory write + OnCommit) above; advance PC, retire
            // it, and halt.
            if (head.RequestHalt) {
                State.Pc = head.PredictedNextPc;
                TrainCriticality(head);
                RecordRetire(head);
                if (head.IcacheMiss) PostIcachePendings();
                RetireMemQueues(head);
                FinishRetire(head);
                _halted = true;
                return;
            }

            // Backstop: halt on an unconditional jump-to-self (resolved target ==
            // own PC), the bare-metal terminator. Gated on Branch (jal/jalr) so a
            // conditional spin-wait is not mistaken for a halt. Mirrors the
            // single-cycle and five-stage trains so the OoO core also stops at the
            // terminator instead of spinning to maxTicks.
            if (head.Instruction?.Class == ToothClass.Branch
             && head.ResolvedNextPc is { HasValue: true, Value: var selfPc, } && selfPc == head.Pc) {
                State.Pc = selfPc;
                TrainCriticality(head);
                RecordRetire(head);
                if (head.IcacheMiss) PostIcachePendings();
                RetireMemQueues(head);
                FinishRetire(head);
                _halted = true;
                return;
            }

            if (head.ResolvedNextPc.HasValue) {
                // STT explicit-branch protection relies on retirement being strictly later than any
                // visibility point: a branch reaching the ROB head must already have a safe SourceYrot,
                // which is exactly why the commit-time mispredict path below never needs its own taint
                // check (only StepComplete's earlier, execute-time squash does). If this ever fires,
                // that invariant — not just this slice's security property — has broken.
                Debug.Assert(
                    !_enableSttImplicitBranches || head.SourceYrot is not { } headYrot || _vpTracker!.IsSafe(headYrot),
                    "STT: branch reached the ROB head with an unsafe SourceYrot — retirement should be " +
                    "strictly later than any visibility point."
                );

                // Capture all fields from head before Retire() clears the slot.
                ulong resolvedPc = head.ResolvedNextPc.Value;
                ulong instrPc = head.Pc;
                ulong predictedPc = head.PredictedNextPc;
                int instrSize = head.Instruction?.SizeBytes ?? 4;

                bool taken = resolvedPc != instrPc + (ulong)instrSize;
                // BranchComponent, not head.Instruction/instrPc directly: for a macro-fused
                // entry (see IMacroFuser), head.Pc/head.Instruction describe the *compare*
                // half, not the branch — but the BTB/predictor tables were trained against
                // the branch's own real address at Fetch time, so training must key off that
                // same address or it silently misses the table entirely.
                ulong branchPc = head.Instruction?.BranchComponent.Pc ?? instrPc;
                if (_predictor is IBranchKindAwareBranchPredictor kindAware)
                    kindAware.NotifyBranchKind(branchPc, ClassifyBranchKind(head.Instruction?.BranchComponent));
                _predictor.Update(branchPc, taken, resolvedPc);
                _valuePredictor?.AdvanceCommittedHistory(taken);

                if (resolvedPc != predictedPc) {
                    _branchMissCounter.Increment();
                    PostBpredWindow(head);
                    State.Pc = resolvedPc;
                    // Critical-path prediction ED edge (Table 2): the very next dispatched
                    // instruction's D-source is this mispredicting branch's E-node.
                    if (_criticalityPredictor is not null) {
                        _pendingRedirect = true;
                        _pendingRedirectInstrId = head.InstrId;
                    }

                    TrainCriticality(head);
                    RecordRetire(head);
                    if (head.IcacheMiss) PostIcachePendings();
                    RetireMemQueues(head);
                    FinishRetire(head);
                    SetFlush(resolvedPc);
                    return;
                }
            }

            State.Pc = head.PredictedNextPc;
            TrainCriticality(head);
            RecordRetire(head);
            if (head.IcacheMiss) PostIcachePendings();
            RetireMemQueues(head);
            FinishRetire(head);
            committed++;
        }

        // Check for pending interrupts when the commit loop drains normally.
        if (!_halted && !_flushPending) {
            TrapInfo? interrupt = _trapController.PeekInterrupt(State);
            if (interrupt is not null) SetFlush(_trapController.RaiseTrap(interrupt, State));
        }
    }

    /// <summary>
    ///     Resolves <paramref name="head" />'s three dependence-graph source edges (Table 2 of
    ///     Fields, Rubin &amp; Bodík, ISCA 2001) from state accumulated at Dispatch/Issue/Execute,
    ///     and trains the critical-path predictor. Called for every instruction that reaches a
    ///     genuine commit (i.e., immediately before each <c>_rob.Retire()</c> in StepCommit) —
    ///     not for the load-violation path, which re-executes rather than commits.
    /// </summary>
    private void TrainCriticality(RobEntry head) {
        if (_criticalityPredictor is null) return;

        var w = (ulong)_rob.Capacity;

        CpNode dNode;
        ulong dSource;
        if (head.DGatedByRedirect) {
            dNode = CpNode.E;
            dSource = head.DRedirectSourceInstrId;
        }
        else if (head.DGatedByStall) {
            dNode = CpNode.C;
            dSource = head.InstrId >= w ? head.InstrId - w : 0;
        }
        else {
            dNode = CpNode.D;
            dSource = head.InstrId - 1;
        }

        CpNode eNode = head.ESourceIsOwnD ? CpNode.D : CpNode.E;
        ulong eSource = head.ESourceIsOwnD ? head.InstrId : head.ESourceProducerInstrId;

        bool cSourceIsOwnE = head.CompletedTick == (ulong)Escapement.CurrentTick;
        CpNode cNode = cSourceIsOwnE ? CpNode.E : CpNode.C;
        ulong cSource = cSourceIsOwnE ? head.InstrId : head.InstrId - 1;

        _criticalityPredictor.OnCommit(
            new CriticalityCommitInfo {
                InstrId = head.InstrId,
                Pc = head.Pc,
                DSourceNode = dNode,
                DSourceInstrId = dSource,
                ESourceNode = eNode,
                ESourceInstrId = eSource,
                CSourceNode = cNode,
                CSourceInstrId = cSource,
            }
        );

        // A fused entry (see IMacroFuser) consumed two InstrIds at Fetch but committed
        // through only this one ROB entry — the branch's own InstrId is otherwise never
        // trained, leaving a gap that a later entry's `head.InstrId - 1`/`head.InstrId - w`
        // D/C-source arithmetic would silently resolve against stale, unrelated token-table
        // state from the InstrId's last real occupant. Training it with the same D/E/C
        // sources as the primary is exact, not approximate: the fused pair shares one
        // Dispatch/Issue/Execute/Commit timing throughout — there never were two separate
        // instants to model.
        if (head.FusedSecondInstrId is { } secondId) {
            _criticalityPredictor.OnCommit(
                new CriticalityCommitInfo {
                    InstrId = secondId,
                    Pc = head.Instruction?.BranchComponent.Pc ?? head.Pc,
                    DSourceNode = dNode,
                    DSourceInstrId = dSource,
                    ESourceNode = eNode,
                    ESourceInstrId = eSource,
                    CSourceNode = cNode,
                    CSourceInstrId = cSource,
                }
            );
        }
    }

    /// <summary>
    ///     Records a Retire event for <paramref name="head" />, and — when it is a macro-fused
    ///     compare+branch pair (see <see cref="IMacroFuser" />) — a matching Retire event for the
    ///     branch's own InstrId (<see cref="RobEntry.FusedSecondInstrId" />), which otherwise gets
    ///     a Fetch event and then nothing: a dangling row in the waterfall visualization.
    /// </summary>
    private void RecordRetire(RobEntry head) {
        PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
        if (head.FusedSecondInstrId is { } secondId)
            PEventLog?.Record(
                secondId, head.Instruction?.BranchComponent.Pc ?? head.Pc, _cyclesCounter.Value, PEventKind.Retire
            );
    }

    /// <summary>Execute instructions issued last tick, filling the CDB buffer.</summary>
    private void StepExecute() {
        // Drain multi-cycle in-flight executes (started in previous ticks).
        // Countdown is decremented; entries reaching zero broadcast on the CDB.
        for (int i = _inFlight.Count - 1; i >= 0; i--) {
            (int countdown, ExecResult result, bool holdsMshr) = _inFlight[i];
            if (--countdown <= 0) {
                _cdbBuffer.Add(result);
                _inFlight.RemoveAt(i);
                if (holdsMshr) _mshrUsed--;
            }
            else { _inFlight[i] = (countdown, result, holdsMshr); }
        }

        // Stalls already pending here are store-commit write misses (StepCommit ran
        // earlier this cycle). Stores are off the load critical path, so charge them
        // lump-sum — and clear the accumulator so each load below sees only its own
        // miss penalty. TMA: frozen store-commit cycles are execution stalls pending
        // on stores (Backend Memory Bound).
        if (_anyCache) {
            long storeStalls = DLayers.ConsumeAllStalls();
            if (storeStalls > 0) {
                ChargeStallCycles(storeStalls);
                _tdExecStallCyclesCounter.IncrementBy(storeStalls);
                _tdMemStallStoreCyclesCounter.IncrementBy(storeStalls);
                _cpiStoreCounter.IncrementBy(storeStalls);
                _cpiStolenCycles += storeStalls;
            }
        }

        // Start executing newly issued instructions.
        int executing = _execBuffer.Count;
        Span<ulong> prefBuf = stackalloc ulong[32];
        foreach (IssuedInstr issued in _execBuffer) {
            // Look up the LQ SeqNo before calling ExecuteOne so TryForwardFromStore
            // can use it for SQ ordering without any ROB index arithmetic.
            RobEntry issuedRob = _rob.At(issued.RobIdx);
            ulong lqSeqNo = issuedRob.LqIdx >= 0 ? _lq.At(issuedRob.LqIdx).SeqNo : 0;

            // CPI stack: snapshot D-side miss counts around a load/atomic's execution so the
            // access can be classified by the deepest level it missed (ASPLOS 2006 short L1
            // vs long L2/TLB backend misses). Consulted when this entry later blocks the
            // head of a full ROB.
            bool classifyDMiss = _anyCache && issued.Instr.Class is ToothClass.Load or ToothClass.Atomic;
            long dm1 = 0, dm2 = 0, dm3 = 0, dmt = 0;
            if (classifyDMiss) {
                dm1 = DLayers.Cache?.Misses ?? 0;
                dm2 = (DLayers.L2Cache?.Misses ?? 0) + (DLayers.L2Bdi?.Misses ?? 0) + (DLayers.L2Ceaser?.Misses ?? 0) +
                      (DLayers.L2Scatter?.Misses ?? 0);
                dm3 = (DLayers.L3Cache?.Misses ?? 0) + (DLayers.L3Bdi?.Misses ?? 0) + (DLayers.L3Ceaser?.Misses ?? 0) +
                      (DLayers.L3Scatter?.Misses ?? 0);
                dmt = DLayers.Tlb?.Misses ?? 0;
            }

            ExecResult result = ExecuteOne(issued, lqSeqNo);

            if (classifyDMiss)
                issuedRob.DMissClass =
                    DLayers.Tlb is { } dTlb && dTlb.Misses > dmt ? CpiMissClass.DTlb :
                    (DLayers.L3Cache is { } dl3 && dl3.Misses > dm3)
                    || (DLayers.L3Bdi is { } dl3b && dl3b.Misses > dm3)
                    || (DLayers.L3Ceaser is { } dl3c && dl3c.Misses > dm3)
                    || (DLayers.L3Scatter is { } dl3s && dl3s.Misses > dm3) ? CpiMissClass.L3D :
                    (DLayers.L2Cache is { } dl2 && dl2.Misses > dm2)
                    || (DLayers.L2Bdi is { } dl2b && dl2b.Misses > dm2)
                    || (DLayers.L2Ceaser is { } dl2c && dl2c.Misses > dm2)
                    || (DLayers.L2Scatter is { } dl2s && dl2s.Misses > dm2) ? CpiMissClass.L2D :
                    DLayers.Cache is { } dl1 && dl1.Misses > dm1 ? CpiMissClass.L1D :
                    CpiMissClass.None;

            PEventLog?.Record(issued.InstrId, issued.Pc, _cyclesCounter.Value, PEventKind.Execute);

            // Register load disambiguation state at EXECUTE time (not at CDB broadcast).
            // Under memory-level parallelism a missed load sits in _inFlight for many
            // cycles before it broadcasts; registering here keeps it visible to
            // CheckLoadViolations for its whole in-flight life, so an older store that
            // resolves meanwhile marks it violated → re-execution at the ROB head. (Doing
            // this only at broadcast time left the load invisible during the miss window,
            // letting an older store resolve+commit unseen and the load return stale data.)
            // The load still cannot commit early: commit requires IsComplete, set only by
            // the CDB broadcast.
            if (result.HasLoadAccess) {
                LqEntry lq = _lq.At(issuedRob.LqIdx);
                lq.Executed = true;
                lq.Address = result.LoadAddr;
                lq.Bytes = result.LoadBytes;
                // If not forwarded from an already-resolved store, check whether an older
                // store already has a known overlapping address (the store resolved before
                // this load executed; the converse ordering is caught by CheckLoadViolations
                // when the store later resolves).
                if (!result.LoadWasForwarded) {
                    ulong? conflictPc = HasOlderConflictingStore(lq.SeqNo, result.LoadAddr, result.LoadBytes);
                    if (conflictPc.HasValue) {
                        lq.Violated = true;
                        lq.ViolatingStorePc = conflictPc.Value;
                    }
                }
            }

            // D-cache prefetch: fire before draining stalls so the prefetch sees the cache
            // state left by this access. Fires only for demand loads (not store-forwarded).
            // The predictor is always trained; the fill itself is dropped when every MSHR
            // slot is busy (prefetches share the miss-tracking slots with demand loads).
            if (result is { HasLoadAccess: true, LoadWasForwarded: false, }) {
                // Vector Runahead (Naithani et al., ISCA 2021): train the shadow lane's own
                // stride table from the real demand-load stream, independent of whether a
                // cache prefetcher is configured.
                if (_enableVectorRunahead) UpdateVrStrideTable(issued.Pc, result.LoadAddr);

                if (DLayers.Prefetcher is not null) {
                    bool wasHit = DLayers.Cache?.LastAccessWasHit ?? true;
                    int prefCount = DLayers.Prefetcher.OnAccess(issued.Pc, result.LoadAddr, wasHit, prefBuf);
                    for (var k = 0; k < prefCount; k++) {
                        if (_mshrCapacity > 0 && _mshrUsed + InFlightPrefetches >= _mshrCapacity) break;
                        DLayers.TryPrefetch(prefBuf[k]);
                    }
                }
            }

            int fuLatency = result.LatencyOverride > 0
                ? result.LatencyOverride
                : _fuConfig.LatencyFor(issued.Instr);
            if (issued.Instr.Class == ToothClass.Load) {
                int cacheHit = DLayers.Cache?.HitLatency ?? 0;
                if (cacheHit > 0) fuLatency = cacheHit;
            }

            int countdown = fuLatency - 1 + _fuConfig.BypassLatency;

            // Memory-level parallelism: a load/atomic that missed (its cache access just
            // accrued a stall) carries the miss penalty in its own latency countdown, so it
            // overlaps with other in-flight work instead of freezing the clock. At most one
            // load issues per cycle (the LoadStore port), so the drained stall is this op's.
            var holdsMshr = false;
            if (_anyCache && issued.Instr.Class is ToothClass.Load or ToothClass.Atomic) {
                long stalls = DLayers.ConsumeAllStalls();
                if (stalls > 0) {
                    countdown += (int)stalls;
                    holdsMshr = true;
                    _mshrUsed++;
                }
            }

            if (countdown <= 0)
                _cdbBuffer.Add(result);
            else
                _inFlight.Add((countdown, result, holdsMshr));
        }

        _execBuffer.Clear();

        // TMA ExecutionStalls / MemStalls.AnyLoad (Table 1) classification is finalized at
        // the end of StepDispatch (later this same tick), which folds in any EOLE Late/Early
        // Execution bypasses — see the comment there.
        _execCountThisTick = executing;
    }

    /// <summary>True when any in-flight FU countdown belongs to a load/atomic.</summary>
    private bool AnyInFlightLoad() {
        foreach ((_, ExecResult result, _) in _inFlight)
            if (_rob.At(result.RobIdx).IsLoad)
                return true;

        return false;
    }

    /// <summary>
    ///     For every in-flight store whose address operand (rs1) is ready but whose SQ entry
    ///     doesn't yet have an address — regardless of whether its data operand (rs2) is also
    ///     ready — resolve and record its effective address early. This never touches
    ///     <see cref="RobEntry.IsComplete" />, the PRF, or the CDB: it only annotates
    ///     <see cref="SqEntry.Address" />/<see cref="SqEntry.AddressKnown" />, a side channel
    ///     consumed solely by address-only aliasing/forwarding-candidate checks. The store's own
    ///     real Issue/Execute (needing both operands, exactly as before this existed) still
    ///     drives retirement and re-sets Address redundantly alongside Value/Width/DataKnown —
    ///     harmless, since rs1's value can't change between here and there (its producer has
    ///     already retired/broadcast by definition of being "ready").
    /// </summary>
    private void StepEarlyStoreAddressResolution() {
        IRegisterFile regs = State.IntegerRegisters;
        for (var iqIdx = 0; iqIdx < _activeIqCount; iqIdx++) {
            IssueQueue iq = _iqs[iqIdx];
            for (var slot = 0; slot < iq.Capacity; slot++) {
                RsEntry rs = iq.At(slot);
                if (!rs.Busy || rs.Instruction?.Class != ToothClass.Store || !rs.Src1Ready) continue;

                RobEntry rob = _rob.At(rs.RobIndex);
                if (rob.SqIdx < 0) continue;
                SqEntry sq = _sq.At(rob.SqIdx);
                if (sq.AddressKnown) continue;

                IReadOnlyList<int> srcs = rs.Instruction.SourceRegisters;
                if (srcs.Count == 0) continue;
                int archRs1 = srcs[0];

                // Save/inject/restore, same technique ExecuteOne uses: the executor reads
                // operands through IArchState.IntegerRegisters by architectural index, so the
                // renamed physical register's captured value must be visible there temporarily.
                ulong saved = archRs1 >= 0 ? regs.Read(archRs1) : 0;
                if (archRs1 >= 0) regs.Write(archRs1, rs.Src1Value);
                ulong? addr = _executor.TryComputeStoreAddress(rs.Instruction, State, DLayers.Accessor);
                if (archRs1 >= 0) regs.Write(archRs1, saved);

                if (addr.HasValue) {
                    sq.AddressKnown = true;
                    sq.Address = addr.Value;
                }
            }
        }
    }

    /// <summary>Select up to issueWidth ready IQ entries and forward to execute.</summary>
    private void StepIssue() {
        // Per-class port counters: limits how many instructions of each functional-unit
        // class can be issued in a single cycle.
        Span<int> classIssued = stackalloc int[16]; // one slot per ToothClass value; sized for current + future growth
        var issued = 0;

        // Critical-path prediction (Fields, Rubin & Bodík, ISCA 2001): a first pass gives
        // predicted-critical instructions priority for scarce FU/port slots. All existing
        // gating logic (below, in TryIssueSlot) is untouched and shared by both passes —
        // gating state (ROB head, MSHR count, SQ addresses) never changes mid-StepIssue, so
        // an entry that fails pass 1 would fail identically if retried in pass 2, and is
        // skipped there rather than re-evaluated. When disabled, only the second pass runs,
        // which is exactly the original single-pass behavior.
        if (_criticalityPredictor is { } cp)
            for (var iqIdx = 0; iqIdx < _activeIqCount && issued < _issueWidth; iqIdx++) {
                IssueQueue iq = _iqs[iqIdx];
                for (var slot = 0; slot < iq.Capacity && issued < _issueWidth; slot++) {
                    RsEntry rs = iq.At(slot);
                    if (!rs.Busy || !rs.IsReady || !cp.PredictCritical(rs.Pc)) continue;
                    if (TryIssueSlot(iq, slot, classIssued)) issued++;
                }
            }

        for (var iqIdx = 0; iqIdx < _activeIqCount && issued < _issueWidth; iqIdx++) {
            IssueQueue iq = _iqs[iqIdx];
            for (var slot = 0; slot < iq.Capacity && issued < _issueWidth; slot++) {
                RsEntry rs = iq.At(slot);
                if (!rs.Busy || !rs.IsReady) continue;
                if (_criticalityPredictor?.PredictCritical(rs.Pc) == true) continue; // tried in pass 1
                if (TryIssueSlot(iq, slot, classIssued)) issued++;
            }
        }
    }

    /// <summary>
    ///     Attempts to issue the instruction in <paramref name="iq" />'s <paramref name="slot" />,
    ///     applying every functional-unit/ordering gate. Returns false (leaving the entry busy,
    ///     to be retried next cycle) without side effects if any gate blocks it.
    /// </summary>
    private bool TryIssueSlot(IssueQueue iq, int slot, Span<int> classIssued) {
        RsEntry rs = iq.At(slot);

        ToothClass cls = rs.Instruction?.Class ?? ToothClass.IntegerAlu;
        int fuSlot = FuLatencyConfig.BudgetSlot(cls);
        if (classIssued[fuSlot] >= _fuConfig.CountFor(cls)) return false;

        switch (cls) {
            // Loads issue speculatively; only block on preceding vector stores or
            // arbitrary-memory ops like ECALL (which write eagerly at execute time, not at
            // commit — see HasPrecedingVectorStore). Scalar store-to-load ordering is
            // maintained through forwarding and, when necessary, memory-order violation
            // detection and squash at the ROB head.
            case ToothClass.Load when HasPrecedingVectorStore(rs.RobIndex):
            // TSO fence: a load may not issue while an older store→load fence is
            // still in the ROB — the fence itself only issues (and then retires)
            // once the write buffer has drained, so this gate delays post-fence
            // loads until every pre-fence store's write-bus penalty has expired.
            case ToothClass.Load when HasPrecedingStoreLoadFence(rs.RobIndex):
                return false;
            // STT-ExpOnly (Yu et al., MICRO 2019): a load is a transmitter — its address may not
            // be used to access memory (and thus reveal, via cache-state/timing, whatever secret
            // it's derived from) while it still carries a taint root that hasn't reached its
            // visibility point. Atomics are exempt: they already issue only at the ROB head (see
            // below), by which point every older branch has necessarily resolved.
            case ToothClass.Load when _enableSttExpOnly
                                   && _rob.At(rs.RobIndex).SourceYrot is { } yrot && !_vpTracker!.IsSafe(yrot):
                _sttLoadIssueStallsCounter?.Increment();
                return false;
            // Conservative load ordering (FuLatencyConfig.ConservativeLoads): a load
            // may not issue while any older SQ entry still has an unresolved address.
            // Models Olympia's allow_speculative_load_exec = false.
            case ToothClass.Load when _fuConfig.ConservativeLoads: {
                int lqIdx = _rob.At(rs.RobIndex).LqIdx;
                if (lqIdx >= 0 && HasUnresolvedPrecedingStore(_lq.At(lqIdx).SeqNo)) return false;
                break;
            }
            case ToothClass.Load when _storeSets is not null: {
                int lqIdx = _rob.At(rs.RobIndex).LqIdx;
                if (lqIdx >= 0) {
                    LqEntry lq = _lq.At(lqIdx);
                    if (StoreSetStallLoad(lq.SeqNo, lq.PredStoreSeqNo)) return false;
                }

                break;
            }
        }

        switch (cls) {
            // MSHR capacity: if all miss-tracking slots are occupied (by demand loads
            // or in-flight prefetches), this load/atomic cannot start yet — it stays
            // in the IQ and retries next cycle.
            case ToothClass.Load or ToothClass.Atomic
                when _mshrCapacity > 0 && _mshrUsed + InFlightPrefetches >= _mshrCapacity:
                _mshrStallsCounter?.Increment();
                return false;
            // CSR serialization: a System instruction may only issue when it is
            // at the ROB head (all older instructions have committed). This prevents
            // out-of-order CSR reads from seeing stale state written by earlier CSR ops.
            case ToothClass.System when rs.RobIndex != _rob.HeadIndex:
            // Vector serialization: vector register renaming is not implemented.
            // Head-gating ensures VRF writes are applied in program order.
            case ToothClass.Vector when rs.RobIndex != _rob.HeadIndex:
            // Secondary-destination serialization (e.g., RV32 amocas.d's register
            // pair): the high half is delivered through SideEffect straight into
            // architectural state, not through the PRF, so it is never renamed.
            // Head-gating guarantees State.IntegerRegisters already reflects every
            // older instruction's commit by the time this one executes, which is
            // the only thing that makes its direct regs.Read() of the pair correct.
            case ToothClass.Atomic when rs.Instruction?.SecondaryDestinationRegister >= 0
                                     && rs.RobIndex != _rob.HeadIndex:
            // SC.W serialization: the reservation check (TryConsume) must see a
            // coherent view of the ReservationTable — all older intra-hart stores
            // must have committed (so their MoesifCache writes, which cancel cross-hart
            // reservations via BusReadInvalidate, have already fired).  Head-gating
            // guarantees this without needing a commit-time re-check.
            case ToothClass.Atomic when rs.Instruction?.IsStoreConditional == true
                                     && rs.RobIndex != _rob.HeadIndex:
            // TSO fence serialization: a store→load fence issues only at the ROB
            // head (all older stores committed) and once the write buffer has fully
            // drained, so every pre-fence store's write-bus penalty has expired
            // before the fence completes and post-fence loads unblock. Fences
            // without W→R ordering are timing no-ops and issue unrestricted.
            case ToothClass.Fence when rs.Instruction?.IsStoreLoadFence == true
                                    && (rs.RobIndex != _rob.HeadIndex || _wbOccupied > 0):
                return false;
            // UVE serialization: stream state is not renamed; head-gating preserves order.
            // Additionally stall until every load-stream source has a buffered element.
            case ToothClass.Uve: {
                if (rs.RobIndex != _rob.HeadIndex) return false;
                var streamStall = false;
                if (rs.Instruction is not null)
                    if (rs.Instruction.UveStreamSources.Any(uid => uid >= 0 && uid < StreamingEngine.MaxStreams
                                                                            && StreamingEngine.IsActive(uid)
                                                                            && !StreamingEngine.HasElement(uid)
                        ))
                        streamStall = true;

                if (streamStall) return false;
                break;
            }
        }

        // Critical-path prediction EE edge (Table 2): now that this instruction has actually
        // issued, stage the last-arriving producer captured by IssueQueue.Broadcast — a no-op
        // ("own D") entry never had PendingSourceCount > 0, so LastArrivingProducerInstrId was
        // never written for it.
        if (_criticalityPredictor is not null) {
            RobEntry rob = _rob.At(rs.RobIndex);
            if (!rob.ESourceIsOwnD) rob.ESourceProducerInstrId = rs.LastArrivingProducerInstrId;
        }

        ulong issuedInstrId = _rob.At(rs.RobIndex).InstrId;
        _execBuffer.Add(
            new IssuedInstr(
                rs.RobIndex, rs.PhysDestination, rs.Instruction!, rs.Pc,
                rs.Src1Value, rs.Src2Value, rs.Src3Value, issuedInstrId
            )
        );
        PEventLog?.Record(issuedInstrId, rs.Pc, _cyclesCounter.Value, PEventKind.Issue);
        if (PEventLog is not null && rs.Instruction is { } issuedInstr) {
            IReadOnlyList<int> srcRegs = issuedInstr.SourceRegisters;
            if (srcRegs.Count > 0) {
                var srcVals = new ulong[srcRegs.Count];
                if (srcRegs.Count > 0) srcVals[0] = rs.Src1Value;
                if (srcRegs.Count > 1) srcVals[1] = rs.Src2Value;
                if (srcRegs.Count > 2) srcVals[2] = rs.Src3Value;
                PEventLog.RecordSourceValues(issuedInstrId, srcRegs, srcVals);
            }
        }

        iq.Free(slot);
        classIssued[fuSlot]++;
        return true;
    }

    // Classifies a resolved branch for IBranchKindAwareBranchPredictor. Mirrors the
    // committed-RAS classification just above (line ~714), re-deriving the FetchHint at
    // retire rather than threading it through the ROB entry.
    private BranchKind ClassifyBranchKind(ITooth? instruction) {
        if (instruction is null) return BranchKind.None;
        var kind = BranchKind.None;
        if (instruction.Class == ToothClass.ConditionalBranch) kind |= BranchKind.Conditional;
        FetchHint hint = _decoder.GetFetchHint(instruction.Pc, instruction.RawEncoding);
        if (hint.IsCall) kind |= BranchKind.Call;
        if (hint.IsReturn) kind |= BranchKind.Return;
        if (!hint.BranchTarget.HasValue) kind |= BranchKind.Indirect;
        return kind;
    }

    /// <summary>
    ///     True if any not-yet-committed instruction (already dispatched to the ROB, or
    ///     still sitting in the rename queue) has a secondary destination register that
    ///     <paramref name="srcs" /> reads (RAW) or that equals <paramref name="destArch" />
    ///     (WAW). See StepRename's dispatch stall for why this must block renaming
    ///     rather than just issue.
    ///     <para>
    ///         The WAW case matters because the secondary-dest write never goes through the
    ///         RAT: CommitRegisters syncs the PRF slot the RAT *currently* maps for that
    ///         architectural register. If a younger instruction were allowed to rename a new
    ///         physical register for it first, that sync would target the wrong slot (or race
    ///         the younger producer's own completion). Blocking here keeps the RAT mapping for
    ///         that register stable until the secondary-dest producer retires and syncs it.
    ///     </para>
    /// </summary>
    private bool HasPendingSecondaryDest(IReadOnlyList<int> srcs, int destArch) {
        foreach ((_, RobEntry entry) in _rob.InOrder()) {
            int sd = entry.Instruction?.SecondaryDestinationRegister ?? -1;
            if (sd >= 0 && (srcs.Contains(sd) || sd == destArch)) return true;
        }

        foreach (RenameEntry ri in _renameQueue) {
            int sd = ri.Decoded?.SecondaryDestinationRegister ?? -1;
            if (sd >= 0 && (srcs.Contains(sd) || sd == destArch)) return true;
        }

        return false;
    }

    /// <summary>
    ///     True if any instruction older than <paramref name="loadRobIndex" /> is a vector store, a
    ///     UVE arithmetic op that writes a <c>ud</c> register (<c>so.a.mac.fp</c> and siblings — see
    ///     <see cref="ITooth.UveDestinationRegister" />), or an instruction that may access arbitrary
    ///     guest memory (e.g. ECALL — see <see cref="ITooth.MayAccessArbitraryMemory" />). All three
    ///     write eagerly at execute time (bypassing CapturingMemory) at an address the pipeline can't
    ///     statically check for overlap, so younger loads must wait until they have cleared the ROB
    ///     rather than relying on the normal store-forwarding/memory-order-violation machinery.
    ///     <para>
    ///         The UVE case blocks unconditionally rather than querying
    ///         <c>IUveScalars.IsStoreStream</c> live: whether <c>ud</c> is currently a store stream
    ///         reflects whatever the <i>last-executed</i> configuring <c>ss.end</c> set it to, but
    ///         that op is itself head-serialized — if it's still stuck behind an even older,
    ///         not-yet-issued UVE op when this check runs, the query answers "not a store stream yet"
    ///         even though it unconditionally will be one by the time this arithmetic op executes.
    ///         A live query is provably too early to trust, so any UVE op with a <c>ud</c> write is
    ///         treated as a potential store, exactly like the ECALL case above.
    ///     </para>
    ///     Without the UVE case, a scalar load reading a UVE kernel's result back right after the
    ///     store-stream write (the universal "consume the computed value" pattern) can issue and
    ///     execute before that write actually lands, reading stale data — reproduced by a real
    ///     compiled dot-product kernel (<c>ss.sta.st.w</c> + <c>so.a.adde.fp</c> into a stream, then
    ///     a plain <c>flw</c> of the same address) silently printing 0 instead of the correct result.
    ///     Without the ECALL case similarly, a younger load can issue and execute before a
    ///     head-serialized ECALL that writes overlapping memory (e.g. LinuxSyscallEmulator's
    ///     fstat/clock_gettime/getrandom/read filling a guest buffer), reading stale data.
    ///     Scalar stores no longer block loads here; they are handled by forwarding and
    ///     memory-order violation detection.
    /// </summary>
    private bool HasPrecedingVectorStore(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            ITooth? instr = entry.Instruction;
            if (instr is { Class: ToothClass.Vector, VectorDestinationRegister: < 0, DestinationRegister: < 0, })
                return true;
            if (instr?.MayAccessArbitraryMemory == true) return true;
            if (instr is { Class: ToothClass.Uve, UveDestinationRegister: >= 0, }) return true;
        }

        return false;
    }

    /// <summary>
    ///     True if any instruction older than <paramref name="loadRobIndex" /> is a
    ///     store→load fence still in the ROB. Younger loads must wait until the fence
    ///     retires; combined with the fence's own issue gate (ROB head + write buffer
    ///     drained) this gives TSO fence semantics: no post-fence load issues before
    ///     every pre-fence store's write-bus penalty has expired.
    /// </summary>
    private bool HasPrecedingStoreLoadFence(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            if (entry.Instruction?.IsStoreLoadFence == true) return true;
        }

        return false;
    }

    /// <summary>
    ///     InvisiSpec TSO validate-vs-expose rule (Yan et al., MICRO 2018, Table 1): true if an
    ///     older load or store→load fence was in the ROB at the moment <paramref name="loadRobIndex" />
    ///     executed. Checked once, at Execute time, since it is a property of program order at that
    ///     instant — unlike <see cref="HasPrecedingStoreLoadFence" />'s live Issue-time gate, this is
    ///     recorded and never re-queried.
    /// </summary>
    private bool HasPrecedingLoadOrFence(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            if (entry.IsLoad || entry.Instruction?.IsStoreLoadFence == true) return true;
        }

        return false;
    }

    /// <summary>
    ///     True if any SQ entry older than <paramref name="loadSeqNo" /> has not yet
    ///     resolved its effective address. Used to implement conservative load ordering
    ///     (FuLatencyConfig.ConservativeLoads), mirroring Olympia's
    ///     allow_speculative_load_exec = false.
    /// </summary>
    private bool HasUnresolvedPrecedingStore(ulong loadSeqNo) =>
        _sq.InOrder().TakeWhile(sq => sq.SeqNo < loadSeqNo).Any(sq => !sq.AddressKnown);

    /// <summary>
    ///     True if the load's predicted dependent store is still in the SQ without a known
    ///     value.  Returns false if the store is not found (committed, flushed, or never
    ///     assigned) or if the atomic guard fires (predStoreSeqNo >= loadSeqNo).
    ///     <para>
    ///         Gates on <see cref="SqEntry.DataKnown" />, not <see cref="SqEntry.AddressKnown" />:
    ///         with early store-address resolution (<c>enableEarlyStoreAddress</c>), a store's
    ///         address can be known well before its data. Releasing the predicted-dependent load
    ///         at address-known would let it race the store's real write — <see
    ///         cref="TryForwardFromStore" /> can't forward yet (it also requires DataKnown), so
    ///         the load would read stale memory instead, guaranteeing a memory-order violation on
    ///         every prediction instead of the clean stall Store Sets exists to provide. Same bug
    ///         class as the one fixed on <c>CprTrain</c>'s equivalent check.
    ///     </para>
    /// </summary>
    private bool StoreSetStallLoad(ulong loadSeqNo, ulong predStoreSeqNo) {
        if (predStoreSeqNo == 0 || predStoreSeqNo >= loadSeqNo) return false;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo == predStoreSeqNo) return !sq.DataKnown;
            if (sq.SeqNo > predStoreSeqNo) break;
        }

        return false; // store already committed or flushed — safe to issue
    }

    /// <summary>
    ///     After a store's address resolves, scan LQ entries younger than the store's
    ///     sequence number for loads that have already executed against the same (or
    ///     overlapping) address. Those loads read a stale value and are flagged for
    ///     re-execution at the ROB head.
    /// </summary>
    private void CheckLoadViolations(ulong storeSeqNo, ulong storeAddr, int storeBytes, ulong storePc = 0) {
        foreach (LqEntry lq in _lq.InOrder()) {
            if (lq.SeqNo <= storeSeqNo) continue; // older than or equal to the store
            if (!lq.Executed) continue;
            if (AddressOverlaps(lq.Address, lq.Bytes, storeAddr, storeBytes)) {
                lq.Violated = true;
                if (storePc != 0) lq.ViolatingStorePc = storePc;
            }
        }
    }

    /// <summary>
    ///     SMB (NoSQ, Sha/Martin/Roth MICRO 2006) early bypass: <paramref name="producer" />'s
    ///     value/width just became known (its address doesn't need to — a bypassing load only
    ///     needed the value). Wake every LQ entry the dispatch-time predictor matched to this
    ///     store with an early PRF write + CDB broadcast, so dependents don't wait for the
    ///     load's own (still-pending) execution. The load's own shadow execution still runs
    ///     normally afterward and is the sole thing that gates its commit — see
    ///     <see cref="LqEntry.SpeculativelyCompleted" /> and the verification in
    ///     <see cref="StepExecute" />/<see cref="StepComplete" />.
    /// </summary>
    private void CheckBypassLoads(SqEntry producer) {
        foreach (LqEntry lq in _lq.InOrder()) {
            if (!lq.Bypassed || lq.SpeculativelyCompleted) continue;
            if (lq.PredictedProducerSeqNo != producer.SeqNo) continue;

            RobEntry loadRob = _rob.At(lq.RobIdx);
            int width = loadRob.Instruction?.MemoryAccessBytes ?? 0;
            if (loadRob.PhysDestination < 0 || width is <= 0 or > 8) {
                lq.SpeculativelyCompleted = true; // nothing sane to broadcast; shadow execution verifies
                continue;
            }

            ulong mask = width >= 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
            ulong value = producer.Value & mask;

            int signExtBytes = loadRob.Instruction?.LoadSignExtendBytes ?? 0;
            if (signExtBytes > 0) {
                ulong signBit = 1UL << (signExtBytes * 8 - 1);
                if ((value & signBit) != 0) value |= ~((1UL << (signExtBytes * 8)) - 1);
            }

            if (loadRob.Instruction?.NanBoxLoadResult == true) value = 0xFFFFFFFF00000000UL | (value & 0xFFFFFFFF);

            _prf.Write(loadRob.PhysDestination, value);
            foreach (IssueQueue iq in _iqs) iq.Broadcast(loadRob.PhysDestination, value, loadRob.InstrId);
            lq.SpeculativelyCompleted = true;
        }
    }

    /// <summary>
    ///     If any older SQ entry's byte range fully contains the load's byte range, return
    ///     the forwarded bytes extracted from the stored value.  Partial overlap (store
    ///     covers some but not all the load's bytes) is not forwarded — the load goes
    ///     to memory and <see cref="CheckLoadViolations" /> will flag it on store resolution.
    ///     The youngest matching store wins (last seen in program order = head-to-tail).
    /// </summary>
    private (ulong Value, bool HasValue) TryForwardFromStore(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        (ulong Value, bool HasValue) result = default;
        ulong loadEnd = loadAddr + (ulong)loadBytes;
        ulong mask = loadBytes >= 8 ? ulong.MaxValue : (1UL << (loadBytes * 8)) - 1;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break;
            // DataKnown, not just AddressKnown: an early-resolved address (see
            // StepEarlyStoreAddressResolution) has no valid Value/Width yet.
            if (!sq.AddressKnown || !sq.DataKnown) continue;
            // Skip unless the store's byte range fully contains the load's byte range.
            if (sq.Address > loadAddr || loadEnd > sq.Address + (ulong)sq.Width) continue;
            // Extract the relevant bytes: shift right by the byte offset within the store.
            int shift = (int)(loadAddr - sq.Address) * 8;
            result = ((sq.Value >> shift) & mask, true); // keep overwriting to get youngest match
        }

        return result;
    }

    /// <summary>
    ///     Same match rule as <see cref="TryForwardFromStore" /> (youngest fully-containing older
    ///     store wins) but returns the producing store's SeqNo instead of its value. Used by SMB
    ///     bypass-mispredict recovery to retrain <see cref="SmbPredictor" /> with the true SSN
    ///     distance — called from <see cref="StepComplete" /> while the producing SQ entry is
    ///     still guaranteed live (it may already have retired by the time the load reaches commit).
    /// </summary>
    private ulong? FindForwardingProducerSeqNo(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        ulong loadEnd = loadAddr + (ulong)loadBytes;
        ulong? producerSeqNo = null;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break;
            // DataKnown, not just AddressKnown: an early-resolved address (see
            // StepEarlyStoreAddressResolution) has no valid Width yet — Width defaults to 0,
            // which the containment check below would already reject, but check explicitly
            // rather than lean on that coincidence.
            if (!sq.AddressKnown || !sq.DataKnown) continue;
            if (sq.Address > loadAddr || loadEnd > sq.Address + (ulong)sq.Width) continue;
            producerSeqNo = sq.SeqNo;
        }

        return producerSeqNo;
    }

    /// <summary>
    ///     Linear scan for the still-live SQ entry with the given SeqNo — mirrors
    ///     <see cref="FindRobByInstrId" />'s style/scope (small, bounded ring buffer; called only
    ///     from the STT memory-dependence predictor-training gate, not a hot path).
    /// </summary>
    private SqEntry? FindSqBySeqNo(ulong seqNo) {
        foreach (SqEntry sq in _sq.InOrder())
            if (sq.SeqNo == seqNo)
                return sq;
        return null;
    }

    /// <summary>
    ///     True if any SQ entry older than <paramref name="loadSeqNo" /> has a known address
    ///     that overlaps the load. Used at load-execute time to detect violations that
    ///     weren't caught by <see cref="CheckLoadViolations" /> (the store resolved before
    ///     the load executed; the converse ordering is caught when the store later resolves).
    /// </summary>
    private ulong? HasOlderConflictingStore(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        ulong? conflict = null;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break; // past the load's position — done
            if (!sq.AddressKnown) continue;
            if (AddressOverlaps(sq.Address, sq.Width, loadAddr, loadBytes)) conflict = sq.Pc;
        }

        return conflict;
    }

    private static bool AddressOverlaps(ulong aAddr, int aBytes, ulong bAddr, int bBytes) {
        ulong aEnd = aAddr + (ulong)aBytes;
        ulong bEnd = bAddr + (ulong)bBytes;
        return aAddr < bEnd && bAddr < aEnd;
    }

    /// <summary>Allocate ROB + IQ + LQ/SQ slots for instructions that have already been renamed.</summary>
    private void StepDispatch() {
        var dispatched = 0;
        var eoleBypassed = 0;
        while (_renameQueue.Count > 0) {
            if (_rob.IsFull) break;

            RenameEntry ri = _renameQueue.Peek();

            // Fetch page fault: park in ROB as a completed trap; skip IQ entirely.
            if (ri.PreTrap is not null) {
                int faultRobIdx = _rob.Allocate();
                RobEntry robFault = _rob.At(faultRobIdx);
                robFault.Pc = ri.Pc;
                robFault.InstrId = ri.InstrId;
                robFault.HasTrap = true;
                robFault.Trap = ri.PreTrap;
                robFault.IsComplete = true;
                robFault.ArchDestination = -1;
                robFault.PhysDestination = -1;
                robFault.DispatchCycle = _cyclesCounter.Value;
                robFault.CpiStolenAtDispatch = _cpiStolenCycles;
                PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Dispatch);
                // Futuristic model: this entry is a guaranteed trap that never traverses
                // StepComplete — it just rides along unresolved until ForceResolve clears it at
                // the ROB head (see FuturisticVisibilityTracker's class doc).
                _futuristicTracker?.Register(ri.InstrId, FuturisticVisibilityTracker.Sources.Trap);
                _renameQueue.Dequeue();
                dispatched++;
                continue;
            }

            ITooth instr = ri.Decoded!;

            // EOLE Late Execution (Perais & Seznec, ISCA 2014): a confidently value-predicted,
            // single-cycle ALU op never enters the IQ at all — its predicted value is already
            // live (and ready) in the PRF from Rename, so it can be marked complete immediately
            // and verified in-order near Commit instead of competing for an issue port.
            bool leEligible = _enableEoleLateExec && ri.WasValuePredicted
                                                  && instr.Class == ToothClass.IntegerAlu;

            // EOLE Early Execution (Perais & Seznec, ISCA 2014, §3.2): already computed for real
            // back in StepRename (its sources were ready then), so this entry is complete now too
            // — no separate Commit-time verification needed (see StepRename's comment on why).
            bool eeEligible = ri.IsEarlyExecEligible;

            if (!leEligible && !eeEligible && _iqs[IqIndex(instr.Class)].IsFull) break;

            bool needsLq = instr.Class is ToothClass.Load or ToothClass.Atomic;
            bool needsSq = instr.Class is ToothClass.Store or ToothClass.Atomic;
            if (needsLq && _lq.IsFull) break;
            if (needsSq && _sq.IsFull) break;

            // Allocate ROB entry using physical register info captured at rename.
            int robIdx = _rob.Allocate();
            RobEntry rob = _rob.At(robIdx);
            rob.Pc = ri.Pc;
            rob.InstrId = ri.InstrId;
            rob.Instruction = instr;
            rob.ArchDestination = ri.ArchDest;
            rob.PhysDestination = ri.PhysDest;
            rob.PrevPhysDestination = ri.PrevPhysDest;
            rob.PredictedNextPc = ri.PredictedNextPc;
            rob.HistCheckpoint = ri.HistCheckpoint;
            rob.VpHistCheckpoint = ri.VpHistCheckpoint;
            rob.IsVpEligible = ri.IsVpEligible;
            rob.WasValuePredicted = ri.WasValuePredicted;
            rob.PredictedValue = ri.PredictedValue;
            rob.IsLateExecEligible = leEligible;
            rob.IsStore = instr.Class == ToothClass.Store;
            rob.IsLoad = instr.Class is ToothClass.Load or ToothClass.Atomic;
            rob.IsHalt = instr.Class == ToothClass.Halt;
            rob.DispatchCycle = _cyclesCounter.Value;
            rob.CpiStolenAtDispatch = _cpiStolenCycles;
            rob.IcacheMiss = ri.IcacheMiss;
            rob.FusedSecondInstrId = ri.FusedSecondInstrId;

            // Shared visibility-point infrastructure (STT-ExpOnly and InvisiSpec both need to know
            // when the oldest older branch resolves): register every branch/conditional branch
            // entering the ROB, regardless of which of the two defenses is actually enabled.
            if (_vpTracker is not null && instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch)
                _vpTracker.OnDispatchBranch(ri.InstrId);

            // STT-ExpOnly (Yu et al., MICRO 2019, §4.1): Youngest Root of Taint, computed once
            // here from source operands' PRF-resident Yrot (sources are always older, already
            // dispatched instructions, so their Yrot is already final). A Load/Atomic additionally
            // roots taint at its own destination — its fetched data isn't visible yet either.
            // Also computed when only _enableSttImplicitBranches is set (no STT-ExpOnly load-issue
            // gating): a branch's own SourceYrot is needed to decide whether its resolution's
            // observable squash effect must be deferred (see StepComplete/StepSttMispredictResolution).
            // Also needed for _enableSttMemDepGating: a STORE's own SourceYrot (it never gets a
            // destination-Yrot update below, having no destination register) is the taint root
            // checked before letting it influence SmbPredictor training.
            if (_enableSttExpOnly || _enableSttImplicitBranches || _enableSttMemDepGating) {
                ulong? srcYrot = null;
                if (ri.P1 >= 0 && _prf.Yrot(ri.P1) is { } y1) srcYrot = y1;
                if (ri.P2 >= 0 && _prf.Yrot(ri.P2) is { } y2)
                    srcYrot = srcYrot is { } sy1 ? Math.Max(sy1, y2) : y2;
                if (ri.P3 >= 0 && _prf.Yrot(ri.P3) is { } y3)
                    srcYrot = srcYrot is { } sy2 ? Math.Max(sy2, y3) : y3;
                rob.SourceYrot = srcYrot;

                if (rob.PhysDestination >= 0) {
                    ulong? destYrot = rob.IsLoad
                        ? srcYrot is { } sy3 ? Math.Max(sy3, ri.InstrId) : ri.InstrId
                        : srcYrot;
                    _prf.SetYrot(rob.PhysDestination, destYrot);
                }
            }

            PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Dispatch);

            // Critical-path prediction D-source (Table 2): ED (post-misprediction redirect)
            // takes priority, then CD (ROB stalled last cycle), else the DD default (D_{i-1}).
            if (_criticalityPredictor is not null) {
                if (_pendingRedirect) {
                    rob.DGatedByRedirect = true;
                    rob.DRedirectSourceInstrId = _pendingRedirectInstrId;
                    _pendingRedirect = false;
                }
                else if (_dispatchStalledPrevCycle) { rob.DGatedByStall = true; }
            }

            if (leEligible || eeEligible) {
                // Skip IQ/LQ/SQ entirely: the value is already the PRF's live value for
                // PhysDest, so this entry is complete the instant it's dispatched. A
                // Late-Execution entry still needs in-order verification near Commit (see
                // StepCommit's IsLateExecEligible branch); an Early-Execution entry doesn't —
                // it was computed for real, not predicted.
                rob.IsComplete = true;
                if (leEligible) {
                    rob.P1 = ri.P1;
                    rob.P2 = ri.P2;
                }

                if (eeEligible) rob.SideEffect = ri.EarlySideEffect;

                // Futuristic model: neither Early nor Late Execution traverses StepComplete, so
                // neither gets an early Trap/Vp resolution there — both just ride along unresolved
                // until ForceResolve clears them at the ROB head (Late Execution's own in-order
                // verification at Commit happens at exactly that same moment, so this costs it no
                // extra latency; Early Execution never mispredicts, so only Trap applies to it).
                if (_futuristicTracker is not null) {
                    FuturisticVisibilityTracker.Sources mask = FuturisticVisibilityTracker.Sources.Trap;
                    if (leEligible) mask |= FuturisticVisibilityTracker.Sources.Vp;
                    _futuristicTracker.Register(ri.InstrId, mask);
                }

                // Synthetic Issue/Execute PEvents at the same cycle, so PEvent-driven consumers
                // (waveform viewer, CPI/TMA views) see a coherent — if instantaneous —
                // Fetch→Dispatch→Issue→Execute trail instead of an instruction stuck forever
                // pre-issue.
                PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Issue);
                PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Execute);
                _renameQueue.Dequeue();
                dispatched++;
                eoleBypassed++;
                continue;
            }

            // ── Allocate LQ/SQ entries ─────────────────────────────────────────
            // Each memory instruction gets a shared SeqNo so that cross-queue
            // program-order comparisons don't need ROB index arithmetic (which wraps).
            // An Atomic occupies one slot in both queues with the same SeqNo.
            ulong memSeqNo = 0;
            if (needsLq || needsSq) memSeqNo = _nextMemSeqNo++;

            if (needsLq) {
                int lqIdx = _lq.Allocate();
                LqEntry lq = _lq.At(lqIdx);
                lq.RobIdx = robIdx;
                lq.InstrId = ri.InstrId;
                lq.SeqNo = memSeqNo;
                if (_storeSets is not null && instr.Class == ToothClass.Load)
                    lq.PredStoreSeqNo = _storeSets.OnLoadDispatch(ri.Pc);

                // SMB (NoSQ, Sha/Martin/Roth MICRO 2006): full-word/zero-offset bypass only —
                // gated on ToothClass.Load (not Atomic, which has RMW semantics this doesn't model)
                // and on the predicted producer's static width matching this load's, so no
                // address is needed on either side to trust the prediction.
                if (_smbPredictor is not null && instr.Class == ToothClass.Load
                                              && _smbPredictor.TryPredict(ri.Pc, out ulong distance)
                                              && distance > 0 && distance <= memSeqNo) {
                    ulong predictedProducerSeqNo = memSeqNo - distance;
                    foreach (SqEntry candidate in _sq.InOrder()) {
                        if (candidate.SeqNo != predictedProducerSeqNo) continue;
                        if (candidate.StaticBytes == instr.MemoryAccessBytes) {
                            lq.Bypassed = true;
                            lq.PredictedProducerSeqNo = predictedProducerSeqNo;
                            _smbBypassesCounter?.Increment();
                        }

                        break;
                    }
                }

                rob.LqIdx = lqIdx;
            }

            if (needsSq) {
                int sqIdx = _sq.Allocate();
                SqEntry sq = _sq.At(sqIdx);
                sq.RobIdx = robIdx;
                sq.InstrId = ri.InstrId;
                sq.SeqNo = memSeqNo;
                sq.Pc = ri.Pc;
                sq.StaticBytes = instr.MemoryAccessBytes;
                _storeSets?.OnStoreDispatch(ri.Pc, memSeqNo);
                rob.SqIdx = sqIdx;
            }

            // Futuristic model: register every instruction that reaches the ordinary IQ path
            // (branches were already registered above via OnDispatchBranch — additive, so this
            // just ORs Trap in again harmlessly) with the squash sources that apply to it. A
            // store's address isn't known yet (StoreAddr); an SMB-bypassing load's verification
            // is still pending (Smb); a value-predicted load or ALU op's prediction is still
            // pending (Vp, including ordinary — non-Late-Execution — value prediction on loads).
            if (_futuristicTracker is not null) {
                FuturisticVisibilityTracker.Sources mask = FuturisticVisibilityTracker.Sources.Trap;
                if (needsSq) mask |= FuturisticVisibilityTracker.Sources.StoreAddr;
                if (needsLq && _lq.At(rob.LqIdx).Bypassed) mask |= FuturisticVisibilityTracker.Sources.Smb;
                if (rob.IsVpEligible) mask |= FuturisticVisibilityTracker.Sources.Vp;
                _futuristicTracker.Register(ri.InstrId, mask);
            }

            // Allocate IQ slot. Check PRF readiness at dispatch time: sources may have
            // become ready between rename and dispatch via CDB broadcast.
            IssueQueue classIq = _iqs[IqIndex(instr.Class)];
            int iqSlot = classIq.Allocate();
            RsEntry rs = classIq.At(iqSlot);
            rs.RobIndex = robIdx;
            rs.InstrId = ri.InstrId;
            rs.Instruction = instr;
            rs.Pc = ri.Pc;
            rs.PredictedNextPc = ri.PredictedNextPc;
            rs.PhysDestination = ri.PhysDest;

            if (ri.P1 >= 0) {
                if (_prf.IsReady(ri.P1)) {
                    rs.Src1Ready = true;
                    rs.Src1Value = _prf.Read(ri.P1);
                }
                else { rs.Src1Tag = ri.P1; }
            }

            if (ri.P2 >= 0) {
                if (_prf.IsReady(ri.P2)) {
                    rs.Src2Ready = true;
                    rs.Src2Value = _prf.Read(ri.P2);
                }
                else { rs.Src2Tag = ri.P2; }
            }

            if (ri.P3 >= 0) {
                if (_prf.IsReady(ri.P3)) {
                    rs.Src3Ready = true;
                    rs.Src3Value = _prf.Read(ri.P3);
                }
                else { rs.Src3Tag = ri.P3; }
            }

            // Critical-path prediction E-source (Table 2, DE/EE): if every source was already
            // ready at dispatch, E_i's source is D_i itself; otherwise it's the producer of
            // whichever source resolves last (staged via IssueQueue.Broadcast, copied in at Issue).
            if (_criticalityPredictor is not null) {
                rs.PendingSourceCount = (rs.Src1Tag >= 0 ? 1 : 0) + (rs.Src2Tag >= 0 ? 1 : 0)
                                                                  + (rs.Src3Tag >= 0 ? 1 : 0);
                rob.ESourceIsOwnD = rs.PendingSourceCount == 0;
            }

            _renameQueue.Dequeue();
            dispatched++;
        }

        // Count cycles where the rename queue had work but dispatch was structurally blocked.
        bool stalled = _renameQueue.Count > 0;
        if (stalled) _stallsCounter.Increment();
        _dispatchStalledPrevCycle = stalled;

        // TMA slot accounting at the issue point (Table 1). Every dispatched uop fills an
        // issue slot (wrong-path included). Unutilized slots count as FetchBubbles only when
        // there was no backend stall — a non-empty rename queue or a full ROB means the
        // backend could not have accepted more uops regardless of frontend supply. A cycle
        // that delivers nothing at all is whole-cycle fetch starvation (FetchBubbles ≥ MIW),
        // the paper's Fetch Latency Bound numerator. Dispatch may burst past issueWidth when
        // draining a backlog, so the bubble count is clamped at zero.
        _tdSlotsIssuedCounter.IncrementBy(dispatched);
        if (!stalled && !_rob.IsFull && dispatched < _issueWidth) {
            _tdFetchBubblesCounter.IncrementBy(_issueWidth - dispatched);
            if (dispatched == 0) _tdFetchLatencyCyclesCounter.Increment();
        }

        // TMA ExecutionStalls / MemStalls.AnyLoad (Table 1), continuing from StepExecute:
        // EOLE Late/Early Execution retire IntegerAlu ops without ever occupying an issue
        // slot or execute port (Late's verify-compute is deferred to Commit; Early's real
        // compute already happened in a prior tick's StepRename — see its comment there), so
        // a cycle where the OoO engine itself issued nothing is not a stall if EOLE bypass
        // supplied the throughput instead. Crediting both at this instant (rather than
        // wherever their arithmetic actually happens) matches the synthetic Issue/Execute
        // PEvents already recorded at this same bypass site, above.
        int totalExecuting = _execCountThisTick + eoleBypassed;
        if (totalExecuting * 2 < _issueWidth) {
            _tdExecStallCyclesCounter.Increment();
            if (totalExecuting == 0 && AnyInFlightLoad()) _tdMemStallLoadCyclesCounter.Increment();
        }

        // CPI stack: after a mispredicted-branch redirect, dispatch-empty cycles are the
        // pipeline refill part of the misprediction penalty; charging stops at the first
        // correct-path dispatch (ASPLOS 2006, section 4.1).
        if (_cpiBpredRefill) {
            if (dispatched > 0)
                _cpiBpredRefill = false;
            else if (!stalled && !_rob.IsFull) _cpiBpredCounter.Increment();
        }
    }

    /// <summary>Drain up to issueWidth decoded instructions through the RAT/PRF rename stage.</summary>
    private void StepRename() {
        // Runahead safety: the shadow lane (TryShadowStep) draws physical registers from this
        // same live RenameMap free list and restores a pre-episode snapshot on exit. Nothing
        // about NeedsRunahead()/ROB-fullness stops real rename from also progressing here (rename
        // is gated on decode-queue occupancy and free-list space, not ROB room), so real renames
        // issued during an active episode would be silently discarded by RenameMap.Restore() —
        // or worse, double-allocated against physical registers the shadow lane is still using.
        // Freezing real rename for the episode's duration closes that race for both the scalar
        // and vector runahead shadow lanes.
        if (_runaheadActive) return;

        // EOLE Early Execution (Perais & Seznec, ISCA 2014, §3.2): physical registers written
        // by an Early-Execution computation *this tick* don't count as "ready" for another
        // Early-Execution eligibility check this same tick. The paper found chaining EE results
        // within one rename cycle ("more than a single [ALU] stage") highly inefficient and
        // settled on a 1-deep design — only the *previous* cycle's EE results (or immediates,
        // or value predictions, both available same-cycle) may feed an EE computation. Cleared
        // every call since it must never carry across ticks.
        _eeWrittenThisTick.Clear();

        while (_decodeQueue.Count > 0 && _renameQueue.Count < _maxDecodeDepth) {
            FetchedInstr fi = _decodeQueue.Peek();

            // Pre-trap: pass through rename without RAT allocation.
            if (fi.PreTrap is not null) {
                PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Rename);
                _renameQueue.Enqueue(
                    new RenameEntry(
                        fi.Pc, fi.Decoded, fi.PredictedNextPc, fi.InstrId, fi.PreTrap,
                        -1, -1, -1, -1, -1, -1, IcacheMiss: fi.IcacheMiss
                    )
                );
                _decodeQueue.Dequeue();
                continue;
            }

            ITooth instr = fi.Decoded!;

            // Macro-fusion: must happen here, before source lookup, not at Issue like
            // SuperscalarTrain's execute-at-issue model. An OoO consumer can't read a
            // producer's value until the CDB broadcasts it (typically 1+ cycles after
            // Execute), so co-issuing two separately-renamed entries wouldn't remove that
            // latency — only collapsing to a single RAT/ROB/IQ entry does. `second`'s own
            // PredictedNextPc/HistCheckpoint (its real fetch-time branch prediction) replace
            // `fi`'s trivial ones below; `fi`'s are meaningless for a non-branch compare.
            var fused = false;
            FetchedInstr second = default;
            if (_macroFuser is not null
             && TryPeekSecondDecoded(out second)
             && second.PreTrap is null
             && second.Decoded is not null
             && second.Pc == fi.Pc + (ulong)instr.SizeBytes) {
                ITooth? fusedInstr = _macroFuser.TryFuse(instr, second.Decoded);
                if (fusedInstr is not null) {
                    instr = fusedInstr;
                    fused = true;
                }
            }

            int destArch = instr.DestinationRegister;
            if (destArch > 0 && !_rat.HasFree) break; // stall: no free physical registers

            // Secondary-destination RAW/WAW hazard: an older, uncommitted instruction
            // (e.g., an RV32 amocas.d) will write one of this instruction's sources — or
            // this instruction's own destination — through SideEffect straight into
            // architectural state, bypassing the RAT/PRF entirely. Renaming now would
            // either bind a source to the stale physical register, or (WAW) let this
            // instruction claim a new physical register for that architectural register
            // before the producer's retirement-time PRF sync lands on the old one. Stall
            // dispatch until that producer retires.
            if (HasPendingSecondaryDest(instr.SourceRegisters, destArch)) break;

            // ── Source lookup BEFORE destination rename ────────────────────────
            // Tomasulo invariant: sources must be resolved against the RAT state
            // as it exists just before this instruction's rename, so that an
            // instruction whose source == destination (e.g., addi x1,x1,1) reads
            // the producer's physical register, not its own pending output.
            IReadOnlyList<int> srcs = instr.SourceRegisters;
            int p1 = srcs.Count > 0 ? _rat.Lookup(srcs[0]) : -1;
            int p2 = srcs.Count > 1 ? _rat.Lookup(srcs[1]) : -1;
            int p3 = srcs.Count > 2 ? _rat.Lookup(srcs[2]) : -1;

            // ── Rename destination ─────────────────────────────────────────────
            int newPhys = -1, oldPhys = -1;
            var vpEligible = false;
            var wasValuePredicted = false;
            ulong predictedValue = 0;
            var earlyExecEligible = false;
            Action<IArchState>? earlySideEffect = null;
            if (destArch > 0) {
                (newPhys, oldPhys) = _rat.Rename(destArch);
                _prf.MarkPending(newPhys);

                // EOLE Early Execution (Perais & Seznec, ISCA 2014, §3.2): a single-cycle ALU op
                // whose sources are already available (immediate, an already-ready register, or a
                // value predictor's prediction — anything but another *this-tick* EE result, see
                // _eeWrittenThisTick above) is computed right here, in-order, and never enters the
                // IQ at all. Checked ahead of value prediction: if EE fires, the true result is
                // already known, so there's nothing left to predict for this destination.
                bool srcsReady = (p1 < 0 || (_prf.IsReady(p1) && !_eeWrittenThisTick.Contains(p1)))
                              && (p2 < 0 || (_prf.IsReady(p2) && !_eeWrittenThisTick.Contains(p2)));
                earlyExecEligible = _enableEoleEarlyExec && instr.Class == ToothClass.IntegerAlu
                                                         && srcsReady && _eeWrittenThisTick.Count < _issueWidth;

                if (earlyExecEligible) {
                    ulong src1 = p1 >= 0 ? _prf.Read(p1) : 0;
                    ulong src2 = p2 >= 0 ? _prf.Read(p2) : 0;
                    var issued = new IssuedInstr(0, newPhys, instr, fi.Pc, src1, src2, 0, fi.InstrId);
                    ExecResult er = ExecuteOne(issued, 0);
                    _prf.Write(newPhys, er.RegValue.Value);
                    _eeWrittenThisTick.Add(newPhys);
                    earlySideEffect = er.SideEffect;
                    _eoleEarlyExecCounter?.Increment();
                }

                // Value prediction (Lipasti & Shen, MICRO 1996; Perais & Seznec, HPCA 2014):
                // eligible for any single-register-destination, PRF-resident result — integer
                // ALU/MulDiv, load, floating point (both pipelined and div/sqrt), and CSR reads
                // (System). All six share the same PRF (FP architectural registers are renamed
                // through the same RAT at index rd+32 — see Rv32Decoder.Fp.cs) and the same
                // recovery shape: any speculative side effect a mispredicted producer's younger
                // consumers picked up (e.g. FP fflags) is undone by the same full-squash-at-commit
                // machinery that already recovers wrong-path branch execution, so VP introduces no
                // new hazard class. Deliberately excludes Atomic (secondary destination delivered
                // via SideEffect straight into architectural state, never renamed — see
                // ITooth.SecondaryDestinationRegister) and Vector (register renaming not
                // implemented for the V extension). A confident prediction is written and marked
                // ready immediately — dependents waiting in StepDispatch's IsReady check pick it up
                // with no further plumbing. The producing instruction still executes for real; a
                // mismatch is caught in StepComplete and squashed at commit (StepCommit), never
                // here. Eligible instructions are trained at commit whether or not a prediction was
                // supplied (this still applies to an Early-Executed instruction: it's trained with
                // the ground-truth value it just computed, for free, via the ordinary commit path).
                vpEligible = instr.Class is ToothClass.IntegerAlu or ToothClass.IntegerMulDiv
                                                                  or ToothClass.Load or ToothClass.FloatingPoint
                                                                  or ToothClass.FloatDivSqrt or ToothClass.System;
                if (!earlyExecEligible && vpEligible && _valuePredictor is not null
                 && _valuePredictor.TryPredict(fi.Pc, fi.VpHistCheckpoint, out predictedValue)) {
                    _prf.Write(newPhys, predictedValue);
                    wasValuePredicted = true;
                    _vpPredictionsCounter?.Increment();
                }
            }

            PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Rename);
            _renameQueue.Enqueue(
                new RenameEntry(
                    fi.Pc, instr, fused ? second.PredictedNextPc : fi.PredictedNextPc, fi.InstrId, null,
                    destArch > 0 ? destArch : -1, newPhys, oldPhys, p1, p2, p3,
                    fused ? second.HistCheckpoint : fi.HistCheckpoint,
                    fi.IcacheMiss || (fused && second.IcacheMiss), fi.VpHistCheckpoint, vpEligible,
                    wasValuePredicted, predictedValue, earlyExecEligible, earlySideEffect,
                    fused ? second.InstrId : null
                )
            );
            _decodeQueue.Dequeue();
            if (fused) {
                _decodeQueue.Dequeue();
                // Counted here, past every RAT-full/secondary-dest break above: those breaks
                // re-peek the same still-undequeued pair next cycle without renaming it, so
                // incrementing at detection time would double-count every stalled cycle.
                if (instr.Class == ToothClass.Load) _microFusionsCounter.Increment();
                else _macroFusionsCounter.Increment();
            }
        }
    }

    /// <summary>
    ///     Peeks the decode queue's second entry (the one behind the head) without dequeuing
    ///     anything, for macro-fusion's adjacency check in <see cref="StepRename" />.
    ///     <c>Queue&lt;T&gt;</c> exposes no indexer, so this walks its struct enumerator
    ///     directly — no LINQ, no allocation.
    /// </summary>
    private bool TryPeekSecondDecoded(out FetchedInstr second) {
        if (_decodeQueue.Count < 2) {
            second = default;
            return false;
        }

        Queue<FetchedInstr>.Enumerator e = _decodeQueue.GetEnumerator();
        e.MoveNext();
        e.MoveNext();
        second = e.Current;
        return true;
    }

    /// <summary>Fetch up to issueWidth instructions into the decode queue.</summary>
    private void StepFetch() {
        _fdip?.Tick(_fetchPc);
        if (_fetchFaulted) return; // wait for flush to clear before fetching again

        var fetched = 0;
        while (fetched < _issueWidth && _decodeQueue.Count < _maxDecodeDepth) {
            // CPI stack: snapshot I-side miss counts around this fetch (translation + read)
            // so the fetched instruction can carry the sFMT 'I-cache/I-TLB miss' bit.
            long im1 = 0, im2 = 0, im3 = 0, imt = 0;
            if (_anyCache) {
                im1 = ILayers.Cache?.Misses ?? 0;
                im2 = (ILayers.L2Cache?.Misses ?? 0) + (ILayers.L2Bdi?.Misses ?? 0) + (ILayers.L2Ceaser?.Misses ?? 0) +
                      (ILayers.L2Scatter?.Misses ?? 0);
                im3 = (ILayers.L3Cache?.Misses ?? 0) + (ILayers.L3Bdi?.Misses ?? 0) + (ILayers.L3Ceaser?.Misses ?? 0) +
                      (ILayers.L3Scatter?.Misses ?? 0);
                imt = ILayers.Tlb?.Misses ?? 0;
            }

            // Translate virtual PC to physical (Sv32 or bare mode).
            ulong physPc = _fetchPc;
            if (_fetchTranslator is not null) {
                (ulong pa, int faultCause) = _fetchTranslator.Translate(_fetchPc);
                if (faultCause != 0) {
                    ulong faultId = _nextInstrId++;
                    _decodeQueue.Enqueue(
                        new FetchedInstr(
                            _fetchPc, null, _fetchPc, faultId,
                            new TrapInfo(faultCause, _fetchPc, _fetchPc)
                        )
                    );
                    PEventLog?.Record(faultId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
                    _fetchFaulted = true;
                    return;
                }

                physPc = pa;
            }

            ITooth decoded;
            uint raw;
            try {
                raw = (uint)ILayers.Accessor.Read(physPc, 4);
                decoded = _decoder.Decode(_fetchPc, raw);
            }
            catch (IllegalInstructionException ex) {
                // Enqueue a pre-trap so the fault propagates through the ROB and
                // commits in-order. Without this, _fetchFaulted=true with nothing
                // in the ROB permanently wedges the fetcher until a flush arrives.
                // On a wrong speculative path the flush squashes the PreTrap entry
                // just like any other in-flight instruction.
                ulong faultId = _nextInstrId++;
                _decodeQueue.Enqueue(
                    new FetchedInstr(
                        _fetchPc, null, _fetchPc, faultId,
                        new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, _fetchPc)
                    )
                );
                PEventLog?.Record(faultId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
                _fetchFaulted = true;
                break;
            }
            catch (AccessViolationException) {
                // Fetch address out of bounds. On the correct path this is a genuine instruction
                // access fault; on a wrong path (e.g., an execute-time squash redirected fetch to a
                // wrong-path branch's garbage target) the pre-trap is squashed before it commits,
                // exactly like the illegal-instruction case above. Either way, never crash the sim.
                ulong faultId = _nextInstrId++;
                _decodeQueue.Enqueue(
                    new FetchedInstr(
                        _fetchPc, null, _fetchPc, faultId,
                        new TrapInfo(TrapCause.InstructionAccessFault, _fetchPc, _fetchPc)
                    )
                );
                PEventLog?.Record(faultId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
                _fetchFaulted = true;
                break;
            }

            if (Rdip is not null && ILayers.Cache?.LastAccessWasHit == false) Rdip.OnIcacheMiss(physPc);

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            BranchHistoryCheckpoint histCheckpoint = default;
            // Snapshot the value predictor's speculative history at this exact fetch, for every
            // instruction (not just branches): value prediction's own TryPredict/Update pair needs
            // this same snapshot passed to both calls so a single dynamic instruction's predict and
            // train always agree on which history slot to index, regardless of how much the live
            // speculative history has moved on by the time this instruction reaches Commit.
            ValueHistoryCheckpoint vpHistCheckpoint
                = _valuePredictor?.CaptureHistory() ?? default(ValueHistoryCheckpoint);
            if (hint.IsBranch) {
                // Snapshot the branch predictor's own speculative history before this branch folds
                // its own direction, so an execute-time partial squash can rewind to exactly here.
                histCheckpoint = _predictor.CaptureHistory(_fetchPc);
                if (hint.IsCall) _ras.Push(_fetchPc + (ulong)decoded.SizeBytes);

                BranchPrediction pred;
                if (hint.IsReturn && _ras.TryPop(out ulong ret))
                    pred = BranchPrediction.Taken(ret);
                else if (hint is { IsUnconditional: true, BranchTarget.HasValue: true, })
                    // Direct unconditional jump/call: always taken to the known target.
                    // No direction predictor needed (mirrors gem5, which never
                    // direction-predicts unconditional branches).
                    pred = BranchPrediction.Taken(hint.BranchTarget.Value);
                else
                    pred = _predictor.Predict(_fetchPc, hint.BranchTarget);

                // A direct branch's taken target is statically known — take it from the
                // decode hint, not the predictor's BTB, which may be cold or aliased (a stale
                // 0 there would send speculative fetch to a null address). The predictor's
                // target is used only for indirect branches; a cold indirect target (0) falls
                // through rather than crashing.
                ulong fallThrough = _fetchPc + (ulong)decoded.SizeBytes;
                ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
                predictedNext = pred.PredictedTaken && takenTarget != 0 ? takenTarget : fallThrough;

                // Fold the predicted direction into speculative history so younger in-flight
                // branches index fresh history. Matches the taken bit Update applies at commit
                // (resolvedPc != fall-through); on the correct path the two agree bit-for-bit.
                _predictor.SpeculativeHistoryUpdate(_fetchPc, predictedNext != fallThrough);
                _valuePredictor?.OnBranchFetched(predictedNext != fallThrough);
            }
            else { predictedNext = _fetchPc + (ulong)decoded.SizeBytes; }

            bool icacheMiss = _anyCache && ((ILayers.Cache?.Misses ?? 0) > im1
                                         || (ILayers.L2Cache?.Misses ?? 0) > im2
                                         || (ILayers.L2Bdi?.Misses ?? 0) > im2
                                         || (ILayers.L2Ceaser?.Misses ?? 0) > im2
                                         || (ILayers.L2Scatter?.Misses ?? 0) > im2
                                         || (ILayers.L3Cache?.Misses ?? 0) > im3
                                         || (ILayers.L3Bdi?.Misses ?? 0) > im3
                                         || (ILayers.L3Ceaser?.Misses ?? 0) > im3
                                         || (ILayers.L3Scatter?.Misses ?? 0) > im3
                                         || (ILayers.Tlb?.Misses ?? 0) > imt);

            ulong instrId = _nextInstrId++;
            _decodeQueue.Enqueue(
                new FetchedInstr(
                    _fetchPc, decoded, predictedNext, instrId, HistCheckpoint: histCheckpoint,
                    IcacheMiss: icacheMiss, VpHistCheckpoint: vpHistCheckpoint
                )
            );
            PEventLog?.Record(instrId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
            PEventLog?.RecordDisasm(instrId, _decoder.Disassemble(_fetchPc, decoded.RawEncoding));
            _fetchPc = predictedNext;
            fetched++;
        }
    }

    /// <summary>
    ///     Trains the Vector Runahead stride table (Naithani et al., ISCA 2021) from the real
    ///     (non-shadow) demand-load stream — mirrors <see cref="StridePrefetcher" />'s RPT update,
    ///     PC-indexed, direct-mapped, saturating 0-3 confidence. Only <see cref="TryShadowStep" />
    ///     consults this table; it does not affect real cache prefetching.
    /// </summary>
    private void UpdateVrStrideTable(ulong pc, ulong address) {
        ref VrStrideEntry e = ref _vrStrideTable[(int)((pc >> 2) & (uint)(_vrStrideTable.Length - 1))];
        if (e.Initialized) {
            var stride = (long)(address - e.LastAddr);
            e.Confidence = stride == e.Stride && stride != 0 ? Math.Min(3, e.Confidence + 1) : 0;
            e.Stride = stride;
        }
        else {
            e.Initialized = true;
            e.Stride = 0;
            e.Confidence = 0;
        }

        e.LastAddr = address;
    }

    /// <summary>
    ///     True when instruction fetch is effectively bare-metal at the current fetch PC: either
    ///     no translator exists at all, or the one that does resolves this PC as an identity
    ///     mapping (e.g. RvFetchTranslator in M-mode, or with Sv32 paging disabled). The shadow
    ///     lane reads <see cref="_shadowPc" /> straight through <see cref="ILayers" />.Accessor
    ///     with no translation step of its own, so active paging (a non-identity result) must
    ///     disable runahead entirely — modeling speculative TLB/page-walk behavior in the shadow
    ///     lane is not worth the complexity for v1.
    /// </summary>
    private bool IsBareMetalFetch() {
        if (_fetchTranslator is null) return true;
        (ulong physAddr, int faultCause) = _fetchTranslator.Translate(_fetchPc);
        return faultCause == 0 && physAddr == _fetchPc;
    }

    private bool NeedsRunahead() =>
        _enableRunahead && IsBareMetalFetch() && _rob is { IsFull: true, Head: { IsLoad: true, IsComplete: false, }, };

    private void EnterRunahead() {
        _runaheadActive = true;
        _runaheadRatSnapshot = _rat.Snapshot();
        _shadowPc = _fetchPc;
        _runaheadInstrCount = 0;
        _runaheadTainted.Clear();
        _runaheadStoreBuffer.Clear();
        _runaheadChainActive = false;
        _runaheadChainOrigin = 0;
        _runaheadChainTerminator = 0;
        _runaheadChainRound = 0;
        _runaheadRoundBaseAddr = 0;
        _runaheadVectorLanes.Clear();
        _runaheadOwnedPhys.Clear();
        _runaheadCappedOrigins.Clear();

        // Seed taint with every arch register whose live mapping isn't ready yet — the blocking
        // load's own destination, plus anything else still in flight behind it.
        for (var a = 0; a < State.IntegerRegisters.Count; a++) {
            int phys = _rat.Lookup(a);
            if (!_prf.IsReady(phys)) _runaheadTainted.Add(phys);
        }

        _runaheadEpisodesCounter?.Increment();
    }

    private void ExitRunahead() {
        _rat.Restore(_runaheadRatSnapshot);
        _runaheadActive = false;
        _runaheadTainted.Clear();
        _runaheadStoreBuffer.Clear();
        _runaheadChainActive = false;
        _runaheadChainOrigin = 0;
        _runaheadChainTerminator = 0;
        _runaheadChainRound = 0;
        _runaheadRoundBaseAddr = 0;
        _runaheadVectorLanes.Clear();
        _runaheadOwnedPhys.Clear();
        _runaheadCappedOrigins.Clear();
    }

    /// <summary>
    ///     Register-pressure management for Vector Runahead (Naithani et al., ISCA 2021, "Managing
    ///     Pipeline Resources During Runahead"): frees a physical register as soon as its
    ///     architectural register is renamed again, instead of waiting for <see cref="ExitRunahead" />
    ///     's wholesale RAT restore. This is what lets vector unrolling issue many rounds without
    ///     exhausting <see cref="_rat" />'s free list. The paper needs an explicit in-order register
    ///     deallocation queue (RDQ) for this because its vector-pipelined issue can have several
    ///     rounds' instructions in flight out of program order; Horologium's shadow lane issues
    ///     strictly one instruction at a time along a single <see cref="_shadowPc" />, so any
    ///     instruction that could still read the old value has, by construction, already executed
    ///     by the time we reach the instruction that redefines it — an RDQ's ordering guarantee for
    ///     free, with no queue needed. Only registers this same episode allocated
    ///     (<see cref="_runaheadOwnedPhys" />) are ever freed this way; a pre-episode (real)
    ///     physical register is left untouched and only ever restored via the RAT snapshot.
    /// </summary>
    private void FreeShadowRename(int newPhys, int oldPhys) {
        _runaheadOwnedPhys.Add(newPhys);
        if (oldPhys < 0 || !_runaheadOwnedPhys.Remove(oldPhys)) return;
        _runaheadVectorLanes.Remove(oldPhys);
        _runaheadTainted.Remove(oldPhys);
        _rat.FreePhysical(oldPhys);
    }

    /// <summary>
    ///     Vector Runahead (Naithani et al., ISCA 2021) termination-condition change: while a
    ///     dependent-load chain is actively vectorizing, keep the shadow lane running (see
    ///     <see cref="RunaheadStep" />'s loop-exit check) past the point the real blocking load
    ///     resolves. A chain ends when the shadow PC loops back to the load that started it
    ///     (backfilling the stride table's learned terminator for future episodes, since the
    ///     terminator can't be observed from the real, non-shadow instruction stream alone) or
    ///     reaches a previously learned terminator directly — subject to <see cref="TerminateOrUnroll" />.
    /// </summary>
    private void UpdateChainTermination() {
        if (_shadowPc != _runaheadChainOrigin) {
            if (_runaheadChainTerminator != 0 && _shadowPc == _runaheadChainTerminator) TerminateOrUnroll();
            return;
        }

        ref VrStrideEntry e
            = ref _vrStrideTable[(int)((_runaheadChainOrigin >> 2) & (uint)(_vrStrideTable.Length - 1))];
        if (e.Terminator == 0) e.Terminator = _shadowPc;
        TerminateOrUnroll();
    }

    /// <summary>
    ///     Vector pipelining (Naithani et al., ISCA 2021 §III-G, "P overlapped in-flight rounds"):
    ///     how many of the remaining unroll rounds to pack into the origin load's <em>next</em>
    ///     vectorization event, instead of issuing them across separate loop-body walks. A pure
    ///     function of <see cref="_runaheadChainRound" /> (rounds already completed) so
    ///     <see cref="TryVectorizeTaintedLoad" />/<see cref="TryVectorizeShadowStep" /> (computing
    ///     this visit's lane width) and <see cref="TerminateOrUnroll" /> (advancing the round
    ///     count afterwards) always agree without threading extra state between them — nothing
    ///     else mutates <see cref="_runaheadChainRound" /> in between a single origin visit's two
    ///     calls. Clamped to at least 1 so a chain can never get stuck packing a zero-wide round.
    /// </summary>
    private int PipelineRoundsThisVisit() =>
        Math.Max(1, Math.Min(_runaheadPipelineDepth, _runaheadUnrollLength - _runaheadChainRound));

    /// <summary>
    ///     Vector unrolling and pipelining (Naithani et al., ISCA 2021 §III-G): a chain that has
    ///     just reached one of the two termination points above does not necessarily end the
    ///     episode — instead its most recent origin-load visit already packed
    ///     <see cref="PipelineRoundsThisVisit" /> rounds' worth of N-wide lanes into one
    ///     vectorization event (advancing <see cref="_runaheadRoundBaseAddr" />, see
    ///     <see cref="TryVectorizeTaintedLoad" />/<see cref="TryVectorizeShadowStep" />), so the
    ///     round count only needs to advance by that many at once — requiring just
    ///     <c>⌈U/P⌉</c> total loop-body walks to reach <see cref="_runaheadUnrollLength" /> (U)
    ///     total rounds, rather than U separate walks. With the default <c>runaheadPipelineDepth</c>
    ///     of 1 this reduces exactly to one round advanced per walk, matching the paper's default
    ///     of U=8 rounds of N=8 lanes (64 scalar-equivalent loop iterations) before returning to
    ///     normal mode.
    /// </summary>
    private void TerminateOrUnroll() {
        _runaheadChainRound += PipelineRoundsThisVisit();
        if (_runaheadChainRound < _runaheadUnrollLength)
            return; // stay chain-active: the next pass through the origin PC starts the next round

        _runaheadChainActive = false;

        // Without this, the very next visit to _runaheadChainOrigin (the common case — a tight
        // loop revisiting it) would look identical to a cold "start new chain" to
        // TryVectorizeTaintedLoad/TryVectorizeShadowStep and reset the round counter, so the cap
        // above would never actually take effect.
        _runaheadCappedOrigins.Add(_runaheadChainOrigin);
    }

    /// <summary>Runs up to issueWidth shadow instructions this cycle, or exits the episode.</summary>
    private void RunaheadStep() {
        for (var i = 0; i < _issueWidth; i++) {
            if (_runaheadChainActive) UpdateChainTermination();

            if (!NeedsRunahead() && !_runaheadChainActive) {
                ExitRunahead();
                return;
            } // real head resolved (or ROB no longer full) and no vector chain still in flight

            if (_runaheadInstrCount >= _runaheadBudget || !_rat.HasFree) {
                ExitRunahead();
                return;
            }

            if (!TryShadowStep()) {
                ExitRunahead();
                return;
            } // unsupported class / fault / trap

            _runaheadInstrCount++;
            _runaheadInstructionsCounter?.Increment();
        }
    }

    /// <summary>Fetches, renames, and executes exactly one shadow instruction at <see cref="_shadowPc" />.</summary>
    private bool TryShadowStep() {
        uint raw;
        ITooth instr;
        try {
            raw = (uint)ILayers.Accessor.Read(_shadowPc, 4);
            instr = _decoder.Decode(_shadowPc, raw);
        }
        catch (IllegalInstructionException) { return false; }
        catch (AccessViolationException) { return false; }

        if (!OoOPipelineCore.RunaheadSupportedClasses.Contains(instr.Class)) return false;

        IReadOnlyList<int> srcs = instr.SourceRegisters;
        int p0 = srcs.Count > 0 ? _rat.Lookup(srcs[0]) : -1;
        int p1 = srcs.Count > 1 ? _rat.Lookup(srcs[1]) : -1;
        int p2 = srcs.Count > 2 ? _rat.Lookup(srcs[2]) : -1;
        bool anyTainted = Tainted(p0) || Tainted(p1) || Tainted(p2);

        // Branches never dereference memory and don't need real operand values (prediction is
        // PC/history-indexed) — taint never blocks them. No RAS/history mutation: shadow
        // speculation stays invisible to real predictor and return-address-stack state.
        if (instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch) {
            FetchHint hint = _decoder.GetFetchHint(_shadowPc, raw);
            BranchPrediction pred = hint is { IsUnconditional: true, BranchTarget.HasValue: true, }
                ? BranchPrediction.Taken(hint.BranchTarget.Value)
                : _predictor.Predict(_shadowPc, hint.BranchTarget);
            ulong fallThrough = _shadowPc + (ulong)instr.SizeBytes;
            ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
            _shadowPc = pred.PredictedTaken && takenTarget != 0 ? takenTarget : fallThrough;
            return true;
        }

        int destArch = instr.DestinationRegister;
        if (destArch > 0 && !_rat.HasFree) return false;

        // A tainted operand feeding a load/store is never dereferenced — real reads/writes only
        // happen once every feeding source is known-good, avoiding garbage addresses and false
        // aliasing in the shadow store buffer. Exception: a Load whose own PC has a saturated
        // Vector Runahead stride-table entry can still vectorize off the RPT's predicted address
        // sequence (see TryVectorizeTaintedLoad) even with a tainted address operand — the
        // operand is typically tainted here not because it depends on the stalled load's value,
        // but because the front end has renamed several loop iterations ahead of a stalled
        // dispatch, leaving the loop-induction register's freshest rename stuck, unexecuted, in
        // the rename queue. The live value is unavailable but the RPT's own trained address
        // stream is not blocked by that at all.
        bool isMemOp = instr.Class is ToothClass.Load or ToothClass.Store;
        if (isMemOp && anyTainted) {
            if (instr.Class is ToothClass.Load && _enableVectorRunahead
                                               && TryVectorizeTaintedLoad(instr, destArch)) {
                _shadowPc += (ulong)instr.SizeBytes;
                return true;
            }

            if (destArch > 0) {
                (int newPhys, int oldPhys) = _rat.Rename(destArch);
                _prf.Write(newPhys, 0UL);
                _runaheadTainted.Add(newPhys);
                FreeShadowRename(newPhys, oldPhys);
            }

            _shadowPc += (ulong)instr.SizeBytes;
            return true;
        }

        IRegisterFile regs = State.IntegerRegisters;
        ulong save0 = srcs.Count > 0 ? regs.Read(srcs[0]) : 0;
        ulong save1 = srcs.Count > 1 ? regs.Read(srcs[1]) : 0;
        ulong save2 = srcs.Count > 2 ? regs.Read(srcs[2]) : 0;
        if (srcs.Count > 0) regs.Write(srcs[0], Tainted(p0) ? 0UL : _prf.Read(p0));
        if (srcs.Count > 1) regs.Write(srcs[1], Tainted(p1) ? 0UL : _prf.Read(p1));
        if (srcs.Count > 2) regs.Write(srcs[2], Tainted(p2) ? 0UL : _prf.Read(p2));

        var mem = new RunaheadMemory(DLayers.Accessor, _runaheadStoreBuffer);
        ExecuteResult er;
        try { er = _executor.Execute(instr, State, mem); }
        catch (AccessViolationException) {
            if (srcs.Count > 0) regs.Write(srcs[0], save0);
            if (srcs.Count > 1) regs.Write(srcs[1], save1);
            if (srcs.Count > 2) regs.Write(srcs[2], save2);
            return false;
        }

        if (srcs.Count > 0) regs.Write(srcs[0], save0);
        if (srcs.Count > 1) regs.Write(srcs[1], save1);
        if (srcs.Count > 2) regs.Write(srcs[2], save2);

        if (er.HasTrap) return false; // a shadow instruction that would fault ends the episode

        if (destArch > 0) {
            (int newPhys, int oldPhys) = _rat.Rename(destArch);
            _prf.Write(newPhys, er.RegisterResult.HasValue ? er.RegisterResult.Value : 0UL);
            if (anyTainted)
                _runaheadTainted.Add(newPhys);
            else if (_enableVectorRunahead) TryVectorizeShadowStep(instr, srcs, newPhys, p0, p1, mem, er);
            FreeShadowRename(newPhys, oldPhys);
        }

        _shadowPc += (ulong)instr.SizeBytes;
        return true;

        bool Tainted(int phys) => phys >= 0 && _runaheadTainted.Contains(phys);
    }

    /// <summary>
    ///     RPT-driven bypass (Naithani et al., ISCA 2021): a chain-origin load whose address
    ///     operand is tainted can still vectorize if its own PC has a saturated stride-table
    ///     entry — the N predicted addresses come straight from the trained
    ///     <see cref="VrStrideEntry.LastAddr" />/<see cref="VrStrideEntry.Stride" /> sequence
    ///     rather than from re-deriving the unavailable live operand value. This is what lets
    ///     Vector Runahead chase a simple strided loop-induction address even when the front end
    ///     has renamed several iterations ahead of a stalled dispatch (see the taint comment at
    ///     the call site) — the RPT's own address stream is independent of that backlog.
    /// </summary>
    private bool TryVectorizeTaintedLoad(ITooth instr, int destArch) {
        if (destArch <= 0 || !_rat.HasFree) return false;

        ref VrStrideEntry e = ref _vrStrideTable[(int)((_shadowPc >> 2) & (uint)(_vrStrideTable.Length - 1))];
        if (!e.Initialized || e.Confidence < 3 || e.Stride == 0) return false;

        if (!_runaheadChainActive) {
            if (_runaheadCappedOrigins.Contains(_shadowPc)) return false; // already used up its unroll budget
            _runaheadChainActive = true;
            _runaheadChainOrigin = _shadowPc;
            _runaheadChainTerminator = e.Terminator;
            _runaheadChainRound = 0;
            _runaheadRoundBaseAddr = e.LastAddr;
        }

        // Vector unrolling (Naithani et al., ISCA 2021 §III-G): each round's lanes are computed
        // from _runaheadRoundBaseAddr, which this method itself advances by N*Stride afterwards —
        // not from e.LastAddr directly, which never changes mid-episode (it is trained only from
        // the real demand-load stream). Without this, a second round through the same tainted
        // chain-origin load would re-fetch the exact same N addresses as the first.
        // Vector pipelining (§III-G, "P overlapped in-flight rounds"): pack PipelineRoundsThisVisit()
        // rounds into this single visit's lane array (width N×rounds instead of N) — the paper's
        // decoupling of round-issue rate from the shadow PC's loop-body walk cadence.
        var mem = new RunaheadMemory(DLayers.Accessor, _runaheadStoreBuffer);
        int width = _runaheadVectorWidth * PipelineRoundsThisVisit();
        var lanes = new ulong[width];
        int bytes = instr.MemoryAccessBytes;
        for (var i = 0; i < width; i++) {
            var laneAddr = (ulong)((long)_runaheadRoundBaseAddr + (i + 1) * e.Stride);
            try { lanes[i] = mem.Read(laneAddr, bytes); }
            catch (AccessViolationException) { lanes[i] = 0UL; }
        }

        _runaheadRoundBaseAddr = (ulong)((long)_runaheadRoundBaseAddr + width * e.Stride);

        (int newPhys, int oldPhys) = _rat.Rename(destArch);
        _prf.Write(newPhys, lanes[0]);
        _runaheadVectorLanes[newPhys] = lanes;
        FreeShadowRename(newPhys, oldPhys);
        _runaheadInstrCount += width - 1;
        _runaheadInstructionsCounter?.IncrementBy(width - 1);
        _runaheadVectorLaneAccessesCounter?.IncrementBy(width);
        _runaheadVectorChainsCounter?.Increment();
        return true;
    }

    /// <summary>
    ///     Vector Runahead (Naithani et al., ISCA 2021): after <see cref="TryShadowStep" /> has
    ///     executed a load or dependent-arithmetic instruction's scalar (lane-0) body for real,
    ///     check whether it should become — or continue — a vectorized N-wide chain, and if so
    ///     replicate the remaining lanes by re-issuing the same scalar body with different
    ///     per-lane operand/address values. Only called on an untainted destination (taint, the
    ///     paper's "invalid-bit", always suppresses the "vectorize-bit" — see the plan's
    ///     "Tainted-origin never vectorizes" test). Pure shadow-lane bookkeeping: never touches
    ///     the real VRF (see the "no real VRF touched" design decision in the Vector Runahead
    ///     plan) and never allocates more physical registers than the scalar path already does.
    /// </summary>
    private void TryVectorizeShadowStep(
        ITooth instr,
        IReadOnlyList<int> srcs,
        int newPhys,
        int p0,
        int p1,
        RunaheadMemory mem,
        ExecuteResult er
    ) {
        if (instr.Class is ToothClass.Load) {
            if (p0 >= 0 && _runaheadVectorLanes.TryGetValue(p0, out ulong[]? srcLanes)) {
                // Propagation: dependent/indirect load through an already-vectorized pointer
                // chain (e.g. load-of-a-pointer, then load-through-that-pointer).
                VectorizeByReplay(instr, srcs, 0, srcLanes, newPhys, mem, er);
                return;
            }

            // Chain origin: this load's own PC has a saturated, non-zero stride in the table
            // trained from the real demand-load stream (UpdateVrStrideTable) — vectorize N-wide
            // from here using the stride directly, no re-execution needed.
            ref VrStrideEntry e = ref _vrStrideTable[(int)((_shadowPc >> 2) & (uint)(_vrStrideTable.Length - 1))];
            if (!e.Initialized || e.Confidence < 3 || e.Stride == 0 || !mem.HasRead) return;

            bool firstVisit = !_runaheadChainActive;
            if (firstVisit) {
                if (_runaheadCappedOrigins.Contains(_shadowPc)) return; // already used up its unroll budget
                _runaheadChainActive = true;
                _runaheadChainOrigin = _shadowPc;
                _runaheadChainTerminator = e.Terminator;
                _runaheadChainRound = 0;
                _runaheadRoundBaseAddr = 0; // established below, from this visit's real read
            }

            // Vector pipelining (§III-G): pack PipelineRoundsThisVisit() rounds into this single
            // visit's lane array (width N×rounds) instead of one N-wide round per loop-body walk.
            //
            // Round base tracking: only the first visit's lane 0 can use the real re-executed
            // value (mem.LastReadAddress) — the shadow lane's real register state advances just
            // one iteration per loop-body walk, so by a second visit (forced when P < U) that
            // value is stale near-term data, not the far-future round _runaheadRoundBaseAddr
            // already tracks from the first visit's advance below. So every later visit derives
            // its whole lane array — including lane 0 — from the tracked base instead, mirroring
            // TryVectorizeTaintedLoad exactly (this path previously diverged from that one for no
            // principled reason). Safe to differ from the real executed value because lanes[0] is
            // never consumed by VectorizeByReplay's propagation (only lanes[1..] are, substituting
            // into a fresh execution) and the PRF already holds the real value separately (written
            // by TryShadowStep's caller before this method runs) — lanes[0] here only needs to be
            // internally consistent with lanes[1..]'s stride-offset scheme. This closes a real
            // arithmetic divergence between the two sibling paths, but empirically (several
            // synthetic strided-loop configs, cycle/dcache_misses/cache-residency probes) it
            // produced no measurable behavioral difference in this model — real scalar demand
            // coverage and shadow-lane stepping already reach these addresses independently, so
            // the overlap this fixes was never the actual bottleneck. Kept for correctness and
            // consistency with the tainted path, not for a measured performance gain.
            int width = _runaheadVectorWidth * PipelineRoundsThisVisit();
            var lanes = new ulong[width];
            ulong roundBase;
            var startLane = 0;
            if (firstVisit) {
                roundBase = mem.LastReadAddress;
                lanes[0] = er.RegisterResult.HasValue ? er.RegisterResult.Value : 0UL;
                startLane = 1;
            }
            else { roundBase = _runaheadRoundBaseAddr; }

            for (int i = startLane; i < width; i++) {
                var laneAddr = (ulong)((long)roundBase + i * e.Stride);
                try { lanes[i] = mem.Read(laneAddr, mem.LastReadBytes); }
                catch (AccessViolationException) { lanes[i] = 0UL; }
            }

            _runaheadRoundBaseAddr = (ulong)((long)roundBase + width * e.Stride);
            _runaheadVectorLanes[newPhys] = lanes;
            _runaheadInstrCount += width - 1;
            _runaheadInstructionsCounter?.IncrementBy(width - 1);
            _runaheadVectorLaneAccessesCounter?.IncrementBy(width);
            _runaheadVectorChainsCounter?.Increment();
            return;
        }

        if (instr.Class is not (ToothClass.IntegerAlu or ToothClass.IntegerMulDiv)) return;

        // Propagate vectorization through dependent address arithmetic — whichever of the first
        // two sources is already carrying per-lane values drives the replay.
        if (p0 >= 0 && _runaheadVectorLanes.TryGetValue(p0, out ulong[]? aluLanes0))
            VectorizeByReplay(instr, srcs, 0, aluLanes0, newPhys, mem, er);
        else if (p1 >= 0 && _runaheadVectorLanes.TryGetValue(p1, out ulong[]? aluLanes1))
            VectorizeByReplay(instr, srcs, 1, aluLanes1, newPhys, mem, er);
    }

    /// <summary>
    ///     Shared lane-replication core for both indirect-load propagation and dependent-
    ///     arithmetic propagation: re-invokes the scalar shadow body once per extra lane with
    ///     <paramref name="srcIndex" /> temporarily bound to that lane's value from
    ///     <paramref name="srcLanes" />, exactly as <see cref="TryShadowStep" /> already does for
    ///     its single real (lane-0) invocation. Width-generic (<paramref name="srcLanes" />
    ///     <c>.Length</c>, not the fixed <see cref="_runaheadVectorWidth" />) so propagation
    ///     automatically carries whatever width the origin load produced — N lanes normally, or
    ///     N×<see cref="PipelineRoundsThisVisit" /> when vector pipelining packed multiple rounds
    ///     into that origin visit (Naithani et al., ISCA 2021 §III-G).
    /// </summary>
    private void VectorizeByReplay(
        ITooth instr,
        IReadOnlyList<int> srcs,
        int srcIndex,
        ulong[] srcLanes,
        int newPhys,
        RunaheadMemory mem,
        ExecuteResult lane0Result
    ) {
        int width = srcLanes.Length;
        var lanes = new ulong[width];
        lanes[0] = lane0Result.RegisterResult.HasValue ? lane0Result.RegisterResult.Value : 0UL;

        IRegisterFile regs = State.IntegerRegisters;
        int srcArch = srcs[srcIndex];
        ulong saved = regs.Read(srcArch);
        for (var i = 1; i < width; i++) {
            regs.Write(srcArch, srcLanes[i]);
            try {
                ExecuteResult laneResult = _executor.Execute(instr, State, mem);
                lanes[i] = laneResult.RegisterResult.HasValue ? laneResult.RegisterResult.Value : 0UL;
            }
            catch (AccessViolationException) { lanes[i] = 0UL; }
        }

        regs.Write(srcArch, saved);

        _runaheadVectorLanes[newPhys] = lanes;
        _runaheadInstrCount += width - 1;
        _runaheadInstructionsCounter?.IncrementBy(width - 1);
        _runaheadVectorLaneAccessesCounter?.IncrementBy(width);
    }

    // ── Flush (misprediction / trap) ───────────────────────────────────────────

    private void StepFlush() {
        _flushesCounter.Increment();

        if (PEventLog is not null) {
            foreach ((_, RobEntry entry) in _rob.InOrder()) {
                if (entry.InstrId != 0)
                    PEventLog.Record(entry.InstrId, entry.Pc, _cyclesCounter.Value, PEventKind.Flush);
                if (entry.FusedSecondInstrId is { } secondId)
                    PEventLog.Record(
                        secondId, entry.Instruction?.BranchComponent.Pc ?? entry.Pc, _cyclesCounter.Value,
                        PEventKind.Flush
                    );
            }

            foreach (RenameEntry ri in _renameQueue.Where(ri => ri.InstrId != 0)) {
                PEventLog.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Flush);
                if (ri.FusedSecondInstrId is { } secondId)
                    PEventLog.Record(
                        secondId, ri.Decoded?.BranchComponent.Pc ?? ri.Pc, _cyclesCounter.Value, PEventKind.Flush
                    );
            }
        }

        // Rename queue instructions are younger than any ROB entry. Undo them newest-first
        // (queue tail = newest) before undoing the ROB so RAT restore order is correct.
        RenameEntry[] renameArr = _renameQueue.ToArray();
        for (int i = renameArr.Length - 1; i >= 0; i--) {
            RenameEntry ri = renameArr[i];
            if (ri is { ArchDest: > 0, PhysDest: >= 0, }) {
                _rat.RestoreMapping(ri.ArchDest, ri.PrevPhysDest);
                _rat.FreePhysical(ri.PhysDest);
            }
        }

        // Walk ROB youngest-to-oldest, restoring the RAT to committed state.
        foreach ((_, RobEntry entry) in _rob.InOrder().Reverse())
            if (entry is { ArchDestination: > 0, PhysDestination: >= 0, }) {
                _rat.RestoreMapping(entry.ArchDestination, entry.PrevPhysDestination);
                _rat.FreePhysical(entry.PhysDestination);
            }

        // Apply the trap/return instruction's own rollback last: it's the oldest rename in the
        // chain, and any younger entry just undone above may have recorded its PrevPhysDestination
        // as this instruction's (abandoned) PhysDestination — restoring this one first would get
        // immediately overwritten by those.
        if (_pendingRollbackArch >= 0) {
            _rat.RestoreMapping(_pendingRollbackArch, _pendingRollbackPrevPhys);
            _rat.FreePhysical(_pendingRollbackAbandonedPhys);
            _pendingRollbackArch = -1;
        }

        _rob.Flush();
        _vpTracker?.Clear();
        _pendingUsls.Clear();
        _pendingUslLatency.Clear();
        _llcSb.Clear();
        _pendingTaintedMispredicts.Clear();
        foreach (IssueQueue iq in _iqs) iq.Flush();
        _lq.Flush();
        _sq.Flush();
        _decodeQueue.Clear();
        _renameQueue.Clear();
        _execBuffer.Clear();
        _inFlight.Clear();
        _mshrUsed = 0;
        _cdbBuffer.Clear();

        // Restore the speculative RAS to the architectural shadow, discarding any
        // wrong-path push/pop corruption accumulated by the squashed instructions.
        _ras.CopyFrom(_committedRas);

        // Same for the branch predictor's speculative global history: Update has already
        // applied the redirecting branch's true outcome to the committed shadow, so this
        // resumes fetch with correct history and drops wrong-path history bits.
        _predictor.RecoverSpeculativeHistory();
        _valuePredictor?.RecoverSpeculativeHistory();

        _fetchPc = _flushTarget;
        _flushPending = false;
        _squashPending = false; // a full flush supersedes any pending execute-time partial squash
        _fetchFaulted = false;
        _fdip?.Flush(_flushTarget);
    }

    /// <summary>
    ///     Execute-time partial squash: a branch resolved mispredicted before reaching the ROB head.
    ///     Redirects fetch now (as gem5 O3CPU's iew does) instead of waiting for commit, discarding
    ///     only the instructions younger than the redirecting branch while the branch and every older
    ///     in-flight instruction stay live and commit normally.
    /// </summary>
    private void StepPartialSquash() {
        _flushesCounter.Increment();
        _branchMissCounter.Increment(); // a partial squash is one branch misprediction, resolved at execute
        ulong bId = _squashInstrId;
        RobEntry b = FindRobByInstrId(bId);
        PostBpredWindow(b);

        if (PEventLog is not null) {
            foreach ((_, RobEntry entry) in _rob.InOrder())
                if (entry.InstrId > bId && entry.InstrId != 0) {
                    PEventLog.Record(entry.InstrId, entry.Pc, _cyclesCounter.Value, PEventKind.Flush);
                    if (entry.FusedSecondInstrId is { } secondId)
                        PEventLog.Record(
                            secondId, entry.Instruction?.BranchComponent.Pc ?? entry.Pc, _cyclesCounter.Value,
                            PEventKind.Flush
                        );
                }

            foreach (RenameEntry ri in _renameQueue)
                if (ri.InstrId != 0) {
                    PEventLog.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Flush);
                    if (ri.FusedSecondInstrId is { } secondId)
                        PEventLog.Record(
                            secondId, ri.Decoded?.BranchComponent.Pc ?? ri.Pc, _cyclesCounter.Value, PEventKind.Flush
                        );
                }

            foreach (FetchedInstr fi in _decodeQueue)
                if (fi.InstrId != 0)
                    PEventLog.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Flush);
        }

        // ── Recover speculative predictor history, youngest-to-oldest ──────────────────
        // Global history is a single register restored once (step d) from B's checkpoint; here we
        // rewind each younger branch's per-PC local-history entry so same-PC entries unwind exactly.
        // Non-branch checkpoints carry LocalIdx = -1, so RestoreLocalEntry is a no-op for them.
        FetchedInstr[] decodeArr = _decodeQueue.ToArray(); // youngest of all: fetched, not renamed
        for (int i = decodeArr.Length - 1; i >= 0; i--) _predictor.RestoreLocalEntry(decodeArr[i].HistCheckpoint);

        // Rename queue: younger than any ROB entry. Walk back the RAT (undo the rename) and rewind
        // local history, newest-first so the RAT restore order is correct.
        RenameEntry[] renameArr = _renameQueue.ToArray();
        for (int i = renameArr.Length - 1; i >= 0; i--) {
            RenameEntry ri = renameArr[i];
            if (ri is { ArchDest: > 0, PhysDest: >= 0, }) {
                _rat.RestoreMapping(ri.ArchDest, ri.PrevPhysDest);
                _rat.FreePhysical(ri.PhysDest);
            }

            _predictor.RestoreLocalEntry(ri.HistCheckpoint);
        }

        // ROB entries younger than B, youngest-to-oldest: RAT walk-back + local-history rewind.
        foreach ((_, RobEntry entry) in _rob.InOrder().Reverse()) {
            if (entry.InstrId <= bId) break; // reached B or older — keep these
            if (entry is { ArchDestination: > 0, PhysDestination: >= 0, }) {
                _rat.RestoreMapping(entry.ArchDestination, entry.PrevPhysDestination);
                _rat.FreePhysical(entry.PhysDestination);
            }

            _predictor.RestoreLocalEntry(entry.HistCheckpoint);
        }

        // (d) Restore global history (+ B's own local entry) to as-of-B and fold B's true direction.
        // b.Instruction.BranchComponent.Pc, not b.Pc: for a macro-fused B (see IMacroFuser),
        // b.Pc is the compare half, but local-history recovery must index the same per-PC
        // slot the branch's own Fetch-time capture used.
        _predictor.RestoreHistory(b.HistCheckpoint, b.Instruction!.BranchComponent.Pc, _squashTaken);
        _valuePredictor?.RestoreHistory(b.VpHistCheckpoint, _squashTaken);

        // ── Discard the younger structures (B and everything older survive) ─────────────
        _rob.TruncateYoungerThan(bId);
        _vpTracker?.TruncateYoungerThan(bId);
        _pendingUsls.RemoveAll(p => p.InstrId > bId);
        // _pendingUslLatency needs no truncation here: an entry only lands there once its own
        // visibility point already cleared, and once safe an instruction stays safe (every branch
        // older than it is resolved for good) — so a partial squash can never retroactively catch
        // an entry already draining its post-fire miss latency (see that field's docs).
        _llcSb.RemoveAll(e => e.InstrId > bId);
        _pendingTaintedMispredicts.RemoveAll(id => id > bId);
        foreach (IssueQueue iq in _iqs) iq.SquashYoungerThan(bId);
        _lq.TruncateYoungerThan(bId);
        _sq.TruncateYoungerThan(bId);
        _decodeQueue.Clear();
        _renameQueue.Clear();
        _execBuffer.RemoveAll(e => e.InstrId > bId);
        for (int i = _inFlight.Count - 1; i >= 0; i--)
            if (_inFlight[i].Result.InstrId > bId) {
                if (_inFlight[i].HoldsMshr) _mshrUsed--;
                _inFlight.RemoveAt(i);
            }
        // _cdbBuffer was already drained by StepComplete this cycle.

        // ── Reconstruct the speculative RAS: committed shadow + replay of the surviving
        //    (head..B) in-flight calls/returns, discarding wrong-path push/pop corruption. ──
        _ras.CopyFrom(_committedRas);
        foreach ((_, RobEntry entry) in _rob.InOrder())
            if (entry.Instruction is { Class: ToothClass.Branch, } ins) {
                FetchHint hint = _decoder.GetFetchHint(entry.Pc, ins.RawEncoding);
                if (hint.IsCall) _ras.Push(entry.Pc + (ulong)ins.SizeBytes);
                if (hint.IsReturn) _ras.TryPop(out _);
            }

        // Prevent a second (commit-time) flush when B retires: its prediction now matches its
        // resolved target. Training (_predictor.Update) and the committed RAS/GHR shadows still
        // advance normally at B's commit.
        b.PredictedNextPc = b.ResolvedNextPc.Value;

        // B itself was deliberately left "unresolved" in the shared visibility tracker (see
        // StepComplete) since it mispredicted; now that its own squash has actually taken effect
        // (TruncateYoungerThan above discards everything younger but keeps B, since its InstrId
        // equals, not exceeds, the truncation threshold), resolve it for real so younger
        // surviving/future instructions can become safe.
        _vpTracker?.OnBranchResolved(bId);

        // Critical-path prediction ED edge (Table 2): the very next dispatched instruction's
        // D-source is B's E-node.
        if (_criticalityPredictor is not null) {
            _pendingRedirect = true;
            _pendingRedirectInstrId = bId;
        }

        _fetchPc = _squashTarget;
        _fetchFaulted = false;
        _squashPending = false;
        _fdip?.Flush(_squashTarget);
    }

    private RobEntry FindRobByInstrId(ulong instrId) {
        foreach ((_, RobEntry e) in _rob.InOrder())
            if (e.InstrId == instrId)
                return e;
        throw new InvalidOperationException($"ROB entry for InstrId {instrId} not found during partial squash.");
    }

    /// <summary>
    ///     True when an in-flight instruction older than <paramref name="instrId" /> will redirect or
    ///     halt the machine at commit (a halt, a trap, or a return-from-trap). Such an older entry makes
    ///     every younger instruction wrong-path, so an execute-time squash on a younger branch would act
    ///     on a doomed path the commit-time model never reaches.
    /// </summary>
    private bool AnyOlderHaltOrTrap(ulong instrId) {
        foreach ((_, RobEntry e) in _rob.InOrder()) {
            if (e.InstrId >= instrId) break;
            if (e.IsHalt || e.HasTrap || e.IsReturnFromTrap) return true;
        }

        return false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ExecResult ExecuteOne(IssuedInstr issued, ulong loadSeqNo) {
        IReadOnlyList<int> srcs = issued.Instr.SourceRegisters;
        IRegisterFile regs = State.IntegerRegisters;

        int s0 = srcs.Count > 0 ? srcs[0] : -1;
        int s1 = srcs.Count > 1 ? srcs[1] : -1;
        int s2 = srcs.Count > 2 ? srcs[2] : -1;

        // Save and inject PRF values so the executor sees the correct operands.
        ulong save0 = s0 >= 0 ? regs.Read(s0) : 0;
        ulong save1 = s1 >= 0 ? regs.Read(s1) : 0;
        ulong save2 = s2 >= 0 ? regs.Read(s2) : 0;
        if (s0 >= 0) regs.Write(s0, issued.Src1);
        if (s1 >= 0) regs.Write(s1, issued.Src2);
        if (s2 >= 0) regs.Write(s2, issued.Src3);

        // Vector and UVE ops are head-serialized (non-speculative) and bypass
        // CapturingMemory so their element writes land in real memory.
        _capMem.Reset();
        bool isVec = issued.Instr.Class == ToothClass.Vector;
        bool isUve = issued.Instr.Class == ToothClass.Uve;
        // System (ECALL/CSR) ops are likewise head-serialized (see the Dispatch-stage issue gate:
        // `case ToothClass.System when rs.RobIndex != _rob.HeadIndex`) — never speculative, so
        // their memory accesses must be real and immediate too, not deferred through
        // CapturingMemory. This matters because ECALL routes to LinuxSyscallEmulator, which reads
        // AND writes guest memory directly (e.g. SYS_fstat/SYS_clock_gettime/SYS_getrandom fill a
        // struct via IMemory.Write): through CapturingMemory, a write is only ever *captured*
        // (HasWrite/WriteAddress/...), never applied to backing memory, until a Store Queue entry
        // later drains it at commit — but System-class instructions never get an SQ entry (no
        // ordinary store to disambiguate), so the write would be silently lost, and reading
        // ExecResult.HasLoadAccess/HasStoreCapture off CapturingMemory's flags for a class with no
        // LQ/SQ entry at all previously corrupted `_lq`/`_sq` indexing outright (IndexOutOfRange —
        // found running a real musl binary's startup ECALLs through OoOe for the first time).
        bool isSystem = issued.Instr.Class == ToothClass.System;

        // InvisiSpec (Yan et al., MICRO 2018): every scalar load speculatively peeks its data
        // (no cache-state mutation) rather than doing a real access here — the real access is
        // deferred to this load's own visibility point (see StepUslResolution). Atomics are
        // exempt: they already issue only at the ROB head (see the Dispatch-stage issue gate),
        // so by the time one executes it is definitionally non-speculative already.
        bool isUsl = _enableInvisiSpec && issued.Instr.Class == ToothClass.Load;
        _capMem.PeekMode = isUsl;

        // For UVE ops: inject stream element values into u-register lanes before the executor runs,
        // and sync exhaustion state for branch ops. Vector-mode streams deliver up to VL lanes.
        if (isUve && State.UveScalars is { } uvs) {
            // Buffer for vector lane injection; max VLEN=128 bits = 4 float32 lanes.
            Span<uint> laneBuf = stackalloc uint[16];
            foreach (int uid in issued.Instr.UveStreamSources) {
                if (uid < 0 || uid >= StreamingEngine.MaxStreams || !StreamingEngine.IsActive(uid)
                 || !StreamingEngine.HasElement(uid))
                    continue;
                bool merging = StreamingEngine.GetMergingPredication(uid);
                if (StreamingEngine.IsVectorMode(uid)) {
                    int ew = StreamingEngine.GetElementBytes(uid);
                    int maxLanes = 16 / Math.Max(1, ew); // VLEN=128 bits = 16 bytes
                    int vl = uvs.VectorLength > 0 ? Math.Min(uvs.VectorLength, maxLanes) : maxLanes;
                    for (var i = 0; i < vl && StreamingEngine.HasElement(uid); i++)
                        laneBuf[i] = (uint)StreamingEngine.Consume(uid);
                    uvs.SetVectorRaw(uid, laneBuf[..vl], vl, merging);
                    uvs.SetRegElemBytes(uid, ew);
                }
                else {
                    uvs.SetScalarRaw(uid, (uint)StreamingEngine.Consume(uid), merging);
                    uvs.SetRegElemBytes(uid, StreamingEngine.GetElementBytes(uid));
                }
            }

            foreach (int uid in issued.Instr.UveBranchStreams)
                if (uid >= 0) {
                    // Store streams bypass StreamingEngine entirely (the ISA layer tracks their own
                    // cursor/exhaustion) — querying the engine for one always sees "never configured"
                    // (Active=false), which the OR below collapses to "done" unconditionally,
                    // terminating so.b.[n]c loops after their first iteration.
                    bool done = uvs.IsStoreStream(uid)
                        ? uvs.StoreStreamExhausted(uid)
                        : !StreamingEngine.IsActive(uid) || StreamingEngine.IsExhausted(uid);
                    uvs.SetStreamDone(uid, done);
                }

            // so.b.ndc.D encodes the dimension as funct3 = D-1, counting from the
            // OUTERMOST dimension (Spike: EODTable.at(funct3), dimensions[0] = outermost).
            // The engine indexes dimensions innermost-first, so remap before querying.
            foreach ((int uid, int dim) in issued.Instr.UveDimBranchSources)
                if (uid >= 0) {
                    int engineDim = StreamingEngine.DimensionCount(uid) - 1 - dim;
                    uvs.SetDimDone(uid, dim, StreamingEngine.IsDimPassComplete(uid, engineDim));
                }
        }

        IMemory mem = isVec || isUve || isSystem ? DLayers.Accessor : _capMem;
        mem.SetRequestPc(issued.Pc);
        ExecuteResult er;
        try { er = _executor.Execute(issued.Instr, State, mem); }
        catch (AccessViolationException) {
            // Speculative load/store hit an out-of-bounds address. Restore registers
            // and return a trap result so the ROB can squash it on misprediction or
            // take the fault if it reaches the head while still on the correct path.
            if (s0 >= 0) regs.Write(s0, save0);
            if (s1 >= 0) regs.Write(s1, save1);
            if (s2 >= 0) regs.Write(s2, save2);
            int cause = _capMem.HasRead ? TrapCause.LoadAccessFault : TrapCause.StoreAccessFault;
            ulong faultAddr = _capMem.HasRead ? _capMem.ReadAddress : _capMem.WriteAddress;
            return new ExecResult(
                issued.RobIdx, issued.PhysDest,
                default((ulong Value, bool HasValue)), default((ulong Value, bool HasValue)),
                new TrapInfo(cause, faultAddr, issued.Pc),
                false, null, false, 0, 0, 0, false, 0, 0, false,
                InstrId: issued.InstrId
            );
        }

        // Apply SideEffect immediately for head-serialized ops (VRF/UveState writes
        // must be visible to the next head instruction in the same cycle).
        if (isVec || isUve) er.SideEffect?.Invoke(State);

        // Configure streaming engine if the instruction set up a load stream.
        if (er.StreamConfig is { } sc) StreamingEngine.Configure(sc.StreamId, sc.Descriptor);

        // Restore arch state to committed values.
        if (s0 >= 0) regs.Write(s0, save0);
        if (s1 >= 0) regs.Write(s1, save1);
        if (s2 >= 0) regs.Write(s2, save2);

        (ulong Value, bool HasValue) resolvedNextPc = issued.Instr.Class switch {
            ToothClass.Branch =>
                (er.BranchTarget ?? issued.Pc + (ulong)issued.Instr.SizeBytes, true),
            ToothClass.ConditionalBranch =>
                er.BranchTaken
                    ? (er.BranchTarget ?? issued.Pc + (ulong)issued.Instr.SizeBytes, true)
                    : (issued.Pc + (ulong)issued.Instr.SizeBytes, true),
            // UVE branch ops (so.b.nc) resolve control flow like a conditional branch.
            ToothClass.Uve when er.BranchTarget.HasValue =>
                (er.BranchTarget.Value, true),
            _ => default((ulong, bool)),
        };

        // Notify vector-aware predictor of vector instruction or taken backward branch.
        // issued.Instr.BranchComponent.Pc, not issued.Pc: for a macro-fused entry (see
        // IMacroFuser), issued.Pc is the compare half — the loop-iteration-estimation table
        // is keyed on the branch's own real address, same as the training sites above.
        if (_predictor is IVectorAwareBranchPredictor vbp) {
            if (isVec)
                vbp.NotifyVectorInstruction(issued.Pc);
            else if (issued.Instr.Class == ToothClass.ConditionalBranch && resolvedNextPc.HasValue) {
                ulong branchPc = issued.Instr.BranchComponent.Pc;
                if (resolvedNextPc.Value < branchPc)
                    vbp.NotifyLoopBranchExecute(branchPc, resolvedNextPc.Value, issued.Src1, issued.Src2);
            }
        }

        // For loads: try to forward from an older executed store to the same address.
        // If a forwarding match is found, override the (potentially stale) memory read.
        (ulong Value, bool HasValue) regValue = er.RegisterResult;
        var loadForwarded = false;
        if (_capMem.HasRead) {
            (ulong fwd, bool hasFwd) = TryForwardFromStore(loadSeqNo, _capMem.ReadAddress, _capMem.ReadBytes);
            if (hasFwd) {
                // Apply sign extension for signed loads (lb → 1 byte, lh → 2 bytes).
                // TryForwardFromStore returns the raw masked store value; signed load
                // semantics require extending the sign bit into the upper bits.
                int signExtBytes = issued.Instr.LoadSignExtendBytes;
                if (signExtBytes > 0) {
                    ulong signBit = 1UL << (signExtBytes * 8 - 1);
                    if ((fwd & signBit) != 0) fwd |= ~((1UL << (signExtBytes * 8)) - 1);
                }

                if (issued.Instr.NanBoxLoadResult) fwd = 0xFFFFFFFF00000000UL | (fwd & 0xFFFFFFFF);
                regValue = (fwd, true);
                loadForwarded = true;
            }
        }

        // InvisiSpec: queue this USL's deferred real access. TSO rule (Table 1): validate if an
        // older load or store→load fence was in the ROB at this exact moment (this instant, not
        // "ever" — the check must run here, at Execute, not later); otherwise a cheap exposure
        // suffices. The register value above is already final for CDB broadcast regardless —
        // only retirement (RobEntry.PendingUslAccess, checked in StepCommit) waits on the queue.
        if (isUsl && _capMem.HasRead) {
            bool needsValidation = HasPrecedingLoadOrFence(issued.RobIdx);
            _pendingUsls.Add(
                new PendingUsl(issued.RobIdx, issued.InstrId, _capMem.ReadAddress, _capMem.ReadBytes, needsValidation)
            );
            _rob.At(issued.RobIdx).PendingUslAccess = true;

            // LLC-SB (§VI-C): record the line this USL's speculative peek touched, so its own or a
            // later USL's deferred access to the same line can hit the buffer instead of missing.
            if (_enableInvisiSpecLlcSb && _llcSbCapacity > 0 && LlcBlockBytes is { } bb) {
                ulong lineBase = _capMem.ReadAddress & ~(ulong)(bb - 1);
                if (_llcSb.Count >= _llcSbCapacity) _llcSb.RemoveAt(0);
                _llcSb.Add((lineBase, issued.InstrId));
            }
        }

        // Notify value-aware predictor of the register value this instruction produced.
        if (_predictor is IValueAwareBp vabp
         && issued.Instr.DestinationRegister >= 0
         && regValue.HasValue)
            vabp.NotifyRegisterResult(
                issued.Pc, issued.Instr.DestinationRegister, regValue.Value,
                issued.Instr.Class == ToothClass.Load
            );

        return new ExecResult(
            issued.RobIdx, issued.PhysDest,
            regValue, resolvedNextPc, er.Trap,
            er.IsReturnFromTrap, er.ReturnPrivilege,
            _capMem.HasWrite, _capMem.WriteAddress, _capMem.WriteValue, _capMem.WriteBytes,
            _capMem.HasRead, _capMem.ReadAddress, _capMem.ReadBytes, loadForwarded,
            er.RequestHalt, er.RequestBlock, issued.InstrId,
            // Vec/UVE already applied their SideEffect immediately above; don't reapply at commit.
            isVec || isUve ? null : er.SideEffect,
            er.LatencyOverride ?? 0
        );
    }

    /// <summary>
    ///     Retires the LQ entry (for loads/atomics) and/or SQ entry (for stores/atomics)
    ///     corresponding to a retiring ROB entry. Must be called before <c>_rob.Retire()</c>
    ///     since the LQ/SQ indices are read from the entry's fields.
    /// </summary>
    private void RetireMemQueues(RobEntry head) {
        if (head.LqIdx >= 0) _lq.Retire();
        if (head.SqIdx >= 0) _sq.Retire();
    }

    /// <summary>
    ///     Commits the destination register of a ROB entry: writes the PRF value
    ///     to the arch state, frees the old physical register, and advances State.Pc.
    /// </summary>
    private void CommitRegisters(RobEntry head) {
        head.SideEffect?.Invoke(State);

        // Secondary-destination sync: the SideEffect write above landed directly in
        // State.IntegerRegisters, bypassing the RAT/PRF entirely. HasPendingSecondaryDest's
        // WAW stall guarantees no younger instruction has re-renamed this architectural
        // register since dispatch, so the RAT's current mapping for it is still the one
        // live at (and before) this instruction — safe to push the fresh value into that
        // PRF slot so a later consumer's normal RAT-based read picks it up.
        int sd = head.Instruction?.SecondaryDestinationRegister ?? -1;
        if (sd >= 0) _prf.Write(_rat.Lookup(sd), State.IntegerRegisters.Read(sd));

        if (head is not { PhysDestination: >= 0, ArchDestination: > 0, }) return;
        ulong val = _prf.Read(head.PhysDestination);
        State.IntegerRegisters.Write(head.ArchDestination, val);
        if (head.PrevPhysDestination >= 0) _rat.FreePhysical(head.PrevPhysDestination);
        PEventLog?.RecordDestValue(head.InstrId, head.ArchDestination, val);
    }

    private void SetFlush(ulong target) {
        _flushPending = true;
        _flushTarget = target;
    }

    /// <summary>
    ///     Write a committed store to memory and optionally absorb the write-miss stall
    ///     into the write buffer so the pipeline doesn't freeze for it.
    ///     <para>
    ///         Because the D-cache is write-through / no-write-allocate, data reaches memory
    ///         the instant Write() returns — forwarding correctness is never at risk regardless
    ///         of whether the stall is absorbed or charged to the clock.
    ///     </para>
    /// </summary>
    private void CommitStore(ulong address, ulong value, int width) {
        DLayers.Accessor.Write(address, value, width);
        if (!_anyCache || _wbCapacity == 0 || _wbOccupied >= _wbCapacity) return;

        // Buffer has space: absorb the miss stall so the pipeline can keep running.
        long stalls = DLayers.ConsumeAllStalls();
        if (stalls <= 0) return; // cache hit — nothing to absorb

        for (var i = 0; i < _wbCapacity; i++) {
            if (_wbSlots[i] != 0) continue;
            _wbSlots[i] = (int)stalls;
            _wbOccupied++;
            _wbAbsorbedStallsCounter?.IncrementBy(stalls);
            return;
        }
    }

    /// <summary>
    ///     Tick down all active write-buffer miss countdowns. Called every cycle (including
    ///     halt/flush cycles) because the buffer holds committed state, not speculation.
    ///     Slots whose countdown reaches zero are freed (the write bus penalty has expired).
    /// </summary>
    private void StepWriteBuffer() {
        if (_wbOccupied == 0) return;
        for (var i = 0; i < _wbCapacity; i++) {
            if (_wbSlots[i] <= 0) continue;
            if (--_wbSlots[i] == 0) _wbOccupied--;
        }
    }

    // ── Memory hierarchy stat collection ──────────────────────────────────────

    // Advance the clock by n lump-sum stall cycles (instruction fetch, store commit).
    private void ChargeStallCycles(long n) {
        if (n <= 0) return;
        _cyclesCounter.IncrementBy(n);
        _tdTotalSlotsCounter.IncrementBy(n * _issueWidth);
        _stallsCounter.IncrementBy(n);
        _cacheMissStallsCounter?.IncrementBy(n);
        for (long i = 0; i < n; i++) State.OnCycle();
    }

    // Collects pending lump-sum stall cycles, split by side so TMA can attribute I-fetch
    // misses to the frontend and store-commit misses to the backend, and refreshes the
    // per-level cache/TLB hit-miss counters.
    private (long IStalls, long DStalls) DrainStalls() {
        long iStalls = ILayers.ConsumeAllStalls();
        long dStalls = DLayers.ConsumeAllStalls();

        // CPI stack: split the fetch-stall cycles across I-side hierarchy levels in
        // proportion to (new misses × miss latency) per level, into the provisional
        // (sFMT-local) counters. Posted at the flagged instruction's retirement.
        if (iStalls > 0) {
            long wL1 = ILayers.Cache is { } l1 ? (l1.Misses - _lastIMisses) * l1.MissLatency : 0;
            long wL2 = ILayers.L2Cache is { } l2 ? (l2.Misses - _lastIl2Misses) * l2.MissLatency :
                ILayers.L2Bdi is { } l2b ? (l2b.Misses - _lastIl2Misses) * l2b.MissLatency :
                ILayers.L2Ceaser is { } l2c ? (l2c.Misses - _lastIl2Misses) * l2c.MissLatency :
                ILayers.L2Scatter is { } l2s ? (l2s.Misses - _lastIl2Misses) * l2s.MissLatency : 0;
            long wL3 = ILayers.L3Cache is { } l3 ? (l3.Misses - _lastIl3Misses) * l3.MissLatency :
                ILayers.L3Bdi is { } l3b ? (l3b.Misses - _lastIl3Misses) * l3b.MissLatency :
                ILayers.L3Ceaser is { } l3c ? (l3c.Misses - _lastIl3Misses) * l3c.MissLatency :
                ILayers.L3Scatter is { } l3s ? (l3s.Misses - _lastIl3Misses) * l3s.MissLatency : 0;
            long wTlb = ILayers.Tlb is { } tlb ? (tlb.Misses - _lastITlbMisses) * tlb.MissLatency : 0;
            long wSum = wL1 + wL2 + wL3 + wTlb;
            if (wSum <= 0) { _cpiPendingL1I += iStalls; }
            else {
                _cpiPendingL2I += iStalls * wL2 / wSum;
                _cpiPendingL3I += iStalls * wL3 / wSum;
                _cpiPendingItlb += iStalls * wTlb / wSum;
                // L1 takes its share plus the integer-division remainder.
                _cpiPendingL1I += iStalls - iStalls * wL2 / wSum - iStalls * wL3 / wSum - iStalls * wTlb / wSum;
            }
        }

        UpdateCacheStat(ILayers.Cache, _icacheHitsCounter, _icacheMissesCounter, ref _lastIHits, ref _lastIMisses);
        UpdateCacheStat(
            ILayers.L2Cache, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L2Bdi, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L2Ceaser, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L2Scatter, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L3Cache, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
        );
        UpdateCacheStat(
            ILayers.L3Bdi, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
        );
        UpdateCacheStat(
            ILayers.L3Ceaser, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
        );
        UpdateCacheStat(
            ILayers.L3Scatter, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
        );
        UpdateCacheStat(DLayers.Cache, _dcacheHitsCounter, _dcacheMissesCounter, ref _lastDHits, ref _lastDMisses);
        if (_dcachePrefetchesCounter is not null && DLayers.Cache is not null) {
            _dcachePrefetchesCounter.IncrementBy(DLayers.Cache.Prefetches - _lastDPrefetches);
            _lastDPrefetches = DLayers.Cache.Prefetches;
            if (_dcacheLatePrefetchHitsCounter is not null) {
                _dcacheLatePrefetchHitsCounter.IncrementBy(DLayers.Cache.LatePrefetchHits - _lastDLatePrefetchHits);
                _lastDLatePrefetchHits = DLayers.Cache.LatePrefetchHits;
            }
        }

        UpdateCacheStat(
            DLayers.L2Cache, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDl2Hits, ref _lastDl2Misses
        );
        UpdateCacheStat(
            DLayers.L2Bdi, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDl2Hits, ref _lastDl2Misses
        );
        UpdateCacheStat(
            DLayers.L2Ceaser, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDl2Hits, ref _lastDl2Misses
        );
        UpdateCacheStat(
            DLayers.L2Scatter, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDl2Hits, ref _lastDl2Misses
        );
        UpdateCacheStat(
            DLayers.L3Cache, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateCacheStat(
            DLayers.L3Bdi, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateCacheStat(
            DLayers.L3Ceaser, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateCacheStat(
            DLayers.L3Scatter, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateTlbStat(ILayers.Tlb, _itlbHitsCounter, _itlbMissesCounter, ref _lastITlbHits, ref _lastITlbMisses);
        UpdateTlbStat(DLayers.Tlb, _dtlbHitsCounter, _dtlbMissesCounter, ref _lastDTlbHits, ref _lastDTlbMisses);
        return (iStalls, dStalls);
    }

    // ── CPI-stack helpers (Eyerman et al., ASPLOS 2006) ───────────────────────

    /// <summary>
    ///     Posts a resolved branch misprediction's penalty window to the global counter:
    ///     the branch's ROB residency (dispatch → now), excluding cycles already claimed by
    ///     backend/store components in between, and arms refill charging until the first
    ///     correct-path instruction dispatches.
    /// </summary>
    private void PostBpredWindow(RobEntry branch) {
        long window = _cyclesCounter.Value - branch.DispatchCycle
                                           - (_cpiStolenCycles - branch.CpiStolenAtDispatch);
        if (window > 0) _cpiBpredCounter.IncrementBy(window);
        _cpiBpredRefill = true;
    }

    /// <summary>
    ///     Posts the provisional I-side miss cycles to the global CPI-stack counters — called
    ///     when an instruction carrying the sFMT miss bit retires, proving the stalled fetch
    ///     was on the correct path. Wrong-path pendings are instead discarded on flush.
    /// </summary>
    private void PostIcachePendings() {
        if (_cpiPendingL1I > 0) _cpiL1ICounter.IncrementBy(_cpiPendingL1I);
        if (_cpiPendingL2I > 0) _cpiL2ICounter.IncrementBy(_cpiPendingL2I);
        if (_cpiPendingL3I > 0) _cpiL3ICounter.IncrementBy(_cpiPendingL3I);
        if (_cpiPendingItlb > 0) _cpiITlbCounter.IncrementBy(_cpiPendingItlb);
        _cpiPendingL1I = _cpiPendingL2I = _cpiPendingL3I = _cpiPendingItlb = 0;
    }

    /// <summary>Live CPI stack (Eyerman et al., ASPLOS 2006) from the current counter values.</summary>
    private CpiStack ComputeCpiStack() =>
        CpiStack.Compute(
            _cyclesCounter.Value,
            _retiredCounter.Value,
            _cpiL1ICounter.Value,
            _cpiL2ICounter.Value,
            _cpiL3ICounter.Value,
            _cpiITlbCounter.Value,
            _cpiBpredCounter.Value,
            _cpiL1DCounter.Value,
            _cpiL2DCounter.Value,
            _cpiL3DCounter.Value,
            _cpiDTlbCounter.Value,
            _cpiStoreCounter.Value,
            _cpiResourceCounter.Value
        );

    /// <summary>Live Top-Down breakdown (Yasin, ISPASS 2014) from the current counter values.</summary>
    private TopDownBreakdown ComputeTopDown() =>
        TopDownBreakdown.Compute(
            _tdTotalSlotsCounter.Value,
            _tdSlotsIssuedCounter.Value,
            _tdSlotsRetiredCounter.Value,
            _tdFetchBubblesCounter.Value,
            _tdRecoveryBubblesCounter.Value,
            _cyclesCounter.Value,
            _tdFetchLatencyCyclesCounter.Value,
            _tdExecStallCyclesCounter.Value,
            _tdMemStallLoadCyclesCounter.Value,
            _tdMemStallStoreCyclesCounter.Value,
            _branchMissCounter.Value,
            _flushesCounter.Value
        );

    private static void UpdateCacheStat(
        SetAssociativeCache? cache,
        Counter? hitsCounter,
        Counter? missesCounter,
        ref long lastHits,
        ref long lastMisses
    ) {
        if (cache is null) return;
        hitsCounter!.IncrementBy(cache.Hits - lastHits);
        missesCounter!.IncrementBy(cache.Misses - lastMisses);
        lastHits = cache.Hits;
        lastMisses = cache.Misses;
    }

    private static void UpdateCacheStat(
        BdiCache? cache,
        Counter? hitsCounter,
        Counter? missesCounter,
        ref long lastHits,
        ref long lastMisses
    ) {
        if (cache is null) return;
        hitsCounter!.IncrementBy(cache.Hits - lastHits);
        missesCounter!.IncrementBy(cache.Misses - lastMisses);
        lastHits = cache.Hits;
        lastMisses = cache.Misses;
    }

    private static void UpdateCacheStat(
        CeaserCache? cache,
        Counter? hitsCounter,
        Counter? missesCounter,
        ref long lastHits,
        ref long lastMisses
    ) {
        if (cache is null) return;
        hitsCounter!.IncrementBy(cache.Hits - lastHits);
        missesCounter!.IncrementBy(cache.Misses - lastMisses);
        lastHits = cache.Hits;
        lastMisses = cache.Misses;
    }

    private static void UpdateCacheStat(
        ScatterCache? cache,
        Counter? hitsCounter,
        Counter? missesCounter,
        ref long lastHits,
        ref long lastMisses
    ) {
        if (cache is null) return;
        hitsCounter!.IncrementBy(cache.Hits - lastHits);
        missesCounter!.IncrementBy(cache.Misses - lastMisses);
        lastHits = cache.Hits;
        lastMisses = cache.Misses;
    }

    private static void UpdateTlbStat(
        Tlb? tlb,
        Counter? hitsCounter,
        Counter? missesCounter,
        ref long lastHits,
        ref long lastMisses
    ) {
        if (tlb is null) return;
        hitsCounter!.IncrementBy(tlb.Hits - lastHits);
        missesCounter!.IncrementBy(tlb.Misses - lastMisses);
        lastHits = tlb.Hits;
        lastMisses = tlb.Misses;
    }

    /// <summary>
    ///     PC-indexed, direct-mapped, untagged stride table for Vector Runahead (Naithani et al.,
    ///     ISCA 2021) — mirrors <see cref="StridePrefetcher" />'s RPT plus a learned chain-terminator
    ///     PC. Trained only from the real (non-shadow) demand-load stream; consulted by the shadow
    ///     lane to decide whether a load's PC is confirmed-striding and safe to vectorize.
    /// </summary>
    private struct VrStrideEntry {
        public ulong LastAddr;
        public long Stride;
        public int Confidence;   // 0-3 saturating; vectorization requires ==3
        public ulong Terminator; // PC where the chain historically stops; 0 = not yet learned
        public bool Initialized;
    }
    // ── Nested helper types ────────────────────────────────────────────────────

    private readonly record struct FetchedInstr(
        ulong Pc,
        ITooth? Decoded,
        ulong PredictedNextPc,
        ulong InstrId = 0,
        TrapInfo? PreTrap = null,
        BranchHistoryCheckpoint HistCheckpoint = default,
        bool IcacheMiss = false, // fetch access missed the I-cache/I-TLB (sFMT miss bit)
        ValueHistoryCheckpoint VpHistCheckpoint = default
    );

    // Instruction that has been renamed but not yet dispatched to ROB/IQ.
    private readonly record struct RenameEntry(
        ulong Pc,
        ITooth? Decoded,
        ulong PredictedNextPc,
        ulong InstrId,
        TrapInfo? PreTrap,
        int ArchDest,     // -1 if no architectural destination
        int PhysDest,     // -1 if no architectural destination
        int PrevPhysDest, // -1 if no architectural destination
        int P1,
        int P2,
        int P3, // physical source tags captured from RAT, -1 if unused
        BranchHistoryCheckpoint HistCheckpoint = default,
        bool IcacheMiss = false, // fetch access missed the I-cache/I-TLB (sFMT miss bit)
        ValueHistoryCheckpoint VpHistCheckpoint = default,
        bool IsVpEligible = false,
        bool WasValuePredicted = false,
        ulong PredictedValue = 0,
        bool IsEarlyExecEligible = false,
        Action<IArchState>? EarlySideEffect = null,
        ulong? FusedSecondInstrId = null // the branch's own InstrId when this entry is a fused pair
    );

    private readonly record struct IssuedInstr(
        int RobIdx,
        int PhysDest,
        ITooth Instr,
        ulong Pc,
        ulong Src1,
        ulong Src2,
        ulong Src3,
        ulong InstrId = 0
    );

    private readonly record struct ExecResult(
        int RobIdx,
        int PhysDest,
        (ulong Value, bool HasValue) RegValue,
        (ulong Value, bool HasValue) ResolvedNextPc,
        TrapInfo? Trap,
        bool IsReturnFromTrap,
        PrivilegeLevel? ReturnPrivilege,
        bool HasStoreCapture,
        ulong StoreAddr,
        ulong StoreVal,
        int StoreBytes,
        bool HasLoadAccess,
        ulong LoadAddr,
        int LoadBytes,
        bool LoadWasForwarded,     // true if TryForwardFromStore supplied the register value
        bool RequestHalt = false,  // true for an HTIF tohost-exit store: halt after commit
        bool RequestBlock = false, // true for a still-blocked syscall (e.g. futex FUTEX_WAIT)
        ulong InstrId = 0,         // per-instruction age, for pruning in-flight results on a partial squash
        Action<IArchState>? SideEffect
            = null, // deferred to Commit for scalar ops; null for vec/uve (applied at Execute)
        int LatencyOverride
            = 0 // per-instruction FU latency from ExecuteResult.LatencyOverride; 0 = use FuLatencyConfig
    );

    /// <summary>
    ///     Passes reads through to backing memory while recording the last read address;
    ///     captures writes instead of executing them. Used to defer store writes until
    ///     ROB commit and to capture load addresses for memory-ordering checks.
    /// </summary>
    private sealed class CapturingMemory(IMemory backing) : IMemory {
        public bool HasWrite { get; private set; }
        public ulong WriteAddress { get; private set; }
        public ulong WriteValue { get; private set; }
        public int WriteBytes { get; private set; }

        public bool HasRead { get; private set; }
        public ulong ReadAddress { get; private set; }
        public int ReadBytes { get; private set; }

        /// <summary>
        ///     InvisiSpec (Yan et al., MICRO 2018): when set, <see cref="Read" /> routes through
        ///     <see cref="IMemory.PeekRead" /> instead of <see cref="IMemory.Read" /> — a speculative
        ///     load's Execute-time access must not mutate cache state until its own visibility point.
        ///     Set by <c>ExecuteOne</c> just before invoking the executor; always overwritten per
        ///     instruction, so its value never leaks across cycles.
        /// </summary>
        public bool PeekMode { get; set; }

        public ulong Read(ulong address, int bytes) {
            HasRead = true;
            ReadAddress = address;
            ReadBytes = bytes;
            return PeekMode ? backing.PeekRead(address, bytes) : backing.Read(address, bytes);
        }

        public void Load(ulong address, ReadOnlySpan<byte> data) => backing.Load(address, data);

        public void InvalidateLine(ulong address) => backing.InvalidateLine(address);
        public void CleanLine(ulong address) => backing.CleanLine(address);
        public void FlushLine(ulong address) => backing.FlushLine(address);
        public void SetRequestPc(ulong pc) => backing.SetRequestPc(pc);

        public void Write(ulong address, ulong value, int bytes) {
            HasWrite = true;
            WriteAddress = address;
            WriteValue = value;
            WriteBytes = bytes;
        }

        public void Reset() {
            HasWrite = false;
            HasRead = false;
            PeekMode = false;
        }
    }

    /// <summary>
    ///     Memory view for the runahead shadow lane. Writes never reach real memory — they land only
    ///     in a scratch buffer keyed by exact (address, bytes), discarded when the episode ends. Reads
    ///     check that buffer first (full-word/zero-offset store-to-load forwarding within an episode),
    ///     then fall through to the real backing accessor — which is what actually warms the real
    ///     cache for the real pipeline to find hot once it resumes.
    /// </summary>
    private sealed class RunaheadMemory(IMemory backing, Dictionary<ulong, (ulong Value, int Bytes)> storeBuffer)
        : IMemory {
        // Vector Runahead (Naithani et al., ISCA 2021): TryVectorizeShadowStep needs the address
        // a shadow load actually read from, to derive the remaining lanes' addresses via the
        // stride table — mirrors CapturingMemory's real-pipeline read-tracking pattern.
        public bool HasRead { get; private set; }
        public ulong LastReadAddress { get; private set; }
        public int LastReadBytes { get; private set; }

        public ulong Read(ulong address, int bytes) {
            HasRead = true;
            LastReadAddress = address;
            LastReadBytes = bytes;
            return storeBuffer.TryGetValue(address, out (ulong Value, int Bytes) e) && e.Bytes == bytes
                ? e.Value
                : backing.Read(address, bytes);
        }

        public void Write(ulong address, ulong value, int bytes) => storeBuffer[address] = (value, bytes);

        public void Load(ulong address, ReadOnlySpan<byte> data) { }
        public void SetRequestPc(ulong pc) => backing.SetRequestPc(pc);
    }
}