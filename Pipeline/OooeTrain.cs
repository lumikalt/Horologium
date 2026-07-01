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
        int mshrCapacity = 0
    ) {
        var esc = new Escapement();
        _train = new Train("ooo", esc);
        _core = _train.AddGear(
            new OoOPipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, entryPoint,
                issueWidth, robCapacity, iqCapacity, extraPhysRegs,
                predictor ?? new AlwaysNotTakenPredictor(),
                iMemConfig ?? MemoryConfig.None,
                dMemConfig ?? MemoryConfig.None,
                fuLatency ?? FuLatencyConfig.Default,
                pEventLog,
                streamPrefetchDepth,
                commitObserver,
                lqCapacity,
                sqCapacity,
                writeBufferCapacity,
                mshrCapacity
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

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
        TrapInfo? PreTrap = null
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
        bool LoadWasForwarded,   // true if TryForwardFromStore supplied the register value
        bool RequestHalt = false // true for an HTIF tohost-exit store: halt after commit
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

    private static int IqIndex(ToothClass cls) => cls switch {
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

    // Cross-tick latches
    private readonly Queue<FetchedInstr> _decodeQueue = new();
    private readonly List<IssuedInstr> _execBuffer = [];
    private readonly List<(int Countdown, ExecResult Result, bool HoldsMshr)> _inFlight = [];
    private readonly List<ExecResult> _cdbBuffer = [];

    // Runtime state
    private ulong _fetchPc;
    private bool _halted;
    private bool _flushPending;
    private ulong _flushTarget;
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
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;

    private bool _anyCache;

    // Delta tracking for hit/miss/prefetch counters
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;
    private long _lastDPrefetches;
    private long _lastITlbHits, _lastITlbMisses, _lastDTlbHits, _lastDTlbMisses;

    public IArchState State { get; }

    public OoOPipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint,
        int issueWidth,
        int robCapacity,
        int iqCapacity,
        int extraPhysRegs,
        IBranchPredictor predictor,
        MemoryConfig iMemConfig,
        MemoryConfig dMemConfig,
        FuLatencyConfig fuConfig,
        PEventLog? pEventLog = null,
        int streamPrefetchDepth = 4,
        ICommitObserver? commitObserver = null,
        int lqCapacity = 0,
        int sqCapacity = 0,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0
    ) : base(name, parent, esc) {
        PEventLog = pEventLog;
        _commitObserver = commitObserver;
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
        _predictor = predictor;
        _fuConfig = fuConfig;
        ILayers = MemoryLayers.Build(memory, iMemConfig);
        DLayers = MemoryLayers.Build(memory, dMemConfig);
        _capMem = new CapturingMemory(DLayers.Accessor);
        _issueWidth = issueWidth;
        _maxDecodeDepth = issueWidth * 4;
        _fetchPc = entryPoint;

        State = mechanism.CreateArchState();
        State.Pc = entryPoint;
        _fetchTranslator = mechanism.CreateFetchTranslator(State, ILayers.Accessor);

        int archRegs = State.IntegerRegisters.Count;
        int physRegs = archRegs + extraPhysRegs;
        _prf = new PhysicalRegisterFile(physRegs);
        _rat = new RenameMap(archRegs, physRegs);
        _rob = new ReorderBuffer(robCapacity);
        _iqs = new IssueQueue[OoOPipelineCore.IqCount];
        for (var i = 0; i < OoOPipelineCore.IqCount; i++) _iqs[i] = new IssueQueue(iqCapacity);
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
            "stalls", "Dispatch-stall cycles (structural hazards) + cache miss penalties"
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
            if (DLayers.Prefetcher is not null)
                _dcachePrefetchesCounter = Dials.AddCounter("dcache_prefetches", "L1 D-cache prefetch fills");
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
        StreamingEngine.Step(DLayers.Accessor);

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

        if (_halted || _flushPending) {
            if (_flushPending) StepFlush();
            if (!_halted) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
            return;
        }

        // Execute: run last tick's issued instructions.
        StepExecute();

        // Issue: select up to issueWidth ready IQ entries.
        StepIssue();

        // Dispatch: rename and allocate ROB + IQ slots from the decode queue.
        StepDispatch();

        // Fetch: fill the decode queue with new speculative instructions.
        StepFetch();

        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // ── Pipeline stages ────────────────────────────────────────────────────────

    /// <summary>CDB broadcast: apply Execute T-1 results to PRF + IQ + ROB.</summary>
    private void StepComplete() {
        foreach (ExecResult r in _cdbBuffer) {
            RobEntry rob = _rob.At(r.RobIdx);
            rob.IsComplete = true;
            rob.ResolvedNextPc = r.ResolvedNextPc;
            rob.HasTrap = r.Trap is not null;
            rob.Trap = r.Trap;
            rob.IsReturnFromTrap = r.IsReturnFromTrap;
            rob.ReturnPrivilege = r.ReturnPrivilege;
            rob.RequestHalt = r.RequestHalt;

            if (r.HasStoreCapture) {
                // Update the SQ entry with the resolved store address and value.
                SqEntry sq = _sq.At(rob.SqIdx);
                sq.AddressKnown = true;
                sq.Address = r.StoreAddr;
                sq.Value = r.StoreVal;
                sq.Width = r.StoreBytes;
                // A store's address just became known: check whether any younger speculative
                // load has already executed against the same address with a stale value.
                CheckLoadViolations(sq.SeqNo, r.StoreAddr, r.StoreBytes);
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
                    CommitRegisters(head);
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
            if (_commitObserver is not null && head.Instruction is not null)
                _commitObserver.OnCommit(head.Pc, head.Instruction.RawEncoding, State);

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
                if (!result.LoadWasForwarded
                 && HasOlderConflictingStore(lq.SeqNo, result.LoadAddr, result.LoadBytes))
                    lq.Violated = true;
            }

            // D-cache prefetch: fire before draining stalls so the prefetch sees the cache
            // state left by this access. Fires only for demand loads (not store-forwarded).
            if (result.HasLoadAccess && !result.LoadWasForwarded && DLayers.Prefetcher is not null) {
                bool wasHit = DLayers.Cache?.LastAccessWasHit ?? true;
                ulong? pAddr = DLayers.Prefetcher.OnAccess(issued.Pc, result.LoadAddr, wasHit);
                if (pAddr.HasValue) DLayers.TryPrefetch(pAddr.Value);
            }

            int countdown = _fuConfig.LatencyFor(issued.Instr) - 1 + _fuConfig.BypassLatency;

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
        for (var iqIdx = 0; iqIdx < OoOPipelineCore.IqCount && issued < _issueWidth; iqIdx++) {
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
                    // Conservative load ordering (FuLatencyConfig.ConservativeLoads): a load
                    // may not issue while any older SQ entry still has an unresolved address.
                    // Models Olympia's allow_speculative_load_exec = false.
                    case ToothClass.Load when _fuConfig.ConservativeLoads: {
                        int lqIdx = _rob.At(rs.RobIndex).LqIdx;
                        if (lqIdx >= 0 && HasUnresolvedPrecedingStore(_lq.At(lqIdx).SeqNo)) continue;
                        break;
                    }
                }

                switch (cls) {
                    // MSHR capacity: if all miss-tracking slots are occupied, this load/atomic
                    // cannot start yet — it stays in the IQ and retries next cycle.
                    case ToothClass.Load or ToothClass.Atomic
                        when _mshrCapacity > 0 && _mshrUsed >= _mshrCapacity:
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
                    // must have committed (so their MesiCache writes, which cancel cross-hart
                    // reservations via BusReadInvalidate, have already fired).  Head-gating
                    // guarantees this without needing a commit-time re-check.
                    case ToothClass.Atomic when rs.Instruction?.IsStoreConditional == true
                                             && rs.RobIndex != _rob.HeadIndex:
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
    /// After a store's address resolves, scan LQ entries younger than the store's
    /// sequence number for loads that have already executed against the same (or
    /// overlapping) address. Those loads read a stale value and are flagged for
    /// re-execution at the ROB head.
    /// </summary>
    private void CheckLoadViolations(ulong storeSeqNo, ulong storeAddr, int storeBytes) {
        foreach (LqEntry lq in _lq.InOrder()) {
            if (lq.SeqNo <= storeSeqNo) continue; // older than or equal to the store
            if (!lq.Executed) continue;
            if (AddressOverlaps(lq.Address, lq.Bytes, storeAddr, storeBytes)) lq.Violated = true;
        }
    }

    /// <summary>
    /// If any older SQ entry has already executed against the same address as
    /// <paramref name="loadAddr"/>, return its stored value as a forwarded result.
    /// Returns <c>default</c> (HasValue=false) when no forwarding match is found.
    /// The youngest matching store wins (last seen in program order = head-to-tail).
    /// </summary>
    private (ulong Value, bool HasValue) TryForwardFromStore(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        (ulong Value, bool HasValue) result = default;
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break; // reached entries younger than or equal to this load
            if (!sq.AddressKnown) continue;
            // Only exact base-address forwarding; partial-overlap cases require shifting.
            if (sq.Address != loadAddr || sq.Width < loadBytes) continue;
            ulong mask = loadBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, _ => 0xFFFF_FFFFUL, };
            result = (sq.Value & mask, true); // keep overwriting to get youngest match
        }

        return result;
    }

    /// <summary>
    /// True if any SQ entry older than <paramref name="loadSeqNo"/> has a known address
    /// that overlaps the load. Used at load-execute time to detect violations that
    /// weren't caught by <see cref="CheckLoadViolations"/> (the store resolved before
    /// the load executed; the converse ordering is caught when the store later resolves).
    /// </summary>
    private bool HasOlderConflictingStore(ulong loadSeqNo, ulong loadAddr, int loadBytes) {
        foreach (SqEntry sq in _sq.InOrder()) {
            if (sq.SeqNo >= loadSeqNo) break; // past the load's position — done
            if (!sq.AddressKnown) continue;
            if (AddressOverlaps(sq.Address, sq.Width, loadAddr, loadBytes)) return true;
        }

        return false;
    }

    private static bool AddressOverlaps(ulong aAddr, int aBytes, ulong bAddr, int bBytes) {
        ulong aEnd = aAddr + (ulong)aBytes;
        ulong bEnd = bAddr + (ulong)bBytes;
        return aAddr < bEnd && bAddr < aEnd;
    }

    /// <summary>Rename and allocate ROB + IQ + LQ/SQ slots for decoded instructions.</summary>
    private void StepDispatch() {
        while (_decodeQueue.Count > 0) {
            // Stop if any structural resource is exhausted.
            if (_rob.IsFull) break;

            FetchedInstr fi = _decodeQueue.Peek();

            // Fetch page fault: park in ROB as a completed trap; skip IQ entirely.
            if (fi.PreTrap is not null) {
                int faultRobIdx = _rob.Allocate();
                RobEntry robFault = _rob.At(faultRobIdx);
                robFault.Pc = fi.Pc;
                robFault.InstrId = fi.InstrId;
                robFault.HasTrap = true;
                robFault.Trap = fi.PreTrap;
                robFault.IsComplete = true;
                robFault.ArchDestination = -1;
                robFault.PhysDestination = -1;
                PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Dispatch);
                _decodeQueue.Dequeue();
                continue;
            }

            ITooth instr = fi.Decoded!;
            if (_iqs[IqIndex(instr.Class)].IsFull) break;

            int destArch = instr.DestinationRegister;

            // Check memory queue capacity before committing any allocation.
            bool needsLq = instr.Class is ToothClass.Load or ToothClass.Atomic;
            bool needsSq = instr.Class is ToothClass.Store or ToothClass.Atomic;
            if (needsLq && _lq.IsFull) break;
            if (needsSq && _sq.IsFull) break;

            bool needsRename = destArch > 0 && _rat.HasFree;
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
            if (needsRename) {
                (newPhys, oldPhys) = _rat.Rename(destArch);
                _prf.MarkPending(newPhys);
            }

            // Allocate ROB entry.
            int robIdx = _rob.Allocate();
            RobEntry rob = _rob.At(robIdx);
            rob.Pc = fi.Pc;
            rob.InstrId = fi.InstrId;
            rob.Instruction = instr;
            rob.ArchDestination = destArch > 0 ? destArch : -1;
            rob.PhysDestination = newPhys;
            rob.PrevPhysDestination = oldPhys;
            rob.PredictedNextPc = fi.PredictedNextPc;
            rob.IsStore = instr.Class == ToothClass.Store;
            rob.IsLoad = instr.Class is ToothClass.Load or ToothClass.Atomic;
            rob.IsHalt = instr.Class == ToothClass.Halt;
            PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Dispatch);

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
                lq.SeqNo = memSeqNo;
                rob.LqIdx = lqIdx;
            }

            if (needsSq) {
                int sqIdx = _sq.Allocate();
                SqEntry sq = _sq.At(sqIdx);
                sq.RobIdx = robIdx;
                sq.SeqNo = memSeqNo;
                rob.SqIdx = sqIdx;
            }

            // Allocate IQ slot and fill source operands from pre-rename RAT snapshot.
            IssueQueue classIq = _iqs[IqIndex(instr.Class)];
            int iqSlot = classIq.Allocate();
            RsEntry rs = classIq.At(iqSlot);
            rs.RobIndex = robIdx;
            rs.Instruction = instr;
            rs.Pc = fi.Pc;
            rs.PredictedNextPc = fi.PredictedNextPc;
            rs.PhysDestination = newPhys;

            if (p1 >= 0) {
                if (_prf.IsReady(p1)) {
                    rs.Src1Ready = true;
                    rs.Src1Value = _prf.Read(p1);
                }
                else { rs.Src1Tag = p1; }
            }

            if (p2 >= 0) {
                if (_prf.IsReady(p2)) {
                    rs.Src2Ready = true;
                    rs.Src2Value = _prf.Read(p2);
                }
                else { rs.Src2Tag = p2; }
            }

            if (p3 >= 0) {
                if (_prf.IsReady(p3)) {
                    rs.Src3Ready = true;
                    rs.Src3Value = _prf.Read(p3);
                }
                else { rs.Src3Tag = p3; }
            }

            _decodeQueue.Dequeue();
        }

        // Count cycles where we had work to dispatch but were blocked by a structural limit
        // (ROB full, IQ full, or no free physical registers).
        if (_decodeQueue.Count > 0) _stallsCounter.Increment();
    }

    /// <summary>Fetch up to issueWidth instructions into the decode queue.</summary>
    private void StepFetch() {
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

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            if (hint.IsBranch) {
                if (hint.IsCall) _ras.Push(_fetchPc + (ulong)decoded.SizeBytes);

                BranchPrediction pred;
                if (hint.IsReturn && _ras.TryPop(out ulong ret))
                    pred = BranchPrediction.Taken(ret);
                else
                    pred = _predictor.Predict(_fetchPc, hint.BranchTarget);
                predictedNext = pred.PredictedTaken ? pred.PredictedTarget : _fetchPc + (ulong)decoded.SizeBytes;
            }
            else { predictedNext = _fetchPc + (ulong)decoded.SizeBytes; }

            ulong instrId = _nextInstrId++;
            _decodeQueue.Enqueue(new FetchedInstr(_fetchPc, decoded, predictedNext, instrId));
            PEventLog?.Record(instrId, _fetchPc, _cyclesCounter.Value, PEventKind.Fetch);
            _fetchPc = predictedNext;
            fetched++;
        }
    }

    // ── Flush (misprediction / trap) ───────────────────────────────────────────

    private void StepFlush() {
        _flushesCounter.Increment();

        if (PEventLog is not null)
            foreach ((_, RobEntry entry) in _rob.InOrder())
                if (entry.InstrId != 0)
                    PEventLog.Record(entry.InstrId, entry.Pc, _cyclesCounter.Value, PEventKind.Flush);

        // Walk ROB youngest-to-oldest, restoring the RAT to committed state.
        foreach ((_, RobEntry entry) in _rob.InOrder().Reverse())
            if (entry is { ArchDestination: > 0, PhysDestination: >= 0, }) {
                _rat.RestoreMapping(entry.ArchDestination, entry.PrevPhysDestination);
                _rat.FreePhysical(entry.PhysDestination);
            }

        _rob.Flush();
        foreach (IssueQueue iq in _iqs) iq.Flush();
        _lq.Flush();
        _sq.Flush();
        _decodeQueue.Clear();
        _execBuffer.Clear();
        _inFlight.Clear();
        _mshrUsed = 0;
        _cdbBuffer.Clear();

        _fetchPc = _flushTarget;
        _flushPending = false;
        _fetchFaulted = false;
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

        // For UVE ops: inject stream element values into UveState.Scalars before the
        // executor runs, and sync exhaustion state for branch ops. The pipeline owns
        // the StreamingEngine; the executor reads results from IUveScalars.
        if (isUve && State.UveScalars is { } uvs) {
            foreach (int uid in issued.Instr.UveStreamSources)
                if (uid >= 0 && StreamingEngine.IsActive(uid) && StreamingEngine.HasElement(uid))
                    uvs.SetScalar(uid, BitConverter.Int32BitsToSingle((int)(uint)StreamingEngine.Consume(uid)));
            foreach (int uid in issued.Instr.UveBranchStreams)
                if (uid >= 0)
                    uvs.SetStreamDone(
                        uid, StreamingEngine.IsActive(uid)
                            ? StreamingEngine.IsExhausted(uid)
                            : true
                    ); // inactive = deactivated = done
            foreach ((int uid, int dim) in issued.Instr.UveDimBranchSources)
                if (uid >= 0)
                    uvs.SetDimDone(uid, dim, StreamingEngine.IsDimPassComplete(uid, dim));
        }

        IMemory mem = isVec || isUve ? DLayers.Accessor : _capMem;
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
                false, null, false, 0, 0, 0, false, 0, 0, false
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
            er.RequestHalt
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
        if (head is not { PhysDestination: >= 0, ArchDestination: > 0, }) return;
        ulong val = _prf.Read(head.PhysDestination);
        State.IntegerRegisters.Write(head.ArchDestination, val);
        if (head.PrevPhysDestination >= 0) _rat.FreePhysical(head.PrevPhysDestination);
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