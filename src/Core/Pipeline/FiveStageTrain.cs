#region

using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Stages;

#endregion

namespace Pipeline;

public sealed class FiveStageTrain : ISteppableTrain {
    private readonly PipelineCore _core;
    private readonly Train _train;

    public FiveStageTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        bool forwardingEnabled = true,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        int storeBufferCapacity = 0,
        PEventLog? pEventLog = null,
        ICommitObserver? commitObserver = null,
        int fdipFtqCapacity = 0,
        bool rdip = false,
        IMemory? fdipBackingMemory = null
    ) {
        var esc = new Escapement();
        _train = new Train("five_stage", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new PipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, iLayers, dLayers, entryPoint, forwardingEnabled,
                predictor ?? new AlwaysNotTakenPredictor(),
                storeBufferCapacity, pEventLog, commitObserver, fdipFtqCapacity, rdip, fdipBackingMemory
            )
        );
        _train.Build();
    }

    internal FiveStageTrain(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        bool forwardingEnabled = true,
        IBranchPredictor? predictor = null,
        int storeBufferCapacity = 0,
        PEventLog? pEventLog = null,
        ICommitObserver? commitObserver = null,
        int fdipFtqCapacity = 0,
        bool rdip = false,
        IMemory? fdipBackingMemory = null
    ) {
        var esc = new Escapement();
        _train = new Train("five_stage", esc);
        _core = _train.AddGear(
            new PipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, iLayers.Accessor, iLayers, dLayers, entryPoint, forwardingEnabled,
                predictor ?? new AlwaysNotTakenPredictor(),
                storeBufferCapacity, pEventLog, commitObserver, fdipFtqCapacity, rdip, fdipBackingMemory
            )
        );
        _train.Build();
    }

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;
    public StoreBuffer? StoreBuffer => _core.StoreBuffer;
    public PEventLog? PEventLog => _core.PEventLog;

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
}

internal sealed class PipelineCore : Gear {
    private readonly IDecoder _decoder;
    private readonly ExecuteStage _ex;

    // Pre-allocated for per-cycle forwarding/hazard checks — avoids heap allocation every RunCycle.
    private readonly PipelineResident[] _fwdProviders = new PipelineResident[2];
    private readonly HazardUnit _hazard;
    private readonly PipelineResident[] _hazardResidents = new PipelineResident[2];
    private readonly DecodeStage _id;

    private readonly FetchStage _if;
    private readonly LoadTracker _loadTracker;
    private readonly MemoryStage _mem;

    // PEvent recording — null means recording is disabled (zero overhead path)
    private readonly IBranchPredictor _predictor;
    private readonly WritebackStage _wb;

    private bool _anyCache;
    private Counter? _cacheMissStallsCounter;

    // Counters
    private Counter _cyclesCounter = null!;
    private Counter? _dcacheHitsCounter;
    private Counter? _dcacheMissesCounter;
    private Counter? _dtlbHitsCounter;
    private Counter? _dtlbMissesCounter;
    private Counter _flushesCounter = null!;
    private Counter? _icacheHitsCounter;
    private Counter? _icacheMissesCounter;
    private Counter? _itlbHitsCounter;
    private Counter? _itlbMissesCounter;
    private Counter? _l2DcacheHitsCounter;
    private Counter? _l2DcacheMissesCounter;
    private Counter? _l2IcacheHitsCounter;
    private Counter? _l2IcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter;
    private Counter? _l3DcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter;
    private Counter? _l3IcacheMissesCounter;
    private long _lastDHits, _lastDMisses;
    private long _lastDl2Hits, _lastDl2Misses;
    private long _lastDl3Hits, _lastDl3Misses;
    private long _lastDTlbHits, _lastDTlbMisses;
    private ulong _lastFetchedInstrId;

    // Delta tracking for cache/TLB stat counters
    private long _lastIHits, _lastIMisses;
    private long _lastIl2Hits, _lastIl2Misses;
    private long _lastIl3Hits, _lastIl3Misses;
    private long _lastITlbHits, _lastITlbMisses;

    private long _lastRetired;
    private long _lastStoreForwards;
    private Counter _missesCounter = null!;
    private long _missStallBudget;
    private Counter _retiredCounter = null!;

    // Cached delegate: a method-group conversion (RunCycle) allocates a fresh
    // Action on every ScheduleNextTick call — once per simulated cycle. Cache it.
    private Action? _runCycle;
    private Counter _stallsCounter = null!;
    private Counter? _storeForwardsCounter;

    public PipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        IMemory fetchTranslatorMemory,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        bool forwardingEnabled,
        IBranchPredictor predictor,
        int storeBufferCapacity = 0,
        PEventLog? pEventLog = null,
        ICommitObserver? commitObserver = null,
        int fdipFtqCapacity = 0,
        bool rdipEnabled = false,
        IMemory? fdipBackingMemory = null
    )
        : base(name, parent, esc) {
        PEventLog = pEventLog;
        _predictor = predictor;
        _hazard = new HazardUnit(forwardingEnabled);
        _decoder = mechanism.Decoder;
        State = mechanism.CreateArchState();
        State.Pc = entryPoint;

        ILayers = iLayers;
        DLayers = dLayers;

        _loadTracker = new LoadTracker(DLayers.Accessor);
        IMemory dAccessor = _loadTracker;
        if (storeBufferCapacity > 0) {
            StoreBuffer = new StoreBuffer(_loadTracker, esc, storeBufferCapacity);
            dAccessor = StoreBuffer;
        }

        // fdipBackingMemory is deliberately distinct from fetchTranslatorMemory: the latter feeds
        // mechanism.CreateFetchTranslator (page-table-walk reads, unrelated to caching), and must not
        // be repointed to raw backing — only FDIP's own instruction-lookahead reads should bypass
        // the I-cache. Falls back to fetchTranslatorMemory (today's behavior) when not given.
        FdipPrefetcher? fdip = fdipFtqCapacity > 0 && iLayers.Cache is not null
            ? new FdipPrefetcher(
                predictor, _decoder, fdipBackingMemory ?? fetchTranslatorMemory, iLayers.Cache, entryPoint,
                fdipFtqCapacity
            )
            : null;

        RdipPrefetcher? rdip = rdipEnabled && iLayers.Cache is not null
            ? new RdipPrefetcher(iLayers.Cache, _decoder)
            : null;

        // Create stages — IF uses instruction memory, EX/MEM use data memory.
        _if = new FetchStage(
            "if", parent, esc, ILayers.Accessor, predictor, _decoder,
            fetchTranslator: mechanism.CreateFetchTranslator(State, fetchTranslatorMemory),
            fdip: fdip,
            rdipICache: iLayers.Cache,
            rdip: rdip
        );
        _id = new DecodeStage("id", parent, esc, mechanism.Decoder, State);
        _ex = new ExecuteStage(
            "ex", parent, esc,
            mechanism.Executor, State, dAccessor, _hazard
        );
        _mem = new MemoryStage("mem", parent, esc);
        _wb = new WritebackStage(
            "wb", parent, esc,
            State, mechanism.TrapController, commitObserver, rdip
        );

        _if.Pc = entryPoint;

        // Wire: IF.Output → ID.Input → EX.Input → MEM.Input → WB.Input
        _if.Output.Bind(_id.Input);
        _id.Output.Bind(_ex.Input);
        _ex.Output.Bind(_mem.Input);
        _mem.Output.Bind(_wb.Input);
    }

    public IArchState State { get; }
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }
    public StoreBuffer? StoreBuffer { get; }
    public PEventLog? PEventLog { get; }

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Stall cycles");
        _flushesCounter = Dials.AddCounter("flushes", "Pipeline flushes");
        _missesCounter = Dials.AddCounter("branch_misses", "Branch mispredictions");
        _wb.OpcodeHistogram = Dials.AddHistogram("opcodes", "Retired instructions by opcode");

        Dials.AddDial(
            "cpi", () =>
                _wb.RetiredCount == 0 ? 0.0 : _cyclesCounter.Value / (double)_wb.RetiredCount,
            "Cycles per instruction"
        );
        Dials.AddDial(
            "ipc", () =>
                _cyclesCounter.Value == 0 ? 0.0 : _wb.RetiredCount / (double)_cyclesCounter.Value,
            "Instructions per cycle"
        );

        bool anyCache = ILayers.Cache is not null || DLayers.Cache is not null
                                                  || ILayers.L2Cache is not null || DLayers.L2Cache is not null
                                                  || ILayers.L3Cache is not null || DLayers.L3Cache is not null
                                                  || ILayers.Tlb is not null || DLayers.Tlb is not null;
        _anyCache = anyCache;
        if (anyCache)
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

        if (StoreBuffer is not null)
            _storeForwardsCounter = Dials.AddCounter("store_forwards", "Store-to-load forwardings");
    }

    public override void Wind() =>
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);

    // One clock cycle. Control logic snapshots the pipeline registers from last
    // cycle, then drives all stages directly. WB runs before ID so a register
    // written this cycle is visible to Decode's reads in the same cycle.
    private void RunCycle() {
        // Collect pending stalls from memory hierarchy (generated last cycle's stage execution).
        if (_anyCache) {
            _missStallBudget += CollectMemoryStalls();
            ILayers.TickWb();
            DLayers.TickWb();
            ILayers.TickMshr();
            DLayers.TickMshr();
            ILayers.TickPorts();
            DLayers.TickPorts();
        }

        // Reflect retirements produced by last cycle's Writeback.
        long newRetired = _wb.RetiredCount;
        while (_lastRetired < newRetired) {
            _retiredCounter.Increment();
            State.OnRetire();
            _lastRetired++;
        }

        if (_wb.Halted) {
            StoreBuffer?.DrainAll(); // ensure all pending stores reach backing memory
            return;
        }

        _cyclesCounter.Increment();
        State.OnCycle();

        if (_missStallBudget > 0) {
            // Drain one stall cycle: freeze all stages, advance the clock.
            _stallsCounter.Increment();
            _cacheMissStallsCounter?.Increment();
            _missStallBudget--;
            Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
            return;
        }

        // Snapshot pipeline-register contents from last cycle before any stage runs.
        IfIdLatch ifIdLast = _if.LastSent;
        IdExLatch idExLast = _id.LastSent;
        ExMemLatch exMemLast = _ex.LastSent;
        MemWbLatch memWbLast = _mem.LastSent;

        // Forwarding providers: oldest-first so the freshest source wins.
        // MEM/WB (index 0, oldest) and EX/MEM (index 1, newest).
        _fwdProviders[0] = new PipelineResident(
            memWbLast.IsValid, memWbLast.DestinationRegister, default(ToothClass), memWbLast.WritebackValue
        );
        _fwdProviders[1] = new PipelineResident(
            exMemLast.IsValid, exMemLast.DestinationRegister, default(ToothClass),
            exMemLast.Result is { RegisterResult.HasValue: true, } fwdR ? fwdR.RegisterResult.Value : null
        );
        _ex.SetForwardingContext(_fwdProviders);

        // Hazard detection: residents newest-first (EX at 0, MEM at 1).
        _hazardResidents[0] = new PipelineResident(
            idExLast.IsValid, idExLast.DestinationRegister, idExLast.Instruction?.Class ?? default(ToothClass), null
        );
        _hazardResidents[1] = new PipelineResident(
            exMemLast.IsValid, exMemLast.DestinationRegister, exMemLast.Instruction?.Class ?? default(ToothClass),
            exMemLast.Result is { RegisterResult.HasValue: true, } hzR ? hzR.RegisterResult.Value : null
        );
        ITooth? incoming = TryDecode(ifIdLast);
        if (incoming is { HasRuntimeSizedVectorDestination: true, })
            throw new NotSupportedException(
                "FiveStageTrain cannot safely run element-group vector-crypto instructions: their "
              + "register span depends on runtime LMUL, which VectorRawHazard's decode-time "
              + "register list can't capture. Use OooTrain for vector-crypto workloads."
            );
        bool stall = _hazard.MustStall(incoming?.SourceRegisters ?? [], _hazardResidents);

        // Vector RAW hazard: VRF writes complete via SideEffect in WB with no
        // forwarding path. Stall while any in-flight instruction writes a vector
        // register read by the incoming instruction.
        if (!stall && incoming != null)
            stall = VectorRawHazard(incoming, idExLast.Instruction)
                 || VectorRawHazard(incoming, exMemLast.Instruction);

        // Secondary-destination RAW hazard (e.g. RV32 amocas.d's register-pair high
        // half): that write completes via SideEffect in WB with no forwarding path,
        // same treatment as the vector case above.
        if (!stall && incoming != null)
            stall = SecondaryDestRawHazard(incoming, idExLast.Instruction)
                 || SecondaryDestRawHazard(incoming, exMemLast.Instruction);

        // fflags CSR hazard: FP ops OR their exception flags into fflags via SideEffect
        // in WB, with no forwarding path. A System-class instruction (csrr*, including
        // fsflags/frflags) reads CSRs synchronously in EX, so it must stall while any
        // in-flight FP op ahead of it hasn't reached WB yet, or it observes a stale fflags.
        if (!stall && incoming != null)
            stall = FflagsHazard(incoming, idExLast.Instruction)
                 || FflagsHazard(incoming, exMemLast.Instruction);

        // Reconcile any branch leaving EX with the prediction made at fetch.
        // The predictor is trained on every resolved branch; a flush (and a
        // misprediction penalty) is only paid when the speculated next PC was
        // wrong.
        var flush = false;
        if (exMemLast is {
                IsValid: true, Result: not null,
                Instruction.Class: ToothClass.Branch or ToothClass.ConditionalBranch,
            }) {
            bool taken = exMemLast.Result.BranchTaken;
            ulong actualNext = taken
                ? exMemLast.Result.BranchTarget!.Value
                : exMemLast.Pc + (ulong)(exMemLast.Instruction?.SizeBytes ?? 4);
            if (_predictor is IBranchKindAwareBranchPredictor kindAware)
                kindAware.NotifyBranchKind(exMemLast.Pc, ClassifyBranchKind(exMemLast.Instruction));
            _predictor.Update(exMemLast.Pc, taken, actualNext);

            // Notify vector-aware predictor of taken backward branch execution.
            if (taken
             && exMemLast.Instruction?.Class == ToothClass.ConditionalBranch
             && actualNext < exMemLast.Pc
             && _predictor is IVectorAwareBranchPredictor vbpL)
                vbpL.NotifyLoopBranchExecute(exMemLast.Pc, actualNext, exMemLast.Rs1Value, exMemLast.Rs2Value);

            if (actualNext != exMemLast.PredictedNextPc) {
                flush = true;
                _if.FlushTarget = actualNext;
                _ex.Squash = true; // kill the wrong-path instruction now in EX
                _flushesCounter.Increment();
                _missesCounter.Increment();
            }
        }

        // Notify vector-aware predictor of vector instruction execution.
        if (exMemLast is { IsValid: true, Instruction.Class: ToothClass.Vector, }
         && _predictor is IVectorAwareBranchPredictor vbpV)
            vbpV.NotifyVectorInstruction(exMemLast.Pc);

        // Notify value-aware predictor of the register value this instruction produced.
        if (exMemLast is { IsValid: true, Result.RegisterResult.HasValue: true, DestinationRegister: >= 0, }
         && _predictor is IValueAwareBp vabp)
            vabp.NotifyRegisterResult(
                exMemLast.Pc, exMemLast.DestinationRegister, exMemLast.Result.RegisterResult.Value,
                exMemLast.Instruction?.Class == ToothClass.Load
            );

        _if.Stall = stall;
        _id.Stall = stall;
        _if.Flush = flush;
        _id.Flush = flush;

        if (stall) _stallsCounter.Increment();

        // Kill instructions speculatively fetched past a halt, trap, or
        // return-from-trap. A branch's redirect target is known as soon as it
        // resolves in EX (handled above), so only the instruction already in
        // EX needs squashing. A trap/return's target isn't known until the
        // triggering instruction reaches WB — two cycles later — so by the
        // time it's even detected, wrong-path instructions may already be
        // sitting in ID and about to enter EX. Catch it at the earliest point
        // it's visible (EX→MEM boundary, mirroring branch resolution) and
        // again one cycle later (MEM→WB boundary) to squash EX and flush ID
        // both times, before the third and final round (the WB.TrapRedirect
        // check below) fires with the real target.
        if (exMemLast is {
                IsValid: true, Result: { IsHalt: true, } or { HasTrap: true, } or { IsReturnFromTrap: true, },
            }
         || (memWbLast is { IsValid: true, }
          && (memWbLast.IsHalt || memWbLast.HasTrap || memWbLast.IsReturnFromTrap))) {
            _ex.Squash = true;
            _id.Flush = true;
        }

        // Trap redirect from WB (computed last cycle) — the third and final
        // round: the trapper/returner has now reached WB and the real target
        // is known. Flush IF (send fetch to the redirect Pc) and ID (kill
        // whatever IF fetched wrong-path in the cycle since the second round).
        if (_wb.TrapRedirect.HasValue) {
            _if.FlushTarget = _wb.TrapRedirect.Value;
            _if.Flush = true;
            _id.Flush = true;
        }

        // PEvents: record DECODE/FLUSH for instruction in ID, EXECUTE/FLUSH for instruction in EX.
        // These checks happen after all stall/squash/flush flags are set.
        if (PEventLog is not null) {
            long cyc = _cyclesCounter.Value;
            switch (_if.Flush) {
                case true when ifIdLast is { IsValid: true, InstrId: not 0, }:
                    PEventLog.Record(ifIdLast.InstrId, ifIdLast.Pc, cyc, PEventKind.Flush);
                    break;
                case false when ifIdLast is { IsValid: true, InstrId: not 0, }:
                    PEventLog.Record(ifIdLast.InstrId, ifIdLast.Pc, cyc, PEventKind.Decode);
                    break;
            }

            switch (_ex.Squash) {
                case true when idExLast is { IsValid: true, InstrId: not 0, }:
                    PEventLog.Record(idExLast.InstrId, idExLast.Pc, cyc, PEventKind.Flush);
                    break;
                case false when idExLast is { IsValid: true, InstrId: not 0, }:
                    PEventLog.Record(idExLast.InstrId, idExLast.Pc, cyc, PEventKind.Execute);
                    if (idExLast.Instruction is { SourceRegisters.Count: > 0, } execInstr) {
                        int cnt = execInstr.SourceRegisters.Count;
                        var vals = new ulong[cnt];
                        for (var i = 0; i < cnt; i++) {
                            int r = execInstr.SourceRegisters[i];
                            vals[i] = r >= 0 ? State.IntegerRegisters.Read(r) : 0;
                        }

                        PEventLog.RecordSourceValues(idExLast.InstrId, execInstr.SourceRegisters, vals);
                    }

                    break;
            }
        }

        // Drive stages directly — WB before ID so the register file write
        // is visible to Decode's reads within the same cycle.
        long preRetire = _wb.RetiredCount;
        _wb.Inject(memWbLast);
        _wb.Cycle();
        if (PEventLog is not null && memWbLast is { IsValid: true, InstrId: not 0, } &&
            (_wb.RetiredCount > preRetire || _wb.Halted)) {
            PEventLog.Record(memWbLast.InstrId, memWbLast.Pc, _cyclesCounter.Value, PEventKind.Retire);
            if (memWbLast.Instruction is { DestinationRegister: > 0, } retireInstr)
                PEventLog.RecordDestValue(
                    memWbLast.InstrId, retireInstr.DestinationRegister,
                    State.IntegerRegisters.Read(retireInstr.DestinationRegister)
                );
        }

        _id.Inject(ifIdLast);
        _id.Cycle();
        _loadTracker.Reset();
        _ex.Inject(idExLast);
        _ex.Cycle();
        if (DLayers.Prefetcher is not null && _loadTracker.HasRead) {
            Span<ulong> prefBuf = stackalloc ulong[32];
            bool wasHit = DLayers.Cache?.LastAccessWasHit ?? true;
            int prefCount = DLayers.Prefetcher.OnAccess(
                _loadTracker.RequestPc, _loadTracker.ReadAddress, wasHit, prefBuf
            );
            for (var k = 0; k < prefCount; k++) DLayers.TryPrefetch(prefBuf[k]);
        }

        _mem.Inject(exMemLast);
        _mem.Cycle();
        _if.Cycle();

        // PEvent: record FETCH for the instruction just produced by IF this cycle.
        if (PEventLog is not null) {
            IfIdLatch ifSent = _if.LastSent;
            if (ifSent is { IsValid: true, InstrId: not 0, } && ifSent.InstrId != _lastFetchedInstrId) {
                _lastFetchedInstrId = ifSent.InstrId;
                PEventLog.Record(ifSent.InstrId, ifSent.Pc, _cyclesCounter.Value, PEventKind.Fetch);
                PEventLog.RecordDisasm(ifSent.InstrId, _decoder.Disassemble(ifSent.Pc, ifSent.RawEncoding));
            }
        }

        StoreBuffer?.DrainEligible();

        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Fetch);
    }

    // Drain accumulated stall cycles from all memory hierarchy layers and
    // update DialBoard counters with deltas since the last call.
    private long CollectMemoryStalls() {
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

        if (ILayers.Tlb is { } it) {
            _itlbHitsCounter!.IncrementBy(it.Hits - _lastITlbHits);
            _itlbMissesCounter!.IncrementBy(it.Misses - _lastITlbMisses);
            _lastITlbHits = it.Hits;
            _lastITlbMisses = it.Misses;
        }

        if (DLayers.Tlb is { } dt) {
            _dtlbHitsCounter!.IncrementBy(dt.Hits - _lastDTlbHits);
            _dtlbMissesCounter!.IncrementBy(dt.Misses - _lastDTlbMisses);
            _lastDTlbHits = dt.Hits;
            _lastDTlbMisses = dt.Misses;
        }

        if (StoreBuffer is not null) {
            _storeForwardsCounter!.IncrementBy(StoreBuffer.Forwards - _lastStoreForwards);
            _lastStoreForwards = StoreBuffer.Forwards;
        }

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

    // Source registers of the instruction IF produced last cycle — the one
    // Decode will read this cycle. Decoding is side-effect free, so the
    // controller can peek without disturbing the pipeline.
    private ITooth? TryDecode(IfIdLatch latch) {
        if (!latch.IsValid) return null;
        try { return _decoder.Decode(latch.Pc, latch.RawEncoding); }
        catch (IllegalInstructionException) { return null; }
    }

    // Returns true when the in-flight producer writes a vector register read by consumer.
    private static bool VectorRawHazard(ITooth? consumer, ITooth? producer) {
        if (producer is null || consumer is null) return false;
        int vd = producer.VectorDestinationRegister;
        return vd >= 0 && consumer.VectorSourceRegisters.Contains(vd);
    }

    // Returns true when the in-flight producer writes a secondary destination register
    // (e.g. RV32 amocas.d's paired high half) read by consumer.
    private static bool SecondaryDestRawHazard(ITooth? consumer, ITooth? producer) {
        if (producer is null || consumer is null) return false;
        int sd = producer.SecondaryDestinationRegister;
        return sd >= 0 && consumer.SourceRegisters.Contains(sd);
    }

    // Returns true when the in-flight producer may still OR flags into fflags (via
    // SideEffect, not yet applied) and the consumer is a CSR-class instruction that
    // could read it. Coarse-grained (ToothClass.System covers ecall/ebreak/mret/wfi
    // too, not just CSR ops) but safe — those are rare and never fflags-dependent.
    private static bool FflagsHazard(ITooth? consumer, ITooth? producer) {
        if (producer is null || consumer is null) return false;
        if (producer.Class is not (ToothClass.FloatingPoint or ToothClass.FloatDivSqrt)) return false;
        return consumer.Class == ToothClass.System;
    }

    // Classifies a resolved branch for IBranchKindAwareBranchPredictor. Re-derives the
    // FetchHint at commit rather than threading it through the pipeline latches, mirroring
    // the same GetFetchHint(pc, rawEncoding) call FdipPrefetcher/RdipPrefetcher already do
    // at other points.
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

    // Sits between DLayers.Accessor and the StoreBuffer so the prefetcher sees
    // only demand loads that actually reach the cache (not store-forwarded reads).
    private sealed class LoadTracker(IMemory backing) : IMemory {
        public bool HasRead { get; private set; }
        public ulong ReadAddress { get; private set; }
        public ulong RequestPc { get; private set; }

        public ulong Read(ulong address, int bytes) {
            HasRead = true;
            ReadAddress = address;
            return backing.Read(address, bytes);
        }

        public void Write(ulong address, ulong value, int bytes) => backing.Write(address, value, bytes);
        public void Load(ulong address, ReadOnlySpan<byte> data) => backing.Load(address, data);

        public void SetRequestPc(ulong pc) {
            RequestPc = pc;
            backing.SetRequestPc(pc);
        }

        public void InvalidateLine(ulong address) => backing.InvalidateLine(address);
        public void CleanLine(ulong address) => backing.CleanLine(address);
        public void FlushLine(ulong address) => backing.FlushLine(address);

        public void Reset() => HasRead = false;
    }
}