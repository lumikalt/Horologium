using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
///     Superscalar in-order Train: issues up to <c>issueWidth</c> instructions per
///     cycle, executing them sequentially so intra-group RAW dependencies resolve
///     naturally without any hazard detection logic.
///     <para>
///         Without a predictor there is no speculation across branches — the issue
///         group stops at any branch or jump, paying a "group-cutoff" penalty instead
///         of a flush penalty. With a predictor, a correctly predicted branch lets the
///         group continue fetching at the predicted target within the same cycle
///         (calls/returns steered by a RAS, direct jumps always taken), while a
///         misprediction cuts the group and charges a fixed frontend-redirect penalty —
///         the same two squashed stages a <see cref="FiveStageTrain" /> flush costs.
///         Branches resolve immediately after issue, so the predictor is trained
///         in-order with no outstanding speculation to recover.
///     </para>
/// </summary>
public sealed class SuperscalarTrain : ISteppableTrain {
    private readonly SuperscalarCore _core;
    private readonly Train _train;

    public SuperscalarTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int issueWidth = 2,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        IBranchPredictor? predictor = null,
        PEventLog? pEventLog = null
    ) {
        var esc = new Escapement();
        _train = new Train("superscalar", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new SuperscalarCore(
                "pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, issueWidth,
                predictor, pEventLog
            )
        );
        _train.Build();
    }

    internal SuperscalarTrain(
        IMechanism mechanism,
        MemoryLayers iLayers,
        MemoryLayers dLayers,
        ulong entryPoint,
        int issueWidth = 2,
        IBranchPredictor? predictor = null,
        PEventLog? pEventLog = null
    ) {
        var esc = new Escapement();
        _train = new Train("superscalar", esc);
        _core = _train.AddGear(
            new SuperscalarCore(
                "pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, issueWidth,
                predictor, pEventLog
            )
        );
        _train.Build();
    }

    public PEventLog? PEventLog => _core.PEventLog;

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;

    public bool IsIdle => _train.IsIdle;

    public IArchState ArchState => _core.ArchState;

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();

    public DialBoardSnapshot SnapshotPipeline() => _core.Dials.Snapshot();
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
///     The superscalar core Gear. Each tick it issues up to <c>issueWidth</c>
///     instructions in program order.
///     <para>
///         Stalls are counted as cycles where the group ran shorter than the issue
///         width (due to a mispredicted or unpredicted branch, halt, or memory fault
///         cutting the group short). Cache miss penalties are added as extra cycles
///         after each issue group. Without a predictor, branch_misses is always zero
///         because there is no speculative fetch.
///     </para>
/// </summary>
internal sealed class SuperscalarCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    MemoryLayers iLayers,
    MemoryLayers dLayers,
    ulong entryPoint,
    int issueWidth,
    IBranchPredictor? predictor = null,
    PEventLog? pEventLog = null
) : Gear(name, parent, esc) {
    // Frontend-redirect cost of a mispredicted branch: the same two squashed stages a
    // FiveStageTrain flush pays (IF + ID refill).
    private const int MispredictPenaltyCycles = 2;

    // Calls/returns steered by a RAS when a predictor is attached; resolution is
    // immediate, so no committed shadow copy is needed (no wrong path can corrupt it).
    private readonly ReturnAddressStack _ras = new();
    private bool _anyCache;
    private Counter _branchMissCounter = null!;
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
    private ulong _nextInstrId = 1;
    private Counter _retiredCounter = null!;

    // Cached to avoid a fresh Action allocation per simulated cycle.
    private Action? _runCycle;
    private Counter _stallsCounter = null!;
    public MemoryLayers ILayers { get; } = iLayers;
    public MemoryLayers DLayers { get; } = dLayers;
    public PEventLog? PEventLog { get; } = pEventLog;

    public IArchState ArchState { get; } = mechanism.CreateArchState();

    public override void Initialize() {
        _fetchTranslator = mechanism.CreateFetchTranslator(ArchState, ILayers.Accessor);
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Cycles where issue group < issueWidth or cache miss");
        _branchMissCounter = Dials.AddCounter(
            "branch_misses", "Branch mispredictions (always 0 without a predictor: no speculation)"
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

    public override void Reset() {
        base.Reset();
        ArchState.Reset();
        ArchState.Pc = entryPoint;
    }

    public override void Wind() {
        ArchState.Pc = entryPoint;
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    private void RunCycle() {
        var issued = 0;
        var halt = false;
        long mispredictPenalty = 0;
        long cyc = _cyclesCounter.Value;

        while (issued < issueWidth) {
            ulong pc = ArchState.Pc;

            // Fetch & Decode (through I-cache accessor)
            ITooth instr;
            if (_fetchTranslator is not null) {
                (ulong physPc, int faultCause) = _fetchTranslator.Translate(pc);
                if (faultCause != 0) {
                    ArchState.Pc = mechanism.TrapController.RaiseTrap(
                        new TrapInfo(faultCause, pc, pc), ArchState
                    );
                    break;
                }

                try {
                    var raw = (uint)ILayers.Accessor.Read(physPc, 4);
                    instr = mechanism.Decoder.Decode(pc, raw);
                }
                catch (IllegalInstructionException ex) {
                    ArchState.Pc = mechanism.TrapController.RaiseTrap(
                        new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), ArchState
                    );
                    break;
                }
            }
            else {
                try { instr = mechanism.Decoder.Decode(pc, ILayers.Accessor); }
                catch (IllegalInstructionException ex) {
                    var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
                    ArchState.Pc = mechanism.TrapController.RaiseTrap(trap, ArchState);
                    break;
                }
            }

            ulong instrId = _nextInstrId++;
            if (PEventLog is not null) {
                PEventLog.Record(instrId, pc, cyc, PEventKind.Fetch);
                PEventLog.RecordDisasm(instrId, mechanism.Decoder.Disassemble(pc, instr.RawEncoding));
            }

            // Predict the branch's successor before executing it. Resolution is immediate
            // (same loop iteration), so prediction only decides whether the group continues.
            ulong fallThrough = pc + (ulong)instr.SizeBytes;
            ulong predictedNext = fallThrough;
            FetchHint hint = default;
            bool isBranch = instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch;
            if (predictor is not null && isBranch) {
                hint = mechanism.Decoder.GetFetchHint(pc, instr.RawEncoding);
                // Direct unconditional jumps/calls are always taken to their known target and
                // bypass the predictor (mirrors the five-stage fetch stage / gem5).
                BranchPrediction pred = hint is { IsUnconditional: true, BranchTarget.HasValue: true, }
                    ? BranchPrediction.Taken(hint.BranchTarget.Value)
                    : predictor.Predict(pc, hint.BranchTarget);
                if (hint.IsCall)
                    _ras.Push(fallThrough);
                else if (hint.IsReturn && _ras.TryPop(out ulong ret)) pred = BranchPrediction.Taken(ret);

                // A direct branch's taken target comes from the decode hint, not the BTB
                // (which may be cold or aliased); a cold indirect target (0) falls through.
                ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
                predictedNext = pred.PredictedTaken && takenTarget != 0 ? takenTarget : fallThrough;
            }

            // Execute (through D-cache accessor)
            DLayers.Accessor.SetRequestPc(pc);
            ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, DLayers.Accessor);
            issued++;
            _retiredCounter.Increment();
            if (PEventLog is not null) {
                PEventLog.Record(instrId, pc, cyc, PEventKind.Execute);
                PEventLog.Record(instrId, pc, cyc, PEventKind.Retire);
            }

            // IsHalt stops before any state change (ebreak); RequestHalt (an HTIF
            // tohost-exit store) halts after the instruction's effects apply below.
            if (result.IsHalt) {
                halt = true;
                break;
            }

            if (result.HasTrap) {
                ArchState.Pc = mechanism.TrapController.RaiseTrap(result.Trap!, ArchState);
                break;
            }

            if (result.IsReturnFromTrap) {
                ArchState.Pc = mechanism.TrapController.ReturnFromTrap(result.ReturnPrivilege!.Value, ArchState);
                break;
            }

            result.SideEffect?.Invoke(ArchState);
            if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
                ArchState.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

            if (result is { BranchTaken: true, BranchTarget: not null, })
                ArchState.Pc = result.BranchTarget.Value;
            else
                ArchState.Pc = pc + (ulong)instr.SizeBytes;

            if (result.RequestHalt) {
                halt = true;
                break;
            }

            if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) {
                halt = true;
                break;
            }

            if (isBranch) {
                // No predictor: legacy semantics — every branch cuts the issue group.
                if (predictor is null) break;

                // Train on every resolved branch (as the other trains do), then either
                // continue the group at the correctly predicted target or pay the redirect.
                if (predictor is IBranchKindAwareBranchPredictor kindAware)
                    kindAware.NotifyBranchKind(pc, ClassifyBranchKind(instr, hint));
                predictor.Update(pc, ArchState.Pc != fallThrough, ArchState.Pc);

                if (ArchState.Pc != predictedNext) {
                    _branchMissCounter.Increment();
                    mispredictPenalty += SuperscalarCore.MispredictPenaltyCycles;
                    PEventLog?.Record(instrId, pc, cyc, PEventKind.Flush);
                    break;
                }
            }
        }

        // Check for pending interrupts after the issue group retires normally.
        if (!halt) {
            TrapInfo? interrupt = mechanism.TrapController.PeekInterrupt(ArchState);
            if (interrupt is not null) ArchState.Pc = mechanism.TrapController.RaiseTrap(interrupt, ArchState);
        }

        // Drain cache stall penalties accumulated during this group's memory operations.
        long cacheStalls = _anyCache ? DrainAndChargeStalls() : 0;

        // Count the issue cycle (plus any cache penalty and mispredict-redirect cycles).
        // ArchState.OnCycle advances the cycle CSR — self-timing workloads (rdcycle
        // calibration loops) never terminate without it.
        _cyclesCounter.Increment();
        ArchState.OnCycle();
        if (cacheStalls > 0) {
            _cacheMissStallsCounter?.IncrementBy(cacheStalls);
            _stallsCounter.IncrementBy(cacheStalls);
            _cyclesCounter.IncrementBy(cacheStalls);
            for (long i = 0; i < cacheStalls; i++) ArchState.OnCycle();
        }

        if (mispredictPenalty > 0) {
            _stallsCounter.IncrementBy(mispredictPenalty);
            _cyclesCounter.IncrementBy(mispredictPenalty);
            for (long i = 0; i < mispredictPenalty; i++) ArchState.OnCycle();
        }

        // A cycle where the group ran short (branch cut / halt) counts as a stall cycle.
        if (issued < issueWidth) _stallsCounter.Increment();

        if (!halt) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    // Classifies a resolved branch for IBranchKindAwareBranchPredictor, from the fetch
    // hint already derived for prediction.
    private static BranchKind ClassifyBranchKind(ITooth instruction, FetchHint hint) {
        var kind = BranchKind.None;
        if (instruction.Class == ToothClass.ConditionalBranch) kind |= BranchKind.Conditional;
        if (hint.IsCall) kind |= BranchKind.Call;
        if (hint.IsReturn) kind |= BranchKind.Return;
        if (!hint.BranchTarget.HasValue) kind |= BranchKind.Indirect;
        return kind;
    }

    // Drains all pending stalls and updates hit/miss counters. Returns total stall count.
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