using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Ooo;

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
///     Checkpoint Processing and Recovery train (Akkary, Rajwar &amp; Srinivasan, MICRO 2003),
///     optionally extended with Continual Flow Pipelines (Srinivasan, Rajwar, Akkary, Gandhi
///     &amp; Upton, ASPLOS 2004) via <c>enableCfp</c>. A ROB-free out-of-order pipeline:
///     <list type="bullet">
///         <item>
///             Branch misprediction recovery restores a whole rename-map checkpoint created
///             selectively at low-confidence branches (JRS estimator), instead of walking a ROB.
///             Instructions between the checkpoint and the mispredicted branch re-execute
///             (checkpoint overhead, COVHD), with the recorded branch outcome replayed to prevent
///             a repeat misprediction.
///         </item>
///         <item>
///             Instructions retire in bulk: a whole checkpoint's instructions commit at once when
///             its completion counter fills, bounded only by the D-cache store port.
///         </item>
///         <item>
///             Physical registers are reclaimed aggressively via per-register use counters and
///             unmapped flags (after Moudgill et al.), decoupled from retirement.
///         </item>
///         <item>
///             Stores live in a two-tier hierarchical store queue with a membership test buffer;
///             forwarding from the L2 tier (or an MTB false positive) costs a
///             data-cache-miss-equivalent penalty.
///         </item>
///         <item>
///             With CFP, an L2-class-miss load and its forward slice drain out of the scheduler
///             and register file into a Slice Data Buffer — recorded ready source values travel
///             with the slice, so both completed source registers and slice destination registers
///             are released — and re-enter later through back-end (physical→physical) renaming.
///         </item>
///     </list>
///     Scalar ISAs only in v1: vector/UVE instructions are rejected at rename.
/// </summary>
public sealed class CprTrain : ISteppableTrain {
    private readonly CprPipelineCore _core;
    private readonly Train _train;

    public CprTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int issueWidth = 2,
        int iqCapacity = 8,
        int extraPhysRegs = 32,
        int checkpointCount = 8,
        int checkpointMaxInstructions = 256,
        int lqCapacity = 32,
        int l1SqCapacity = 16,
        int l2SqCapacity = 240,
        int mtbSize = 1024,
        int mtbBlockBytes = 64,
        int l2SqForwardPenalty = 10,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        bool enableStoreSets = true,
        bool enableCfp = false,
        int cfpMissThresholdCycles = 8,
        int sdbCapacity = 256,
        int cfpReservedRegs = 8
    ) {
        var esc = new Escapement();
        _train = new Train("cpr", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new CprPipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, iLayers, dLayers, entryPoint,
                issueWidth, iqCapacity, extraPhysRegs,
                checkpointCount, checkpointMaxInstructions,
                lqCapacity, l1SqCapacity, l2SqCapacity, mtbSize, mtbBlockBytes, l2SqForwardPenalty,
                predictor ?? new AlwaysNotTakenPredictor(),
                fuLatency ?? FuLatencyConfig.Default,
                enableStoreSets,
                enableCfp, cfpMissThresholdCycles, sdbCapacity, cfpReservedRegs
            )
        );
        _train.Build();
    }

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;

    public long CurrentTick => _train.CurrentTick;
    public bool IsIdle => _train.IsIdle;

    public IArchState ArchState => _core.State;

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();

    public DialBoardSnapshot SnapshotPipeline() => _core.Dials.Snapshot();
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
///     The CPR/CFP pipeline Gear. Stage order within one tick mirrors <see cref="OoOPipelineCore" />:
///     Complete → Commit → [recovery/flush] → Execute → Issue → SliceReinsert → Dispatch → Rename → Fetch.
///     <para>
///         Divergences from the papers, chosen deliberately for this simulator:
///         (1) each checkpoint keeps a per-instruction entry list with the completed result value —
///         the hardware needs none of this (its architectural state <em>is</em> PRF+RAT), but the
///         simulator must keep a separate <c>IArchState</c> mirror in sync, and under aggressive
///         reclamation the PRF slot may be legally reused before its checkpoint retires;
///         (2) precise traps commit the completed prefix of their checkpoint in-order and then
///         flush, instead of the paper's recover-and-re-execute-with-a-forced-checkpoint dance —
///         same architectural outcome, slightly less re-execution;
///         (3) memory disambiguation uses the codebase's store-set predictor, which is what the
///         CFP paper's own CPR baseline uses (ASPLOS 2004 §2), rather than MICRO 2003's
///         store-distance predictor;
///         (4) store-to-load forwarding merges all older overlapping resolved stores over the
///         memory value byte-by-byte, so partial-overlap cases never need the
///         wait-until-store-commits dance a checkpointed (bulk, all-or-nothing) commit could not
///         express without deadlocking inside one checkpoint.
///     </para>
/// </summary>
internal sealed class CprPipelineCore : Gear {
    // IQ index 0=INT(Alu/MulDiv/Sys/Fence/Halt), 1=FP, 2=BR, 3=LSU
    private const int IqCount = 4;

    private readonly CapturingMemory _capMem;
    private readonly List<ExecResult> _cdbBuffer = [];
    private readonly int _cfpMissThreshold;
    private readonly int _cfpReservedRegs;
    private readonly int _checkpointMaxInstructions;

    // Architectural RAS shadow: advanced only at bulk commit; recovery restores the
    // speculative RAS from this plus a replay of surviving in-flight calls/returns.
    private readonly ReturnAddressStack _committedRas = new();
    private readonly CheckpointConfidencePredictor _confidence = new();
    private readonly CheckpointList _cpList;
    private readonly Queue<FetchedInstr> _decodeQueue = new();
    private readonly IDecoder _decoder;
    private readonly bool _enableCfp;

    /// <summary>In-flight (renamed, not yet committed or squashed) instructions by InstrId.</summary>
    private readonly Dictionary<ulong, CheckpointEntry> _entryByInstrId = [];

    private readonly List<IssuedInstr> _execBuffer = [];
    private readonly IExecutor _executor;
    private readonly IFetchTranslator? _fetchTranslator;
    private readonly FuLatencyConfig _fuConfig;
    private readonly HierarchicalStoreQueue _hsq;
    private readonly List<(int Countdown, ExecResult Result)> _inFlight = [];
    private readonly IssueQueue[] _iqs;
    private readonly int _issueWidth;
    private readonly int _l2SqForwardPenalty;
    private readonly LoadQueue _lq;
    private readonly int _maxDecodeDepth;

    /// <summary>Miss-return countdowns for CFP slice-head loads: (InstrId, remaining cycles).</summary>
    private readonly List<(ulong InstrId, int Countdown)> _pendingSlices = [];

    private readonly IBranchPredictor _predictor;
    private readonly PhysicalRegisterFile _prf;
    private readonly ReturnAddressStack _ras = new();
    private readonly RenameMap _rat;
    private readonly Queue<CprRenameEntry> _renameQueue = new();
    private readonly SliceDataBuffer? _sdb;

    /// <summary>
    ///     CFP slice remapper: original physical name → the physical register the slice's most
    ///     recent re-insertion of that producer acquired via back-end renaming. Persistent across
    ///     drain sessions — a consumer always re-inserts after its producer (FIFO), so the entry it
    ///     reads is always the mapping of the correct producer incarnation.
    /// </summary>
    private readonly Dictionary<int, int> _sliceRemap = [];

    private readonly StoreSetPredictor? _storeSets;
    private readonly ITrapController _trapController;

    private bool _anyCache;
    private Counter _branchMissCounter = null!;
    private Counter? _cacheMissStallsCounter;
    private Counter? _cfpReinsertionsCounter, _cfpSlicesCounter;
    private Counter _checkpointsCreatedCounter = null!;
    private Counter _checkpointsRetiredCounter = null!;
    private Counter _covhdCounter = null!;
    private Counter _cyclesCounter = null!;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;
    private bool _fetchFaulted;
    private ulong _fetchPc;
    private Counter _flushesCounter = null!;

    // Forced checkpoint at the first branch after a recovery, irrespective of confidence
    // (MICRO 2003 §4.1.1 forward-progress rule).
    private bool _forceCheckpointAtNextBranch;
    private bool _fullFlushPending;
    private ulong _fullFlushTarget;
    private bool _halted;
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastITlbHits, _lastITlbMisses, _lastDTlbHits, _lastDTlbMisses;
    private Counter _memViolationsCounter = null!;
    private ulong _nextCheckpointSeq = 1;
    private ulong _nextInstrId = 1;
    private ulong _nextMemSeqNo;
    private Counter _recoveriesCounter = null!;
    private int _recoveryCovhd;
    private bool _recoveryIsBranch;
    private ulong _recoveryKeyInstrId;

    // Pending recovery, collected during Complete/Commit; oldest faulting instruction wins.
    private bool _recoveryPending;
    private int _recoveryReplayDistance;
    private ulong _recoveryReplayTarget;
    private ulong _recoveryTargetSeq;

    // Recorded-outcome replay after a branch recovery (MICRO 2003 §4.1.1): the b-th branch
    // fetched after the restart takes the previously resolved target instead of a prediction.
    private int _replayBranchesRemaining;
    private ulong _replayTarget;
    private Counter _retiredCounter = null!;

    // Cached to avoid a fresh Action allocation per simulated cycle.
    private Action? _runCycle;
    private Counter _stallsCounter = null!;

    public CprPipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        int issueWidth,
        int iqCapacity,
        int extraPhysRegs,
        int checkpointCount,
        int checkpointMaxInstructions,
        int lqCapacity,
        int l1SqCapacity,
        int l2SqCapacity,
        int mtbSize,
        int mtbBlockBytes,
        int l2SqForwardPenalty,
        IBranchPredictor predictor,
        FuLatencyConfig fuConfig,
        bool enableStoreSets,
        bool enableCfp,
        int cfpMissThresholdCycles,
        int sdbCapacity,
        int cfpReservedRegs
    ) : base(name, parent, esc) {
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
        _predictor = predictor;
        _fuConfig = fuConfig;
        ILayers = iLayers;
        DLayers = dLayers;
        _capMem = new CapturingMemory(DLayers.Accessor);
        _issueWidth = issueWidth;
        _maxDecodeDepth = issueWidth * 4;
        _fetchPc = entryPoint;
        _checkpointMaxInstructions = checkpointMaxInstructions;
        _l2SqForwardPenalty = l2SqForwardPenalty;
        _enableCfp = enableCfp;
        _cfpMissThreshold = cfpMissThresholdCycles;
        _cfpReservedRegs = cfpReservedRegs;

        State = mechanism.CreateArchState();
        State.Pc = entryPoint;
        _fetchTranslator = mechanism.CreateFetchTranslator(State, ILayers.Accessor);

        int archRegs = State.IntegerRegisters.Count;
        int physRegs = archRegs + extraPhysRegs;
        _prf = new PhysicalRegisterFile(physRegs);
        _prf.InitializeAllocation(archRegs);
        _rat = new RenameMap(archRegs, physRegs);
        _cpList = new CheckpointList(checkpointCount);
        _iqs = new IssueQueue[CprPipelineCore.IqCount];
        for (var i = 0; i < CprPipelineCore.IqCount; i++) _iqs[i] = new IssueQueue(iqCapacity);
        _lq = new LoadQueue(lqCapacity);
        _hsq = new HierarchicalStoreQueue(l1SqCapacity, l2SqCapacity, mtbSize, mtbBlockBytes);
        _storeSets = enableStoreSets ? new StoreSetPredictor() : null;
        _sdb = enableCfp ? new SliceDataBuffer(sdbCapacity) : null;
    }

    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }
    public IArchState State { get; }

    /// <summary>True while the SDB head is drainable — the front end waits (ASPLOS 2004 §4.1.3).</summary>
    private bool SliceDrainActive => _sdb is { IsEmpty: false, } && !IsSliceStillPending(_sdb.Head.InstrId);

    private static int IqIndex(ToothClass cls) => cls switch {
        ToothClass.FloatingPoint or ToothClass.FloatDivSqrt      => 1,
        ToothClass.Branch or ToothClass.ConditionalBranch        => 2,
        ToothClass.Load or ToothClass.Store or ToothClass.Atomic => 3,
        _                                                        => 0,
    };

    /// <summary>
    ///     Classes that force a checkpoint at themselves and issue only when architecturally
    ///     oldest — the paper's serializing-instruction handling (MICRO 2003 §4.1). Being entry 0
    ///     of the head checkpoint means every older instruction has bulk-committed.
    /// </summary>
    private static bool IsSerialized(ToothClass cls) =>
        cls is ToothClass.System or ToothClass.Fence or ToothClass.Atomic or ToothClass.Halt;

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _flushesCounter = Dials.AddCounter("flushes", "Full pipeline flushes (trap/interrupt/mret)");
        _recoveriesCounter = Dials.AddCounter("recoveries", "Checkpoint recoveries (mispredict + memory violation)");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions recovered");
        _stallsCounter = Dials.AddCounter("stalls", "Dispatch-stall cycles + cache miss penalties");
        _memViolationsCounter = Dials.AddCounter(
            "mem_order_violations", "Memory-order violations: speculative load read stale data"
        );
        _checkpointsCreatedCounter = Dials.AddCounter("checkpoints_created", "Map-table checkpoints opened");
        _checkpointsRetiredCounter = Dials.AddCounter("checkpoints_retired", "Checkpoints bulk-committed");
        _covhdCounter = Dials.AddCounter(
            "covhd_squashed", "Good instructions squashed for re-execution by checkpoint rollback (COVHD)"
        );
        if (_enableCfp) {
            _cfpSlicesCounter = Dials.AddCounter(
                "cfp_slice_instructions", "Instructions drained into the Slice Data Buffer"
            );
            _cfpReinsertionsCounter = Dials.AddCounter(
                "cfp_reinsertions", "Slice instructions re-inserted into the pipeline"
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

        _anyCache = ILayers.Cache is not null || DLayers.Cache is not null
                                              || ILayers.L2Cache is not null || DLayers.L2Cache is not null
                                              || ILayers.L3Cache is not null || DLayers.L3Cache is not null
                                              || ILayers.Tlb is not null || DLayers.Tlb is not null;
        if (_anyCache)
            _cacheMissStallsCounter = Dials.AddCounter(
                "cache_miss_stalls", "Stall cycles from memory hierarchy misses"
            );

        if (ILayers.Cache is not null) {
            _icacheHitsCounter = Dials.AddCounter("icache_hits", "L1 I-cache hits");
            _icacheMissesCounter = Dials.AddCounter("icache_misses", "L1 I-cache misses");
        }

        if (ILayers.L2Cache is not null) {
            _l2IcacheHitsCounter = Dials.AddCounter("l2_icache_hits", "L2 I-cache hits");
            _l2IcacheMissesCounter = Dials.AddCounter("l2_icache_misses", "L2 I-cache misses");
        }

        if (ILayers.L3Cache is not null) {
            _l3IcacheHitsCounter = Dials.AddCounter("l3_icache_hits", "L3 I-cache hits");
            _l3IcacheMissesCounter = Dials.AddCounter("l3_icache_misses", "L3 I-cache misses");
        }

        if (DLayers.Cache is not null) {
            _dcacheHitsCounter = Dials.AddCounter("dcache_hits", "L1 D-cache hits");
            _dcacheMissesCounter = Dials.AddCounter("dcache_misses", "L1 D-cache misses");
        }

        if (DLayers.L2Cache is not null) {
            _l2DcacheHitsCounter = Dials.AddCounter("l2_dcache_hits", "L2 D-cache hits");
            _l2DcacheMissesCounter = Dials.AddCounter("l2_dcache_misses", "L2 D-cache misses");
        }

        if (DLayers.L3Cache is not null) {
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

    public override void Wind() =>
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);

    // ── Main driver ────────────────────────────────────────────────────────────

    private void RunCycle() {
        if (_halted) return;

        if (_anyCache) ChargeStallCycles(DrainAndChargeStalls());

        _cyclesCounter.Increment();
        State.OnCycle();

        StepComplete();
        StepCommit();
        TickPendingSlices();

        if (_anyCache) {
            ILayers.TickWb();
            DLayers.TickWb();
            ILayers.TickMshr();
            DLayers.TickMshr();
            ILayers.TickPorts();
            DLayers.TickPorts();
        }

        if (_halted || _fullFlushPending || _recoveryPending) {
            if (_fullFlushPending)
                ApplyFullFlush();
            else if (_recoveryPending) ApplyRecovery();
            if (!_halted) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
            return;
        }

        StepExecute();
        StepIssue();
        StepSliceReinsert();
        StepDispatch();
        StepRename();
        StepFetch();

        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // ── Complete ───────────────────────────────────────────────────────────────

    /// <summary>CDB broadcast: apply Execute T-1 results to PRF + IQ + checkpoint entries.</summary>
    private void StepComplete() {
        foreach (ExecResult r in _cdbBuffer) {
            if (!_entryByInstrId.TryGetValue(r.InstrId, out CheckpointEntry? entry)) continue; // squashed in flight

            entry.IsComplete = true;
            entry.ResolvedNextPc = r.ResolvedNextPc;
            entry.HasTrap = r.Trap is not null;
            entry.Trap = r.Trap;
            entry.IsReturnFromTrap = r.IsReturnFromTrap;
            entry.ReturnPrivilege = r.ReturnPrivilege;
            entry.RequestHalt = r.RequestHalt;
            entry.SideEffect = r.SideEffect;
            FindCheckpointBySeq(entry.CheckpointSeq)?.MarkEntryComplete();

            if (r.HasStoreCapture) {
                SqEntry sq = _hsq.At(entry.SqIdx);
                _hsq.RecordAddressKnown(entry.SqIdx, r.StoreAddr); // sets AddressKnown + MTB count
                sq.Value = r.StoreVal;
                sq.Width = r.StoreBytes;
                // The store's address just resolved: flag younger already-executed overlapping
                // loads. Recovery happens at the pre-commit scan of the load's checkpoint.
                CheckLoadViolations(sq.SeqNo, r.StoreAddr, r.StoreBytes, sq.Pc);
                _storeSets?.OnStoreIssued(sq.Pc, sq.SeqNo);
            }

            // Branch resolution: train predictor + confidence estimator speculatively at execute
            // (MICRO 2003 §5.1 updates the predictor "once the branch executes"), then schedule a
            // checkpoint recovery on a mispredict.
            if (entry.ResolvedNextPc is { HasValue: true, Value: var resolved, }) {
                ulong fallThrough = entry.Pc + (ulong)(entry.Instruction?.SizeBytes ?? 4);
                bool taken = resolved != fallThrough;
                bool correct = resolved == entry.PredictedNextPc;
                _predictor.Update(entry.Pc, taken, resolved);
                _confidence.Update(entry.Pc, correct, taken);
                if (!correct) ScheduleBranchRecovery(entry, resolved);
            }

            if (r.RegValue.HasValue && r.PhysDest >= 0) {
                entry.ResultValue = r.RegValue.Value;
                entry.HasResultValue = true;
                _prf.Write(r.PhysDest, r.RegValue.Value);
                var captured = 0;
                foreach (IssueQueue iq in _iqs) captured += iq.Broadcast(r.PhysDest, r.RegValue.Value, r.InstrId);
                // Each capture is a "register read" — drop that many references (MICRO 2003 §4.3).
                for (var i = 0; i < captured; i++) _prf.Release(r.PhysDest);
                TryReclaim(r.PhysDest);
            }
        }

        _cdbBuffer.Clear();
    }

    private void ScheduleBranchRecovery(CheckpointEntry branch, ulong resolvedTarget) {
        Checkpoint? cp = FindCheckpointBySeq(branch.CheckpointSeq);
        if (cp is null) return;

        // Distance (in branches) from the checkpoint to the mispredicted branch, and the number
        // of good instructions that will re-execute (COVHD).
        var branchDistance = 0;
        var covhd = 0;
        foreach (CheckpointEntry e in cp.Entries) {
            if (e.Instruction?.Class is ToothClass.Branch or ToothClass.ConditionalBranch) branchDistance++;
            if (e.InstrId == branch.InstrId) break;
            covhd++;
        }

        ScheduleRecovery(
            branch.InstrId, cp.Seq, covhd,
            true, branchDistance, resolvedTarget
        );
    }

    private void ScheduleRecovery(
        ulong keyInstrId,
        ulong targetSeq,
        int covhd,
        bool isBranch,
        int replayDistance = 0,
        ulong replayTarget = 0
    ) {
        if (_recoveryPending && _recoveryKeyInstrId <= keyInstrId) return; // an older recovery wins
        _recoveryPending = true;
        _recoveryKeyInstrId = keyInstrId;
        _recoveryTargetSeq = targetSeq;
        _recoveryCovhd = covhd;
        _recoveryIsBranch = isBranch;
        _recoveryReplayDistance = replayDistance;
        _recoveryReplayTarget = replayTarget;
    }

    // ── Commit ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Bulk retirement: the head checkpoint's instructions commit together once its completion
    ///     counter fills (all entries complete), bounded only by the one-store-per-cycle D-cache
    ///     write port. A completed prefix ending in a trap/halt commits early (see class summary).
    /// </summary>
    private void StepCommit() {
        var dcachePortUsed = false;
        while (!_cpList.IsEmpty && !_halted && !_fullFlushPending && !_recoveryPending) {
            Checkpoint head = _cpList.Head;

            if (head.Entries.Count == 0) {
                // An empty tail can't retire — it is still receiving instructions. An empty
                // non-tail head (possible only through unusual recovery interleavings) holds no
                // work and must not wedge the FIFO: release its snapshot references and drop it.
                if (_cpList.Count == 1) break;
                foreach (int p in head.RatSnapshot) {
                    _prf.Release(p);
                    TryReclaim(p);
                }

                _cpList.RetireHead();
                continue;
            }

            bool committable = head.AllComplete || HasCompletableTerminalPrefix(head);
            if (!committable) break;

            // Pre-commit violation scan: a load that read stale data forces a rollback to this
            // checkpoint *before* anything here commits, so re-execution never double-commits.
            // Once the walk has started (CommittedCount > 0, e.g., paused on the store port), no
            // new violation can appear in this checkpoint: all its stores resolved before the
            // bulk gate opened, and younger-checkpoint stores only flag younger loads.
            if (head.CommittedCount == 0 && ScanForViolatedLoads(head)) break;

            IReadOnlyList<CheckpointEntry> entries = head.Entries;
            var stoppedForPort = false;
            while (head.CommittedCount < entries.Count) {
                CheckpointEntry e = entries[head.CommittedCount];
                if (!e.IsComplete) break; // only possible on the terminal-prefix path

                switch (e) {
                    case { HasTrap: true, Trap: not null, }: {
                        ulong target = _trapController.RaiseTrap(e.Trap, State);
                        RetireEntry(head, e);
                        SetFullFlush(target);
                        return;
                    }
                    case { IsReturnFromTrap: true, ReturnPrivilege: not null, }: {
                        ulong target = _trapController.ReturnFromTrap(e.ReturnPrivilege.Value, State);
                        CommitEntryState(e);
                        RetireEntry(head, e);
                        SetFullFlush(target);
                        return;
                    }
                    case { IsHalt: true, }:
                        RetireEntry(head, e);
                        _halted = true;
                        return;
                }

                // Store write: one D-cache write port per cycle.
                if (e.SqIdx >= 0) {
                    SqEntry sq = _hsq.At(e.SqIdx);
                    if (sq.AddressKnown) {
                        if (dcachePortUsed) {
                            stoppedForPort = true;
                            break;
                        }

                        DLayers.Accessor.Write(sq.Address, sq.Value, sq.Width);
                        dcachePortUsed = true;
                    }
                }

                CommitEntryState(e);

                switch (e) {
                    // HTIF tohost-exit store: halt after the write commits.
                    case { RequestHalt: true, }:
                        State.Pc = e.PredictedNextPc;
                        RetireEntry(head, e);
                        _halted = true;
                        return;
                    // Unconditional jump-to-self: the bare-metal terminator.
                    case { Instruction.Class: ToothClass.Branch, } and
                         { ResolvedNextPc: { HasValue: true, Value: var selfPc, }, } when selfPc == e.Pc:
                        State.Pc = selfPc;
                        RetireEntry(head, e);
                        _halted = true;
                        return;
                    default:
                        State.Pc = e.ResolvedNextPc.HasValue ? e.ResolvedNextPc.Value : e.PredictedNextPc;
                        RetireEntry(head, e);
                        break;
                }
            }

            // Reclaim the checkpoint only when fully committed and a younger checkpoint exists
            // (the paper's "next checkpoint has been allocated" condition — the tail must have
            // somewhere else to append).
            if (head.CommittedCount == entries.Count && _cpList.Count > 1) {
                foreach (int p in head.RatSnapshot) {
                    _prf.Release(p);
                    TryReclaim(p);
                }

                _cpList.RetireHead();
                _checkpointsRetiredCounter.Increment();
                continue; // bulk: multiple checkpoints may retire in one cycle
            }

            if (stoppedForPort || head.CommittedCount < entries.Count) { }

            break; // fully committed but still the only checkpoint — wait for a successor
        }

        // Check for pending interrupts when the commit path is quiescent.
        if (!_halted && !_fullFlushPending && !_recoveryPending) {
            TrapInfo? interrupt = _trapController.PeekInterrupt(State);
            if (interrupt is not null) SetFullFlush(_trapController.RaiseTrap(interrupt, State));
        }
    }

    /// <summary>
    ///     True when the head checkpoint's completed in-order prefix (starting at the commit
    ///     cursor) reaches a terminal entry — a trap, halt, mret, HTIF exit, or jump-to-self.
    ///     Such a prefix may commit without waiting for the checkpoint's remaining (younger,
    ///     doomed, or blocked) entries to complete.
    /// </summary>
    private static bool HasCompletableTerminalPrefix(Checkpoint cp) {
        for (int i = cp.CommittedCount; i < cp.Entries.Count; i++) {
            CheckpointEntry e = cp.Entries[i];
            if (!e.IsComplete) return false;
            if (e.HasTrap || e.IsHalt || e.IsReturnFromTrap || e.RequestHalt) return true;
            if (e.Instruction?.Class == ToothClass.Branch
             && e.ResolvedNextPc.HasValue && e.ResolvedNextPc.Value == e.Pc)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Scans the head checkpoint for loads flagged as memory-order violations and, if any,
    ///     schedules a rollback to this checkpoint (MICRO 2003 §4.2.4: the processor rolls back
    ///     to a prior checkpoint on a memory dependence misprediction). Returns true if scheduled.
    /// </summary>
    private bool ScanForViolatedLoads(Checkpoint head) {
        for (int i = head.CommittedCount; i < head.Entries.Count; i++) {
            CheckpointEntry e = head.Entries[i];
            if (!e.IsLoad || e.LqIdx < 0) continue;
            LqEntry lq = _lq.At(e.LqIdx);
            if (lq.InstrId != e.InstrId || !lq.Violated) continue;
            _memViolationsCounter.Increment();
            _storeSets?.RecordViolation(lq.ViolatingStorePc, e.Pc);
            ScheduleRecovery(e.InstrId, head.Seq, i - head.CommittedCount, false);
            return true;
        }

        return false;
    }

    /// <summary>Applies an entry's architectural register write and side effect, in program order.</summary>
    private void CommitEntryState(CheckpointEntry e) {
        e.SideEffect?.Invoke(State);

        // Secondary-destination sync (e.g., amocas.d's pair half): the SideEffect wrote arch state
        // directly, bypassing rename. The rename-time HasPendingSecondaryDest stall guarantees the
        // RAT still maps that register to the slot consumers will read.
        int sd = e.Instruction?.SecondaryDestinationRegister ?? -1;
        if (sd >= 0) _prf.Write(_rat.Lookup(sd), State.IntegerRegisters.Read(sd));

        if (e is { ArchDestination: > 0, HasResultValue: true, })
            State.IntegerRegisters.Write(e.ArchDestination, e.ResultValue);

        // Advance the architectural RAS shadow for a retiring call/return.
        if (e.Instruction is { Class: ToothClass.Branch, } ins) {
            FetchHint hint = _decoder.GetFetchHint(e.Pc, ins.RawEncoding);
            if (hint.IsCall) _committedRas.Push(e.Pc + (ulong)ins.SizeBytes);
            if (hint.IsReturn) _committedRas.TryPop(out _);
        }
    }

    private void RetireEntry(Checkpoint cp, CheckpointEntry e) {
        if (e.LqIdx >= 0) _lq.Retire();
        if (e.SqIdx >= 0) _hsq.Retire();
        _entryByInstrId.Remove(e.InstrId);
        cp.CommittedCount++;
        _retiredCounter.Increment();
        State.OnRetire();
    }

    // ── Execute ────────────────────────────────────────────────────────────────

    private void StepExecute() {
        // Drain multi-cycle in-flight executes; entries reaching zero broadcast on the CDB.
        for (int i = _inFlight.Count - 1; i >= 0; i--) {
            (int countdown, ExecResult result) = _inFlight[i];
            if (--countdown <= 0) {
                _cdbBuffer.Add(result);
                _inFlight.RemoveAt(i);
            }
            else { _inFlight[i] = (countdown, result); }
        }

        // Stalls pending here are store-commit write misses (StepCommit ran earlier this cycle).
        if (_anyCache) ChargeStallCycles(DLayers.ConsumeAllStalls());

        foreach (IssuedInstr issued in _execBuffer) {
            if (!_entryByInstrId.TryGetValue(issued.InstrId, out CheckpointEntry? entry)) continue; // squashed

            ulong lqSeqNo = entry.LqIdx >= 0 ? _lq.At(entry.LqIdx).SeqNo : 0;
            (ExecResult result, bool forwardPenalty) = ExecuteOne(issued, lqSeqNo);

            // Register load disambiguation state at execute time so a later-resolving older store
            // can flag this load while its miss is still in flight.
            if (result.HasLoadAccess && entry.LqIdx >= 0) {
                LqEntry lq = _lq.At(entry.LqIdx);
                lq.Executed = true;
                lq.Address = result.LoadAddr;
                lq.Bytes = result.LoadBytes;
            }

            int fuLatency = result.LatencyOverride > 0
                ? result.LatencyOverride
                : _fuConfig.LatencyFor(issued.Instr);
            if (issued.Instr.Class == ToothClass.Load) {
                int cacheHit = DLayers.Cache?.HitLatency ?? 0;
                if (cacheHit > 0) fuLatency = cacheHit;
            }

            int countdown = fuLatency - 1 + _fuConfig.BypassLatency;
            if (forwardPenalty) countdown += _l2SqForwardPenalty;

            // Memory-level parallelism: a load/atomic that missed carries the penalty in its own
            // countdown so independent misses overlap.
            long stalls = 0;
            if (_anyCache && issued.Instr.Class is ToothClass.Load or ToothClass.Atomic)
                stalls = DLayers.ConsumeAllStalls();

            // CFP trigger: a long-latency (L2-class) miss load drains into the SDB as a slice
            // head instead of blocking a register and waiting in flight (ASPLOS 2004 §4.2.1).
            if (_enableCfp && issued.Instr.Class == ToothClass.Load && stalls >= _cfpMissThreshold
             && issued.PhysDest >= 0 && _sdb is { IsFull: false, }) {
                DrainMissLoadToSlice(issued, entry, (int)stalls + countdown);
                continue;
            }

            countdown += (int)stalls;
            if (countdown <= 0)
                _cdbBuffer.Add(result);
            else
                _inFlight.Add((countdown, result));
        }

        _execBuffer.Clear();
    }

    private (ExecResult Result, bool ForwardPenalty) ExecuteOne(IssuedInstr issued, ulong loadSeqNo) {
        IReadOnlyList<int> srcs = issued.Instr.SourceRegisters;
        IRegisterFile regs = State.IntegerRegisters;

        int s0 = srcs.Count > 0 ? srcs[0] : -1;
        int s1 = srcs.Count > 1 ? srcs[1] : -1;
        int s2 = srcs.Count > 2 ? srcs[2] : -1;

        ulong save0 = s0 >= 0 ? regs.Read(s0) : 0;
        ulong save1 = s1 >= 0 ? regs.Read(s1) : 0;
        ulong save2 = s2 >= 0 ? regs.Read(s2) : 0;
        if (s0 >= 0) regs.Write(s0, issued.Src1);
        if (s1 >= 0) regs.Write(s1, issued.Src2);
        if (s2 >= 0) regs.Write(s2, issued.Src3);

        _capMem.Reset();
        _capMem.SetRequestPc(issued.Pc);
        ExecuteResult er;
        try { er = _executor.Execute(issued.Instr, State, _capMem); }
        catch (AccessViolationException) {
            if (s0 >= 0) regs.Write(s0, save0);
            if (s1 >= 0) regs.Write(s1, save1);
            if (s2 >= 0) regs.Write(s2, save2);
            int cause = _capMem.HasRead ? TrapCause.LoadAccessFault : TrapCause.StoreAccessFault;
            ulong faultAddr = _capMem.HasRead ? _capMem.ReadAddress : _capMem.WriteAddress;
            return (new ExecResult(
                        issued.InstrId, issued.PhysDest,
                        default((ulong Value, bool HasValue)), default((ulong Value, bool HasValue)),
                        new TrapInfo(cause, faultAddr, issued.Pc),
                        false, null, false, 0, 0, 0, false, 0, 0
                    ), false);
        }

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
            _ => default((ulong, bool)),
        };

        // Store-to-load forwarding through the hierarchical store queue: merge every older
        // resolved overlapping store over the raw memory bytes (oldest → youngest, so the
        // youngest byte wins). Forwarding sourced beyond the L1 tier — or a load whose MTB probe
        // says "maybe" but no match exists (untagged aliasing) — pays the L2-STQ penalty
        // (MICRO 2003 §4.2.3).
        (ulong Value, bool HasValue) regValue = er.RegisterResult;
        var forwardPenalty = false;
        if (_capMem.HasRead) {
            (ulong merged, bool anyForward, bool anyL2) = MergeForwardFromStores(
                loadSeqNo, _capMem.ReadAddress, _capMem.ReadBytes, _capMem.ReadValue
            );
            if (anyForward) {
                int signExtBytes = issued.Instr.LoadSignExtendBytes;
                if (signExtBytes > 0) {
                    ulong signBit = 1UL << (signExtBytes * 8 - 1);
                    if ((merged & signBit) != 0)
                        merged |= ~((1UL << (signExtBytes * 8)) - 1);
                    else
                        merged &= (1UL << (signExtBytes * 8)) - 1;
                }

                if (issued.Instr.NanBoxLoadResult) merged = 0xFFFFFFFF00000000UL | (merged & 0xFFFFFFFF);
                regValue = (merged, true);
                forwardPenalty = anyL2;
            }
            else if (_hsq.MayHaveMatchingStore(_capMem.ReadAddress)) {
                forwardPenalty = true; // MTB said maybe; the full walk found nothing — miss-equivalent delay
            }
        }

        return (new ExecResult(
                    issued.InstrId, issued.PhysDest,
                    regValue, resolvedNextPc, er.Trap,
                    er.IsReturnFromTrap, er.ReturnPrivilege,
                    _capMem.HasWrite, _capMem.WriteAddress, _capMem.WriteValue, _capMem.WriteBytes,
                    _capMem.HasRead, _capMem.ReadAddress, _capMem.ReadBytes,
                    er.RequestHalt, er.SideEffect,
                    er.LatencyOverride ?? 0
                ), forwardPenalty);
    }

    /// <summary>
    ///     Byte-merges all older resolved stores over the memory value the load just read.
    ///     Returns the merged raw value, whether any store contributed, and whether any
    ///     contributing store sits in the slow L2 tier of the hierarchical store queue.
    /// </summary>
    private (ulong Value, bool AnyForward, bool AnyL2) MergeForwardFromStores(
        ulong loadSeqNo,
        ulong loadAddr,
        int loadBytes,
        ulong memValue
    ) {
        ulong value = memValue;
        var any = false;
        var anyL2 = false;
        foreach ((int idx, SqEntry sq) in _hsq.InOrderIndexed()) {
            if (sq.SeqNo >= loadSeqNo) break;
            if (!sq.AddressKnown) continue;
            ulong sqEnd = sq.Address + (ulong)sq.Width;
            ulong loadEnd = loadAddr + (ulong)loadBytes;
            if (sq.Address >= loadEnd || loadAddr >= sqEnd) continue;
            for (var b = 0; b < loadBytes; b++) {
                ulong abs = loadAddr + (ulong)b;
                if (abs < sq.Address || abs >= sqEnd) continue;
                ulong storeByte = (sq.Value >> (int)((abs - sq.Address) * 8)) & 0xFF;
                value = (value & ~(0xFFUL << (b * 8))) | (storeByte << (b * 8));
            }

            any = true;
            if (!_hsq.IsL1(idx)) anyL2 = true;
        }

        return (value, any, anyL2);
    }

    /// <summary>
    ///     Flags younger already-executed loads that overlap a store whose address just resolved.
    ///     Recovery is deferred to the pre-commit scan of the load's checkpoint.
    /// </summary>
    private void CheckLoadViolations(ulong storeSeqNo, ulong storeAddr, int storeBytes, ulong storePc) {
        foreach (LqEntry lq in _lq.InOrder()) {
            if (lq.SeqNo <= storeSeqNo || !lq.Executed) continue;
            ulong aEnd = lq.Address + (ulong)lq.Bytes;
            ulong bEnd = storeAddr + (ulong)storeBytes;
            if (lq.Address < bEnd && storeAddr < aEnd) {
                lq.Violated = true;
                lq.ViolatingStorePc = storePc;
            }
        }
    }

    // ── CFP: slice drain and re-insertion ──────────────────────────────────────

    /// <summary>Drains an L2-miss load (already executed functionally) into the SDB as a slice head.</summary>
    private void DrainMissLoadToSlice(IssuedInstr issued, CheckpointEntry entry, int returnCountdown) {
        _prf.MarkNav(issued.PhysDest);
        _prf.MarkAbandoned(issued.PhysDest); // this name is only written again if the rename filter keeps it
        entry.IsNav = true;
        IReadOnlyList<int> srcs = issued.Instr.SourceRegisters;
        _sdb!.Append(
            new SdbEntry {
                Instruction = issued.Instr,
                Pc = issued.Pc,
                InstrId = issued.InstrId,
                CheckpointSeq = entry.CheckpointSeq,
                Entry = entry,
                Src1Tag = srcs.Count > 0 ? 0 : -1,
                Src1IsValue = srcs.Count > 0,
                Src1Value = issued.Src1,
                Src2Tag = srcs.Count > 1 ? 0 : -1,
                Src2IsValue = srcs.Count > 1,
                Src2Value = issued.Src2,
                Src3Tag = srcs.Count > 2 ? 0 : -1,
                Src3IsValue = srcs.Count > 2,
                Src3Value = issued.Src3,
                DestPhys = issued.PhysDest,
                DestArch = entry.ArchDestination,
                LqIdx = entry.LqIdx,
                SqIdx = entry.SqIdx,
                PredictedNextPc = entry.PredictedNextPc,
            }
        );
        _pendingSlices.Add((issued.InstrId, returnCountdown));
        _cfpSlicesCounter?.Increment();
    }

    /// <summary>
    ///     Drains an issue-queue entry whose sources are all either ready or NAV into the SDB:
    ///     ready source values are recorded (releasing those registers' references — the paper's
    ///     "mark as read"), NAV sources keep their physical names for the slice remapper, and the
    ///     destination is NAV-tagged so transitively dependent instructions drain too.
    /// </summary>
    private void DrainRsEntryToSlice(IssueQueue iq, int slot) {
        RsEntry rs = iq.At(slot);
        CheckpointEntry entry = _entryByInstrId[rs.InstrId];

        // NAV sources are "read" at drain: release their references now.
        if (rs is { Src1Tag: >= 0, Src1Ready: false, }) _prf.Release(rs.Src1Tag);
        if (rs is { Src2Tag: >= 0, Src2Ready: false, }) _prf.Release(rs.Src2Tag);
        if (rs is { Src3Tag: >= 0, Src3Ready: false, }) _prf.Release(rs.Src3Tag);

        if (rs.PhysDestination >= 0) {
            _prf.MarkNav(rs.PhysDestination);
            _prf.MarkAbandoned(rs.PhysDestination);
        }

        if (entry.SqIdx >= 0) _hsq.At(entry.SqIdx).IsNav = true;
        entry.IsNav = true;

        _sdb!.Append(
            new SdbEntry {
                Instruction = rs.Instruction!,
                Pc = rs.Pc,
                InstrId = rs.InstrId,
                CheckpointSeq = entry.CheckpointSeq,
                Entry = entry,
                Src1Tag = rs.Src1Tag,
                Src1IsValue = rs is { Src1Tag: >= 0, Src1Ready: true, },
                Src1Value = rs.Src1Value,
                Src2Tag = rs.Src2Tag,
                Src2IsValue = rs is { Src2Tag: >= 0, Src2Ready: true, },
                Src2Value = rs.Src2Value,
                Src3Tag = rs.Src3Tag,
                Src3IsValue = rs is { Src3Tag: >= 0, Src3Ready: true, },
                Src3Value = rs.Src3Value,
                DestPhys = rs.PhysDestination,
                DestArch = entry.ArchDestination,
                LqIdx = entry.LqIdx,
                SqIdx = entry.SqIdx,
                PredictedNextPc = rs.PredictedNextPc,
            }
        );
        iq.Free(slot);
        _cfpSlicesCounter?.Increment();
    }

    private void TickPendingSlices() {
        for (int i = _pendingSlices.Count - 1; i >= 0; i--) {
            (ulong id, int countdown) = _pendingSlices[i];
            if (--countdown <= 0)
                _pendingSlices.RemoveAt(i);
            else
                _pendingSlices[i] = (id, countdown);
        }
    }

    private bool IsSliceStillPending(ulong instrId) {
        foreach ((ulong id, _) in _pendingSlices)
            if (id == instrId)
                return true;
        return false;
    }

    /// <summary>
    ///     Re-inserts up to issueWidth slice instructions from the SDB head into the issue queues,
    ///     remapping their physical registers: a still-live-out destination (the RAT still maps
    ///     its architectural register to the original name — the rename filter check) keeps its
    ///     register; everything else acquires a fresh one via back-end renaming. NAV source names
    ///     resolve through the slice remapper; recorded values are immediate.
    /// </summary>
    private void StepSliceReinsert() {
        if (_sdb is null) return;
        var reinserted = 0;
        while (reinserted < _issueWidth && !_sdb.IsEmpty) {
            SdbEntry head = _sdb.Head;
            if (IsSliceStillPending(head.InstrId)) break; // strict FIFO: wait for this miss to return

            if (!_entryByInstrId.ContainsKey(head.InstrId)) {
                _sdb.PopHead(); // squashed while parked (belt: SquashFromCheckpoint should have removed it)
                continue;
            }

            IssueQueue iq = _iqs[IqIndex(head.Instruction.Class)];
            if (iq.IsFull) break;

            CheckpointEntry entry = head.Entry;
            int newDest = head.DestPhys;
            if (head.DestPhys >= 0) {
                bool liveOut = head.DestArch > 0 && _rat.Lookup(head.DestArch) == head.DestPhys;
                if (liveOut) {
                    // Rename filter (ASPLOS 2004 §4.2.2): no later instruction remapped this
                    // logical register, so front-end consumers still name this physical register.
                    // Keep it; waiting consumers now block on its broadcast instead of draining.
                    _prf.ClearNav(head.DestPhys);
                    _sliceRemap[head.DestPhys] = head.DestPhys;
                }
                else {
                    if (!_rat.HasFree) break; // retry next cycle; the CFP reserve exists for this
                    newDest = _rat.AllocatePhysical();
                    _prf.MarkPending(newDest);
                    _prf.MarkUnmapped(newDest); // back-end renamed: no architectural register ever maps it
                    _sliceRemap[head.DestPhys] = newDest;
                }

                // Pre-add one reference per slice consumer still parked in the SDB (they released
                // their reference on the old name when they drained); each resolves — captures at
                // broadcast or reads immediately — exactly one of these on its own re-insertion.
                foreach (SdbEntry rest in _sdb.InOrder()) {
                    if (ReferenceEquals(rest, head)) continue;
                    if (rest is { Src1Tag: >= 0, Src1IsValue: false, } && rest.Src1Tag == head.DestPhys)
                        _prf.AddRef(newDest);
                    if (rest is { Src2Tag: >= 0, Src2IsValue: false, } && rest.Src2Tag == head.DestPhys)
                        _prf.AddRef(newDest);
                    if (rest is { Src3Tag: >= 0, Src3IsValue: false, } && rest.Src3Tag == head.DestPhys)
                        _prf.AddRef(newDest);
                }

                if (!liveOut) TryReclaim(head.DestPhys); // the old name was abandoned at drain

                entry.PhysDestination = newDest;
                entry.PhysDestGen = _prf.AllocationGeneration(newDest);
            }

            if (head.SqIdx >= 0) _hsq.At(head.SqIdx).IsNav = false;
            entry.IsNav = false;

            int slot = iq.Allocate();
            RsEntry rs = iq.At(slot);
            rs.InstrId = head.InstrId;
            rs.Instruction = head.Instruction;
            rs.Pc = head.Pc;
            rs.PredictedNextPc = head.PredictedNextPc;
            rs.PhysDestination = newDest;
            rs.RobIndex = -1;
            FillReinsertedSource(rs, 0, head.Src1Tag, head.Src1IsValue, head.Src1Value);
            FillReinsertedSource(rs, 1, head.Src2Tag, head.Src2IsValue, head.Src2Value);
            FillReinsertedSource(rs, 2, head.Src3Tag, head.Src3IsValue, head.Src3Value);

            _sdb.PopHead();
            _cfpReinsertionsCounter?.Increment();
            reinserted++;
        }
    }

    private void FillReinsertedSource(RsEntry rs, int index, int tag, bool isValue, ulong value) {
        var ready = false;
        int resolvedTag = -1;
        ulong resolvedValue = 0;
        if (tag >= 0) {
            if (isValue) {
                ready = true;
                resolvedValue = value;
            }
            else {
                resolvedTag = _sliceRemap.GetValueOrDefault(tag, tag);
                if (_prf.IsReady(resolvedTag) && !_prf.IsNav(resolvedTag)) {
                    ready = true;
                    resolvedValue = _prf.Read(resolvedTag);
                    _prf.Release(resolvedTag); // consumes the reference the producer pre-added
                    TryReclaim(resolvedTag);
                }
            }
        }

        switch (index) {
            case 0:
                rs.Src1Tag = ready ? -1 : resolvedTag;
                rs.Src1Ready = ready;
                rs.Src1Value = resolvedValue;
                break;
            case 1:
                rs.Src2Tag = ready ? -1 : resolvedTag;
                rs.Src2Ready = ready;
                rs.Src2Value = resolvedValue;
                break;
            default:
                rs.Src3Tag = ready ? -1 : resolvedTag;
                rs.Src3Ready = ready;
                rs.Src3Value = resolvedValue;
                break;
        }
    }

    // ── Issue ──────────────────────────────────────────────────────────────────

    private void StepIssue() {
        Span<int> classIssued = stackalloc int[16];
        var issued = 0;

        // CFP NAV scan: entries whose every source is ready-or-NAV, with at least one NAV,
        // drain into the SDB through the pipeline instead of executing (they occupy drain
        // bandwidth, hence count against issue width).
        if (_enableCfp && _sdb is not null)
            foreach (IssueQueue iq in _iqs)
                for (var slot = 0; slot < iq.Capacity && issued < _issueWidth && !_sdb.IsFull; slot++) {
                    RsEntry rs = iq.At(slot);
                    if (!rs.Busy || !IsNavDrainable(rs)) continue;
                    DrainRsEntryToSlice(iq, slot);
                    issued++;
                }

        for (var iqIdx = 0; iqIdx < CprPipelineCore.IqCount && issued < _issueWidth; iqIdx++) {
            IssueQueue iq = _iqs[iqIdx];
            for (var slot = 0; slot < iq.Capacity && issued < _issueWidth; slot++) {
                RsEntry rs = iq.At(slot);
                if (!rs.Busy || !rs.IsReady) continue;
                if (TryIssueSlot(iq, slot, classIssued)) issued++;
            }
        }
    }

    private bool IsNavDrainable(RsEntry rs) {
        var anyNav = false;
        if (rs is { Src1Tag: >= 0, Src1Ready: false, }) {
            if (!_prf.IsNav(rs.Src1Tag)) return false;
            anyNav = true;
        }

        if (rs is { Src2Tag: >= 0, Src2Ready: false, }) {
            if (!_prf.IsNav(rs.Src2Tag)) return false;
            anyNav = true;
        }

        if (rs is { Src3Tag: >= 0, Src3Ready: false, }) {
            if (!_prf.IsNav(rs.Src3Tag)) return false;
            anyNav = true;
        }

        return anyNav;
    }

    private bool TryIssueSlot(IssueQueue iq, int slot, Span<int> classIssued) {
        RsEntry rs = iq.At(slot);
        ToothClass cls = rs.Instruction?.Class ?? ToothClass.IntegerAlu;
        int fuSlot = FuLatencyConfig.BudgetSlot(cls);
        if (classIssued[fuSlot] >= _fuConfig.CountFor(cls)) return false;

        CheckpointEntry entry = _entryByInstrId[rs.InstrId];

        // Serialized instructions issue only when architecturally oldest: they were forced to be
        // entry 0 of their own checkpoint, so "oldest" means their checkpoint is the head with
        // nothing committed yet.
        if (IsSerialized(cls)) {
            Checkpoint head = _cpList.Head;
            if (head.Seq != entry.CheckpointSeq || head.CommittedCount != 0
                                                || head.Entries.Count == 0 || head.Entries[0].InstrId != rs.InstrId)
                return false;
        }

        if (cls == ToothClass.Load) {
            // TSO store→load fence: no load issues past an uncommitted fence.
            if (HasPrecedingStoreLoadFence(rs.InstrId)) return false;

            // Memory dependence prediction (store sets — the CFP paper's own CPR baseline):
            // stall behind the predicted producing store until its address resolves. If that
            // store is itself NAV, the load joins the slice instead of stalling (ASPLOS 2004
            // §4.2.1: NAV flows from stores to dependent loads through the predictor).
            if (entry.LqIdx >= 0) {
                LqEntry lq = _lq.At(entry.LqIdx);
                if (lq.PredStoreSeqNo != 0 && lq.PredStoreSeqNo < lq.SeqNo)
                    foreach (SqEntry sq in _hsq.InOrder()) {
                        if (sq.SeqNo > lq.PredStoreSeqNo) break;
                        if (sq.SeqNo != lq.PredStoreSeqNo || sq.AddressKnown) continue;
                        if (_enableCfp && sq.IsNav && _sdb is { IsFull: false, }) {
                            DrainRsEntryToSlice(iq, slot);
                            return true; // drained: consumes issue bandwidth
                        }

                        return false;
                    }
            }
        }

        _execBuffer.Add(
            new IssuedInstr(
                rs.InstrId, rs.PhysDestination, rs.Instruction!, rs.Pc,
                rs.Src1Value, rs.Src2Value, rs.Src3Value
            )
        );
        iq.Free(slot);
        classIssued[fuSlot]++;
        return true;
    }

    private bool HasPrecedingStoreLoadFence(ulong instrId) {
        foreach (Checkpoint cp in _cpList.InOrder())
            for (int i = cp.CommittedCount; i < cp.Entries.Count; i++) {
                CheckpointEntry e = cp.Entries[i];
                if (e.InstrId >= instrId) return false;
                if (e.Instruction?.IsStoreLoadFence == true) return true;
            }

        return false;
    }

    // ── Dispatch ───────────────────────────────────────────────────────────────

    private void StepDispatch() {
        while (_renameQueue.Count > 0) {
            CprRenameEntry ri = _renameQueue.Peek();
            ITooth instr = ri.Decoded;
            if (_iqs[IqIndex(instr.Class)].IsFull) break;

            bool needsLq = instr.Class is ToothClass.Load or ToothClass.Atomic;
            bool needsSq = instr.Class is ToothClass.Store or ToothClass.Atomic;
            if (needsLq && _lq.IsFull) break;
            if (needsSq && _hsq.IsFull) break;

            ulong memSeqNo = 0;
            if (needsLq || needsSq) memSeqNo = _nextMemSeqNo++;

            if (needsLq) {
                int lqIdx = _lq.Allocate();
                LqEntry lq = _lq.At(lqIdx);
                lq.RobIdx = -1;
                lq.InstrId = ri.Entry.InstrId;
                lq.SeqNo = memSeqNo;
                if (_storeSets is not null && instr.Class == ToothClass.Load)
                    lq.PredStoreSeqNo = _storeSets.OnLoadDispatch(ri.Entry.Pc);
                ri.Entry.LqIdx = lqIdx;
            }

            if (needsSq) {
                int sqIdx = _hsq.Allocate();
                SqEntry sq = _hsq.At(sqIdx);
                sq.RobIdx = -1;
                sq.InstrId = ri.Entry.InstrId;
                sq.SeqNo = memSeqNo;
                sq.Pc = ri.Entry.Pc;
                sq.StaticBytes = instr.MemoryAccessBytes;
                _storeSets?.OnStoreDispatch(ri.Entry.Pc, memSeqNo);
                ri.Entry.SqIdx = sqIdx;
            }

            IssueQueue classIq = _iqs[IqIndex(instr.Class)];
            int iqSlot = classIq.Allocate();
            RsEntry rs = classIq.At(iqSlot);
            rs.RobIndex = -1;
            rs.InstrId = ri.Entry.InstrId;
            rs.Instruction = instr;
            rs.Pc = ri.Entry.Pc;
            rs.PredictedNextPc = ri.Entry.PredictedNextPc;
            rs.PhysDestination = ri.Entry.PhysDestination;

            FillDispatchSource(rs, 0, ri.P1);
            FillDispatchSource(rs, 1, ri.P2);
            FillDispatchSource(rs, 2, ri.P3);

            _renameQueue.Dequeue();
        }

        if (_renameQueue.Count > 0) _stallsCounter.Increment();
    }

    private void FillDispatchSource(RsEntry rs, int index, int phys) {
        var ready = false;
        ulong value = 0;
        int tag = -1;
        if (phys >= 0) {
            if (_prf.IsReady(phys)) {
                // Dispatch-time capture is a register read: drop this reader's reference.
                ready = true;
                value = _prf.Read(phys);
                _prf.Release(phys);
                TryReclaim(phys);
            }
            else { tag = phys; }
        }

        switch (index) {
            case 0:
                rs.Src1Tag = tag;
                rs.Src1Ready = ready;
                rs.Src1Value = value;
                break;
            case 1:
                rs.Src2Tag = tag;
                rs.Src2Ready = ready;
                rs.Src2Value = value;
                break;
            default:
                rs.Src3Tag = tag;
                rs.Src3Ready = ready;
                rs.Src3Value = value;
                break;
        }
    }

    // ── Rename ─────────────────────────────────────────────────────────────────

    private void StepRename() {
        // The front end waits while a slice is actively draining back into the pipeline —
        // re-inserted slice instructions must win the scheduler-entry and free-register races
        // to guarantee forward progress (ASPLOS 2004 §4.1.3).
        if (SliceDrainActive) return;

        while (_decodeQueue.Count > 0 && _renameQueue.Count < _maxDecodeDepth) {
            FetchedInstr fi = _decodeQueue.Peek();

            if (fi.PreTrap is not null) {
                bool preTrapMustOpen = _cpList.IsEmpty
                                    || _cpList.Tail.Entries.Count >= _checkpointMaxInstructions;
                if (!EnsureCheckpoint(fi.Pc, preTrapMustOpen, false)) break;
                CheckpointEntry faultEntry = AppendEntry(fi.Pc, fi.InstrId, null, fi.PredictedNextPc);
                faultEntry.HasTrap = true;
                faultEntry.Trap = fi.PreTrap;
                faultEntry.IsComplete = true;
                _cpList.Tail.MarkEntryComplete();
                _decodeQueue.Dequeue();
                continue;
            }

            ITooth instr = fi.Decoded!;
            if (instr.Class is ToothClass.Vector or ToothClass.Uve)
                throw new NotSupportedException(
                    "CprTrain does not support vector/UVE instructions; use OooeTrain for vector workloads."
                );

            int destArch = instr.DestinationRegister;
            int reserve = _enableCfp ? _cfpReservedRegs : 0;
            if (destArch > 0 && _rat.FreeCount <= reserve) break; // free registers exhausted (minus CFP reserve)

            if (HasPendingSecondaryDest(instr.SourceRegisters, destArch)) break;

            bool isBranch = instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch;
            bool serialized = IsSerialized(instr.Class);
            // An empty tail checkpoint (freshly opened, or reopened by a recovery whose restart
            // instruction this is) already checkpoints exactly this point: its snapshot was taken
            // at the current RAT state and nothing has renamed since. Reuse it instead of opening
            // a duplicate — opening one would orphan the empty checkpoint at the FIFO head.
            bool tailEmpty = _cpList is { IsEmpty: false, Tail.Entries.Count: 0, };
            bool mustOpen = !tailEmpty
                         && (_cpList.IsEmpty
                          || serialized
                          || _cpList.Tail.Entries.Count >= _checkpointMaxInstructions
                          || (isBranch && _forceCheckpointAtNextBranch));
            bool wantOpen = !tailEmpty && isBranch && fi.WantsCheckpoint;
            if (!EnsureCheckpoint(fi.Pc, mustOpen, wantOpen)) break; // checkpoint buffer full on a must-open
            if (isBranch) _forceCheckpointAtNextBranch = false;

            // Source lookup before destination rename (Tomasulo invariant), adding one
            // use-counter reference per renamed reader (MICRO 2003 §4.3).
            IReadOnlyList<int> srcs = instr.SourceRegisters;
            int p1 = srcs.Count > 0 ? _rat.Lookup(srcs[0]) : -1;
            int p2 = srcs.Count > 1 ? _rat.Lookup(srcs[1]) : -1;
            int p3 = srcs.Count > 2 ? _rat.Lookup(srcs[2]) : -1;
            if (p1 >= 0) _prf.AddRef(p1);
            if (p2 >= 0) _prf.AddRef(p2);
            if (p3 >= 0) _prf.AddRef(p3);

            int newPhys = -1;
            if (destArch > 0) {
                (newPhys, int oldPhys) = _rat.Rename(destArch);
                _prf.MarkPending(newPhys);
                _prf.MarkUnmapped(oldPhys); // the unmapped leg of the reclaim condition
                TryReclaim(oldPhys);
            }

            CheckpointEntry entry = AppendEntry(fi.Pc, fi.InstrId, instr, fi.PredictedNextPc);
            entry.ArchDestination = destArch > 0 ? destArch : -1;
            entry.PhysDestination = newPhys;
            entry.PhysDestGen = newPhys >= 0 ? _prf.AllocationGeneration(newPhys) : 0;
            entry.IsStore = instr.Class == ToothClass.Store;
            entry.IsLoad = instr.Class is ToothClass.Load or ToothClass.Atomic;
            entry.IsHalt = instr.Class == ToothClass.Halt;

            _renameQueue.Enqueue(new CprRenameEntry(entry, instr, p1, p2, p3));
            _decodeQueue.Dequeue();
        }
    }

    /// <summary>
    ///     Opens a new checkpoint at the tail if required. Returns false when a mandatory open is
    ///     blocked by a full checkpoint buffer (rename stalls). A merely-wanted open (low-confidence
    ///     branch) is silently skipped when the buffer is full — the paper's no-stall rule.
    /// </summary>
    private bool EnsureCheckpoint(ulong restartPc, bool mustOpen, bool wantOpen) {
        if (!mustOpen && !wantOpen) return true;
        if (_cpList.IsFull) return !mustOpen;
        Checkpoint cp = _cpList.Open(_rat.SnapshotRat(), restartPc, _nextCheckpointSeq++);
        foreach (int p in cp.RatSnapshot) _prf.AddRef(p); // checkpoints are readers of their snapshot registers
        _checkpointsCreatedCounter.Increment();
        return true;
    }

    private CheckpointEntry AppendEntry(ulong pc, ulong instrId, ITooth? instr, ulong predictedNextPc) {
        Checkpoint tail = _cpList.Tail;
        CheckpointEntry entry = tail.Append();
        entry.Pc = pc;
        entry.InstrId = instrId;
        entry.Instruction = instr;
        entry.CheckpointSeq = tail.Seq;
        entry.PredictedNextPc = predictedNextPc;
        _entryByInstrId[instrId] = entry;
        return entry;
    }

    private bool HasPendingSecondaryDest(IReadOnlyList<int> srcs, int destArch) {
        foreach (Checkpoint cp in _cpList.InOrder())
            for (int i = cp.CommittedCount; i < cp.Entries.Count; i++) {
                int sd = cp.Entries[i].Instruction?.SecondaryDestinationRegister ?? -1;
                if (sd >= 0 && (srcs.Contains(sd) || sd == destArch)) return true;
            }

        foreach (CprRenameEntry ri in _renameQueue) {
            int sd = ri.Decoded.SecondaryDestinationRegister;
            if (sd >= 0 && (srcs.Contains(sd) || sd == destArch)) return true;
        }

        return false;
    }

    // ── Fetch ──────────────────────────────────────────────────────────────────

    private void StepFetch() {
        if (_fetchFaulted) return;

        var fetched = 0;
        while (fetched < _issueWidth && _decodeQueue.Count < _maxDecodeDepth) {
            ulong physPc = _fetchPc;
            if (_fetchTranslator is not null) {
                (ulong pa, int faultCause) = _fetchTranslator.Translate(_fetchPc);
                if (faultCause != 0) {
                    _decodeQueue.Enqueue(
                        new FetchedInstr(
                            _fetchPc, null, _fetchPc, _nextInstrId++,
                            new TrapInfo(faultCause, _fetchPc, _fetchPc)
                        )
                    );
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
                _decodeQueue.Enqueue(
                    new FetchedInstr(
                        _fetchPc, null, _fetchPc, _nextInstrId++,
                        new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, _fetchPc)
                    )
                );
                _fetchFaulted = true;
                break;
            }
            catch (AccessViolationException) {
                _decodeQueue.Enqueue(
                    new FetchedInstr(
                        _fetchPc, null, _fetchPc, _nextInstrId++,
                        new TrapInfo(TrapCause.InstructionAccessFault, _fetchPc, _fetchPc)
                    )
                );
                _fetchFaulted = true;
                break;
            }

            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            var wantsCheckpoint = false;
            if (hint.IsBranch) {
                if (hint.IsCall) _ras.Push(_fetchPc + (ulong)decoded.SizeBytes);

                BranchPrediction pred;
                if (hint.IsReturn && _ras.TryPop(out ulong ret))
                    pred = BranchPrediction.Taken(ret);
                else if (hint is { IsUnconditional: true, BranchTarget.HasValue: true, })
                    pred = BranchPrediction.Taken(hint.BranchTarget.Value);
                else
                    pred = _predictor.Predict(_fetchPc, hint.BranchTarget);

                ulong fallThrough = _fetchPc + (ulong)decoded.SizeBytes;
                ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
                predictedNext = pred.PredictedTaken && takenTarget != 0 ? takenTarget : fallThrough;

                // Recorded-outcome replay after a recovery: the b-th branch refetched takes the
                // previously resolved target instead of a (possibly repeat-wrong) prediction.
                if (_replayBranchesRemaining > 0 && --_replayBranchesRemaining == 0) predictedNext = _replayTarget;

                _predictor.SpeculativeHistoryUpdate(_fetchPc, predictedNext != fallThrough);

                // Selective checkpointing: only low-confidence branches warrant a checkpoint.
                // Statically-known unconditional direct jumps never mispredict — skip those.
                if (!(hint is { IsUnconditional: true, BranchTarget.HasValue: true, IsReturn: false, }))
                    wantsCheckpoint = _confidence.IsLowConfidence(_fetchPc);
            }
            else { predictedNext = _fetchPc + (ulong)decoded.SizeBytes; }

            _decodeQueue.Enqueue(
                new FetchedInstr(_fetchPc, decoded, predictedNext, _nextInstrId++, WantsCheckpoint: wantsCheckpoint)
            );
            _fetchPc = predictedNext;
            fetched++;
        }
    }

    // ── Recovery and flush ─────────────────────────────────────────────────────

    /// <summary>
    ///     Checkpoint recovery: restores the target checkpoint's RAT snapshot in one shot,
    ///     squashes every younger instruction (balancing all use-counter references they hold),
    ///     rebuilds the free list via a reclaim sweep, and restarts fetch at the checkpoint's
    ///     first instruction. No per-instruction RAT walk-back — that is the whole point.
    /// </summary>
    private void ApplyRecovery() {
        _recoveryPending = false;
        _recoveriesCounter.Increment();
        _covhdCounter.IncrementBy(_recoveryCovhd);

        Checkpoint? target = FindCheckpointBySeq(_recoveryTargetSeq);
        if (target is null || target.Entries.Count == 0) return; // already unwound by an older event
        ulong boundaryInstrId = target.FirstInstrId;

        // Issue queues: squash entries at/after the boundary, releasing uncaptured source refs.
        foreach (IssueQueue iq in _iqs)
            for (var slot = 0; slot < iq.Capacity; slot++) {
                RsEntry rs = iq.At(slot);
                if (!rs.Busy || rs.InstrId < boundaryInstrId) continue;
                if (rs is { Src1Tag: >= 0, Src1Ready: false, }) _prf.Release(rs.Src1Tag);
                if (rs is { Src2Tag: >= 0, Src2Ready: false, }) _prf.Release(rs.Src2Tag);
                if (rs is { Src3Tag: >= 0, Src3Ready: false, }) _prf.Release(rs.Src3Tag);
                iq.Free(slot);
            }

        // Rename queue: nothing dispatched yet, so every source reference is still held.
        foreach (CprRenameEntry ri in _renameQueue) {
            if (ri.P1 >= 0) _prf.Release(ri.P1);
            if (ri.P2 >= 0) _prf.Release(ri.P2);
            if (ri.P3 >= 0) _prf.Release(ri.P3);
        }

        _renameQueue.Clear();
        _decodeQueue.Clear();
        _execBuffer.RemoveAll(e => e.InstrId >= boundaryInstrId);
        _inFlight.RemoveAll(f => f.Result.InstrId >= boundaryInstrId);
        _cdbBuffer.RemoveAll(r => r.InstrId >= boundaryInstrId);
        _pendingSlices.RemoveAll(s => s.InstrId >= boundaryInstrId);
        _sdb?.SquashFromCheckpoint(_recoveryTargetSeq);
        _lq.TruncateYoungerThan(boundaryInstrId - 1);
        _hsq.TruncateYoungerThan(boundaryInstrId - 1);

        // Unwind checkpoints younger than the target, then reopen the target itself. Squashed
        // instructions "drain out of the pipe decrementing any counters they incremented"
        // (MICRO 2003 §4.3) — modeled by the releases above plus destination marking here.
        while (_cpList.Tail.Seq > _recoveryTargetSeq) {
            Checkpoint cp = _cpList.DiscardTail();
            SquashCheckpointEntries(cp);
            foreach (int p in cp.RatSnapshot) _prf.Release(p);
            cp.Clear();
        }

        SquashCheckpointEntries(target);
        target.ReopenForRecovery();

        // Restore the map table from the checkpoint — the one-shot recovery.
        _rat.RestoreRat(target.RatSnapshot);
        foreach (int p in target.RatSnapshot) _prf.MarkMapped(p);
        for (var p = 0; p < _prf.Count; p++) TryReclaim(p);

        // Speculative RAS: committed shadow + replay of surviving in-flight calls/returns.
        _ras.CopyFrom(_committedRas);
        foreach (Checkpoint cp in _cpList.InOrder()) {
            if (cp.Seq >= _recoveryTargetSeq) break;
            for (int i = cp.CommittedCount; i < cp.Entries.Count; i++)
                if (cp.Entries[i].Instruction is { Class: ToothClass.Branch, } ins) {
                    FetchHint hint = _decoder.GetFetchHint(cp.Entries[i].Pc, ins.RawEncoding);
                    if (hint.IsCall) _ras.Push(cp.Entries[i].Pc + (ulong)ins.SizeBytes);
                    if (hint.IsReturn) _ras.TryPop(out _);
                }
        }

        _predictor.RecoverSpeculativeHistory();

        _fetchPc = target.RestartPc;
        _fetchFaulted = false;
        if (_recoveryIsBranch) {
            _branchMissCounter.Increment();
            _replayBranchesRemaining = _recoveryReplayDistance;
            _replayTarget = _recoveryReplayTarget;
            _forceCheckpointAtNextBranch = true; // guarantee forward progress (MICRO 2003 §4.1.1)
        }
        else { _replayBranchesRemaining = 0; }
    }

    private void SquashCheckpointEntries(Checkpoint cp) {
        foreach (CheckpointEntry e in cp.Entries) {
            _entryByInstrId.Remove(e.InstrId);
            if (e.PhysDestination < 0) continue;
            // Only mark the destination if this entry still owns the allocation — aggressive
            // reclamation may have freed and re-allocated it (to a younger, also-squashed
            // instruction, or to a surviving slice via back-end renaming) in the meantime.
            if (_prf.AllocationGeneration(e.PhysDestination) != e.PhysDestGen) continue;
            _prf.MarkUnmapped(e.PhysDestination);
            _prf.MarkAbandoned(e.PhysDestination); // no write will arrive: its producer is squashed
        }
    }

    private void SetFullFlush(ulong target) {
        _fullFlushPending = true;
        _fullFlushTarget = target;
    }

    /// <summary>
    ///     Full flush (trap / interrupt / mret): everything in flight is discarded and the rename
    ///     state is rebuilt from the committed architectural state — identity RAT, PRF loaded from
    ///     the architectural registers, everything else free.
    /// </summary>
    private void ApplyFullFlush() {
        _fullFlushPending = false;
        _flushesCounter.Increment();

        _cpList.Flush();
        foreach (IssueQueue iq in _iqs) iq.Flush();
        _lq.Flush();
        _hsq.Flush();
        _decodeQueue.Clear();
        _renameQueue.Clear();
        _execBuffer.Clear();
        _inFlight.Clear();
        _cdbBuffer.Clear();
        _entryByInstrId.Clear();
        _sdb?.Flush();
        _pendingSlices.Clear();
        _sliceRemap.Clear();

        _rat.Reset();
        _prf.Reset();
        int archRegs = State.IntegerRegisters.Count;
        for (var a = 0; a < archRegs; a++) _prf.Write(a, State.IntegerRegisters.Read(a));
        _prf.InitializeAllocation(archRegs);

        _ras.CopyFrom(_committedRas);
        _predictor.RecoverSpeculativeHistory();

        _recoveryPending = false;
        _replayBranchesRemaining = 0;
        _forceCheckpointAtNextBranch = false;
        _fetchPc = _fullFlushTarget;
        _fetchFaulted = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private Checkpoint? FindCheckpointBySeq(ulong seq) {
        foreach (Checkpoint cp in _cpList.InOrder())
            if (cp.Seq == seq)
                return cp;
        return null;
    }

    /// <summary>Returns a register to the free list if the CPR reclaim condition holds.</summary>
    private void TryReclaim(int phys) {
        if (!_prf.IsReclaimable(phys)) return;
        _prf.MarkFreed(phys);
        _rat.FreePhysical(phys);
    }

    private void ChargeStallCycles(long n) {
        if (n <= 0) return;
        _cyclesCounter.IncrementBy(n);
        _stallsCounter.IncrementBy(n);
        _cacheMissStallsCounter?.IncrementBy(n);
        for (long i = 0; i < n; i++) State.OnCycle();
    }

    private long DrainAndChargeStalls() {
        long stalls = ILayers.ConsumeAllStalls() + DLayers.ConsumeAllStalls();
        UpdateCacheStat(
            ILayers.Cache, _icacheHitsCounter, _icacheMissesCounter, ref _lastIHits, ref _lastIMisses
        );
        UpdateCacheStat(
            ILayers.L2Cache, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L3Cache, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
        );
        UpdateCacheStat(
            DLayers.Cache, _dcacheHitsCounter, _dcacheMissesCounter, ref _lastDHits, ref _lastDMisses
        );
        UpdateCacheStat(
            DLayers.L2Cache, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDl2Hits, ref _lastDl2Misses
        );
        UpdateCacheStat(
            DLayers.L3Cache, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateTlbStat(
            ILayers.Tlb, _itlbHitsCounter, _itlbMissesCounter, ref _lastITlbHits, ref _lastITlbMisses
        );
        UpdateTlbStat(
            DLayers.Tlb, _dtlbHitsCounter, _dtlbMissesCounter, ref _lastDTlbHits, ref _lastDTlbMisses
        );
        return stalls;
    }

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

    // ── Nested helper types ────────────────────────────────────────────────────

    private readonly record struct FetchedInstr(
        ulong Pc,
        ITooth? Decoded,
        ulong PredictedNextPc,
        ulong InstrId = 0,
        TrapInfo? PreTrap = null,
        bool WantsCheckpoint = false
    );

    /// <summary>Renamed but not yet dispatched. The checkpoint entry already exists (appended at rename).</summary>
    private sealed record CprRenameEntry(
        CheckpointEntry Entry,
        ITooth Decoded,
        int P1,
        int P2,
        int P3
    );

    private readonly record struct IssuedInstr(
        ulong InstrId,
        int PhysDest,
        ITooth Instr,
        ulong Pc,
        ulong Src1,
        ulong Src2,
        ulong Src3
    );

    private readonly record struct ExecResult(
        ulong InstrId,
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
        bool RequestHalt = false,
        Action<IArchState>? SideEffect = null,
        int LatencyOverride = 0 // per-instruction FU latency from ExecuteResult.LatencyOverride; 0 = use FuLatencyConfig
    );

    /// <summary>
    ///     Passes reads through to backing memory recording address and raw value (the value is
    ///     needed for byte-merged store forwarding); captures writes instead of executing them so
    ///     stores are deferred to bulk commit.
    /// </summary>
    private sealed class CapturingMemory(IMemory backing) : IMemory {
        public bool HasWrite { get; private set; }
        public ulong WriteAddress { get; private set; }
        public ulong WriteValue { get; private set; }
        public int WriteBytes { get; private set; }

        public bool HasRead { get; private set; }
        public ulong ReadAddress { get; private set; }
        public int ReadBytes { get; private set; }
        public ulong ReadValue { get; private set; }

        public ulong Read(ulong address, int bytes) {
            HasRead = true;
            ReadAddress = address;
            ReadBytes = bytes;
            ReadValue = backing.Read(address, bytes);
            return ReadValue;
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
        }
    }
}