#region

using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Ooo;

#endregion

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
///     Superscalar in-order Train: a pipelined frontend feeds an in-order issue stage of
///     width <c>issueWidth</c> gated by a register scoreboard.
///     <para>
///         Fetch follows the branch predictor speculatively (always-not-taken by default,
///         RAS-steered calls/returns, direct jumps always taken) into a fetch queue;
///         an instruction fetched at cycle T becomes issueable at T + <c>frontendDepth</c>,
///         so a misprediction's penalty is the emergent frontend refill rather than a
///         constant. Issue is strictly in order: it stops at the first instruction whose
///         sources are not ready (RAW, with full bypass — a producer of latency L feeds a
///         consumer issuing L cycles later), whose destination is still pending (WAW,
///         in-order writeback), whose functional-unit ports for the cycle are exhausted
///         (<see cref="Ooo.FuLatencyConfig" /> counts and latencies), or that needs the LSU
///         while a cache miss is outstanding (blocking cache: one miss at a time, but ALU
///         work continues underneath — stall-on-use via the scoreboard). Instructions
///         execute functionally at issue, which is exact for an in-order machine; branches
///         therefore resolve at issue and train the predictor with no outstanding
///         speculation beyond the fetch queue.
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
        PEventLog? pEventLog = null,
        FuLatencyConfig? fuLatency = null,
        int frontendDepth = 2,
        int uopCacheSets = 0,
        int uopCacheWays = 6,
        int uopCacheLineCapacity = 6
    ) {
        var esc = new Escapement();
        _train = new Train("superscalar", esc);
        var iLayers = MemoryLayers.Build(memory, iMemConfig ?? MemoryConfig.None);
        var dLayers = MemoryLayers.Build(memory, dMemConfig ?? MemoryConfig.None);
        _core = _train.AddGear(
            new SuperscalarCore(
                "pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, issueWidth,
                predictor ?? new AlwaysNotTakenPredictor(),
                fuLatency ?? FuLatencyConfig.Default,
                frontendDepth,
                pEventLog,
                uopCacheSets,
                uopCacheWays,
                uopCacheLineCapacity
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
        PEventLog? pEventLog = null,
        FuLatencyConfig? fuLatency = null,
        int frontendDepth = 2,
        int uopCacheSets = 0,
        int uopCacheWays = 6,
        int uopCacheLineCapacity = 6
    ) {
        var esc = new Escapement();
        _train = new Train("superscalar", esc);
        _core = _train.AddGear(
            new SuperscalarCore(
                "pipeline", _train.Root, esc, mechanism, iLayers, dLayers, entryPoint, issueWidth,
                predictor ?? new AlwaysNotTakenPredictor(),
                fuLatency ?? FuLatencyConfig.Default,
                frontendDepth,
                pEventLog,
                uopCacheSets,
                uopCacheWays,
                uopCacheLineCapacity
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
///     The superscalar core Gear. Each cycle: issue up to <c>issueWidth</c> queue heads in
///     program order (scoreboard/port/LSU gated, executing functionally at issue), then
///     fetch up to <c>issueWidth</c> instructions along the predicted path.
///     <para>
///         Stalls count cycles where the issue group ran short of the width for any reason
///         (frontend starvation, interlocks, structural limits, halts). Cache misses are
///         charged where they bind: an I-side miss blocks further fetch until the line
///         arrives, a D-side miss extends the load's result latency and holds the LSU busy.
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
    IBranchPredictor predictor,
    FuLatencyConfig fuConfig,
    int frontendDepth,
    PEventLog? pEventLog = null,
    int uopCacheSets = 0,
    int uopCacheWays = 6,
    int uopCacheLineCapacity = 6
) : Gear(name, parent, esc) {
    private readonly Queue<FetchedEntry> _fetchQueue = new();

    private readonly UopCache? _uopCache =
        uopCacheSets > 0 ? new UopCache(uopCacheSets, uopCacheWays, uopCacheLineCapacity) : null;

    // In-progress UC line build (paper §2.5.1): accumulates decoded uops for the block
    // currently being fetched along the correct path, closed (inserted) at a branch or at
    // capacity, discarded without inserting on a frontend flush (wrong-path instructions
    // must never be cached).
    private readonly ITooth?[] _uopBuildLine = new ITooth?[uopCacheLineCapacity];
    private int _uopBuildCount;
    private ulong _uopBuildStartPc;

    // Fetch-queue capacity: enough to cover the fetch→issue delay plus one full group.
    private readonly int _queueCapacity = (frontendDepth + 1) * issueWidth;

    // Calls/returns steered by a RAS. Updated speculatively at fetch with no committed
    // shadow — wrong-path corruption is possible but shallow (bounded by the fetch queue),
    // matching the five-stage train's fetch-time RAS.
    private readonly ReturnAddressStack _ras = new();
    private bool _anyCache;
    private Counter _branchMissCounter = null!;
    private Counter? _cacheMissStallsCounter;

    private Counter _cyclesCounter = null!;
    private Counter? _dcacheHitsCounter, _dcacheMissesCounter;
    private Counter? _dtlbHitsCounter, _dtlbMissesCounter;

    // True after fetching a faulting/undecodable instruction: fetch waits until the fault
    // reaches issue in program order (where it traps if it was correct-path) or a flush
    // discards it (it was wrong-path).
    private bool _fetchFaulted;
    private ulong _fetchPc = entryPoint;

    // Fetch blocked until this cycle while an I-cache/I-TLB miss is serviced.
    private long _fetchStallUntil;
    private IFetchTranslator? _fetchTranslator;
    private Counter _flushesCounter = null!;
    private Counter _macroFusionsCounter = null!;
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

    // True when the current LSU-busy window was opened by a store miss (TMA MemStalls
    // attribution: loads vs stores).
    private bool _lsuBusyIsStore;

    // Blocking data cache: the LSU accepts no new memory operation until this cycle while
    // a miss is outstanding (no hit-under-miss). Independent ALU work continues.
    private long _lsuBusyUntil;
    private ulong _nextInstrId = 1;

    // Latest cycle at which an issued load's result lands — "a load is in flight" for the
    // TMA MemStalls.AnyLoad event.
    private long _pendingLoadReadyCycle;

    // Scoreboard: cycle at which each architectural integer register's in-flight value
    // becomes readable through the bypass network (index 0 = x0, never pending).
    private long[] _regReadyCycle = [];
    private Counter _retiredCounter = null!;

    // Cached to avoid a fresh Action allocation per simulated cycle.
    private Action? _runCycle;
    private Counter _stallsCounter = null!;

    // Top-Down Microarchitecture Analysis slot accounting (Yasin, ISPASS 2014), in-order
    // flavor: issue never speculates past unresolved branches (branches resolve at issue),
    // so SlotsIssued ≡ SlotsRetired and Bad Speculation consists purely of RecoveryBubbles —
    // the frontend-refill slots after a flush, charged while _tdRefillPending.
    private TopDownCounters _td = null!;
    private bool _tdRefillPending;
    private Counter? _uopCacheHitsCounter, _uopCacheMissesCounter, _uopCacheBuildsCounter;
    private long _lastUcHits, _lastUcMisses, _lastUcBuilds;
    public MemoryLayers ILayers { get; } = iLayers;
    public MemoryLayers DLayers { get; } = dLayers;
    public PEventLog? PEventLog { get; } = pEventLog;

    public IArchState ArchState { get; } = mechanism.CreateArchState();

    public override void Initialize() {
        _fetchTranslator = mechanism.CreateFetchTranslator(ArchState, ILayers.Accessor);
        _regReadyCycle = new long[ArchState.IntegerRegisters.Count];
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Cycles where the issue group ran short of issueWidth");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions (frontend refill penalty)");
        _flushesCounter = Dials.AddCounter(
            "flushes", "Frontend flushes (mispredict + trap + interrupt + mret redirects)"
        );
        _macroFusionsCounter = Dials.AddCounter(
            "macro_fusions", "Instruction pairs issued as a single macro-fused issue slot"
        );

        if (_uopCache is not null) {
            _uopCacheHitsCounter = Dials.AddCounter("uop_cache_hits", "Basic blocks served from the µop cache");
            _uopCacheMissesCounter = Dials.AddCounter(
                "uop_cache_misses", "Fetches that missed the µop cache and went through the decoder"
            );
            _uopCacheBuildsCounter = Dials.AddCounter("uop_cache_builds", "Basic blocks inserted into the µop cache");
        }

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

        // Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014): slot accounting at the
        // issue stage. See the _td field for the in-order adaptations.
        _td = TopDownBreakdown.RegisterCounters(Dials, ComputeTopDown);

        _anyCache = ILayers.Cache is not null || DLayers.Cache is not null
                                              || ILayers.L2Cache is not null || DLayers.L2Cache is not null
                                              || ILayers.L3Cache is not null || DLayers.L3Cache is not null
                                              || ILayers.Tlb is not null || DLayers.Tlb is not null;
        if (_anyCache)
            _cacheMissStallsCounter = Dials.AddCounter(
                "cache_miss_stalls", "Miss cycles charged to fetch (I-side) or the LSU (D-side)"
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
        ArchState.Pc = _fetchPc;
        _fetchQueue.Clear();
        _fetchFaulted = false;
        _fetchStallUntil = 0;
        _lsuBusyUntil = 0;
        _lsuBusyIsStore = false;
        _pendingLoadReadyCycle = 0;
        _tdRefillPending = false;
        Array.Clear(_regReadyCycle);
    }

    public override void Wind() {
        ArchState.Pc = _fetchPc;
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    private void RunCycle() {
        long now = _cyclesCounter.Value;
        bool halt = StepIssue(now);

        if (!halt) StepFetch(now);

        if (_anyCache) {
            UpdateAllMemoryStats();
            ILayers.TickWb();
            DLayers.TickWb();
            ILayers.TickMshr();
            DLayers.TickMshr();
            ILayers.TickPorts();
            DLayers.TickPorts();
        }

        if (_uopCache is not null) {
            _uopCacheHitsCounter!.IncrementBy(_uopCache.Hits - _lastUcHits);
            _uopCacheMissesCounter!.IncrementBy(_uopCache.Misses - _lastUcMisses);
            _uopCacheBuildsCounter!.IncrementBy(_uopCache.Builds - _lastUcBuilds);
            _lastUcHits = _uopCache.Hits;
            _lastUcMisses = _uopCache.Misses;
            _lastUcBuilds = _uopCache.Builds;
        }

        // ArchState.OnCycle advances the cycle CSR — self-timing workloads (rdcycle
        // calibration loops) never terminate without it.
        _cyclesCounter.Increment();
        _td.TotalSlots.IncrementBy(issueWidth);
        ArchState.OnCycle();

        if (!halt) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    // ── Issue ──────────────────────────────────────────────────────────────────

    /// <summary>Issues up to issueWidth queue heads in program order. Returns true on halt.</summary>
    private bool StepIssue(long now) {
        var issued = 0;
        var halt = false;
        bool wasRefillPending = _tdRefillPending;
        var frontendStarved = false;
        Span<int> classIssued = stackalloc int[16]; // one slot per ToothClass value

        while (issued < issueWidth) {
            if (_fetchQueue.Count == 0) {
                frontendStarved = true;
                break;
            }

            FetchedEntry head = _fetchQueue.Peek();
            if (head.ReadyAt > now) {
                frontendStarved = true; // still traversing the frontend pipeline
                break;
            }

            // A fetch fault that reaches issue is on the correct path: raise it.
            if (head.PreTrap is not null) {
                _fetchQueue.Dequeue();
                ArchState.Pc = mechanism.TrapController.RaiseTrap(head.PreTrap, ArchState);
                FlushFrontend(ArchState.Pc, now);
                break;
            }

            ITooth instr = head.Instruction!;

            // Macro-fusion: if the ISA plugin recognises this instruction plus the very
            // next queue entry as a fusible pair (e.g. RV32's SLT+branch idiom), replace
            // both with a single fused Tooth before the RAW/WAW/structural checks below —
            // its SourceRegisters/DestinationRegister/Class mirror `instr`'s own, so those
            // checks apply unchanged, and the branch's own PC/prediction (needed below) is
            // read from `second`, not from the fused Tooth's Pc (the compare's own PC).
            // Adjacency (second.Pc immediately follows instr) is what rules out a redirect
            // having landed on the branch alone: the fetch queue only ever holds a
            // contiguous speculative stream, so two entries are only adjacent here if they
            // were actually fetched back-to-back with nothing between them.
            var fused = false;
            FetchedEntry second = default;
            if (mechanism.MacroFuser is not null
             && TryPeekSecond(out second)
             && second.PreTrap is null
             && second.ReadyAt <= now
             && second.Pc == head.Pc + (ulong)instr.SizeBytes) {
                ITooth? fusedInstr = mechanism.MacroFuser.TryFuse(instr, second.Instruction!);
                if (fusedInstr is not null) {
                    instr = fusedInstr;
                    fused = true;
                }
            }

            // RAW interlock: every source must be readable through the bypass this cycle.
            var blocked = false;
            IReadOnlyList<int> srcs = instr.SourceRegisters;
            for (var i = 0; i < srcs.Count && !blocked; i++)
                if (srcs[i] > 0 && _regReadyCycle[srcs[i]] > now)
                    blocked = true;

            // WAW interlock: in-order writeback — a pending older write to the same
            // destination must land before this one may issue.
            int dest = instr.DestinationRegister;
            if (dest > 0 && _regReadyCycle[dest] > now) blocked = true;
            if (blocked) break;

            // Structural: per-class FU ports this cycle, and the blocking LSU.
            ToothClass cls = instr.Class;
            bool isMem = cls is ToothClass.Load or ToothClass.Store or ToothClass.Atomic;
            if (isMem && now < _lsuBusyUntil) break;
            int fuSlot = FuLatencyConfig.BudgetSlot(cls);
            if (classIssued[fuSlot] >= fuConfig.CountFor(cls)) break;

            // Execute functionally at issue — exact for an in-order machine, since every
            // older instruction has already executed.
            DLayers.Accessor.SetRequestPc(head.Pc);
            ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, DLayers.Accessor);

            if (result.RequestBlock)
                // Still blocked (e.g. futex(FUTEX_WAIT) that hasn't cleared): unlike every
                // other outcome below, don't dequeue at all — the instruction stays exactly
                // where it is at the fetch-queue head, so the next StepIssue call re-peeks and
                // re-executes it from scratch. Nothing has been dequeued or committed, so
                // there's nothing younger to discard and no FlushFrontend/PC redirect is
                // needed. Left unclassified in the TMA slot accounting below (neither
                // frontendStarved nor _tdRefillPending) so it falls into the Backend Bound
                // residual, not FetchBubbles — this is a backend/syscall block, not a
                // fetch-side one.
                break;

            _fetchQueue.Dequeue();
            if (fused) {
                _fetchQueue.Dequeue();
                // Counted here, past every RAW/WAW/structural/RequestBlock break above: those
                // breaks re-peek the same still-undequeued pair next cycle without issuing it,
                // so incrementing at detection time would double-count every stalled cycle.
                _macroFusionsCounter.Increment();
            }

            classIssued[fuSlot]++;
            issued++; // a fused pair still costs one issue-slot-and-cycle, the whole point of fusion
            _retiredCounter.IncrementBy(fused ? 2 : 1); // architectural instruction count, not slot count
            _td.SlotsRetired.Increment(); // one slot retires here regardless of ArchInstructionCount

            int latency = result.LatencyOverride is > 0 and var overridden
                ? overridden
                : fuConfig.LatencyFor(instr);
            if (cls == ToothClass.Load) {
                int cacheHit = DLayers.Cache?.HitLatency ?? 0;
                if (cacheHit > 0) latency = cacheHit;
            }

            // Blocking data cache: a miss extends this operation's latency and holds the
            // LSU until the line arrives; independent non-memory work continues.
            if (_anyCache && isMem) {
                long miss = DLayers.ConsumeAllStalls();
                if (miss > 0) {
                    latency += (int)miss;
                    _lsuBusyUntil = now + 1 + miss;
                    _lsuBusyIsStore = cls == ToothClass.Store;
                    _cacheMissStallsCounter?.IncrementBy(miss);
                }
            }

            // TMA MemStalls.AnyLoad: a load is in flight until its result lands.
            if (cls is ToothClass.Load or ToothClass.Atomic)
                _pendingLoadReadyCycle = Math.Max(_pendingLoadReadyCycle, now + latency);

            if (PEventLog is not null) {
                PEventLog.Record(head.InstrId, head.Pc, now, PEventKind.Execute);
                PEventLog.Record(head.InstrId, head.Pc, now + latency - 1, PEventKind.Retire);
                if (fused) {
                    PEventLog.Record(second.InstrId, second.Pc, now, PEventKind.Execute);
                    PEventLog.Record(second.InstrId, second.Pc, now + latency - 1, PEventKind.Retire);
                }
            }

            // IsHalt stops before any state change (ebreak); RequestHalt (an HTIF
            // tohost-exit store) halts after the instruction's effects apply below.
            if (result.IsHalt) {
                halt = true;
                break;
            }

            if (result.HasTrap) {
                ArchState.Pc = mechanism.TrapController.RaiseTrap(result.Trap!, ArchState);
                FlushFrontend(ArchState.Pc, now);
                break;
            }

            if (result.IsReturnFromTrap) {
                ArchState.Pc = mechanism.TrapController.ReturnFromTrap(result.ReturnPrivilege!.Value, ArchState);
                FlushFrontend(ArchState.Pc, now);
                break;
            }

            result.SideEffect?.Invoke(ArchState);
            if (result.RegisterResult.HasValue && dest >= 0)
                ArchState.IntegerRegisters.Write(dest, result.RegisterResult.Value);
            if (dest > 0) _regReadyCycle[dest] = now + latency;

            if (result is { BranchTaken: true, BranchTarget: not null, })
                ArchState.Pc = result.BranchTarget.Value;
            else
                ArchState.Pc = head.Pc + (ulong)instr.SizeBytes;

            if (result.RequestHalt) {
                halt = true;
                break;
            }

            // Unconditional jump-to-self: the bare-metal terminator.
            if (ArchState.Pc == head.Pc && cls == ToothClass.Branch) {
                halt = true;
                break;
            }

            if (cls is ToothClass.Branch or ToothClass.ConditionalBranch) {
                // Branches resolve at issue: train on every one, then either keep issuing
                // (the queue already holds the correctly predicted path) or flush the
                // frontend — the penalty is the refill, frontendDepth cycles of starvation.
                // When fused, the predictor/RAS-kind machinery must key off the branch's
                // own Pc/prediction (from `second`), not the fused Tooth's Pc (the
                // compare's PC) — the BTB and fetch-time prediction were both keyed on the
                // branch's real address.
                ITooth branchInstr = fused ? second.Instruction! : instr;
                ulong branchPc = fused ? second.Pc : head.Pc;
                ulong branchPredictedNext = fused ? second.PredictedNextPc : head.PredictedNextPc;
                ulong fallThrough = branchPc + (ulong)branchInstr.SizeBytes;
                if (predictor is IBranchKindAwareBranchPredictor kindAware)
                    kindAware.NotifyBranchKind(branchPc, ClassifyBranchKind(branchInstr));
                predictor.Update(branchPc, ArchState.Pc != fallThrough, ArchState.Pc);

                if (ArchState.Pc != branchPredictedNext) {
                    _branchMissCounter.Increment();
                    FlushFrontend(ArchState.Pc, now);
                    break;
                }
            }
        }

        // Check for pending interrupts once the issue group retires normally.
        if (!halt) {
            TrapInfo? interrupt = mechanism.TrapController.PeekInterrupt(ArchState);
            if (interrupt is not null) {
                ArchState.Pc = mechanism.TrapController.RaiseTrap(interrupt, ArchState);
                FlushFrontend(ArchState.Pc, now);
            }
        }

        // A cycle where the group ran short counts as a stall cycle.
        if (issued < issueWidth) _stallsCounter.Increment();

        // ── TMA slot accounting (Table 1, in-order flavor) ───────────────────────
        // Every issued slot is a retired slot (no wrong-path issue), so Bad Speculation
        // is exactly the recovery bubbles charged here. Unused slots classify by the
        // blocking condition at the queue head: post-flush refill → RecoveryBubbles,
        // frontend starvation → FetchBubbles, scoreboard/port/LSU backpressure → the
        // Backend Bound residual.
        _td.SlotsIssued.IncrementBy(issued);
        bool flushedThisCycle = _tdRefillPending && !wasRefillPending;
        if (issued > 0 && !flushedThisCycle) _tdRefillPending = false;
        int leftover = issueWidth - issued;
        if (leftover > 0 && !halt) {
            if (_tdRefillPending) { _td.RecoveryBubbles.IncrementBy(leftover); }
            else if (frontendStarved) {
                _td.FetchBubbles.IncrementBy(leftover);
                if (issued == 0) _td.FetchLatencyCycles.Increment();
            }
        }

        // Level 2: ExecutionStalls / MemStalls (fewer than half the width started).
        if (issued * 2 < issueWidth) {
            _td.ExecStallCycles.Increment();
            if (issued == 0) {
                if (now < _pendingLoadReadyCycle)
                    _td.MemStallLoadCycles.Increment();
                else if (_lsuBusyIsStore && now < _lsuBusyUntil) _td.MemStallStoreCycles.Increment();
            }
        }

        return halt;
    }

    /// <summary>
    ///     Peeks the fetch queue's second entry (the one behind the head) without dequeuing
    ///     anything. <c>Queue&lt;T&gt;</c> exposes no indexer, so this walks its struct
    ///     enumerator directly — no LINQ, no allocation — since StepIssue calls this every
    ///     cycle on the hot path.
    /// </summary>
    private bool TryPeekSecond(out FetchedEntry second) {
        if (_fetchQueue.Count < 2) {
            second = default;
            return false;
        }

        Queue<FetchedEntry>.Enumerator e = _fetchQueue.GetEnumerator();
        e.MoveNext();
        e.MoveNext();
        second = e.Current;
        return true;
    }

    // ── Fetch ──────────────────────────────────────────────────────────────────

    /// <summary>Fetches up to issueWidth instructions along the predicted path into the queue.</summary>
    private void StepFetch(long now) {
        if (_fetchFaulted || now < _fetchStallUntil) return;

        var fetched = 0;
        while (fetched < issueWidth && _fetchQueue.Count < _queueCapacity) {
            ulong pc = _fetchPc;

            // µop cache: a hit delivers the whole cached basic block in this one iteration,
            // skipping the I-cache access and decode entirely (see UopCache's doc comment for
            // why this — not the paper's own parallel-IC-lookup design — is a documented
            // modeling divergence, not a performance result). Only ever hits at a block's
            // recorded start PC.
            if (_uopCache is not null && _uopCache.TryLookup(pc, out IReadOnlyList<ITooth> uops, out bool endsInBranch)) {
                ulong linePc = pc;
                var tookBranch = false;
                for (var i = 0; i < uops.Count && fetched < issueWidth && _fetchQueue.Count < _queueCapacity; i++) {
                    ITooth cached = uops[i];
                    ulong hitFallThrough = linePc + (ulong)cached.SizeBytes;
                    bool isLast = i == uops.Count - 1;
                    ulong hitPredictedNext = isLast && endsInBranch
                        ? PredictNext(linePc, cached.RawEncoding, hitFallThrough)
                        : hitFallThrough;

                    ulong hitInstrId = _nextInstrId++;
                    if (PEventLog is not null) {
                        PEventLog.Record(hitInstrId, linePc, now, PEventKind.Fetch);
                        PEventLog.RecordDisasm(hitInstrId, mechanism.Decoder.Disassemble(linePc, cached.RawEncoding));
                    }

                    _fetchQueue.Enqueue(
                        new FetchedEntry(linePc, cached, hitPredictedNext, hitInstrId, now + frontendDepth)
                    );
                    _fetchPc = hitPredictedNext;
                    fetched++;

                    if (hitPredictedNext != hitFallThrough) {
                        tookBranch = true;
                        break;
                    }

                    linePc = hitPredictedNext;
                }

                if (tookBranch) break;
                continue;
            }

            ulong physPc = pc;
            if (_fetchTranslator is not null) {
                (ulong pa, int faultCause) = _fetchTranslator.Translate(pc);
                if (faultCause != 0) {
                    EnqueueFault(pc, new TrapInfo(faultCause, pc, pc), now);
                    return;
                }

                physPc = pa;
            }

            ITooth instr;
            uint raw;
            try {
                raw = (uint)ILayers.Accessor.Read(physPc, 4);
                instr = mechanism.Decoder.Decode(pc, raw);
            }
            catch (IllegalInstructionException ex) {
                EnqueueFault(pc, new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), now);
                return;
            }
            catch (AccessViolationException) {
                EnqueueFault(pc, new TrapInfo(TrapCause.InstructionAccessFault, pc, pc), now);
                return;
            }

            AppendToUopBuild(pc, instr);

            // I-side miss: the line arrives after the penalty. This instruction's issue
            // readiness and all further fetch wait for it.
            long iStalls = _anyCache ? ILayers.ConsumeAllStalls() : 0;
            if (iStalls > 0) {
                _fetchStallUntil = now + iStalls;
                _cacheMissStallsCounter?.IncrementBy(iStalls);
            }

            // Predict the successor. Direct unconditional jumps/calls are always taken to
            // their known target and bypass the predictor (mirrors the five-stage fetch
            // stage / gem5); returns are steered by the RAS.
            ulong fallThrough = pc + (ulong)instr.SizeBytes;
            ulong predictedNext = PredictNext(pc, raw, fallThrough);

            ulong instrId = _nextInstrId++;
            if (PEventLog is not null) {
                PEventLog.Record(instrId, pc, now, PEventKind.Fetch);
                PEventLog.RecordDisasm(instrId, mechanism.Decoder.Disassemble(pc, raw));
            }

            _fetchQueue.Enqueue(new FetchedEntry(pc, instr, predictedNext, instrId, now + frontendDepth + iStalls));
            _fetchPc = predictedNext;
            fetched++;

            // A taken branch or a line miss ends the sequential fetch group.
            if (predictedNext != fallThrough || iStalls > 0) break;
        }
    }

    /// <summary>
    ///     Shared branch-prediction/RAS logic for both the decode-miss path (raw bytes just read
    ///     from the I-cache) and a µop-cache hit's trailing branch (raw bytes come from the
    ///     cached <see cref="ITooth.RawEncoding" /> instead — the paper's UC and IC share one BPU,
    ///     predicted fresh on every visit, so a cached predicted target is never reused).
    /// </summary>
    private ulong PredictNext(ulong pc, uint raw, ulong fallThrough) {
        FetchHint hint = mechanism.Decoder.GetFetchHint(pc, raw);
        if (!hint.IsBranch) return fallThrough;

        BranchPrediction pred = hint is { IsUnconditional: true, BranchTarget.HasValue: true, }
            ? BranchPrediction.Taken(hint.BranchTarget.Value)
            : predictor.Predict(pc, hint.BranchTarget);
        if (hint.IsCall)
            _ras.Push(fallThrough);
        else if (hint.IsReturn && _ras.TryPop(out ulong ret)) pred = BranchPrediction.Taken(ret);

        // A direct branch's taken target comes from the decode hint, not the BTB
        // (which may be cold or aliased); a cold indirect target (0) falls through.
        ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
        return pred.PredictedTaken && takenTarget != 0 ? takenTarget : fallThrough;
    }

    /// <summary>Appends a freshly-decoded instruction to the in-progress µop-cache line build.</summary>
    private void AppendToUopBuild(ulong pc, ITooth instr) {
        if (_uopCache is null) return;
        if (_uopBuildCount == 0) _uopBuildStartPc = pc;
        _uopBuildLine[_uopBuildCount++] = instr;

        bool isBranch = instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch;
        if (isBranch || _uopBuildCount >= _uopCache.LineCapacity) CloseUopBuild(isBranch);
    }

    /// <summary>Inserts the in-progress build into the µop cache, if non-empty.</summary>
    private void CloseUopBuild(bool endsInBranch) {
        if (_uopBuildCount == 0) return;
        var uops = new ITooth[_uopBuildCount];
        for (var i = 0; i < _uopBuildCount; i++) uops[i] = _uopBuildLine[i]!;
        _uopCache!.Insert(_uopBuildStartPc, uops, endsInBranch);
        _uopBuildCount = 0;
    }

    private void EnqueueFault(ulong pc, TrapInfo trap, long now) {
        // The fault ends the block "in the course of instruction fetch" (paper §2.4's third
        // block-ending rule) — whatever decoded correctly before it is still a valid, cacheable
        // line; the faulting PC itself is never cached.
        CloseUopBuild(false);
        ulong instrId = _nextInstrId++;
        PEventLog?.Record(instrId, pc, now, PEventKind.Fetch);
        _fetchQueue.Enqueue(new FetchedEntry(pc, null, pc, instrId, now + frontendDepth, trap));
        _fetchFaulted = true; // wait for the fault to issue (correct path) or flush (wrong path)
    }

    /// <summary>Discards the speculative frontend contents and redirects fetch.</summary>
    private void FlushFrontend(ulong target, long now) {
        if (PEventLog is not null)
            foreach (FetchedEntry entry in _fetchQueue)
                PEventLog.Record(entry.InstrId, entry.Pc, now, PEventKind.Flush);

        _fetchQueue.Clear();
        _fetchPc = target;
        _fetchFaulted = false;
        _fetchStallUntil = 0; // any in-flight I-miss belonged to the wrong path
        _uopBuildCount = 0; // discard: wrong-path instructions must never be cached
        _flushesCounter.Increment();
        _tdRefillPending = true; // starved slots are recovery, not fetch, until issue resumes
    }

    /// <summary>Live Top-Down breakdown (Yasin, ISPASS 2014) from the current counter values.</summary>
    private TopDownBreakdown ComputeTopDown() =>
        TopDownBreakdown.Compute(
            _td.TotalSlots.Value,
            _td.SlotsIssued.Value,
            _td.SlotsRetired.Value,
            _td.FetchBubbles.Value,
            _td.RecoveryBubbles.Value,
            _cyclesCounter.Value,
            _td.FetchLatencyCycles.Value,
            _td.ExecStallCycles.Value,
            _td.MemStallLoadCycles.Value,
            _td.MemStallStoreCycles.Value,
            _branchMissCounter.Value,
            _flushesCounter.Value
        );

    // Classifies a resolved branch for IBranchKindAwareBranchPredictor.
    private BranchKind ClassifyBranchKind(ITooth instruction) {
        var kind = BranchKind.None;
        if (instruction.Class == ToothClass.ConditionalBranch) kind |= BranchKind.Conditional;
        FetchHint hint = mechanism.Decoder.GetFetchHint(instruction.Pc, instruction.RawEncoding);
        if (hint.IsCall) kind |= BranchKind.Call;
        if (hint.IsReturn) kind |= BranchKind.Return;
        if (!hint.BranchTarget.HasValue) kind |= BranchKind.Indirect;
        return kind;
    }

    // ── Memory hierarchy stat collection ──────────────────────────────────────

    private void UpdateAllMemoryStats() {
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

    /// <summary>One fetched (possibly faulting) instruction traversing the frontend pipeline.</summary>
    private readonly record struct FetchedEntry(
        ulong Pc,
        ITooth? Instruction,
        ulong PredictedNextPc,
        ulong InstrId,
        long ReadyAt, // first cycle this entry may issue (fetch cycle + frontendDepth + I-miss delay)
        TrapInfo? PreTrap = null
    );
}