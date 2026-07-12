using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Streaming;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Ooo;

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

public sealed class OooeTrain : ISteppableTrain {
    private readonly Train _train;
    private readonly OoOPipelineCore _core;

    public IArchState ArchState => _core.State;

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public RdipPrefetcher? Rdip => _core.Rdip;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;
    public PEventLog? PEventLog => _core.PEventLog;
    public StreamingEngine StreamingEngine => _core.StreamingEngine;

    public OooeTrain(
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
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false,
        int fdipFtqCapacity = 0,
        bool rdip = false,
        bool enableStoreSets = false
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
                commitObserver,
                lqCapacity,
                sqCapacity,
                writeBufferCapacity,
                mshrCapacity,
                flatIq,
                memory,
                fdipFtqCapacity,
                rdip,
                enableStoreSets
            )
        );
        _train.Build();
    }

    internal OooeTrain(
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
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false
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
                commitObserver,
                lqCapacity,
                sqCapacity,
                writeBufferCapacity,
                mshrCapacity,
                flatIq
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public DialBoardSnapshot SnapshotPipeline() => _core.Dials.Snapshot();

    public long CurrentTick => _train.CurrentTick;
    public bool IsIdle => _train.IsIdle;
    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
/// Superscalar out-of-order pipeline Gear using Tomasulo's algorithm.
/// <para>
/// Pipeline stages (cross-tick latches connect them):
///   Fetch → [decodeQueue] → Dispatch → [IssueQueue] → Issue
///     → [_execBuffer] → Execute → [_cdbBuffer] → Complete → [ROB] → Commit
/// </para>
/// <para>
/// All six logical stages execute within a single RunCycle tick, reading from
/// the latch populated by the previous tick. Minimum end-to-end latency for
/// an independent instruction is ~4 ticks (Fetch, Dispatch, Execute, Complete+Commit).
/// </para>
/// </summary>
internal sealed class OoOPipelineCore : Gear {
    // ── Nested helper types ────────────────────────────────────────────────────

    private readonly record struct FetchedInstr(
        ulong Pc,
        ITooth? Decoded,
        ulong PredictedNextPc,
        ulong InstrId = 0,
        TrapInfo? PreTrap = null,
        BranchHistoryCheckpoint HistCheckpoint = default
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
        BranchHistoryCheckpoint HistCheckpoint = default
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
        bool LoadWasForwarded,    // true if TryForwardFromStore supplied the register value
        bool RequestHalt = false, // true for an HTIF tohost-exit store: halt after commit
        ulong InstrId = 0,        // per-instruction age, for pruning in-flight results on a partial squash
        Action<IArchState>? SideEffect = null // deferred to Commit for scalar ops; null for vec/uve (applied at Execute)
    );

    /// <summary>
    /// Passes reads through to backing memory while recording the last read address;
    /// captures writes instead of executing them. Used to defer store writes until
    /// ROB commit and to capture load addresses for memory-ordering checks.
    /// </summary>
    private sealed class CapturingMemory(IMemory backing) : IMemory {
        public bool HasWrite { get; private set; }
        public ulong WriteAddress { get; private set; }
        public ulong WriteValue { get; private set; }
        public int WriteBytes { get; private set; }

        public bool HasRead { get; private set; }
        public ulong ReadAddress { get; private set; }
        public int ReadBytes { get; private set; }

        public void Reset() {
            HasWrite = false;
            HasRead = false;
        }

        public ulong Read(ulong address, int bytes) {
            HasRead = true;
            ReadAddress = address;
            ReadBytes = bytes;
            return backing.Read(address, bytes);
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
    }

    // ISA services
    private readonly IDecoder _decoder;
    private readonly IExecutor _executor;
    private readonly ITrapController _trapController;
    private readonly IBranchPredictor _predictor;

    private readonly ReturnAddressStack _ras = new();

    // Architectural RAS shadow: updated only when a call/return actually retires.
    // On a flush the speculative _ras is restored from this, discarding the wrong-path
    // push/pop corruption that would otherwise cascade into return mispredictions.
    private readonly ReturnAddressStack _committedRas = new();
    private readonly IFetchTranslator? _fetchTranslator;
    private readonly CapturingMemory _capMem;
    private readonly FuLatencyConfig _fuConfig;
    private readonly ICommitObserver? _commitObserver;

    // Streaming engine (architectural; survives pipeline flushes)
    public StreamingEngine StreamingEngine { get; }

    // Memory hierarchy layers
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }

    // IQ index 0=INT(Alu/MulDiv/Sys/Fence/Halt), 1=FP, 2=BR, 3=VEC(Vector/UVE), 4=LSU
    private const int IqCount = 5;

    // In per-class mode each IQ has iqCapacity slots; in flat mode all instructions
    // go to IQ[0] which has IqCount×iqCapacity slots so total capacity is the same.
    private readonly bool _flatIq;
    private readonly int _activeIqCount; // 1 when flat, IqCount when per-class

    private int IqIndex(ToothClass cls) => _flatIq
        ? 0
        : cls switch {
            ToothClass.FloatingPoint or ToothClass.FloatDivSqrt      => 1,
            ToothClass.Branch or ToothClass.ConditionalBranch        => 2,
            ToothClass.Vector or ToothClass.Uve                      => 3,
            ToothClass.Load or ToothClass.Store or ToothClass.Atomic => 4,
            _                                                        => 0,
        };

    // OoOE structures
    private readonly PhysicalRegisterFile _prf;
    private readonly RenameMap _rat;
    private readonly ReorderBuffer _rob;
    private readonly IssueQueue[] _iqs;
    private readonly LoadQueue _lq;
    private readonly StoreQueue _sq;
    private readonly int _issueWidth;
    private readonly int _maxDecodeDepth;

    // Monotonically increasing sequence number assigned at dispatch to each
    // load/store/atomic. Shared between LQ and SQ so that program-order comparisons
    // across the two queues don't rely on ROB index arithmetic (which wraps).
    // Not reset on flush — entries are discarded by Flush(), the counter climbs.
    private ulong _nextMemSeqNo;

    // Write buffer: absorbs post-commit store write-miss stalls so the pipeline
    // doesn't freeze for them. Each slot holds a countdown (in cycles) until the
    // corresponding write bus penalty expires. Capacity 0 disables the feature
    // and falls back to lump-sum charging (old behaviour).
    private readonly int _wbCapacity;
    private readonly int[] _wbSlots; // per-slot miss countdown
    private int _wbOccupied;         // number of slots currently counting down

    // MSHR (Miss Status Holding Register) capacity: limits the number of simultaneously
    // outstanding load/atomic cache misses. Capacity 0 means unlimited (old behaviour).
    private readonly int _mshrCapacity;
    private int _mshrUsed; // MSHR slots currently occupied

    // In-flight prefetches also hold miss-tracking slots. A demand hit on an in-flight
    // line retires the prefetch entry and transfers its remaining latency (and MSHR slot)
    // to the load itself, so the two counts never overlap.
    private int InFlightPrefetches => _realisticPrefetch ? DLayers.Cache!.InFlightPrefetchCount : 0;

    // Cross-tick latches
    private readonly Queue<FetchedInstr> _decodeQueue = new();
    private readonly Queue<RenameEntry> _renameQueue = new();
    private readonly List<IssuedInstr> _execBuffer = [];
    private readonly List<(int Countdown, ExecResult Result, bool HoldsMshr)> _inFlight = [];
    private readonly List<ExecResult> _cdbBuffer = [];

    // Runtime state
    private ulong _fetchPc;
    private bool _halted;
    private bool _flushPending;
    private ulong _flushTarget;

    // Pending rename rollback for a trap/return-from-trap instruction that retires (leaves the
    // ROB) as part of raising the flush itself. StepFlush's walk-back only sees entries still in
    // the ROB, so this instruction's own rename must be undone separately — and specifically
    // *after* that walk-back, since younger (already-flushed) entries may have chained their
    // PrevPhysDestination through this instruction's PhysDestination, and undoing them first
    // would overwrite this rollback if applied before them. Set to -1 when nothing is pending.
    private int _pendingRollbackArch = -1;
    private int _pendingRollbackPrevPhys = -1;
    private int _pendingRollbackAbandonedPhys = -1;

    // Execute-time partial squash (branch mispredict resolved before the branch reaches the ROB
    // head). Detected in StepComplete, applied at the flush-check like a full flush but preserving
    // the redirecting branch and every older in-flight instruction. gem5 O3CPU redirects fetch at
    // execute (iew) the same way; this is the microarchitectural analogue.
    private bool _squashPending;
    private ulong _squashInstrId;
    private ulong _squashTarget;
    private bool _squashTaken;
    private bool _fetchFaulted; // suppress repeated fault entries until flush clears

    // PEvent recording
    private ulong _nextInstrId = 1;
    public PEventLog? PEventLog { get; }

    // Counters (initialised in Initialize)
    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Counter _flushesCounter = null!;
    private Counter _branchMissCounter = null!;
    private Counter _stallsCounter = null!;
    private Counter _memViolationsCounter = null!;
    private Counter? _cacheMissStallsCounter;
    private Counter? _wbAbsorbedStallsCounter;
    private Counter? _mshrStallsCounter;
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _dcachePrefetchesCounter;
    private Counter? _dcacheLatePrefetchHitsCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;

    private bool _anyCache;

    // Delta tracking for hit/miss/prefetch counters
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;
    private long _lastDPrefetches, _lastDLatePrefetchHits;

    // True when a D-prefetcher is configured with PrefetchLatency > 0: prefetched lines
    // arrive after a countdown instead of instantly (realistic prefetch latency model).
    private readonly bool _realisticPrefetch;
    private readonly FdipPrefetcher? _fdip;
    private readonly RdipPrefetcher? _rdip;
    public RdipPrefetcher? Rdip => _rdip;
    private readonly StoreSetPredictor? _storeSets;
    private long _lastITlbHits, _lastITlbMisses, _lastDTlbHits, _lastDTlbMisses;

    public IArchState State { get; }

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
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0,
        bool flatIq = false,
        IMemory? fdipBackingMemory = null,
        int fdipFtqCapacity = 0,
        bool rdipEnabled = false,
        bool enableStoreSets = false
    ) : base(name, parent, esc) {
        PEventLog = pEventLog;
        _commitObserver = commitObserver;
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
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

        _rdip = rdipEnabled && iLayers.Cache is not null
            ? new RdipPrefetcher(iLayers.Cache, _decoder)
            : null;
        _storeSets = enableStoreSets ? new StoreSetPredictor() : null;

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
        StreamingEngine = new StreamingEngine(streamPrefetchDepth);
        _wbCapacity = writeBufferCapacity;
        _wbSlots = writeBufferCapacity > 0 ? new int[writeBufferCapacity] : [];
        _mshrCapacity = mshrCapacity;
    }

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _flushesCounter = Dials.AddCounter("flushes", "Pipeline flushes (branch + trap)");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions");
        _stallsCounter = Dials.AddCounter(
            "stalls", "Dispatch-stall cycles (ROB/IQ/LQ/SQ full) + cache miss penalties"
        );
        _memViolationsCounter = Dials.AddCounter(
            "mem_order_violations", "Memory-order violations: speculative load read stale data"
        );

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
            if (DLayers.Prefetcher is not null) {
                _dcachePrefetchesCounter = Dials.AddCounter("dcache_prefetches", "L1 D-cache prefetch fills");
                if (_realisticPrefetch)
                    _dcacheLatePrefetchHitsCounter = Dials.AddCounter(
                        "dcache_late_prefetch_hits",
                        "Demand hits on lines whose prefetch was still in flight (paid the remaining countdown)"
                    );
            }
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

    // Cached to avoid a fresh Action allocation per simulated cycle.
    private Action? _runCycle;

    private void RunCycle() {
        if (_halted) return;

        // Advance all active streams one prefetch step. Streams are architectural state
        // and run every cycle, independent of pipeline flush/stall.
        StreamingEngine.Step(DLayers.Accessor, State.UveScalars?.VectorLength ?? 1);

        // Charge the previous cycle's instruction-fetch (and any store-commit) stall
        // penalties. Load-miss penalties are NOT lump-summed here — StepExecute gives
        // each load its own in-flight latency so independent misses overlap
        // (memory-level parallelism); see StepExecute.
        if (_anyCache) ChargeStallCycles(DrainAndChargeStalls());

        _cyclesCounter.Increment();
        State.OnCycle();

        // Complete: broadcast last tick's execution results onto CDB.
        StepComplete();

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
        }

        if (_halted || _flushPending || _squashPending) {
            if (_flushPending)
                StepFlush();
            else if (_squashPending) StepPartialSquash();
            if (!_halted) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
            return;
        }

        // Execute: run last tick's issued instructions.
        StepExecute();

        // Issue: select up to issueWidth ready IQ entries.
        StepIssue();

        // Dispatch: allocate ROB + IQ slots from the rename queue.
        StepDispatch();

        // Rename: drain decoded instructions through the RAT/PRF rename stage.
        StepRename();

        // Fetch: fill the decode queue with new speculative instructions.
        StepFetch();

        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // ── Pipeline stages ────────────────────────────────────────────────────────

    /// <summary>CDB broadcast: apply Execute T-1 results to PRF + IQ + ROB.</summary>
    private void StepComplete() {
        // Track the oldest branch that resolves mispredicted this tick, to redirect fetch at
        // execute rather than deferring the flush to the ROB head (see StepPartialSquash).
        var haveMispredict = false;
        ulong oldestMispredId = 0;

        foreach (ExecResult r in _cdbBuffer) {
            RobEntry rob = _rob.At(r.RobIdx);
            rob.IsComplete = true;
            rob.ResolvedNextPc = r.ResolvedNextPc;
            rob.HasTrap = r.Trap is not null;
            rob.Trap = r.Trap;
            rob.IsReturnFromTrap = r.IsReturnFromTrap;
            rob.ReturnPrivilege = r.ReturnPrivilege;
            rob.RequestHalt = r.RequestHalt;
            rob.SideEffect = r.SideEffect;

            // A branch (only branches set ResolvedNextPc) that resolved off its predicted path.
            if (rob.ResolvedNextPc is { HasValue: true, Value: var resolved, } && resolved != rob.PredictedNextPc
             && (!haveMispredict || r.InstrId < oldestMispredId)) {
                haveMispredict = true;
                oldestMispredId = r.InstrId;
            }

            if (r.HasStoreCapture) {
                // Update the SQ entry with the resolved store address and value.
                SqEntry sq = _sq.At(rob.SqIdx);
                sq.AddressKnown = true;
                sq.Address = r.StoreAddr;
                sq.Value = r.StoreVal;
                sq.Width = r.StoreBytes;
                // A store's address just became known: check whether any younger speculative
                // load has already executed against the same address with a stale value.
                CheckLoadViolations(sq.SeqNo, r.StoreAddr, r.StoreBytes, sq.Pc);
                _storeSets?.OnStoreIssued(sq.Pc, sq.SeqNo);
            }

            // Load disambiguation state (LQ.Executed/Address + violation check) is
            // registered at EXECUTE time in StepExecute, not here — see the comment there.
            // Registering at broadcast time opened a window, under memory-level
            // parallelism, where a missed load sat invisible in _inFlight while an older
            // store resolved and committed, so the load broadcast a stale value.

            if (!r.RegValue.HasValue || r.PhysDest < 0) continue;
            _prf.Write(r.PhysDest, r.RegValue.Value);
            foreach (IssueQueue iq in _iqs) iq.Broadcast(r.PhysDest, r.RegValue.Value);
        }

        _cdbBuffer.Clear();

        // Arm an execute-time partial squash on the oldest branch that mispredicted this tick.
        // Skip when that branch is already the ROB head (the commit-time path flushes it — an
        // identical outcome, nothing older to overlap), or when an older in-flight halt/trap will
        // redirect or stop the machine at commit: that makes this branch definitively wrong-path,
        // and the commit-time model never counts such a mispredict because the older halt/trap
        // retires first (e.g. speculative fetch of a loop branch past a program-terminating ebreak).
        if (haveMispredict && _rob.Head.InstrId != oldestMispredId && !AnyOlderHaltOrTrap(oldestMispredId)) {
            RobEntry b = FindRobByInstrId(oldestMispredId);
            ulong resolvedPc = b.ResolvedNextPc.Value;
            _squashPending = true;
            _squashInstrId = oldestMispredId;
            _squashTarget = resolvedPc;
            _squashTaken = resolvedPc != b.Pc + (ulong)(b.Instruction?.SizeBytes ?? 4);
        }
    }

    /// <summary>In-order retirement from the ROB head.</summary>
    private void StepCommit() {
        var committed = 0;
        var dcachePortUsed = false;
        while (_rob is { IsEmpty: false, Head.IsComplete: true, } && committed < _issueWidth) {
            RobEntry head = _rob.Head;

            switch (head) {
                case { IsHalt: true, }: {
                    CommitRegisters(head);
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    RetireMemQueues(head);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    State.OnRetire();
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
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    RetireMemQueues(head);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    State.OnRetire();
                    SetFlush(target);
                    return;
                }
                case { IsReturnFromTrap: true, ReturnPrivilege: not null, }: {
                    ulong target = _trapController.ReturnFromTrap(head.ReturnPrivilege.Value, State);
                    CommitRegisters(head);
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    RetireMemQueues(head);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    State.OnRetire();
                    SetFlush(target);
                    return;
                }
            }

            // A load that executed speculatively may have read a stale value if a
            // conflicting store resolved after it. By the time the load reaches the
            // ROB head, all older instructions (including the store) have committed
            // and written memory, so re-executing the load from its own PC is safe.
            // Check LQ violation before the store-write below (matters for atomics).
            if (head.IsLoad && head.LqIdx >= 0 && _lq.At(head.LqIdx).Violated) {
                _memViolationsCounter.Increment();
                _storeSets?.RecordViolation(_lq.At(head.LqIdx).ViolatingStorePc, head.Pc);
                SetFlush(head.Pc); // re-executes from the load's PC; flush clears the ROB+LQ+SQ
                return;
            }

            // Write deferred store/atomic data to memory at commit time.
            // Real hardware has one D-cache write port: break if already used this cycle.
            if (head.SqIdx >= 0) {
                SqEntry sq = _sq.At(head.SqIdx);
                if (sq.AddressKnown) {
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
                _rdip?.OnCommit(head.Pc, head.Instruction.RawEncoding);
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
                PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                RetireMemQueues(head);
                _rob.Retire();
                _retiredCounter.Increment();
                State.OnRetire();
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
                PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                RetireMemQueues(head);
                _rob.Retire();
                _retiredCounter.Increment();
                State.OnRetire();
                _halted = true;
                return;
            }

            if (head.ResolvedNextPc.HasValue) {
                // Capture all fields from head before Retire() clears the slot.
                ulong resolvedPc = head.ResolvedNextPc.Value;
                ulong instrPc = head.Pc;
                ulong predictedPc = head.PredictedNextPc;
                int instrSize = head.Instruction?.SizeBytes ?? 4;

                bool taken = resolvedPc != instrPc + (ulong)instrSize;
                _predictor.Update(instrPc, taken, resolvedPc);

                if (resolvedPc != predictedPc) {
                    _branchMissCounter.Increment();
                    State.Pc = resolvedPc;
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    RetireMemQueues(head);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    State.OnRetire();
                    SetFlush(resolvedPc);
                    return;
                }
            }

            State.Pc = head.PredictedNextPc;
            PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
            RetireMemQueues(head);
            _rob.Retire();
            _retiredCounter.Increment();
            State.OnRetire();
            committed++;
        }

        // Check for pending interrupts when the commit loop drains normally.
        if (!_halted && !_flushPending) {
            TrapInfo? interrupt = _trapController.PeekInterrupt(State);
            if (interrupt is not null) SetFlush(_trapController.RaiseTrap(interrupt, State));
        }
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
        // miss penalty.
        if (_anyCache) ChargeStallCycles(DLayers.ConsumeAllStalls());

        // Start executing newly issued instructions.
        Span<ulong> prefBuf = stackalloc ulong[32];
        foreach (IssuedInstr issued in _execBuffer) {
            // Look up the LQ SeqNo before calling ExecuteOne so TryForwardFromStore
            // can use it for SQ ordering without any ROB index arithmetic.
            RobEntry issuedRob = _rob.At(issued.RobIdx);
            ulong lqSeqNo = issuedRob.LqIdx >= 0 ? _lq.At(issuedRob.LqIdx).SeqNo : 0;

            ExecResult result = ExecuteOne(issued, lqSeqNo);
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
            if (result.HasLoadAccess && !result.LoadWasForwarded && DLayers.Prefetcher is not null) {
                bool wasHit = DLayers.Cache?.LastAccessWasHit ?? true;
                int prefCount = DLayers.Prefetcher.OnAccess(issued.Pc, result.LoadAddr, wasHit, prefBuf);
                for (var k = 0; k < prefCount; k++) {
                    if (_mshrCapacity > 0 && _mshrUsed + InFlightPrefetches >= _mshrCapacity) break;
                    DLayers.TryPrefetch(prefBuf[k]);
                }
            }

            int fuLatency = _fuConfig.LatencyFor(issued.Instr);
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
    }

    /// <summary>Select up to issueWidth ready IQ entries and forward to execute.</summary>
    private void StepIssue() {
        // Per-class port counters: limits how many instructions of each functional-unit
        // class can be issued in a single cycle.
        Span<int> classIssued = stackalloc int[16]; // one slot per ToothClass value; sized for current + future growth
        var issued = 0;
        for (var iqIdx = 0; iqIdx < _activeIqCount && issued < _issueWidth; iqIdx++) {
            IssueQueue iq = _iqs[iqIdx];
            for (var slot = 0; slot < iq.Capacity && issued < _issueWidth; slot++) {
                RsEntry rs = iq.At(slot);
                if (!rs.Busy || !rs.IsReady) continue;

                ToothClass cls = rs.Instruction?.Class ?? ToothClass.IntegerAlu;
                int fuSlot = FuLatencyConfig.BudgetSlot(cls);
                if (classIssued[fuSlot] >= _fuConfig.CountFor(cls)) continue;

                switch (cls) {
                    // Loads issue speculatively; only block on preceding vector stores (which
                    // write eagerly at execute time, not at commit — see HasPrecedingVectorStore).
                    // Scalar store-to-load ordering is maintained through forwarding and, when
                    // necessary, memory-order violation detection and squash at the ROB head.
                    case ToothClass.Load when HasPrecedingVectorStore(rs.RobIndex): continue;
                    // TSO fence: a load may not issue while an older store→load fence is
                    // still in the ROB — the fence itself only issues (and then retires)
                    // once the write buffer has drained, so this gate delays post-fence
                    // loads until every pre-fence store's write-bus penalty has expired.
                    case ToothClass.Load when HasPrecedingStoreLoadFence(rs.RobIndex): continue;
                    // Conservative load ordering (FuLatencyConfig.ConservativeLoads): a load
                    // may not issue while any older SQ entry still has an unresolved address.
                    // Models Olympia's allow_speculative_load_exec = false.
                    case ToothClass.Load when _fuConfig.ConservativeLoads: {
                        int lqIdx = _rob.At(rs.RobIndex).LqIdx;
                        if (lqIdx >= 0 && HasUnresolvedPrecedingStore(_lq.At(lqIdx).SeqNo)) continue;
                        break;
                    }
                    case ToothClass.Load when _storeSets is not null: {
                        int lqIdx = _rob.At(rs.RobIndex).LqIdx;
                        if (lqIdx >= 0) {
                            LqEntry lq = _lq.At(lqIdx);
                            if (StoreSetStallLoad(lq.SeqNo, lq.PredStoreSeqNo)) continue;
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
                        continue;
                    // CSR serialization: a System instruction may only issue when it is
                    // at the ROB head (all older instructions have committed). This prevents
                    // out-of-order CSR reads from seeing stale state written by earlier CSR ops.
                    case ToothClass.System when rs.RobIndex != _rob.HeadIndex:
                    // Vector serialization: vector register renaming is not implemented.
                    // Head-gating ensures VRF writes are applied in program order.
                    case ToothClass.Vector when rs.RobIndex != _rob.HeadIndex:
                        continue;
                    // SC.W serialization: the reservation check (TryConsume) must see a
                    // coherent view of the ReservationTable — all older intra-hart stores
                    // must have committed (so their MoesifCache writes, which cancel cross-hart
                    // reservations via BusReadInvalidate, have already fired).  Head-gating
                    // guarantees this without needing a commit-time re-check.
                    case ToothClass.Atomic when rs.Instruction?.IsStoreConditional == true
                                             && rs.RobIndex != _rob.HeadIndex:
                        continue;
                    // TSO fence serialization: a store→load fence issues only at the ROB
                    // head (all older stores committed) and once the write buffer has fully
                    // drained, so every pre-fence store's write-bus penalty has expired
                    // before the fence completes and post-fence loads unblock. Fences
                    // without W→R ordering are timing no-ops and issue unrestricted.
                    case ToothClass.Fence when rs.Instruction?.IsStoreLoadFence == true
                                            && (rs.RobIndex != _rob.HeadIndex || _wbOccupied > 0):
                        continue;
                    // UVE serialization: stream state is not renamed; head-gating preserves order.
                    // Additionally stall until every load-stream source has a buffered element.
                    case ToothClass.Uve: {
                        if (rs.RobIndex != _rob.HeadIndex) continue;
                        var streamStall = false;
                        if (rs.Instruction is not null)
                            foreach (int uid in rs.Instruction.UveStreamSources)
                                if (uid >= 0 && StreamingEngine.IsActive(uid) && !StreamingEngine.HasElement(uid)) {
                                    streamStall = true;
                                    break;
                                }

                        if (streamStall) continue;
                        break;
                    }
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
                issued++;
            }
        }
    }

    /// <summary>
    /// True if any instruction older than <paramref name="loadRobIndex"/> is a vector store.
    /// Vector stores write eagerly at execute time (bypassing CapturingMemory), so
    /// younger loads must wait until the vector store has cleared the ROB.
    /// Scalar stores no longer block loads here; they are handled by forwarding and
    /// memory-order violation detection.
    /// </summary>
    private bool HasPrecedingVectorStore(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            ITooth? instr = entry.Instruction;
            if (instr is { Class: ToothClass.Vector, VectorDestinationRegister: < 0, DestinationRegister: < 0, })
                return true;
        }

        return false;
    }

    /// <summary>
    /// True if any instruction older than <paramref name="loadRobIndex"/> is a
    /// store→load fence still in the ROB. Younger loads must wait until the fence
    /// retires; combined with the fence's own issue gate (ROB head + write buffer
    /// drained) this gives TSO fence semantics: no post-fence load issues before
    /// every pre-fence store's write-bus penalty has expired.
    /// </summary>
    private bool HasPrecedingStoreLoadFence(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            if (entry.Instruction?.IsStoreLoadFence == true) return true;
        }

        return false;
    }

    /// <summary>
    /// True if any SQ entry older than <paramref name="loadSeqNo"/> has not yet
    /// resolved its effective address. Used to implement conservative load ordering
    /// (FuLatencyConfig.ConservativeLoads), mirroring Olympia's
    /// allow_speculative_load_exec = false.
    /// </summary>
    private bool HasUnresolvedPrecedingStore(ulong loadSeqNo) {
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break; // past the load — done
            if (!sq.AddressKnown) return true;
        }

        return false;
    }

    /// <summary>
    /// True if the load's predicted dependent store is still in the SQ with an unresolved
    /// address.  Returns false if the store is not found (committed, flushed, or never
    /// assigned) or if the atomic guard fires (predStoreSeqNo >= loadSeqNo).
    /// </summary>
    private bool StoreSetStallLoad(ulong loadSeqNo, ulong predStoreSeqNo) {
        if (predStoreSeqNo == 0 || predStoreSeqNo >= loadSeqNo) return false;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo == predStoreSeqNo) return !sq.AddressKnown;
            if (sq.SeqNo > predStoreSeqNo) break;
        }

        return false; // store already committed or flushed — safe to issue
    }

    /// <summary>
    /// After a store's address resolves, scan LQ entries younger than the store's
    /// sequence number for loads that have already executed against the same (or
    /// overlapping) address. Those loads read a stale value and are flagged for
    /// re-execution at the ROB head.
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
    /// If any older SQ entry's byte range fully contains the load's byte range, return
    /// the forwarded bytes extracted from the stored value.  Partial overlap (store
    /// covers some but not all of the load's bytes) is not forwarded — the load goes
    /// to memory and <see cref="CheckLoadViolations"/> will flag it on store resolution.
    /// The youngest matching store wins (last seen in program order = head-to-tail).
    /// </summary>
    private (ulong Value, bool HasValue) TryForwardFromStore(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        (ulong Value, bool HasValue) result = default;
        ulong loadEnd = loadAddr + (ulong)loadBytes;
        ulong mask = loadBytes >= 8 ? ulong.MaxValue : (1UL << (loadBytes * 8)) - 1;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break;
            if (!sq.AddressKnown) continue;
            // Skip unless the store's byte range fully contains the load's byte range.
            if (sq.Address > loadAddr || loadEnd > sq.Address + (ulong)sq.Width) continue;
            // Extract the relevant bytes: shift right by the byte offset within the store.
            int shift = (int)(loadAddr - sq.Address) * 8;
            result = ((sq.Value >> shift) & mask, true); // keep overwriting to get youngest match
        }

        return result;
    }

    /// <summary>
    /// True if any SQ entry older than <paramref name="loadSeqNo"/> has a known address
    /// that overlaps the load. Used at load-execute time to detect violations that
    /// weren't caught by <see cref="CheckLoadViolations"/> (the store resolved before
    /// the load executed; the converse ordering is caught when the store later resolves).
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
                PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Dispatch);
                _renameQueue.Dequeue();
                continue;
            }

            ITooth instr = ri.Decoded!;
            if (_iqs[IqIndex(instr.Class)].IsFull) break;

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
            rob.IsStore = instr.Class == ToothClass.Store;
            rob.IsLoad = instr.Class is ToothClass.Load or ToothClass.Atomic;
            rob.IsHalt = instr.Class == ToothClass.Halt;
            PEventLog?.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Dispatch);

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
                rob.LqIdx = lqIdx;
            }

            if (needsSq) {
                int sqIdx = _sq.Allocate();
                SqEntry sq = _sq.At(sqIdx);
                sq.RobIdx = robIdx;
                sq.InstrId = ri.InstrId;
                sq.SeqNo = memSeqNo;
                sq.Pc = ri.Pc;
                _storeSets?.OnStoreDispatch(ri.Pc, memSeqNo);
                rob.SqIdx = sqIdx;
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

            _renameQueue.Dequeue();
        }

        // Count cycles where the rename queue had work but dispatch was structurally blocked.
        if (_renameQueue.Count > 0) _stallsCounter.Increment();
    }

    /// <summary>Drain up to issueWidth decoded instructions through the RAT/PRF rename stage.</summary>
    private void StepRename() {
        while (_decodeQueue.Count > 0 && _renameQueue.Count < _maxDecodeDepth) {
            FetchedInstr fi = _decodeQueue.Peek();

            // Pre-trap: pass through rename without RAT allocation.
            if (fi.PreTrap is not null) {
                PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Rename);
                _renameQueue.Enqueue(
                    new RenameEntry(
                        fi.Pc, fi.Decoded, fi.PredictedNextPc, fi.InstrId, fi.PreTrap,
                        -1, -1, -1, -1, -1, -1
                    )
                );
                _decodeQueue.Dequeue();
                continue;
            }

            ITooth instr = fi.Decoded!;
            int destArch = instr.DestinationRegister;
            if (destArch > 0 && !_rat.HasFree) break; // stall: no free physical registers

            // ── Source lookup BEFORE destination rename ────────────────────────
            // Tomasulo invariant: sources must be resolved against the RAT state
            // as it exists just before this instruction's rename, so that an
            // instruction whose source == destination (e.g. addi x1,x1,1) reads
            // the producer's physical register, not its own pending output.
            IReadOnlyList<int> srcs = instr.SourceRegisters;
            int p1 = srcs.Count > 0 ? _rat.Lookup(srcs[0]) : -1;
            int p2 = srcs.Count > 1 ? _rat.Lookup(srcs[1]) : -1;
            int p3 = srcs.Count > 2 ? _rat.Lookup(srcs[2]) : -1;

            // ── Rename destination ─────────────────────────────────────────────
            int newPhys = -1, oldPhys = -1;
            if (destArch > 0) {
                (newPhys, oldPhys) = _rat.Rename(destArch);
                _prf.MarkPending(newPhys);
            }

            PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Rename);
            _renameQueue.Enqueue(
                new RenameEntry(
                    fi.Pc, instr, fi.PredictedNextPc, fi.InstrId, null,
                    destArch > 0 ? destArch : -1, newPhys, oldPhys, p1, p2, p3, fi.HistCheckpoint
                )
            );
            _decodeQueue.Dequeue();
        }
    }

    /// <summary>Fetch up to issueWidth instructions into the decode queue.</summary>
    private void StepFetch() {
        _fdip?.Tick(_fetchPc);
        if (_fetchFaulted) return; // wait for flush to clear before fetching again

        var fetched = 0;
        while (fetched < _issueWidth && _decodeQueue.Count < _maxDecodeDepth) {
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
                // On a wrong speculative path the PreTrap entry is squashed by the
                // flush just like any other in-flight instruction.
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
                // access fault; on a wrong path (e.g. an execute-time squash redirected fetch to a
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

            if (_rdip is not null && ILayers.Cache?.LastAccessWasHit == false) _rdip.OnIcacheMiss(physPc);

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            BranchHistoryCheckpoint histCheckpoint = default;
            if (hint.IsBranch) {
                // Snapshot the predictor's speculative history before this branch folds its own
                // direction, so an execute-time partial squash can rewind to exactly here.
                histCheckpoint = _predictor.CaptureHistory(_fetchPc);
                if (hint.IsCall) _ras.Push(_fetchPc + (ulong)decoded.SizeBytes);

                BranchPrediction pred;
                if (hint.IsReturn && _ras.TryPop(out ulong ret))
                    pred = BranchPrediction.Taken(ret);
                else if (hint.IsUnconditional && hint.BranchTarget.HasValue)
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
            }
            else { predictedNext = _fetchPc + (ulong)decoded.SizeBytes; }

            ulong instrId = _nextInstrId++;
            _decodeQueue.Enqueue(
                new FetchedInstr(_fetchPc, decoded, predictedNext, instrId, HistCheckpoint: histCheckpoint)
            );
            PEventLog?.Record(instrId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
            PEventLog?.RecordDisasm(instrId, _decoder.Disassemble(_fetchPc, decoded.RawEncoding));
            _fetchPc = predictedNext;
            fetched++;
        }
    }

    // ── Flush (misprediction / trap) ───────────────────────────────────────────

    private void StepFlush() {
        _flushesCounter.Increment();

        if (PEventLog is not null) {
            foreach ((_, RobEntry entry) in _rob.InOrder())
                if (entry.InstrId != 0)
                    PEventLog.Record(entry.InstrId, entry.Pc, _cyclesCounter.Value, PEventKind.Flush);
            foreach (RenameEntry ri in _renameQueue)
                if (ri.InstrId != 0)
                    PEventLog.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Flush);
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

        _fetchPc = _flushTarget;
        _flushPending = false;
        _squashPending = false; // a full flush supersedes any pending execute-time partial squash
        _fetchFaulted = false;
        _fdip?.Flush(_flushTarget);
    }

    /// <summary>
    /// Execute-time partial squash: a branch resolved mispredicted before reaching the ROB head.
    /// Redirects fetch now (as gem5 O3CPU's iew does) instead of waiting for commit, discarding
    /// only the instructions younger than the redirecting branch while the branch and every older
    /// in-flight instruction stay live and commit normally.
    /// </summary>
    private void StepPartialSquash() {
        _flushesCounter.Increment();
        _branchMissCounter.Increment(); // a partial squash is one branch misprediction, resolved at execute
        ulong bId = _squashInstrId;
        RobEntry b = FindRobByInstrId(bId);

        if (PEventLog is not null) {
            foreach ((_, RobEntry entry) in _rob.InOrder())
                if (entry.InstrId > bId && entry.InstrId != 0)
                    PEventLog.Record(entry.InstrId, entry.Pc, _cyclesCounter.Value, PEventKind.Flush);
            foreach (RenameEntry ri in _renameQueue)
                if (ri.InstrId != 0)
                    PEventLog.Record(ri.InstrId, ri.Pc, _cyclesCounter.Value, PEventKind.Flush);
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
        _predictor.RestoreHistory(b.HistCheckpoint, b.Pc, _squashTaken);

        // ── Discard the younger structures (B and everything older survive) ─────────────
        _rob.TruncateYoungerThan(bId);
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
    /// True when an in-flight instruction older than <paramref name="instrId"/> will redirect or
    /// halt the machine at commit (a halt, a trap, or a return-from-trap). Such an older entry makes
    /// every younger instruction wrong-path, so an execute-time squash on a younger branch would act
    /// on a doomed path the commit-time model never reaches.
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

        // For UVE ops: inject stream element values into u-register lanes before the executor runs,
        // and sync exhaustion state for branch ops. Vector-mode streams deliver up to VL lanes.
        if (isUve && State.UveScalars is { } uvs) {
            // Buffer for vector lane injection; max VLEN=128 bits = 4 float32 lanes.
            Span<uint> laneBuf = stackalloc uint[16];
            foreach (int uid in issued.Instr.UveStreamSources) {
                if (uid < 0 || !StreamingEngine.IsActive(uid) || !StreamingEngine.HasElement(uid)) continue;
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
                if (uid >= 0)
                    uvs.SetStreamDone(
                        uid, StreamingEngine.IsActive(uid)
                            ? StreamingEngine.IsExhausted(uid)
                            : true
                    ); // inactive = deactivated = done
            // so.b.ndc.D encodes the dimension as funct3 = D-1, counting from the
            // OUTERMOST dimension (Spike: EODTable.at(funct3), dimensions[0] = outermost).
            // The engine indexes dimensions innermost-first, so remap before querying.
            foreach ((int uid, int dim) in issued.Instr.UveDimBranchSources)
                if (uid >= 0) {
                    int engineDim = StreamingEngine.DimensionCount(uid) - 1 - dim;
                    uvs.SetDimDone(uid, dim, StreamingEngine.IsDimPassComplete(uid, engineDim));
                }
        }

        IMemory mem = isVec || isUve ? DLayers.Accessor : _capMem;
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
        if (_predictor is IVectorAwareBranchPredictor vbp) {
            if (isVec)
                vbp.NotifyVectorInstruction(issued.Pc);
            else if (issued.Instr.Class == ToothClass.ConditionalBranch
                  && resolvedNextPc.HasValue && resolvedNextPc.Value < issued.Pc)
                vbp.NotifyLoopBranchExecute(issued.Pc, resolvedNextPc.Value, issued.Src1, issued.Src2);
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

        return new ExecResult(
            issued.RobIdx, issued.PhysDest,
            regValue, resolvedNextPc, er.Trap,
            er.IsReturnFromTrap, er.ReturnPrivilege,
            _capMem.HasWrite, _capMem.WriteAddress, _capMem.WriteValue, _capMem.WriteBytes,
            _capMem.HasRead, _capMem.ReadAddress, _capMem.ReadBytes, loadForwarded,
            er.RequestHalt, issued.InstrId,
            // Vec/UVE already applied their SideEffect immediately above; don't reapply at commit.
            isVec || isUve ? null : er.SideEffect
        );
    }

    /// <summary>
    /// Retires the LQ entry (for loads/atomics) and/or SQ entry (for stores/atomics)
    /// corresponding to a retiring ROB entry. Must be called before <c>_rob.Retire()</c>
    /// since the LQ/SQ indices are read from the entry's fields.
    /// </summary>
    private void RetireMemQueues(RobEntry head) {
        if (head.LqIdx >= 0) _lq.Retire();
        if (head.SqIdx >= 0) _sq.Retire();
    }

    /// <summary>
    /// Commits the destination register of a ROB entry: writes the PRF value
    /// to the arch state, frees the old physical register, and advances State.Pc.
    /// </summary>
    private void CommitRegisters(RobEntry head) {
        head.SideEffect?.Invoke(State);
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
    /// Write a committed store to memory and optionally absorb the write-miss stall
    /// into the write buffer so the pipeline doesn't freeze for it.
    /// <para>
    /// Because the D-cache is write-through / no-write-allocate, data reaches memory
    /// the instant Write() returns — forwarding correctness is never at risk regardless
    /// of whether the stall is absorbed or charged to the clock.
    /// </para>
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
    /// Tick down all active write-buffer miss countdowns. Called every cycle (including
    /// halt/flush cycles) because the buffer holds committed state, not speculation.
    /// Slots whose countdown reaches zero are freed (the write bus penalty has expired).
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
        _stallsCounter.IncrementBy(n);
        _cacheMissStallsCounter?.IncrementBy(n);
        for (long i = 0; i < n; i++) State.OnCycle();
    }

    private long DrainAndChargeStalls() {
        long stalls = ILayers.ConsumeAllStalls() + DLayers.ConsumeAllStalls();
        UpdateCacheStat(ILayers.Cache, _icacheHitsCounter, _icacheMissesCounter, ref _lastIHits, ref _lastIMisses);
        UpdateCacheStat(
            ILayers.L2Cache, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIl2Hits, ref _lastIl2Misses
        );
        UpdateCacheStat(
            ILayers.L3Cache, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIl3Hits, ref _lastIl3Misses
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
            DLayers.L3Cache, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDl3Hits, ref _lastDl3Misses
        );
        UpdateTlbStat(ILayers.Tlb, _itlbHitsCounter, _itlbMissesCounter, ref _lastITlbHits, ref _lastITlbMisses);
        UpdateTlbStat(DLayers.Tlb, _dtlbHitsCounter, _dtlbMissesCounter, ref _lastDTlbHits, ref _lastDTlbMisses);
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
}