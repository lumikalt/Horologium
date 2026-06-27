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

public sealed class OooeTrain {
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
        int iqCapacity = 16,
        int extraPhysRegs = 32,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null,
        PEventLog? pEventLog = null,
        int streamPrefetchDepth = 4
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
                streamPrefetchDepth
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
/// Superscalar out-of-order pipeline Gear using Tomasulo's algorithm.
///
/// Pipeline stages (cross-tick latches connect them):
///   Fetch → [decodeQueue] → Dispatch → [IssueQueue] → Issue
///     → [_execBuffer] → Execute → [_cdbBuffer] → Complete → [ROB] → Commit
///
/// All six logical stages execute within a single RunCycle tick, reading from
/// the latch populated by the previous tick. Minimum end-to-end latency for
/// an independent instruction is ~4 ticks (Fetch, Dispatch, Execute, Complete+Commit).
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
        bool LoadWasForwarded // true if TryForwardFromStore supplied the register value
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
    private readonly IFetchTranslator? _fetchTranslator;
    private readonly CapturingMemory _capMem;
    private readonly FuLatencyConfig _fuConfig;

    // Streaming engine (architectural; survives pipeline flushes)
    public StreamingEngine StreamingEngine { get; }

    // Memory hierarchy layers
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }

    // OoOE structures
    private readonly PhysicalRegisterFile _prf;
    private readonly RenameMap _rat;
    private readonly ReorderBuffer _rob;
    private readonly IssueQueue _iq;
    private readonly int _issueWidth;
    private readonly int _maxDecodeDepth;

    // Cross-tick latches
    private readonly Queue<FetchedInstr> _decodeQueue = new();
    private readonly List<IssuedInstr> _execBuffer = [];
    private readonly List<(int Countdown, ExecResult Result)> _inFlight = [];
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
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;

    private bool _anyCache;

    // Delta tracking for hit/miss counters
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;
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
        int streamPrefetchDepth = 4
    ) : base(name, parent, esc) {
        PEventLog = pEventLog;
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
        _iq = new IssueQueue(iqCapacity);
        StreamingEngine = new StreamingEngine(streamPrefetchDepth);
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
        if (_anyCache)
            // Lump-sum model: penalty cycles are appended per cycle, not overlapped.
            // This overestimates stalls relative to real out-of-order memory-level parallelism.
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
        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);

    // ── Main driver ────────────────────────────────────────────────────────────

    private void RunCycle() {
        if (_halted) return;

        // Advance all active streams one prefetch step. Streams are architectural state
        // and run every cycle, independent of pipeline flush/stall.
        StreamingEngine.Step(DLayers.Accessor);

        // Drain cache/TLB stall penalties from the previous cycle's memory operations.
        // Lump-sum: does not model memory-level parallelism available in real OoO hardware.
        long cacheStalls = _anyCache ? DrainAndChargeStalls() : 0;
        if (cacheStalls > 0) {
            _cyclesCounter.IncrementBy(cacheStalls);
            _stallsCounter.IncrementBy(cacheStalls);
            _cacheMissStallsCounter?.IncrementBy(cacheStalls);
        }

        _cyclesCounter.Increment();

        // Complete: broadcast last tick's execution results onto CDB.
        StepComplete();

        // Commit: retire completed ROB heads in program order.
        StepCommit();

        if (_halted || _flushPending) {
            if (_flushPending) StepFlush();
            if (!_halted) Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
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

        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
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

            if (r.HasStoreCapture) {
                rob.StoreAddressKnown = true;
                rob.StoreAddress = r.StoreAddr;
                rob.StoreValue = r.StoreVal;
                rob.StoreWidth = r.StoreBytes;
                // A store's address just became known: check whether any younger speculative
                // load has already executed against the same address with a stale value.
                CheckLoadViolations(r.RobIdx, r.StoreAddr, r.StoreBytes);
            }

            if (r.HasLoadAccess) {
                rob.LoadExecuted = true;
                rob.LoadAddress = r.LoadAddr;
                rob.LoadBytes = r.LoadBytes;
                // If the load was not forwarded from an already-resolved store, check
                // whether any older store now has a known address that conflicts. This
                // catches the case where the store and load complete in the same CDB
                // batch (store processed first → StoreAddressKnown=true by the time
                // we reach the load entry).
                if (!r.LoadWasForwarded && HasOlderConflictingStore(r.RobIdx, r.LoadAddr, r.LoadBytes))
                    rob.LoadViolated = true;
            }

            if (!r.RegValue.HasValue || r.PhysDest < 0) continue;
            _prf.Write(r.PhysDest, r.RegValue.Value);
            _iq.Broadcast(r.PhysDest, r.RegValue.Value);
        }

        _cdbBuffer.Clear();
    }

    /// <summary>In-order retirement from the ROB head.</summary>
    private void StepCommit() {
        var committed = 0;
        while (_rob is { IsEmpty: false, Head.IsComplete: true, } && committed < _issueWidth) {
            RobEntry head = _rob.Head;

            switch (head) {
                case { IsHalt: true, }: {
                    CommitRegisters(head);
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    _halted = true;
                    return;
                }
                case { HasTrap: true, Trap: not null, }: {
                    ulong target = _trapController.RaiseTrap(head.Trap, State);
                    CommitRegisters(head);
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    SetFlush(target);
                    return;
                }
                case { IsReturnFromTrap: true, ReturnPrivilege: not null, }: {
                    ulong target = _trapController.ReturnFromTrap(head.ReturnPrivilege.Value, State);
                    CommitRegisters(head);
                    PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
                    _rob.Retire();
                    _retiredCounter.Increment();
                    SetFlush(target);
                    return;
                }
            }

            // A load that executed speculatively may have read a stale value if a
            // conflicting store resolved after it. By the time the load reaches the
            // ROB head, all older instructions (including the store) have committed
            // and written memory, so re-executing the load from its own PC is safe.
            if (head is { IsLoad: true, LoadViolated: true, }) {
                _memViolationsCounter.Increment();
                SetFlush(head.Pc); // re-executes from the load's PC; flush clears the ROB
                return;
            }

            if (head.IsStore) DLayers.Accessor.Write(head.StoreAddress, head.StoreValue, head.StoreWidth);

            CommitRegisters(head);

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
                    _rob.Retire();
                    _retiredCounter.Increment();
                    SetFlush(resolvedPc);
                    return;
                }
            }

            State.Pc = head.PredictedNextPc;
            PEventLog?.Record(head.InstrId, head.Pc, _cyclesCounter.Value, PEventKind.Retire);
            _rob.Retire();
            _retiredCounter.Increment();
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
            (int countdown, ExecResult result) = _inFlight[i];
            if (--countdown <= 0) {
                _cdbBuffer.Add(result);
                _inFlight.RemoveAt(i);
            }
            else { _inFlight[i] = (countdown, result); }
        }

        // Start executing newly issued instructions.
        foreach (IssuedInstr issued in _execBuffer) {
            ExecResult result = ExecuteOne(issued);
            PEventLog?.Record(issued.InstrId, issued.Pc, _cyclesCounter.Value, PEventKind.Execute);
            int countdown = _fuConfig.LatencyFor(issued.Instr.Class) - 1;
            if (countdown == 0)
                _cdbBuffer.Add(result);
            else
                _inFlight.Add((countdown, result));
        }

        _execBuffer.Clear();
    }

    /// <summary>Select up to issueWidth ready IQ entries and forward to execute.</summary>
    private void StepIssue() {
        // Per-class port counters: limits how many instructions of each functional-unit
        // class can be issued in a single cycle.
        Span<int> classIssued = stackalloc int[16]; // one slot per ToothClass value; sized for current + future growth
        var issued = 0;
        for (var slot = 0; slot < _iq.Capacity && issued < _issueWidth; slot++) {
            RsEntry rs = _iq.At(slot);
            if (!rs.Busy || !rs.IsReady) continue;

            ToothClass cls = rs.Instruction?.Class ?? ToothClass.IntegerAlu;
            if (classIssued[(int)cls] >= _fuConfig.CountFor(cls)) continue;

            // Loads issue speculatively; only block on preceding vector stores (which
            // write eagerly at execute time, not at commit — see HasPrecedingVectorStore).
            // Scalar store-to-load ordering is maintained through forwarding and, when
            // necessary, memory-order violation detection and squash at the ROB head.
            if (cls == ToothClass.Load && HasPrecedingVectorStore(rs.RobIndex)) continue;

            // CSR serialization: a System instruction may only issue when it is
            // at the ROB head (all older instructions have committed). This prevents
            // out-of-order CSR reads from seeing stale state written by earlier CSR ops.
            if (cls == ToothClass.System && rs.RobIndex != _rob.HeadIndex) continue;

            // Vector serialization: vector register renaming is not implemented.
            // Head-gating ensures VRF writes are applied in program order.
            if (cls == ToothClass.Vector && rs.RobIndex != _rob.HeadIndex) continue;

            ulong issuedInstrId = _rob.At(rs.RobIndex).InstrId;
            _execBuffer.Add(
                new IssuedInstr(
                    rs.RobIndex, rs.PhysDestination, rs.Instruction!, rs.Pc,
                    rs.Src1Value, rs.Src2Value, rs.Src3Value, issuedInstrId
                )
            );
            PEventLog?.Record(issuedInstrId, rs.Pc, _cyclesCounter.Value, PEventKind.Issue);
            _iq.Free(slot);
            classIssued[(int)cls]++;
            issued++;
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
    /// After a scalar store's address resolves, scan younger ROB entries for loads
    /// that have already executed against the same (or overlapping) address.
    /// Those loads read a stale value and are flagged for re-execution at the ROB head.
    /// </summary>
    private void CheckLoadViolations(int storeRobIdx, ulong storeAddr, int storeBytes) {
        var pastStore = false;
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (!pastStore) {
                if (idx == storeRobIdx) pastStore = true;
                continue;
            }

            if (!entry.IsLoad || !entry.LoadExecuted) continue;
            if (AddressOverlaps(entry.LoadAddress, entry.LoadBytes, storeAddr, storeBytes)) entry.LoadViolated = true;
        }
    }

    /// <summary>
    /// If any older ROB store has already executed against the same address as
    /// <paramref name="loadAddr"/>, return its stored value as a forwarded result.
    /// Returns <c>default</c> (HasValue=false) when no forwarding match is found.
    /// The youngest matching store wins (last seen in program order = head-to-tail).
    /// </summary>
    private (ulong Value, bool HasValue) TryForwardFromStore(int loadRobIdx, ulong loadAddr, int loadBytes) {
        (ulong Value, bool HasValue) result = default;
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIdx) break;
            if (!entry.IsStore || !entry.StoreAddressKnown) continue;
            // Only exact base-address forwarding; partial-overlap cases require shifting.
            if (entry.StoreAddress != loadAddr || entry.StoreWidth < loadBytes) continue;
            ulong mask = loadBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, _ => 0xFFFF_FFFFUL, };
            result = (entry.StoreValue & mask, true);
        }

        return result;
    }

    /// <summary>
    /// True if any store OLDER than <paramref name="loadRobIdx"/> has a known address
    /// that overlaps the load. Used at load-result time to detect violations that
    /// weren't caught by <see cref="CheckLoadViolations"/> (e.g., same-CDB-batch case
    /// where the store was processed before the load in the same StepComplete iteration).
    /// </summary>
    private bool HasOlderConflictingStore(int loadRobIdx, ulong loadAddr, int loadBytes) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIdx) return false;
            if (!entry.IsStore || !entry.StoreAddressKnown) continue;
            if (AddressOverlaps(entry.StoreAddress, entry.StoreWidth, loadAddr, loadBytes)) return true;
        }

        return false;
    }

    private static bool AddressOverlaps(ulong aAddr, int aBytes, ulong bAddr, int bBytes) {
        ulong aEnd = aAddr + (ulong)aBytes;
        ulong bEnd = bAddr + (ulong)bBytes;
        return aAddr < bEnd && bAddr < aEnd;
    }

    /// <summary>Rename and allocate ROB + IQ slots for decoded instructions.</summary>
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

            if (_iq.IsFull) break;

            ITooth instr = fi.Decoded!;
            int destArch = instr.DestinationRegister;

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
            rob.IsLoad = instr.Class == ToothClass.Load;
            rob.IsHalt = instr.Class == ToothClass.Halt;
            PEventLog?.Record(fi.InstrId, fi.Pc, _cyclesCounter.Value, PEventKind.Dispatch);

            // Allocate IQ slot and fill source operands from pre-rename RAT snapshot.
            int iqSlot = _iq.Allocate();
            RsEntry rs = _iq.At(iqSlot);
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
            catch {
                _fetchFaulted = true; // stop retrying until flush redirects _fetchPc
                break;
            }

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            if (hint.IsBranch) {
                BranchPrediction pred = _predictor.Predict(_fetchPc, hint.BranchTarget);
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
        _iq.Flush();
        _decodeQueue.Clear();
        _execBuffer.Clear();
        _inFlight.Clear();
        _cdbBuffer.Clear();

        _fetchPc = _flushTarget;
        _flushPending = false;
        _fetchFaulted = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ExecResult ExecuteOne(IssuedInstr issued) {
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

        // Vector ops are head-serialized (non-speculative) and may write multiple
        // elements to memory. Pass the real accessor so all element writes land;
        // CapturingMemory can only capture a single write.
        _capMem.Reset();
        bool isVec = issued.Instr.Class == ToothClass.Vector;
        IMemory mem = isVec ? DLayers.Accessor : _capMem;
        ExecuteResult er = _executor.Execute(issued.Instr, State, mem);
        if (isVec) er.SideEffect?.Invoke(State);

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
            _ => default((ulong, bool)),
        };

        // For loads: try to forward from an older executed store to the same address.
        // If a forwarding match is found, override the (potentially stale) memory read.
        (ulong Value, bool HasValue) regValue = er.RegisterResult;
        var loadForwarded = false;
        if (_capMem.HasRead) {
            (ulong fwd, bool hasFwd) = TryForwardFromStore(issued.RobIdx, _capMem.ReadAddress, _capMem.ReadBytes);
            if (hasFwd) {
                regValue = (fwd, true);
                loadForwarded = true;
            }
        }

        return new ExecResult(
            issued.RobIdx, issued.PhysDest,
            regValue, resolvedNextPc, er.Trap,
            er.IsReturnFromTrap, er.ReturnPrivilege,
            _capMem.HasWrite, _capMem.WriteAddress, _capMem.WriteValue, _capMem.WriteBytes,
            _capMem.HasRead, _capMem.ReadAddress, _capMem.ReadBytes, loadForwarded
        );
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

    // ── Memory hierarchy stat collection ──────────────────────────────────────

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