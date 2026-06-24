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
        MemoryConfig? dMemConfig = null
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
                dMemConfig ?? MemoryConfig.None
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
        ITooth Decoded,
        ulong PredictedNextPc
    );

    private readonly record struct IssuedInstr(
        int RobIdx,
        int PhysDest,
        ITooth Instr,
        ulong Pc,
        ulong Src1,
        ulong Src2,
        ulong Src3
    );

    private readonly record struct ExecResult(
        int RobIdx,
        int PhysDest,
        ulong? RegValue,
        ulong? ResolvedNextPc,
        TrapInfo? Trap,
        bool IsReturnFromTrap,
        PrivilegeLevel? ReturnPrivilege,
        bool HasStoreCapture,
        ulong StoreAddr,
        ulong StoreVal,
        int StoreBytes
    );

    /// <summary>
    /// Passes reads through to backing memory; captures writes instead of
    /// executing them. Used to defer store writes until ROB commit.
    /// </summary>
    private sealed class CapturingMemory(IMemory backing) : IMemory {
        public bool HasWrite { get; private set; }
        public ulong WriteAddress { get; private set; }
        public ulong WriteValue { get; private set; }
        public int WriteBytes { get; private set; }

        public void Reset() => HasWrite = false;
        public ulong Read(ulong address, int bytes) => backing.Read(address, bytes);
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
    private readonly CapturingMemory _capMem;

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
    private readonly List<IssuedInstr> _execBuffer = new();
    private readonly List<ExecResult> _cdbBuffer = new();

    // Runtime state
    private ulong _fetchPc;
    private bool _halted;
    private bool _flushPending;
    private ulong _flushTarget;

    // Counters (initialised in Initialize)
    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Counter _flushesCounter = null!;
    private Counter _branchMissCounter = null!;
    private Counter _stallsCounter = null!;
    private Counter? _cacheMissStallsCounter;
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;

    // Delta tracking for hit/miss counters
    private long _lastIHits, _lastIMisses, _lastIL2Hits, _lastIL2Misses, _lastIL3Hits, _lastIL3Misses;
    private long _lastDHits, _lastDMisses, _lastDL2Hits, _lastDL2Misses, _lastDL3Hits, _lastDL3Misses;
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
        MemoryConfig dMemConfig
    ) : base(name, parent, esc) {
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
        _predictor = predictor;
        ILayers = MemoryLayers.Build(memory, iMemConfig);
        DLayers = MemoryLayers.Build(memory, dMemConfig);
        _capMem = new CapturingMemory(DLayers.Accessor);
        _issueWidth = issueWidth;
        _maxDecodeDepth = issueWidth * 4;
        _fetchPc = entryPoint;

        State = mechanism.CreateArchState();
        State.Pc = entryPoint;

        int archRegs = State.IntegerRegisters.Count;
        int physRegs = archRegs + extraPhysRegs;
        _prf = new PhysicalRegisterFile(physRegs);
        _rat = new RenameMap(archRegs, physRegs);
        _rob = new ReorderBuffer(robCapacity);
        _iq = new IssueQueue(iqCapacity);
    }

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _flushesCounter = Dials.AddCounter("flushes", "Pipeline flushes (branch + trap)");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions");
        _stallsCounter = Dials.AddCounter(
            "stalls", "Dispatch-stall cycles (structural hazards) + cache miss penalties"
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

        bool anyCache = ILayers.Cache is not null || DLayers.Cache is not null
                                                  || ILayers.L2Cache is not null || DLayers.L2Cache is not null
                                                  || ILayers.L3Cache is not null || DLayers.L3Cache is not null
                                                  || ILayers.Tlb is not null || DLayers.Tlb is not null;
        if (anyCache)
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

        // Drain cache/TLB stall penalties from the previous cycle's memory operations.
        // Lump-sum: does not model memory-level parallelism available in real OoO hardware.
        long cacheStalls = DrainAndChargeStalls();
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
                rob.StoreAddress = r.StoreAddr;
                rob.StoreValue = r.StoreVal;
                rob.StoreWidth = r.StoreBytes;
            }

            if (r.RegValue.HasValue && r.PhysDest >= 0) {
                _prf.Write(r.PhysDest, r.RegValue.Value);
                _iq.Broadcast(r.PhysDest, r.RegValue.Value);
            }
        }

        _cdbBuffer.Clear();
    }

    /// <summary>In-order retirement from the ROB head.</summary>
    private void StepCommit() {
        var committed = 0;
        while (!_rob.IsEmpty && _rob.Head.IsComplete && committed < _issueWidth) {
            RobEntry head = _rob.Head;

            if (head.IsHalt) {
                CommitRegisters(head);
                _rob.Retire();
                _retiredCounter.Increment();
                _halted = true;
                return;
            }

            if (head.HasTrap && head.Trap is not null) {
                ulong target = _trapController.RaiseTrap(head.Trap, State);
                CommitRegisters(head);
                _rob.Retire();
                _retiredCounter.Increment();
                SetFlush(target);
                return;
            }

            if (head.IsReturnFromTrap && head.ReturnPrivilege.HasValue) {
                ulong target = _trapController.ReturnFromTrap(head.ReturnPrivilege.Value, State);
                CommitRegisters(head);
                _rob.Retire();
                _retiredCounter.Increment();
                SetFlush(target);
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
                    _rob.Retire();
                    _retiredCounter.Increment();
                    SetFlush(resolvedPc);
                    return;
                }
            }

            State.Pc = head.PredictedNextPc;
            _rob.Retire();
            _retiredCounter.Increment();
            committed++;
        }
    }

    /// <summary>Execute instructions issued last tick, filling the CDB buffer.</summary>
    private void StepExecute() {
        foreach (IssuedInstr issued in _execBuffer) _cdbBuffer.Add(ExecuteOne(issued));
        _execBuffer.Clear();
    }

    /// <summary>Select up to issueWidth ready IQ entries and forward to execute.</summary>
    private void StepIssue() {
        var issued = 0;
        for (var slot = 0; slot < _iq.Capacity && issued < _issueWidth; slot++) {
            RsEntry rs = _iq.At(slot);
            if (!rs.Busy || !rs.IsReady) continue;

            // Conservative load ordering: stall a load if any preceding in-flight
            // store hasn't committed yet (its write is deferred to ROB commit).
            if (rs.Instruction?.Class == ToothClass.Load && HasPrecedingPendingStore(rs.RobIndex)) continue;

            _execBuffer.Add(
                new IssuedInstr(
                    rs.RobIndex, rs.PhysDestination, rs.Instruction!, rs.Pc,
                    rs.Src1Value, rs.Src2Value, rs.Src3Value
                )
            );
            _iq.Free(slot);
            issued++;
        }
    }

    private bool HasPrecedingPendingStore(int loadRobIndex) {
        foreach ((int idx, RobEntry entry) in _rob.InOrder()) {
            if (idx == loadRobIndex) return false;
            // Block if any preceding store is still in the ROB — stores write to
            // memory only at commit, so a load must not execute until all prior
            // stores have left the ROB (i.e., committed).
            if (entry.IsStore) return true;
        }

        return false;
    }

    /// <summary>Rename and allocate ROB + IQ slots for decoded instructions.</summary>
    private void StepDispatch() {
        while (_decodeQueue.Count > 0) {
            // Stop if any structural resource is exhausted.
            if (_rob.IsFull || _iq.IsFull) break;

            FetchedInstr fi = _decodeQueue.Peek();
            ITooth instr = fi.Decoded;
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
            rob.Instruction = instr;
            rob.ArchDestination = destArch > 0 ? destArch : -1;
            rob.PhysDestination = newPhys;
            rob.PrevPhysDestination = oldPhys;
            rob.PredictedNextPc = fi.PredictedNextPc;
            rob.IsStore = instr.Class == ToothClass.Store;
            rob.IsHalt = instr.Class == ToothClass.Halt;

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
        var fetched = 0;
        while (fetched < _issueWidth && _decodeQueue.Count < _maxDecodeDepth) {
            ITooth decoded;
            uint raw;
            try {
                raw = (uint)ILayers.Accessor.Read(_fetchPc, 4);
                decoded = _decoder.Decode(_fetchPc, raw);
            }
            catch {
                break; // memory fault or illegal instruction — stop fetching
            }

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            FetchHint hint = _decoder.GetFetchHint(_fetchPc, raw);
            ulong predictedNext;
            if (hint.IsBranch) {
                BranchPrediction pred = _predictor.Predict(_fetchPc);
                predictedNext = pred.PredictedTaken ? pred.PredictedTarget : _fetchPc + (ulong)decoded.SizeBytes;
            }
            else { predictedNext = _fetchPc + (ulong)decoded.SizeBytes; }

            _decodeQueue.Enqueue(new FetchedInstr(_fetchPc, decoded, predictedNext));
            _fetchPc = predictedNext;
            fetched++;
        }
    }

    // ── Flush (misprediction / trap) ───────────────────────────────────────────

    private void StepFlush() {
        _flushesCounter.Increment();

        // Walk ROB youngest-to-oldest, restoring the RAT to committed state.
        foreach ((_, RobEntry entry) in _rob.InOrder().Reverse())
            if (entry.ArchDestination > 0 && entry.PhysDestination >= 0) {
                _rat.RestoreMapping(entry.ArchDestination, entry.PrevPhysDestination);
                _rat.FreePhysical(entry.PhysDestination);
            }

        _rob.Flush();
        _iq.Flush();
        _decodeQueue.Clear();
        _execBuffer.Clear();
        _cdbBuffer.Clear();

        _fetchPc = _flushTarget;
        _flushPending = false;
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

        _capMem.Reset();
        ExecuteResult er = _executor.Execute(issued.Instr, State, _capMem);

        // Restore arch state to committed values.
        if (s0 >= 0) regs.Write(s0, save0);
        if (s1 >= 0) regs.Write(s1, save1);
        if (s2 >= 0) regs.Write(s2, save2);

        ulong? resolvedNextPc = issued.Instr.Class switch {
            ToothClass.Branch =>
                er.BranchTarget ?? issued.Pc + (ulong)issued.Instr.SizeBytes,
            ToothClass.ConditionalBranch =>
                er.BranchTaken
                    ? er.BranchTarget ?? issued.Pc + (ulong)issued.Instr.SizeBytes
                    : issued.Pc + (ulong)issued.Instr.SizeBytes,
            _ => null,
        };

        return new ExecResult(
            issued.RobIdx, issued.PhysDest,
            er.RegisterResult, resolvedNextPc, er.Trap,
            er.IsReturnFromTrap, er.ReturnPrivilege,
            _capMem.HasWrite, _capMem.WriteAddress, _capMem.WriteValue, _capMem.WriteBytes
        );
    }

    /// <summary>
    /// Commits the destination register of a ROB entry: writes the PRF value
    /// to the arch state, frees the old physical register, and advances State.Pc.
    /// </summary>
    private void CommitRegisters(RobEntry head) {
        if (head.PhysDestination >= 0 && head.ArchDestination > 0) {
            ulong val = _prf.Read(head.PhysDestination);
            State.IntegerRegisters.Write(head.ArchDestination, val);
            if (head.PrevPhysDestination >= 0) _rat.FreePhysical(head.PrevPhysDestination);
        }
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
            ILayers.L2Cache, _l2IcacheHitsCounter, _l2IcacheMissesCounter, ref _lastIL2Hits, ref _lastIL2Misses
        );
        UpdateCacheStat(
            ILayers.L3Cache, _l3IcacheHitsCounter, _l3IcacheMissesCounter, ref _lastIL3Hits, ref _lastIL3Misses
        );
        UpdateCacheStat(DLayers.Cache, _dcacheHitsCounter, _dcacheMissesCounter, ref _lastDHits, ref _lastDMisses);
        UpdateCacheStat(
            DLayers.L2Cache, _l2DcacheHitsCounter, _l2DcacheMissesCounter, ref _lastDL2Hits, ref _lastDL2Misses
        );
        UpdateCacheStat(
            DLayers.L3Cache, _l3DcacheHitsCounter, _l3DcacheMissesCounter, ref _lastDL3Hits, ref _lastDL3Misses
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