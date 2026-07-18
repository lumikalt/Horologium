using Mechanism;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
///     Decoupled Access-Execute (DAE) Train (Smith, ISCA 1982): a single hart is split into an
///     Access lane (address generation, loads, stores) and an Execute lane (everything else),
///     each an independent in-order queue, so a load miss stalls only the address-dependent
///     chain behind it rather than unrelated compute already queued on the other lane.
///     <para>
///         Unlike Smith's original split — which required a compiler that partitioned the
///         program into two static instruction streams — this is a <em>runtime-slicing</em>
///         implementation that runs stock, unmodified binaries: a single shared front end
///         fetches and decodes sequentially and classifies each instruction into a lane at
///         dispatch time via a forward dependency-chain heuristic (an ALU op is Access-class
///         if any source register was produced by an address-generating Access-class op; a
///         load's result is explicitly <em>not</em> tagged as address-chain, so "load then
///         compute" correctly lands on the Execute lane). Control-flow, system, and
///         multi-cycle-class instructions (branches, CSR/ECALL, fences, atomics, vector,
///         floating point, UVE) are resolved as synchronizing barriers: the front end drains
///         both lanes before executing them directly against the live architectural state, so
///         PC redirection stays precise and simple.
///     </para>
///     <para>
///         Cross-lane RAW dependencies are satisfied through per-write <see cref="HandoffSlot" />
///         objects captured at dispatch time (not a sequence-number scoreboard): a consumer in
///         the other lane reads the exact producer instance it depended on, so a same-lane
///         instruction reusing that register before the cross-lane read happens can never cause
///         the consumer to observe the wrong value. A same-lane dependency needs no slot — each
///         lane retires its own queue strictly in program order, so the producer has always
///         already written the live register file by the time a same-lane consumer executes.
///     </para>
///     <para>
///         <b>Precise exceptions from lane instructions</b> (a faulting load/store, most
///         notably) are handled without ever halting: every lane-instruction register write is
///         logged to an undo list tagged with its dispatch-order sequence number, and every
///         memory write goes through <see cref="UndoLoggingMemory" />, which logs the prior
///         value the same way. When a lane instruction traps, the front end pauses and both
///         lanes are allowed to keep draining — but only instructions strictly older, in program
///         order, than the trap — until nothing older remains in flight (cross-lane read
///         dependencies only ever point backward in program order, so this always terminates).
///         At that point every logged write younger than the trap is unwound in reverse order,
///         both lane queues and any stale pending barrier are flushed, and the trap is raised
///         against now-precise architectural state. The undo log is cleared (not just the
///         rolled-back suffix) whenever a trap resolves or a barrier drains both lanes, since
///         those are exactly the points at which nothing still in flight can ever be older.
///     </para>
/// </summary>
public sealed class DaeTrain : ISteppableTrain {
    private readonly DaeCore _core;
    private readonly Train _train;

    public DaeTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int laneQueueDepth = 8,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        PEventLog? pEventLog = null
    ) {
        ArgumentNullException.ThrowIfNull(mechanism);
        ArgumentNullException.ThrowIfNull(memory);
        if (laneQueueDepth < 1) throw new ArgumentException("Must be at least 1.", nameof(laneQueueDepth));

        var esc = new Escapement();
        _train = new Train("dae", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new DaeCore(
                "pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, laneQueueDepth, pEventLog
            )
        );
        _train.Build();
    }

    public PEventLog? PEventLog => _core.PEventLog;

    public long CurrentTick => _train.CurrentTick;
    public bool IsIdle => _train.IsIdle;

    public IArchState ArchState => _core.State;

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();
}

// ── Lane synchronization primitives ─────────────────────────────────────────────

internal enum Lane { Access, Execute, }

/// <summary>
///     A one-shot, value-capturing handoff for a single cross-lane register write. Allocated at
///     dispatch time for every register-writing instruction and filled in exactly once, when
///     that specific instruction retires. A cross-lane consumer references the slot instance
///     it depended on at dispatch time, not a live register index — so a later same-lane
///     write to the same architectural register can never be mistaken for the value this
///     consumer actually needs.
/// </summary>
internal sealed class HandoffSlot {
    public bool Ready;
    public ulong Value;
}

/// <summary>One instruction dispatched into a lane queue, with its resolved cross-lane reads.</summary>
internal sealed class DaeInstruction {
    /// <summary>The slot to fill when this instruction retires, or null if it writes no register.</summary>
    public HandoffSlot? CaptureWrite;

    /// <summary>Source register index → the specific producer's slot, for cross-lane RAW sources only.</summary>
    public Dictionary<int, HandoffSlot>? CrossLaneReads;

    /// <summary>PEvent instruction id (shared numbering with barriers), 0 when tracing is off.</summary>
    public ulong InstrId;

    public required ulong Pc;

    /// <summary>Monotonic dispatch-order index — the program-order tiebreaker used to resolve precise exceptions.</summary>
    public required ulong Seq;

    public required ITooth Tooth;
}

/// <summary>
///     One undone-able architectural mutation: either a register write (<see cref="IsMemory" /> =
///     false) or a memory write (<see cref="IsMemory" /> = true), tagged with the dispatch-order
///     <see cref="Seq" /> of the instruction that made it. Used to unwind writes made by
///     instructions that turn out to be younger, in program order, than a lane instruction that
///     later traps.
/// </summary>
internal readonly struct UndoEntry {
    public UndoEntry(ulong seq, int register, ulong prevValue) {
        Seq = seq;
        IsMemory = false;
        Register = register;
        PrevValue = prevValue;
        Address = 0;
        Bytes = 0;
    }

    public UndoEntry(ulong seq, ulong address, int bytes, ulong prevValue) {
        Seq = seq;
        IsMemory = true;
        Register = -1;
        PrevValue = prevValue;
        Address = address;
        Bytes = bytes;
    }

    public ulong Seq { get; }
    public bool IsMemory { get; }
    public int Register { get; }
    public ulong Address { get; }
    public int Bytes { get; }
    public ulong PrevValue { get; }
}

/// <summary>
///     Wraps the shared data memory accessor and logs the prior value of every write into
///     <paramref name="log" />, tagged with <see cref="CurrentSeq" /> (set by the caller before
///     each <see cref="IExecutor.Execute" /> call). Only lane-instruction writes need to be
///     undo-able — barrier instructions execute once both lanes are fully drained and write
///     straight to the unwrapped accessor.
/// </summary>
internal sealed class UndoLoggingMemory(IMemory inner, List<UndoEntry> log) : IMemory {
    public ulong CurrentSeq;

    public ulong Read(ulong address, int bytes) => inner.Read(address, bytes);

    public void Write(ulong address, ulong value, int bytes) {
        log.Add(new UndoEntry(CurrentSeq, address, bytes, inner.Read(address, bytes)));
        inner.Write(address, value, bytes);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) => inner.Load(address, data);
    public void InvalidateLine(ulong address) => inner.InvalidateLine(address);
    public void CleanLine(ulong address) => inner.CleanLine(address);
    public void FlushLine(ulong address) => inner.FlushLine(address);
    public void SetRequestPc(ulong pc) => inner.SetRequestPc(pc);
}

/// <summary>
///     A lane trap awaiting resolution: the dispatch-order <see cref="Seq" /> of the trapping instruction and its
///     captured <see cref="Trap" /> info.
/// </summary>
internal readonly struct PendingTrap(ulong seq, TrapInfo trap) {
    public ulong Seq { get; } = seq;
    public TrapInfo Trap { get; } = trap;
}

/// <summary>
///     Intercepts reads of specific register indices during one <see cref="IExecutor.Execute" />
///     call, redirecting them to captured <see cref="HandoffSlot" /> values instead of the live
///     register file. Writes always pass through to the real register file untouched.
/// </summary>
internal sealed class OverrideRegisterFile(IRegisterFile inner, Dictionary<int, HandoffSlot> overrides)
    : IRegisterFile {
    public int Count => inner.Count;
    public int Width => inner.Width;

    public ulong Read(int index) =>
        overrides.TryGetValue(index, out HandoffSlot? slot) ? slot.Value : inner.Read(index);

    public void Write(int index, ulong value) => inner.Write(index, value);
    public void Reset() => inner.Reset();
}

internal sealed class OverrideArchState(IArchState inner, IRegisterFile registers) : IArchState {
    public ulong Pc {
        get => inner.Pc;
        set => inner.Pc = value;
    }

    public PrivilegeLevel PrivilegeLevel {
        get => inner.PrivilegeLevel;
        set => inner.PrivilegeLevel = value;
    }

    public IRegisterFile IntegerRegisters { get; set; } = registers;
    public ISystemRegisters SystemRegisters => inner.SystemRegisters;
    public IArchState Snapshot() => inner.Snapshot();
    public void Reset() => inner.Reset();
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
///     The DAE core Gear. Each tick: (1) each lane may retire the instruction at its queue head
///     if its cross-lane dependencies are ready; (2) the shared front end fetches, decodes,
///     classifies, and dispatches one instruction into a lane, or stages a barrier instruction
///     and waits for both lanes to drain before executing it synchronously.
/// </summary>
internal sealed class DaeCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    MemoryLayers iLayers,
    MemoryLayers dLayers,
    ulong entryPoint,
    int laneQueueDepth,
    PEventLog? pEventLog = null
) : Gear(name, parent, esc) {
    private readonly Queue<DaeInstruction> _accessQueue = new();
    private readonly Queue<DaeInstruction> _executeQueue = new();
    private readonly List<UndoEntry> _undoLog = [];
    private Counter _accessIssuedCounter = null!;

    private bool[] _addressTaint = [];
    private Counter _crossLaneStallCounter = null!;

    private Counter _cyclesCounter = null!;
    private Counter _executeIssuedCounter = null!;
    private ulong _fetchPc;
    private IFetchTranslator? _fetchTranslator;
    private bool _halted;
    private (Lane Lane, HandoffSlot Slot)?[] _lastWriter = [];
    private ulong _nextInstrId = 1;
    private ulong _nextSeq;
    private ITooth? _pendingBarrier;
    private ulong _pendingBarrierInstrId;
    private ulong _pendingBarrierPc;
    private PendingTrap? _pendingTrap;
    private Counter _preciseTrapsCounter = null!;
    private Counter _retiredCounter = null!;
    private Action? _runCycle;
    private Counter _stallsCounter = null!;
    private UndoLoggingMemory _undoMemory = null!;
    public PEventLog? PEventLog { get; } = pEventLog;

    public IArchState State { get; } = CreateInitialState(mechanism, entryPoint);

    private static IArchState CreateInitialState(IMechanism m, ulong ep) {
        IArchState s = m.CreateArchState();
        s.Pc = ep;
        return s;
    }

    public override void Initialize() {
        _fetchTranslator = mechanism.CreateFetchTranslator(State, iLayers.Accessor);
        _undoMemory = new UndoLoggingMemory(dLayers.Accessor, _undoLog);

        int regCount = State.IntegerRegisters.Count;
        _addressTaint = new bool[regCount];
        _lastWriter = new (Lane, HandoffSlot)?[regCount];

        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Cycles lost to cache/TLB misses or a fully idle front end");
        _crossLaneStallCounter = Dials.AddCounter(
            "cross_lane_stalls", "Lane-head stalls waiting on a not-yet-ready cross-lane value"
        );
        _accessIssuedCounter = Dials.AddCounter("access_issued", "Instructions dispatched to the Access lane");
        _executeIssuedCounter = Dials.AddCounter("execute_issued", "Instructions dispatched to the Execute lane");
        _preciseTrapsCounter = Dials.AddCounter(
            "precise_traps", "Lane-instruction traps resolved via undo-log rollback"
        );
        Dials.AddDial(
            "ipc",
            () => _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
            "Instructions per cycle"
        );
        Dials.AddDial(
            "cpi",
            () => _retiredCounter.Value == 0 ? 0.0 : _cyclesCounter.Value / (double)_retiredCounter.Value,
            "Cycles per instruction"
        );
    }

    public override void Wind() {
        State.Pc = entryPoint;
        _fetchPc = entryPoint;
        _accessQueue.Clear();
        _executeQueue.Clear();
        _pendingBarrier = null;
        _pendingTrap = null;
        _halted = false;
        _nextSeq = 0;
        _undoLog.Clear();
        Array.Clear(_addressTaint);
        Array.Clear(_lastWriter);
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    private void RunCycle() {
        bool accessAdvanced = TryExecuteLaneHead(Lane.Access);
        bool executeAdvanced = TryExecuteLaneHead(Lane.Execute);

        var dispatched = false;
        if (_pendingTrap is { } trap) {
            bool accessClear = _accessQueue.Count == 0 || _accessQueue.Peek().Seq > trap.Seq;
            bool executeClear = _executeQueue.Count == 0 || _executeQueue.Peek().Seq > trap.Seq;
            if (accessClear && executeClear) ResolveTrap(trap);
        }
        else if (_pendingBarrier is not null) {
            if (_accessQueue.Count == 0 && _executeQueue.Count == 0) ExecuteBarrier();
        }
        else if (!_halted) { dispatched = TryDispatchOne(); }

        if (!accessAdvanced && _accessQueue.Count > 0) _crossLaneStallCounter.Increment();
        if (!executeAdvanced && _executeQueue.Count > 0) _crossLaneStallCounter.Increment();

        long cacheStalls = iLayers.ConsumeAllStalls() + dLayers.ConsumeAllStalls();
        // State.OnCycle advances the cycle CSR — self-timing workloads (rdcycle
        // calibration loops) never terminate without it.
        _cyclesCounter.Increment();
        State.OnCycle();
        if (cacheStalls > 0) {
            _stallsCounter.IncrementBy(cacheStalls);
            _cyclesCounter.IncrementBy(cacheStalls);
            for (long i = 0; i < cacheStalls; i++) State.OnCycle();
        }

        bool idleCycle = !accessAdvanced && !executeAdvanced && !dispatched
                      && _pendingBarrier is null && _pendingTrap is null && !_halted;
        if (idleCycle) _stallsCounter.Increment();

        if (!_halted) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // Fetches, decodes, classifies, and dispatches (at most) one instruction. Returns true if
    // the front end made progress this cycle (including staging a barrier), false if it was
    // blocked by a full target-lane queue and must retry the same PC next cycle.
    private bool TryDispatchOne() {
        ulong pc = _fetchPc;
        ITooth instr;

        if (_fetchTranslator is not null) {
            (ulong physPc, int faultCause) = _fetchTranslator.Translate(pc);
            if (faultCause != 0) {
                State.Pc = mechanism.TrapController.RaiseTrap(new TrapInfo(faultCause, pc, pc), State);
                _fetchPc = State.Pc;
                return true;
            }

            try {
                var raw = (uint)iLayers.Accessor.Read(physPc, 4);
                instr = mechanism.Decoder.Decode(pc, raw);
            }
            catch (IllegalInstructionException ex) {
                State.Pc = mechanism.TrapController.RaiseTrap(
                    new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), State
                );
                _fetchPc = State.Pc;
                return true;
            }
        }
        else {
            try { instr = mechanism.Decoder.Decode(pc, iLayers.Accessor); }
            catch (IllegalInstructionException ex) {
                State.Pc = mechanism.TrapController.RaiseTrap(
                    new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), State
                );
                _fetchPc = State.Pc;
                return true;
            }
        }

        if (IsBarrierClass(instr.Class)) {
            _pendingBarrier = instr;
            _pendingBarrierPc = pc;
            _pendingBarrierInstrId = _nextInstrId++;
            if (PEventLog is not null) {
                PEventLog.Record(_pendingBarrierInstrId, pc, _cyclesCounter.Value, PEventKind.Fetch);
                PEventLog.RecordDisasm(_pendingBarrierInstrId, mechanism.Decoder.Disassemble(pc, instr.RawEncoding));
            }

            return true;
        }

        Lane lane = ClassifyLane(instr);
        Queue<DaeInstruction> queue = lane == Lane.Access ? _accessQueue : _executeQueue;
        if (queue.Count >= laneQueueDepth) return false;

        Dictionary<int, HandoffSlot>? crossLaneReads = null;
        foreach (int src in instr.SourceRegisters) {
            (Lane Lane, HandoffSlot Slot)? writer = _lastWriter[src];
            if (writer is { } w && w.Lane != lane) {
                crossLaneReads ??= [];
                crossLaneReads[src] = w.Slot;
            }
        }

        HandoffSlot? captureWrite = null;
        if (instr.DestinationRegister >= 0) {
            captureWrite = new HandoffSlot();
            _lastWriter[instr.DestinationRegister] = (lane, captureWrite);
        }

        UpdateTaint(instr, lane);

        ulong instrId = _nextInstrId++;
        if (PEventLog is not null) {
            // Fetch at dispatch, Execute/Retire at lane execution: the waterfall's F→EX gap
            // makes the Access/Execute lane slip directly visible.
            PEventLog.Record(instrId, pc, _cyclesCounter.Value, PEventKind.Fetch);
            PEventLog.Record(instrId, pc, _cyclesCounter.Value, PEventKind.Dispatch);
            PEventLog.RecordDisasm(instrId, mechanism.Decoder.Disassemble(pc, instr.RawEncoding));
        }

        queue.Enqueue(
            new DaeInstruction {
                Tooth = instr, Pc = pc, Seq = _nextSeq++, CrossLaneReads = crossLaneReads,
                CaptureWrite = captureWrite, InstrId = instrId,
            }
        );
        (lane == Lane.Access ? _accessIssuedCounter : _executeIssuedCounter).Increment();

        _fetchPc = pc + (ulong)instr.SizeBytes;
        return true;
    }

    // Access/Execute classification: loads and stores are always Access-class. An ALU/MulDiv
    // op is Access-class if any source register is address-tainted, else Execute-class.
    private Lane ClassifyLane(ITooth instr) {
        if (instr.Class is ToothClass.Load or ToothClass.Store) return Lane.Access;
        foreach (int src in instr.SourceRegisters)
            if (_addressTaint[src])
                return Lane.Access;
        return Lane.Execute;
    }

    // Propagates the address-chain taint used purely for lane classification (a heuristic, not
    // a correctness mechanism — cross-lane value correctness comes from _lastWriter/HandoffSlot
    // regardless of how instructions are classified). Taint is seeded forward, with no
    // lookahead: a register is marked address-chain the moment a Load or Store is *observed*
    // reading it (so a later re-derivation of that register, e.g. a pointer increment for the
    // next iteration of an array walk, correctly lands back on the Access lane), and an ALU op
    // propagates taint from any tainted source to its destination. A load's own destination is
    // explicitly untainted: the loaded value is data, not an address, even though the load
    // itself is always Access-class — this is what keeps "load a value, then compute with it"
    // on the Execute lane instead of dragging it back into Access. Register 0 is never tainted
    // since it is conventionally hardwired zero, and tainting it would collapse ordinary
    // "addi rd, x0, imm" idioms onto the Access lane and defeat the whole classification.
    // Note: ITooth.SourceRegisters does not distinguish a store's address operand from its
    // value-to-store operand, so a store taints both — a harmless over-approximation, since
    // taint only affects classification quality, never correctness.
    private void UpdateTaint(ITooth instr, Lane lane) {
        if (instr.Class is ToothClass.Load or ToothClass.Store)
            foreach (int src in instr.SourceRegisters)
                if (src > 0)
                    _addressTaint[src] = true;

        int dest = instr.DestinationRegister;
        if (dest <= 0) return;
        _addressTaint[dest] = instr.Class != ToothClass.Load && lane == Lane.Access;
    }

    private static bool IsBarrierClass(ToothClass cls) =>
        cls is not (ToothClass.IntegerAlu or ToothClass.IntegerMulDiv or ToothClass.Load or ToothClass.Store);

    // Executes the instruction at the head of the given lane's queue if it is not blocked on a
    // not-yet-ready cross-lane dependency. Returns true if it retired this cycle.
    private bool TryExecuteLaneHead(Lane lane) {
        Queue<DaeInstruction> queue = lane == Lane.Access ? _accessQueue : _executeQueue;
        if (queue.Count == 0) return false;

        DaeInstruction inst = queue.Peek();
        if (_pendingTrap is { } trap && inst.Seq > trap.Seq) return false;
        if (inst.CrossLaneReads is not null)
            foreach (HandoffSlot slot in inst.CrossLaneReads.Values)
                if (!slot.Ready)
                    return false;

        queue.Dequeue();
        ExecuteLaneInstruction(inst);
        return true;
    }

    private void ExecuteLaneInstruction(DaeInstruction inst) {
        ITooth instr = inst.Tooth;
        IArchState execState = inst.CrossLaneReads is null
            ? State
            : new OverrideArchState(State, new OverrideRegisterFile(State.IntegerRegisters, inst.CrossLaneReads));

        _undoMemory.SetRequestPc(inst.Pc);
        _undoMemory.CurrentSeq = inst.Seq;
        ExecuteResult result = mechanism.Executor.Execute(instr, execState, _undoMemory);
        _retiredCounter.Increment();
        if (PEventLog is not null) {
            PEventLog.Record(inst.InstrId, inst.Pc, _cyclesCounter.Value, PEventKind.Execute);
            if (!result.HasTrap) PEventLog.Record(inst.InstrId, inst.Pc, _cyclesCounter.Value, PEventKind.Retire);
        }

        if (result.HasTrap) {
            // A faulting Load/Store never reaches its RegisterResult/SideEffect/memory-write
            // stage, so there is nothing to undo for this instruction itself. Stage the trap;
            // RunCycle resolves it (rolling back any younger writes already made by the other
            // lane) once both lanes have drained down to program-order-older instructions only.
            _pendingTrap = new PendingTrap(inst.Seq, result.Trap!);
            _preciseTrapsCounter.Increment();
            return;
        }

        if (result.IsHalt || result.RequestHalt || result.IsReturnFromTrap) {
            // Unreachable for lane-class instructions (IntegerAlu/IntegerMulDiv/Load/Store never
            // halt or return-from-trap — those are System-class barriers) but kept as a defensive
            // fallback rather than silently mishandling an assumption violation.
            _halted = true;
            return;
        }

        result.SideEffect?.Invoke(execState);
        if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0) {
            ulong prevValue = State.IntegerRegisters.Read(instr.DestinationRegister);
            _undoLog.Add(new UndoEntry(inst.Seq, instr.DestinationRegister, prevValue));
            State.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);
        }

        if (inst.CaptureWrite is not null) {
            inst.CaptureWrite.Value = instr.DestinationRegister >= 0
                ? State.IntegerRegisters.Read(instr.DestinationRegister)
                : 0;
            inst.CaptureWrite.Ready = true;
        }
    }

    // Rolls back every logged write younger than the trapping instruction (in reverse
    // application order, so chains of writes to the same register/address unwind correctly),
    // flushes both lanes (everything remaining in them is younger than the trap by
    // construction — RunCycle only calls this once both queue heads clear trap.Seq), discards
    // the now-stale pending barrier (barriers are always staged after every currently-queued
    // lane instruction, so they are unconditionally younger than any lane trap), and redirects
    // to the trap handler. Clearing the whole undo log is safe here, not just the rolled-back
    // suffix: RunCycle only resolves a trap once nothing older remains in-flight, so everything
    // at or before trap.Seq has already permanently retired and can never need to be undone.
    private void ResolveTrap(PendingTrap trap) {
        _pendingTrap = null;
        if (PEventLog is not null) {
            // Everything still queued (and any staged barrier) is younger than the trap.
            foreach (DaeInstruction inst in _accessQueue)
                PEventLog.Record(inst.InstrId, inst.Pc, _cyclesCounter.Value, PEventKind.Flush);
            foreach (DaeInstruction inst in _executeQueue)
                PEventLog.Record(inst.InstrId, inst.Pc, _cyclesCounter.Value, PEventKind.Flush);
            if (_pendingBarrier is not null)
                PEventLog.Record(_pendingBarrierInstrId, _pendingBarrierPc, _cyclesCounter.Value, PEventKind.Flush);
        }

        _pendingBarrier = null;

        for (int i = _undoLog.Count - 1; i >= 0; i--) {
            UndoEntry e = _undoLog[i];
            if (e.Seq <= trap.Seq) continue;
            if (e.IsMemory)
                dLayers.Accessor.Write(e.Address, e.PrevValue, e.Bytes);
            else
                State.IntegerRegisters.Write(e.Register, e.PrevValue);
        }

        _undoLog.Clear();
        _accessQueue.Clear();
        _executeQueue.Clear();
        Array.Clear(_lastWriter);
        Array.Clear(_addressTaint);

        State.Pc = mechanism.TrapController.RaiseTrap(trap.Trap, State);
        _fetchPc = State.Pc;
    }

    // Executes a staged control-flow/system/multi-cycle-class instruction directly against the
    // live architectural state. Only called once both lanes have fully drained, so PC
    // redirection is always precise.
    private void ExecuteBarrier() {
        ITooth instr = _pendingBarrier!;
        ulong pc = _pendingBarrierPc;
        _pendingBarrier = null;

        // Both lanes are fully drained here, so everything retired so far is permanent — no
        // future trap can ever be older than this point. Bounds undo-log growth.
        _undoLog.Clear();

        dLayers.Accessor.SetRequestPc(pc);
        ExecuteResult result = mechanism.Executor.Execute(instr, State, dLayers.Accessor);
        _retiredCounter.Increment();
        if (PEventLog is not null) {
            PEventLog.Record(_pendingBarrierInstrId, pc, _cyclesCounter.Value, PEventKind.Execute);
            PEventLog.Record(_pendingBarrierInstrId, pc, _cyclesCounter.Value, PEventKind.Retire);
        }

        if (result.IsHalt || result.RequestHalt) {
            _halted = true;
            return;
        }

        if (result.HasTrap) {
            State.Pc = mechanism.TrapController.RaiseTrap(result.Trap!, State);
            _fetchPc = State.Pc;
            return;
        }

        if (result.IsReturnFromTrap) {
            State.Pc = mechanism.TrapController.ReturnFromTrap(result.ReturnPrivilege!.Value, State);
            _fetchPc = State.Pc;
            return;
        }

        result.SideEffect?.Invoke(State);
        if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0) {
            State.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);
            _lastWriter[instr.DestinationRegister] = null;
            _addressTaint[instr.DestinationRegister] = false;
        }

        if (result is { BranchTaken: true, BranchTarget: not null, })
            State.Pc = result.BranchTarget.Value;
        else
            State.Pc = pc + (ulong)instr.SizeBytes;

        if (State.Pc == pc && instr.Class == ToothClass.Branch) {
            _halted = true;
            return;
        }

        TrapInfo? interrupt = mechanism.TrapController.PeekInterrupt(State);
        if (interrupt is not null) State.Pc = mechanism.TrapController.RaiseTrap(interrupt, State);

        _fetchPc = State.Pc;
    }
}