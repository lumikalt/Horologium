using System.Linq;
using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using RiscV.Decode;
using RiscV.State;
using RiscV.Trains.Ooo;

namespace RiscV.Trains;

// ── Public wrapper ─────────────────────────────────────────────────────────────

public sealed class OoOETrain {
    private readonly Train _train;
    private readonly OoOPipelineCore _core;

    public IArchState ArchState => _core.State;

    public OoOETrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 16,
        int extraPhysRegs = 32,
        IBranchPredictor? predictor = null
    ) {
        var esc = new Escapement();
        _train = new Train("ooo", esc);
        _core = _train.AddGear(
            new OoOPipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, entryPoint,
                issueWidth, robCapacity, iqCapacity, extraPhysRegs,
                predictor ?? new AlwaysNotTakenPredictor()
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
    private readonly IMemory _memory;
    private readonly CapturingMemory _capMem;

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

    public RvArchState State { get; }

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
        IBranchPredictor predictor
    ) : base(name, parent, esc) {
        _decoder = mechanism.Decoder;
        _executor = mechanism.Executor;
        _trapController = mechanism.TrapController;
        _predictor = predictor;
        _memory = memory;
        _capMem = new CapturingMemory(memory);
        _issueWidth = issueWidth;
        _maxDecodeDepth = issueWidth * 4;
        _fetchPc = entryPoint;

        State = (RvArchState)mechanism.CreateArchState();
        State.Pc = entryPoint;

        const int archRegs = 64; // RV32IF unified: x0–x31 + f0–f31
        int physRegs = archRegs + extraPhysRegs;
        _prf = new PhysicalRegisterFile(physRegs);
        _rat = new RenameMap(archRegs, physRegs);
        _rob = new ReorderBuffer(robCapacity);
        _iq = new IssueQueue(iqCapacity);
    }

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _flushesCounter = Dials.AddCounter("flushes", "Pipeline flushes");

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
    }

    public override void Wind() =>
        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);

    // ── Main driver ────────────────────────────────────────────────────────────

    private void RunCycle() {
        if (_halted) return;

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
                ulong target = head.Instruction?.Payload is RvMret
                    ? _trapController.ReturnFromTrap(PrivilegeLevel.Machine, State)
                    : _trapController.RaiseTrap(head.Trap, State);
                CommitRegisters(head);
                _rob.Retire();
                _retiredCounter.Increment();
                SetFlush(target);
                return;
            }

            if (head.IsStore) _memory.Write(head.StoreAddress, head.StoreValue, head.StoreWidth);

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
            if (entry.IsStore && !entry.IsComplete) return true;
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
            rob.IsHalt = instr.Payload is RvEbreak;

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
    }

    /// <summary>Fetch up to issueWidth instructions into the decode queue.</summary>
    private void StepFetch() {
        var fetched = 0;
        while (fetched < _issueWidth && _decodeQueue.Count < _maxDecodeDepth) {
            ITooth decoded;
            uint raw;
            try {
                raw = (uint)_memory.Read(_fetchPc, 4);
                decoded = _decoder.Decode(_fetchPc, raw);
            }
            catch {
                break; // memory fault or illegal instruction — stop fetching
            }

            // Only branch/jump instructions consult the predictor; all others
            // continue sequentially to avoid corrupting the BTB.
            uint opcode = raw & 0x7F;
            bool isBranch = opcode is 0x63 or 0x6F or 0x67;
            ulong predictedNext;
            if (isBranch) {
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
}