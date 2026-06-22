using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace RiscV.Trains.Pipeline.Stages;

public sealed class FetchStage(
    string name,
    SimNode parent,
    Escapement esc,
    IMemory memory,
    IBranchPredictor predictor
)
    : Gear(name, parent, esc) {
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

    public void Cycle() {
        if (Flush) {
            Pc = FlushTarget;
            Flush = false;
            _held = IfIdLatch.Bubble;
            LastSent = IfIdLatch.Bubble;
            Output.Send(IfIdLatch.Bubble);
            return;
        }

        if (Stall) {
            // Hold: do not advance PC, re-present the instruction already in flight.
            LastSent = _held;
            Output.Send(_held);
            return;
        }

        var raw = (uint)memory.Read(Pc, 4);
        BranchPrediction pred = predictor.Predict(Pc);
        ulong nextPc = pred.PredictedTaken ? pred.PredictedTarget : Pc + 4;
        var latch = new IfIdLatch {
            IsValid = true, Pc = Pc, RawEncoding = raw, PredictedNextPc = nextPc,
        };
        _held = latch;
        LastSent = latch;
        Output.Send(latch);
        Pc = nextPc;
    }
}