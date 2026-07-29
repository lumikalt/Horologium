#region

using Mechanism;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

#endregion

namespace Pipeline;

/// <summary>
///     The simplest possible Train: one Gear that fetches, decodes, executes,
///     and writes back one instruction per tick. No pipeline, no hazards.
///     Used to validate the Mechanism before any pipeline complexity is added.
///     <para>
///         An optional <see cref="IBranchPredictor" /> can be attached for SMARTS-style
///         functional warming (Wunderlich et al., ISCA 2003, Section 3.1): since this train never
///         speculates, every resolved branch trains the predictor via <see cref="IBranchPredictor.Update" />
///         alone — no <c>Predict</c>/<c>SpeculativeHistoryUpdate</c> call is made, so working history
///         stays lock-stepped with committed history, which is bit-identical to the training a
///         detailed in-order pipeline performs at commit. RAS state is not warmed this way (the
///         return-address stack lives outside <see cref="IBranchPredictor" />, driven directly by a
///         detailed train's fetch/commit logic).
///     </para>
/// </summary>
public sealed class SingleCycleTrain : ISteppableTrain {
    private readonly SingleCycleCore _core;
    private readonly Train _train;

    public SingleCycleTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        ICommitObserver? commitObserver = null,
        IBranchPredictor? predictor = null
    ) {
        var esc = new Escapement();
        _train = new Train("single_cycle", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new SingleCycleCore(
                "core", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, commitObserver, predictor
            )
        );
        _train.Build();
    }

    internal SingleCycleTrain(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        ICommitObserver? commitObserver = null,
        IBranchPredictor? predictor = null
    ) {
        var esc = new Escapement();
        _train = new Train("single_cycle", esc);
        _core = _train.AddGear(
            new SingleCycleCore(
                "core", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, commitObserver, predictor
            )
        );
        _train.Build();
    }

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
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

    public bool IsIdle => _train.IsIdle;

    public long CurrentTick => _train.CurrentTick;

    public IArchState ArchState => _core.ArchState;

    public RevolutionResult Run(long maxTicks = 100_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();
    public IReadOnlyList<DialBoardSnapshot> SnapshotDials() => _train.SnapshotDials();

    public RevolutionResult FinishStepping(IReadOnlyList<DialBoardSnapshot> baseline) =>
        _train.FinishStepping(baseline);

    public string DumpTopology() => _train.DumpTopology();
}

/// <summary>
///     The single-cycle core Gear. Each tick: fetch → decode → execute → writeback.
///     Stops scheduling when it encounters a halt condition (infinite loop to self,
///     or explicit EBREAK).
///     Cache miss penalties are charged as extra cycles appended to the retiring instruction.
/// </summary>
internal sealed class SingleCycleCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    MemoryLayers iLayers,
    MemoryLayers dLayers,
    ulong entryPoint,
    ICommitObserver? commitObserver = null,
    IBranchPredictor? predictor = null
)
    : Gear(name, parent, esc) {
    private bool _anyCache;
    private Counter? _cacheMissStallsCounter;

    private Counter _cyclesCounter = null!;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;
    private IFetchTranslator? _fetchTranslator;
    private Counter? _icacheHitsCounter, _icacheMissesCounter;
    private Counter? _itlbHitsCounter, _itlbMissesCounter;
    private Counter? _l2DcacheHitsCounter, _l2DcacheMissesCounter;
    private Counter? _l2IcacheHitsCounter, _l2IcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter, _l3DcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter, _l3IcacheMissesCounter;
    private long _lastDHits, _lastDMisses, _lastDl2Hits, _lastDl2Misses, _lastDl3Hits, _lastDl3Misses;

    // Delta tracking for hit/miss counters
    private long _lastIHits, _lastIMisses, _lastIl2Hits, _lastIl2Misses, _lastIl3Hits, _lastIl3Misses;
    private long _lastITlbHits, _lastITlbMisses, _lastDTlbHits, _lastDTlbMisses;
    private Histogram _opcodeHistogram = null!;
    private Counter _retiredCounter = null!;
    private Counter _stallsCounter = null!;
    public MemoryLayers ILayers { get; } = iLayers;
    public MemoryLayers DLayers { get; } = dLayers;

    public IArchState ArchState { get; } = mechanism.CreateArchState();

    public override void Initialize() {
        _fetchTranslator = mechanism.CreateFetchTranslator(ArchState, ILayers.Accessor);
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles elapsed");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Stall cycles from memory hierarchy misses");
        _opcodeHistogram = Dials.AddHistogram("opcodes", "Retired instructions by opcode");
        Dials.AddDial(
            "ipc", () =>
                _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
            "Instructions per cycle"
        );
        Dials.AddDial(
            "cpi", () =>
                _retiredCounter.Value == 0 ? 0.0 : _cyclesCounter.Value / (double)_retiredCounter.Value,
            "Cycles per instruction"
        );

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
        if (_anyCache)
            _cacheMissStallsCounter = Dials.AddCounter(
                "cache_miss_stalls", "Stall cycles from memory hierarchy misses"
            );

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

    public override void Reset() {
        base.Reset();
        ArchState.Reset();
        ArchState.Pc = entryPoint;
    }

    public override void Wind() {
        ArchState.Pc = entryPoint;
        ScheduleNextInstruction();
    }

    private void ScheduleNextInstruction() { Escapement.ScheduleNextTick(ExecuteOneCycle, Phase.Execute); }

    private void ExecuteOneCycle() {
        ulong pc = ArchState.Pc;

        // Fetch & Decode (through I-cache accessor).
        // When a fetch translator is present, translate virtual→physical first;
        // read physical bytes, then decode with the virtual PC for correct targets.
        ITooth instr;
        if (_fetchTranslator is not null) {
            (ulong physPc, int faultCause) = _fetchTranslator.Translate(pc);
            if (faultCause != 0) {
                if (_anyCache) DrainAndChargeStalls();
                _cyclesCounter.Increment();
                ArchState.Pc = mechanism.TrapController.RaiseTrap(new TrapInfo(faultCause, pc, pc), ArchState);
                ScheduleNextInstruction();
                return;
            }

            try {
                var raw = (uint)ILayers.Accessor.Read(physPc, 4);
                instr = mechanism.Decoder.Decode(pc, raw);
            }
            catch (IllegalInstructionException ex) {
                if (_anyCache) DrainAndChargeStalls();
                _cyclesCounter.Increment();
                ArchState.Pc = mechanism.TrapController.RaiseTrap(
                    new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), ArchState
                );
                ScheduleNextInstruction();
                return;
            }
        }
        else {
            try { instr = mechanism.Decoder.Decode(pc, ILayers.Accessor); }
            catch (IllegalInstructionException ex) {
                if (_anyCache) DrainAndChargeStalls();
                _cyclesCounter.Increment();
                var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
                ArchState.Pc = mechanism.TrapController.RaiseTrap(trap, ArchState);
                ScheduleNextInstruction();
                return;
            }
        }

        // Execute (through D-cache accessor)
        DLayers.Accessor.SetRequestPc(pc);
        ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, DLayers.Accessor);

        // EBREAK halts the simulation without consuming a cycle or retiring.
        if (result.IsHalt) return;

        // Collect stall cycles from memory hierarchy and update hit/miss counters.
        long stalls = _anyCache ? DrainAndChargeStalls() : 0;

        _cyclesCounter.Increment();
        ArchState.OnCycle();
        if (stalls > 0) {
            _stallsCounter.IncrementBy(stalls);
            _cacheMissStallsCounter?.IncrementBy(stalls);
            _cyclesCounter.IncrementBy(stalls);
            for (long i = 0; i < stalls; i++) ArchState.OnCycle();
        }

        // Still-blocked syscall (futex FUTEX_WAIT etc.): the cycle is charged like any other
        // instruction (real hardware time passes even while retrying), but nothing commits or
        // retires and PC stays put, so the same ecall is re-decoded and re-executed next tick —
        // mirrors MultiHartKernel.StepHart's functional retry-in-place at this train's own
        // cycle-accurate granularity.
        if (result.RequestBlock) {
            ScheduleNextInstruction();
            return;
        }

        // Writeback
        if (result.HasTrap) { ArchState.Pc = mechanism.TrapController.RaiseTrap(result.Trap!, ArchState); }
        else if (result.IsReturnFromTrap) {
            ArchState.Pc = mechanism.TrapController.ReturnFromTrap(result.ReturnPrivilege!.Value, ArchState);
        }
        else {
            result.SideEffect?.Invoke(ArchState);
            if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
                ArchState.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

            if (result is { BranchTaken: true, BranchTarget: not null, })
                ArchState.Pc = result.BranchTarget.Value;
            else
                ArchState.Pc = pc + (ulong)instr.SizeBytes;

            if (predictor is not null && instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch) {
                bool taken = result.BranchTaken;
                ulong actualNext = taken ? result.BranchTarget!.Value : pc + (ulong)instr.SizeBytes;
                if (predictor is IBranchKindAwareBranchPredictor kindAware)
                    kindAware.NotifyBranchKind(pc, ClassifyBranchKind(instr));
                predictor.Update(pc, taken, actualNext);
            }

            commitObserver?.OnCommit(pc, instr.RawEncoding, ArchState);
        }

        Type? instrType = instr.Payload?.GetType();
        if (instrType is not null) _opcodeHistogram.Observe(instrType);
        _retiredCounter.Increment();
        ArchState.OnRetire();

        // First-class HTIF tohost exit: the store flagged a post-commit halt.
        // It has committed and retired above; stop without scheduling the next.
        if (result.RequestHalt) return;

        // Detect halt: infinite self-loop (JAL x0, 0 — common halt idiom)
        if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) return;

        // Check for pending interrupts after every normal retire (not after trap/MRET/SRET).
        if (result is { HasTrap: false, IsReturnFromTrap: false, }) {
            TrapInfo? interrupt = mechanism.TrapController.PeekInterrupt(ArchState);
            if (interrupt is not null) ArchState.Pc = mechanism.TrapController.RaiseTrap(interrupt, ArchState);
        }

        ScheduleNextInstruction();
    }

    // Classifies a resolved branch for IBranchKindAwareBranchPredictor, mirroring
    // FiveStageTrain.ClassifyBranchKind.
    private BranchKind ClassifyBranchKind(ITooth instruction) {
        var kind = BranchKind.None;
        if (instruction.Class == ToothClass.ConditionalBranch) kind |= BranchKind.Conditional;
        FetchHint hint = mechanism.Decoder.GetFetchHint(instruction.Pc, instruction.RawEncoding);
        if (hint.IsCall) kind |= BranchKind.Call;
        if (hint.IsReturn) kind |= BranchKind.Return;
        if (!hint.BranchTarget.HasValue) kind |= BranchKind.Indirect;
        return kind;
    }

    // Drains all pending stalls from every memory layer, updates hit/miss counters,
    // and returns the total stall cycle count.
    private long DrainAndChargeStalls() {
        long stalls = ILayers.ConsumeAllStalls() + DLayers.ConsumeAllStalls();
        ILayers.TickMshr();
        DLayers.TickMshr();
        ILayers.TickPorts();
        DLayers.TickPorts();
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
}