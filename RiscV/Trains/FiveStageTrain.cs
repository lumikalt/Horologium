using Mechanism;
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

    public FiveStageTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        bool forwardingEnabled = true,
        IBranchPredictor? predictor = null
    ) {
        var esc = new Escapement();
        _train = new Train("five_stage", esc);
        _core = _train.AddGear(
            new PipelineCore(
                "pipeline", _train.Root, esc,
                mechanism, memory, entryPoint, forwardingEnabled,
                predictor ?? new AlwaysNotTakenPredictor()
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

    private long _lastRetired;

    public RvArchState State { get; }

    public PipelineCore(
        string name,
        SimNode parent,
        Escapement esc,
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint,
        bool forwardingEnabled,
        IBranchPredictor predictor
    )
        : base(name, parent, esc) {
        _predictor = predictor;
        _hazard = new HazardUnit(forwardingEnabled);
        _decoder = mechanism.Decoder;
        State = (RvArchState)mechanism.CreateArchState();
        State.Pc = entryPoint;

        // Create stages
        _if = new FetchStage("if", parent, esc, memory, predictor);
        _id = new DecodeStage("id", parent, esc, mechanism.Decoder, State);
        _ex = new ExecuteStage(
            "ex", parent, esc,
            mechanism.Executor, State, memory, _hazard
        );
        _mem = new MemoryStage("mem", parent, esc, memory);
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
        // Reflect retirements produced by last cycle's Writeback.
        long newRetired = _wb.RetiredCount;
        while (_lastRetired < newRetired) {
            _retiredCounter.Increment();
            _lastRetired++;
        }

        if (_wb.Halted) return; // pipeline drained — stop the clock

        _cyclesCounter.Increment();

        // Push forwarding context into EX before it runs.
        _ex.SetForwardingContext(_ex.LastSent, _mem.LastSent);

        // Hazard detection protects the instruction about to enter Decode
        // (the one IF produced last cycle) against producers still in EX/MEM.
        bool stall = _hazard.MustStall(IncomingSources(), _id.LastSent, _ex.LastSent);

        // Reconcile any branch leaving EX with the prediction made at fetch.
        // The predictor is trained on every resolved branch; a flush (and a
        // misprediction penalty) is only paid when the speculated next PC was
        // wrong.
        bool flush = false;
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

    // Source registers of the instruction IF produced last cycle — the one
    // Decode will read this cycle. Decoding is side-effect free, so the
    // controller can peek without disturbing the pipeline.
    private IReadOnlyList<int> IncomingSources() {
        IfIdLatch incoming = _if.LastSent;
        if (!incoming.IsValid) return Array.Empty<int>();
        try {
            return _decoder.Decode(incoming.Pc, incoming.RawEncoding).SourceRegisters;
        }
        catch (IllegalInstructionException) {
            return Array.Empty<int>();
        }
    }
}

public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc) => BranchPrediction.NotTaken(pc + 4);
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

public sealed class TwoBitPredictor : IBranchPredictor {
    private readonly int _tableSize;
    private readonly byte[] _counters;
    private readonly ulong[] _btb;

    public TwoBitPredictor(int tableSize = 1024) {
        _tableSize = tableSize;
        _counters = new byte[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, (byte)1);
    }

    public BranchPrediction Predict(ulong pc) {
        int idx = Index(pc);
        bool taken = _counters[idx] >= 2;
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _counters[idx] < 3:  _counters[idx]++; break;
            case false when _counters[idx] > 0: _counters[idx]--; break;
        }
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_tableSize);
}