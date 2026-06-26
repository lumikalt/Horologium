using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using Pipeline.Stages;

namespace Pipeline;

public sealed class FiveStageTrain {
    private readonly Train _train;
    private readonly PipelineCore _core;

    public IArchState ArchState => _core.State;

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;
    public StoreBuffer? StoreBuffer => _core.StoreBuffer;

    public FiveStageTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        bool forwardingEnabled = true,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        int storeBufferCapacity = 0
    ) {
        var esc = new Escapement();
        _train = new Train("five_stage", esc);
        _core = _train.AddGear(
            new PipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, entryPoint, forwardingEnabled,
                predictor ?? new AlwaysNotTakenPredictor(),
                iMemConfig ?? MemoryConfig.None,
                dMemConfig ?? MemoryConfig.None,
                storeBufferCapacity
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);
}

internal sealed class PipelineCore : Gear {
    private readonly IBranchPredictor _predictor;
    private readonly HazardUnit _hazard;
    private readonly IDecoder _decoder;

    private readonly FetchStage _if;
    private readonly DecodeStage _id;
    private readonly ExecuteStage _ex;
    private readonly MemoryStage _mem;
    private readonly WritebackStage _wb;

    // Counters
    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Counter _stallsCounter = null!;
    private Counter _flushesCounter = null!;
    private Counter _missesCounter = null!;
    private Counter? _cacheMissStallsCounter;
    private Counter? _icacheHitsCounter;
    private Counter? _icacheMissesCounter;
    private Counter? _l2IcacheHitsCounter;
    private Counter? _l2IcacheMissesCounter;
    private Counter? _l3IcacheHitsCounter;
    private Counter? _l3IcacheMissesCounter;
    private Counter? _dcacheHitsCounter;
    private Counter? _dcacheMissesCounter;
    private Counter? _l2DcacheHitsCounter;
    private Counter? _l2DcacheMissesCounter;
    private Counter? _l3DcacheHitsCounter;
    private Counter? _l3DcacheMissesCounter;
    private Counter? _itlbHitsCounter;
    private Counter? _itlbMissesCounter;
    private Counter? _dtlbHitsCounter;
    private Counter? _dtlbMissesCounter;
    private Counter? _storeForwardsCounter;

    private long _lastRetired;
    private long _missStallBudget;

    // Pre-allocated for per-cycle forwarding/hazard checks — avoids heap allocation every RunCycle.
    private readonly PipelineResident[] _fwdProviders = new PipelineResident[2];
    private readonly PipelineResident[] _hazardResidents = new PipelineResident[2];

    private bool _anyCache;

    // Delta tracking for cache/TLB stat counters
    private long _lastIHits, _lastIMisses;
    private long _lastIl2Hits, _lastIl2Misses;
    private long _lastIl3Hits, _lastIl3Misses;
    private long _lastDHits, _lastDMisses;
    private long _lastDl2Hits, _lastDl2Misses;
    private long _lastDl3Hits, _lastDl3Misses;
    private long _lastITlbHits, _lastITlbMisses;
    private long _lastDTlbHits, _lastDTlbMisses;
    private long _lastStoreForwards;

    public IArchState State { get; }
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }
    public StoreBuffer? StoreBuffer { get; }

    public PipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint,
        bool forwardingEnabled,
        IBranchPredictor predictor,
        MemoryConfig iMemConfig,
        MemoryConfig dMemConfig,
        int storeBufferCapacity = 0
    )
        : base(name, parent, esc) {
        _predictor = predictor;
        _hazard = new HazardUnit(forwardingEnabled);
        _decoder = mechanism.Decoder;
        State = mechanism.CreateArchState();
        State.Pc = entryPoint;

        ILayers = MemoryLayers.Build(memory, iMemConfig);
        DLayers = MemoryLayers.Build(memory, dMemConfig);

        IMemory dAccessor = DLayers.Accessor;
        if (storeBufferCapacity > 0) {
            StoreBuffer = new StoreBuffer(dAccessor, esc, storeBufferCapacity);
            dAccessor = StoreBuffer;
        }

        // Create stages — IF uses instruction memory, EX/MEM use data memory.
        _if = new FetchStage("if", parent, esc, ILayers.Accessor, predictor, _decoder,
            fetchTranslator: mechanism.CreateFetchTranslator(State, memory));
        _id = new DecodeStage("id", parent, esc, mechanism.Decoder, State);
        _ex = new ExecuteStage(
            "ex", parent, esc,
            mechanism.Executor, State, dAccessor, _hazard
        );
        _mem = new MemoryStage("mem", parent, esc);
        _wb = new WritebackStage(
            "wb", parent, esc,
            State, mechanism.TrapController
        );

        _if.Pc = entryPoint;

        // Wire: IF.Output → ID.Input → EX.Input → MEM.Input → WB.Input
        _if.Output.Bind(_id.Input);
        _id.Output.Bind(_ex.Input);
        _ex.Output.Bind(_mem.Input);
        _mem.Output.Bind(_wb.Input);
    }

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
        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);

    // One clock cycle. Control logic snapshots the pipeline registers from last
    // cycle, then drives all stages directly. WB runs before ID so a register
    // written this cycle is visible to Decode's reads in the same cycle.
    private void RunCycle() {
        // Collect pending stalls from memory hierarchy (generated last cycle's stage execution).
        if (_anyCache) _missStallBudget += CollectMemoryStalls();

        // Reflect retirements produced by last cycle's Writeback.
        long newRetired = _wb.RetiredCount;
        while (_lastRetired < newRetired) {
            _retiredCounter.Increment();
            _lastRetired++;
        }

        if (_wb.Halted) {
            StoreBuffer?.DrainAll(); // ensure all pending stores reach backing memory
            return;
        }

        _cyclesCounter.Increment();

        if (_missStallBudget > 0) {
            // Drain one stall cycle: freeze all stages, advance the clock.
            _stallsCounter.Increment();
            _cacheMissStallsCounter?.Increment();
            _missStallBudget--;
            Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
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
            exMemLast.Result is { } fwdR && fwdR.RegisterResult.HasValue ? fwdR.RegisterResult.Value : null
        );
        _ex.SetForwardingContext(_fwdProviders);

        // Hazard detection: residents newest-first (EX at 0, MEM at 1).
        _hazardResidents[0] = new PipelineResident(
            idExLast.IsValid, idExLast.DestinationRegister, idExLast.Instruction?.Class ?? default(ToothClass), null
        );
        _hazardResidents[1] = new PipelineResident(
            exMemLast.IsValid, exMemLast.DestinationRegister, exMemLast.Instruction?.Class ?? default(ToothClass),
            exMemLast.Result is { } hzR && hzR.RegisterResult.HasValue ? hzR.RegisterResult.Value : null
        );
        ITooth? incoming = TryDecode(ifIdLast);
        bool stall = _hazard.MustStall(incoming?.SourceRegisters ?? [], _hazardResidents);

        // Vector RAW hazard: VRF writes complete via SideEffect in WB with no
        // forwarding path. Stall while any in-flight instruction writes a vector
        // register read by the incoming instruction.
        if (!stall && incoming != null)
            stall = VectorRawHazard(incoming, idExLast.Instruction)
                 || VectorRawHazard(incoming, exMemLast.Instruction);

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
            _predictor.Update(exMemLast.Pc, taken, actualNext);

            if (actualNext != exMemLast.PredictedNextPc) {
                flush = true;
                _if.FlushTarget = actualNext;
                _ex.Squash = true; // kill the wrong-path instruction now in EX
                _flushesCounter.Increment();
                _missesCounter.Increment();
            }
        }

        _if.Stall = stall;
        _id.Stall = stall;
        _if.Flush = flush;
        _id.Flush = flush;

        if (stall) _stallsCounter.Increment();

        // If a halt is about to retire through WB this tick, squash EX so
        // instructions speculatively fetched past the halt cannot execute.
        if (memWbLast is { IsValid: true, IsHalt: true, }) _ex.Squash = true;

        // Trap redirect from WB (computed last cycle).
        if (_wb.TrapRedirect.HasValue) {
            _if.FlushTarget = _wb.TrapRedirect.Value;
            _if.Flush = true;
        }

        // Drive stages directly — WB before ID so the register file write
        // is visible to Decode's reads within the same cycle.
        _wb.Inject(memWbLast);
        _wb.Cycle();
        _id.Inject(ifIdLast);
        _id.Cycle();
        _ex.Inject(idExLast);
        _ex.Cycle();
        _mem.Inject(exMemLast);
        _mem.Cycle();
        _if.Cycle();
        StoreBuffer?.DrainEligible();

        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
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
        if (vd < 0) return false;
        return consumer.VectorSourceRegisters.Contains(vd);
    }
}