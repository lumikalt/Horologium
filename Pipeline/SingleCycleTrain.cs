using Mechanism;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

namespace Pipeline;

/// <summary>
/// The simplest possible Train: one Gear that fetches, decodes, executes,
/// and writes back one instruction per tick. No pipeline, no hazards.
/// Used to validate the Mechanism before any pipeline complexity is added.
/// </summary>
public sealed class SingleCycleTrain {
    private readonly Train _train;
    private readonly SingleCycleCore _core;

    public IArchState ArchState => _core.ArchState;

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public SetAssociativeCache? L2Cache => _core.ILayers.L2Cache; // unified; same config on I and D paths
    public SetAssociativeCache? L3Cache => _core.ILayers.L3Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;

    public SingleCycleTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) {
        var esc = new Escapement();
        _train = new Train("single_cycle", esc);
        _core = _train.AddGear(
            new SingleCycleCore(
                "core", _train.Root, esc, mechanism, memory, entryPoint,
                iMemConfig ?? MemoryConfig.None,
                dMemConfig ?? MemoryConfig.None
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 100_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public string DumpTopology() => _train.DumpTopology();
}

/// <summary>
/// The single-cycle core Gear. Each tick: fetch → decode → execute → writeback.
/// Stops scheduling when it encounters a halt condition (infinite loop to self,
/// or explicit EBREAK).
/// Cache miss penalties are charged as extra cycles appended to the retiring instruction.
/// </summary>
internal sealed class SingleCycleCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    IMemory memory,
    ulong entryPoint,
    MemoryConfig iMemConfig,
    MemoryConfig dMemConfig
)
    : Gear(name, parent, esc) {
    public MemoryLayers ILayers { get; } = MemoryLayers.Build(memory, iMemConfig);
    public MemoryLayers DLayers { get; } = MemoryLayers.Build(memory, dMemConfig);

    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Counter _stallsCounter = null!;
    private Histogram _opcodeHistogram = null!;
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

    public IArchState ArchState { get; } = mechanism.CreateArchState();

    public override void Initialize() {
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
        ScheduleNextInstruction();
    }

    private void ScheduleNextInstruction() { Escapement.ScheduleNextTick(ExecuteOneCycle, Phase.Execute); }

    private void ExecuteOneCycle() {
        ulong pc = ArchState.Pc;

        // Fetch & Decode (through I-cache accessor)
        ITooth instr;
        try { instr = mechanism.Decoder.Decode(pc, ILayers.Accessor); }
        catch (IllegalInstructionException ex) {
            if (_anyCache) DrainAndChargeStalls();
            _cyclesCounter.Increment();
            var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
            ulong vector = mechanism.TrapController.RaiseTrap(trap, ArchState);
            ArchState.Pc = vector;
            ScheduleNextInstruction();
            return;
        }

        // Execute (through D-cache accessor)
        ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, DLayers.Accessor);

        // EBREAK halts the simulation without consuming a cycle or retiring.
        if (result.IsHalt) return;

        // Collect stall cycles from memory hierarchy and update hit/miss counters.
        long stalls = _anyCache ? DrainAndChargeStalls() : 0;

        _cyclesCounter.Increment();
        if (stalls > 0) {
            _stallsCounter.IncrementBy(stalls);
            _cacheMissStallsCounter?.IncrementBy(stalls);
            _cyclesCounter.IncrementBy(stalls);
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
        }

        Type? instrType = instr.Payload?.GetType();
        if (instrType is not null) _opcodeHistogram.Observe(instrType);
        _retiredCounter.Increment();

        // Detect halt: infinite self-loop (JAL x0, 0 — common halt idiom)
        if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) return;

        ScheduleNextInstruction();
    }

    // Drains all pending stalls from every memory layer, updates hit/miss counters,
    // and returns the total stall cycle count.
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