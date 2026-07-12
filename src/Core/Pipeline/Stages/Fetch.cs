using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class FetchStage(
    string name,
    SimNode parent,
    Escapement esc,
    IMemory memory,
    IBranchPredictor predictor,
    IDecoder decoder,
    int rasDepth = 16,
    IFetchTranslator? fetchTranslator = null,
    FdipPrefetcher? fdip = null,
    SetAssociativeCache? rdipICache = null,
    RdipPrefetcher? rdip = null
)
    : Gear(name, parent, esc) {
    private readonly ReturnAddressStack _ras = new(rasDepth);
    private ulong _nextInstrId = 1;

    public ulong Pc { get; set; }
    public bool Stall { get; set; }
    public bool Flush { get; set; }
    public ulong FlushTarget { get; set; }

    public OutArbor<IfIdLatch> Output { get; } = new($"{name}.out", esc);

    // The latch most recently sent downstream — equals the instruction the
    // Decode stage will consume next cycle. The pipeline controller reads it
    // to detect hazards against the instruction about to enter Decode.
    public IfIdLatch LastSent { get; private set; } = IfIdLatch.Bubble;

    // The last real instruction sent downstream — re-sent on stall so the
    // Decode stage retains the instruction it is holding.
    private IfIdLatch _held = IfIdLatch.Bubble;

    // Suppress repeated fault latches: set when a fetch page-fault latch has
    // been sent; cleared on flush when the trap redirect arrives.
    private bool _fetchFaulted;

    public void Cycle() {
        if (Flush) {
            fdip?.Flush(FlushTarget);
            Pc = FlushTarget;
            Flush = false;
            _fetchFaulted = false;
            _held = IfIdLatch.Bubble;
            LastSent = IfIdLatch.Bubble;
            return;
        }

        fdip?.Tick(Pc);

        if (Stall) {
            // Hold: do not advance PC, re-present the instruction already in flight.
            LastSent = _held;
            return;
        }

        // If a fault latch is already in the pipe, emit bubbles until the flush arrives.
        if (_fetchFaulted) {
            LastSent = IfIdLatch.Bubble;
            return;
        }

        // Translate virtual PC → physical PC (Sv32 or bare mode).
        ulong physPc = Pc;
        if (fetchTranslator is not null) {
            (ulong pa, int faultCause) = fetchTranslator.Translate(Pc);
            if (faultCause != 0) {
                var faultLatch = new IfIdLatch {
                    IsValid = true,
                    Pc = Pc,
                    InstrId = _nextInstrId++,
                    PreTrap = new TrapInfo(faultCause, Pc, Pc),
                };
                _fetchFaulted = true;
                _held = IfIdLatch.Bubble; // stall must not re-emit the fault
                LastSent = faultLatch;
                return;
            }

            physPc = pa;
        }

        uint raw;
        try {
            raw = (uint)memory.Read(physPc, 4);
        }
        catch (AccessViolationException) {
            // Fetch address out of bounds. On the correct path this is a genuine instruction
            // access fault; on a wrong path (e.g. a mispredicted branch landing on garbage
            // data that itself decodes to a wild branch target) the fault latch is squashed
            // before it commits, just like the translation-fault case above. Either way,
            // never let the exception escape and crash the sim.
            var faultLatch = new IfIdLatch {
                IsValid = true,
                Pc = Pc,
                InstrId = _nextInstrId++,
                PreTrap = new TrapInfo(TrapCause.InstructionAccessFault, Pc, Pc),
            };
            _fetchFaulted = true;
            _held = IfIdLatch.Bubble;
            LastSent = faultLatch;
            return;
        }
        if (rdipICache?.LastAccessWasHit == false) rdip?.OnIcacheMiss(physPc);
        FetchHint hint = decoder.GetFetchHint(Pc, raw);
        int instrSize = hint.InstructionSize;

        // Only consult the predictor for actual branch/jump instructions.
        // Non-branch instructions always continue to PC+instrSize; feeding them to the
        // predictor would corrupt the BTB with non-branch addresses. Direct unconditional
        // jumps/calls are always taken to their known target and bypass the predictor
        // (mirrors gem5, which never direction-predicts unconditional branches).
        BranchPrediction pred;
        if (hint.IsUnconditional && hint.BranchTarget.HasValue)
            pred = BranchPrediction.Taken(hint.BranchTarget.Value);
        else if (hint.IsBranch)
            pred = predictor.Predict(Pc, hint.BranchTarget);
        else
            pred = BranchPrediction.NotTaken(Pc + (ulong)instrSize);

        if (hint.IsCall)
            _ras.Push(Pc + (ulong)instrSize);
        else if (hint.IsReturn)
            if (_ras.TryPop(out ulong ret))
                pred = BranchPrediction.Taken(ret);

        // A direct branch's taken target is statically known — take it from the decode hint,
        // not the predictor's BTB, which may be cold or aliased (a stale 0 there would send
        // fetch to a null address). The predictor target is used only for indirect branches
        // (and RAS returns); a cold indirect target (0) falls through rather than crashing.
        ulong takenTarget = hint.BranchTarget.HasValue ? hint.BranchTarget.Value : pred.PredictedTarget;
        ulong nextPc = pred.PredictedTaken && takenTarget != 0 ? takenTarget : Pc + (ulong)instrSize;
        var latch = new IfIdLatch {
            IsValid = true, Pc = Pc, InstrId = _nextInstrId++, RawEncoding = raw, PredictedNextPc = nextPc,
        };
        _held = latch;
        LastSent = latch;
        Pc = nextPc;
    }
}