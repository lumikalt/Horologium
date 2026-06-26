using Mechanism;
using Mechanism.BranchPredictModels;
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
    IFetchTranslator? fetchTranslator = null
)
    : Gear(name, parent, esc) {
    private readonly ReturnAddressStack _ras = new(rasDepth);

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
            Pc = FlushTarget;
            Flush = false;
            _fetchFaulted = false;
            _held = IfIdLatch.Bubble;
            LastSent = IfIdLatch.Bubble;
            return;
        }

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
            var (pa, faultCause) = fetchTranslator.Translate(Pc);
            if (faultCause != 0) {
                var faultLatch = new IfIdLatch {
                    IsValid = true,
                    Pc = Pc,
                    PreTrap = new TrapInfo(faultCause, Pc, Pc),
                };
                _fetchFaulted = true;
                _held = IfIdLatch.Bubble; // stall must not re-emit the fault
                LastSent = faultLatch;
                return;
            }
            physPc = pa;
        }

        var raw = (uint)memory.Read(physPc, 4);
        FetchHint hint = decoder.GetFetchHint(Pc, raw);
        int instrSize = hint.InstructionSize;

        // Only consult the predictor for actual branch/jump instructions.
        // Non-branch instructions always continue to PC+instrSize; feeding them to the
        // predictor would corrupt the BTB with non-branch addresses.
        BranchPrediction pred = hint.IsBranch
            ? predictor.Predict(Pc, hint.BranchTarget)
            : BranchPrediction.NotTaken(Pc + (ulong)instrSize);

        if (hint.IsCall)
            _ras.Push(Pc + (ulong)instrSize);
        else if (hint.IsReturn)
            if (_ras.TryPop(out ulong ret))
                pred = BranchPrediction.Taken(ret);

        ulong nextPc = pred.PredictedTaken ? pred.PredictedTarget : Pc + (ulong)instrSize;
        var latch = new IfIdLatch {
            IsValid = true, Pc = Pc, RawEncoding = raw, PredictedNextPc = nextPc,
        };
        _held = latch;
        LastSent = latch;
        Pc = nextPc;
    }
}