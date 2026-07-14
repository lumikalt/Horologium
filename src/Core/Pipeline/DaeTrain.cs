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
///         <b>Known v1 limitation:</b> precise exceptions from a lane instruction (a misaligned
///         or faulting load/store, most notably) are not supported — the other lane may already
///         be ahead in program order with no rollback mechanism, so a lane-instruction trap
///         simply halts the simulation rather than risk silently producing imprecise
///         architectural state. Barrier instructions (which include all traps from branches,
///         ECALL, etc.) are unaffected since they only ever execute once both lanes are fully
///         drained.
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
        MemoryConfig? dMemConfig = null
    ) {
        ArgumentNullException.ThrowIfNull(mechanism);
        ArgumentNullException.ThrowIfNull(memory);
        if (laneQueueDepth < 1) throw new ArgumentException("Must be at least 1.", nameof(laneQueueDepth));

        var esc = new Escapement();
        _train = new Train("dae", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new DaeCore("pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, laneQueueDepth)
        );
        _train.Build();
    }

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

    public required ulong Pc;
    public required ITooth Tooth;
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

    public IRegisterFile IntegerRegisters => registers;
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
    int laneQueueDepth
) : Gear(name, parent, esc) {
    private readonly Queue<DaeInstruction> _accessQueue = new();
    private readonly Queue<DaeInstruction> _executeQueue = new();
    private Counter _accessIssuedCounter = null!;

    private bool[] _addressTaint = [];
    private Counter _crossLaneStallCounter = null!;

    private Counter _cyclesCounter = null!;
    private Counter _executeIssuedCounter = null!;
    private ulong _fetchPc;
    private IFetchTranslator? _fetchTranslator;
    private bool _halted;
    private (Lane Lane, HandoffSlot Slot)?[] _lastWriter = [];
    private ITooth? _pendingBarrier;
    private ulong _pendingBarrierPc;
    private Counter _retiredCounter = null!;
    private Action? _runCycle;
    private Counter _stallsCounter = null!;

    public IArchState State { get; } = CreateInitialState(mechanism, entryPoint);

    private static IArchState CreateInitialState(IMechanism m, ulong ep) {
        IArchState s = m.CreateArchState();
        s.Pc = ep;
        return s;
    }

    public override void Initialize() {
        _fetchTranslator = mechanism.CreateFetchTranslator(State, iLayers.Accessor);

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
        _halted = false;
        Array.Clear(_addressTaint);
        Array.Clear(_lastWriter);
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    private void RunCycle() {
        bool accessAdvanced = TryExecuteLaneHead(Lane.Access);
        bool executeAdvanced = TryExecuteLaneHead(Lane.Execute);

        var dispatched = false;
        if (_pendingBarrier is not null) {
            if (_accessQueue.Count == 0 && _executeQueue.Count == 0) ExecuteBarrier();
        }
        else if (!_halted) { dispatched = TryDispatchOne(); }

        if (!accessAdvanced && _accessQueue.Count > 0) _crossLaneStallCounter.Increment();
        if (!executeAdvanced && _executeQueue.Count > 0) _crossLaneStallCounter.Increment();

        long cacheStalls = iLayers.ConsumeAllStalls() + dLayers.ConsumeAllStalls();
        _cyclesCounter.Increment();
        if (cacheStalls > 0) {
            _stallsCounter.IncrementBy(cacheStalls);
            _cyclesCounter.IncrementBy(cacheStalls);
        }

        bool idleCycle = !accessAdvanced && !executeAdvanced && !dispatched && _pendingBarrier is null && !_halted;
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

        queue.Enqueue(
            new DaeInstruction { Tooth = instr, Pc = pc, CrossLaneReads = crossLaneReads, CaptureWrite = captureWrite, }
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

        dLayers.Accessor.SetRequestPc(inst.Pc);
        ExecuteResult result = mechanism.Executor.Execute(instr, execState, dLayers.Accessor);
        _retiredCounter.Increment();

        if (result.IsHalt || result.RequestHalt || result.HasTrap || result.IsReturnFromTrap) {
            // See class doc: precise exceptions from a lane instruction are not supported in v1.
            _halted = true;
            return;
        }

        result.SideEffect?.Invoke(execState);
        if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
            State.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

        if (inst.CaptureWrite is not null) {
            inst.CaptureWrite.Value = instr.DestinationRegister >= 0
                ? State.IntegerRegisters.Read(instr.DestinationRegister)
                : 0;
            inst.CaptureWrite.Ready = true;
        }
    }

    // Executes a staged control-flow/system/multi-cycle-class instruction directly against the
    // live architectural state. Only called once both lanes have fully drained, so PC
    // redirection is always precise.
    private void ExecuteBarrier() {
        ITooth instr = _pendingBarrier!;
        ulong pc = _pendingBarrierPc;
        _pendingBarrier = null;

        dLayers.Accessor.SetRequestPc(pc);
        ExecuteResult result = mechanism.Executor.Execute(instr, State, dLayers.Accessor);
        _retiredCounter.Increment();

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