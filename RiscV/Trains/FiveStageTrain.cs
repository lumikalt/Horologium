using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using RiscV.State;
using RiscV.Trains.Pipeline;
using RiscV.Trains.Pipeline.Stages;

namespace RiscV.Trains;

public sealed class FiveStageTrain {
    private readonly Train _train;
    private readonly PipelineCore _core;

    public IArchState ArchState => _core.State;

    public SetAssociativeCache? ICache => _core.ILayers.Cache;
    public SetAssociativeCache? DCache => _core.DLayers.Cache;
    public Tlb? ITlb => _core.ILayers.Tlb;
    public Tlb? DTlb => _core.DLayers.Tlb;

    public FiveStageTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        bool forwardingEnabled = true,
        IBranchPredictor? predictor = null,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null
    ) {
        var esc = new Escapement();
        _train = new Train("five_stage", esc);
        _core = _train.AddGear(
            new PipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, entryPoint, forwardingEnabled,
                predictor ?? new AlwaysNotTakenPredictor(),
                iMemConfig ?? MemoryConfig.None,
                dMemConfig ?? MemoryConfig.None
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000) =>
        _train.Run(maxTicks);
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
    private Counter _cyclesCounter;
    private Counter _retiredCounter;
    private Counter _stallsCounter;
    private Counter _flushesCounter;
    private Counter _missesCounter;
    private Counter? _cacheMissStallsCounter;
    private Counter? _icacheHitsCounter;
    private Counter? _icacheMissesCounter;
    private Counter? _dcacheHitsCounter;
    private Counter? _dcacheMissesCounter;
    private Counter? _itlbHitsCounter;
    private Counter? _itlbMissesCounter;
    private Counter? _dtlbHitsCounter;
    private Counter? _dtlbMissesCounter;

    private long _lastRetired;
    private long _missStallBudget;

    // Delta tracking for cache/TLB stat counters
    private long _lastIHits, _lastIMisses;
    private long _lastDHits, _lastDMisses;
    private long _lastITlbHits, _lastITlbMisses;
    private long _lastDTlbHits, _lastDTlbMisses;

    public RvArchState State { get; }
    public MemoryLayers ILayers { get; }
    public MemoryLayers DLayers { get; }

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
        MemoryConfig dMemConfig
    )
        : base(name, parent, esc) {
        _predictor = predictor;
        _hazard = new HazardUnit(forwardingEnabled);
        _decoder = mechanism.Decoder;
        State = (RvArchState)mechanism.CreateArchState();
        State.Pc = entryPoint;

        ILayers = MemoryLayers.Build(memory, iMemConfig);
        DLayers = MemoryLayers.Build(memory, dMemConfig);

        // Create stages — IF uses instruction memory, EX/MEM use data memory.
        _if = new FetchStage("if", parent, esc, ILayers.Accessor, predictor);
        _id = new DecodeStage("id", parent, esc, mechanism.Decoder, State);
        _ex = new ExecuteStage(
            "ex", parent, esc,
            mechanism.Executor, State, DLayers.Accessor, _hazard
        );
        _mem = new MemoryStage("mem", parent, esc, DLayers.Accessor);
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
                                                  || ILayers.Tlb is not null || DLayers.Tlb is not null;
        if (anyCache)
            _cacheMissStallsCounter = Dials.AddCounter(
                "cache_miss_stalls", "Stall cycles from memory hierarchy misses"
            );

        if (ILayers.Cache is not null) {
            _icacheHitsCounter = Dials.AddCounter("icache_hits", "I-cache hits");
            _icacheMissesCounter = Dials.AddCounter("icache_misses", "I-cache misses");
        }

        if (DLayers.Cache is not null) {
            _dcacheHitsCounter = Dials.AddCounter("dcache_hits", "D-cache hits");
            _dcacheMissesCounter = Dials.AddCounter("dcache_misses", "D-cache misses");
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

    public override void Tick() =>
        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);

    // One clock cycle. Control logic runs first (Phase.Fetch), reading the
    // latches produced by last cycle's stages. The stages themselves are then
    // scheduled later this same tick — after Arbor delivery (Phase.PortUpdate)
    // has populated their input latches — so each inter-stage hop costs exactly
    // one cycle. Writeback runs before Decode so a register written this cycle
    // is visible to a read in the same cycle.
    private void RunCycle() {
        // Collect pending stalls from memory hierarchy (generated last cycle's stage execution).
        _missStallBudget += CollectMemoryStalls();

        // Reflect retirements produced by last cycle's Writeback.
        long newRetired = _wb.RetiredCount;
        while (_lastRetired < newRetired) {
            _retiredCounter.Increment();
            _lastRetired++;
        }

        if (_wb.Halted) return; // pipeline drained — stop the clock

        _cyclesCounter.Increment();

        if (_missStallBudget > 0) {
            // Drain one stall cycle: freeze all stages, advance the clock.
            _stallsCounter.Increment();
            _cacheMissStallsCounter?.Increment();
            _missStallBudget--;
            Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
            return;
        }

        // Push forwarding context into EX before it runs.
        _ex.SetForwardingContext(_ex.LastSent, _mem.LastSent);

        // Hazard detection protects the instruction about to enter Decode
        // (the one IF produced last cycle) against producers still in EX/MEM.
        bool stall = _hazard.MustStall(IncomingSources(), _id.LastSent, _ex.LastSent);

        // Reconcile any branch leaving EX with the prediction made at fetch.
        // The predictor is trained on every resolved branch; a flush (and a
        // misprediction penalty) is only paid when the speculated next PC was
        // wrong.
        var flush = false;
        ExMemLatch resolved = _ex.LastSent;
        if (resolved is {
                IsValid: true, Result: not null,
                Instruction.Class: InstructionClass.Branch or InstructionClass.ConditionalBranch,
            }) {
            bool taken = resolved.Result.BranchTaken;
            ulong actualNext = taken ? resolved.Result.BranchTarget!.Value : resolved.Pc + 4;
            _predictor.Update(resolved.Pc, taken, actualNext);

            if (actualNext != resolved.PredictedNextPc) {
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

        // Trap redirect from WB (computed last cycle).
        if (_wb.TrapRedirect.HasValue) {
            _if.Pc = _wb.TrapRedirect.Value;
            _if.Flush = true;
        }

        // Drive the stages this tick, after Arbor delivery (PortUpdate, phase 2).
        long t = Escapement.CurrentTick;
        Escapement.Schedule(_wb.Cycle, t, Phase.Writeback); // write regfile first
        Escapement.Schedule(_id.Cycle, t, Phase.Commit);    // then read regfile
        Escapement.Schedule(_ex.Cycle, t, Phase.Commit);
        Escapement.Schedule(_mem.Cycle, t, Phase.Commit);
        Escapement.Schedule(_if.Cycle, t, Phase.Commit);

        Escapement.ScheduleNextTick(RunCycle, Phase.Fetch);
    }

    // Drain accumulated stall cycles from all memory hierarchy layers and
    // update DialBoard counters with deltas since the last call.
    private long CollectMemoryStalls() {
        long stalls = 0;
        stalls += ILayers.Cache?.ConsumePendingStalls() ?? 0;
        stalls += DLayers.Cache?.ConsumePendingStalls() ?? 0;
        stalls += ILayers.Tlb?.ConsumePendingStalls() ?? 0;
        stalls += DLayers.Tlb?.ConsumePendingStalls() ?? 0;

        if (ILayers.Cache is { } ic) {
            _icacheHitsCounter!.IncrementBy(ic.Hits - _lastIHits);
            _icacheMissesCounter!.IncrementBy(ic.Misses - _lastIMisses);
            _lastIHits = ic.Hits;
            _lastIMisses = ic.Misses;
        }

        if (DLayers.Cache is { } dc) {
            _dcacheHitsCounter!.IncrementBy(dc.Hits - _lastDHits);
            _dcacheMissesCounter!.IncrementBy(dc.Misses - _lastDMisses);
            _lastDHits = dc.Hits;
            _lastDMisses = dc.Misses;
        }

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

        return stalls;
    }

    // Source registers of the instruction IF produced last cycle — the one
    // Decode will read this cycle. Decoding is side-effect free, so the
    // controller can peek without disturbing the pipeline.
    private IReadOnlyList<int> IncomingSources() {
        IfIdLatch incoming = _if.LastSent;
        if (!incoming.IsValid) return [];
        try { return _decoder.Decode(incoming.Pc, incoming.RawEncoding).SourceRegisters; }
        catch (IllegalInstructionException) { return []; }
    }
}