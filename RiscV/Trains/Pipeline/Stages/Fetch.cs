using Mechanism;
using Mechanism.BranchPredictModels;
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
    IBranchPredictor predictor,
    int rasDepth = 16
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

        // Decode opcode fields needed for both predictor gating and RAS.
        // Link registers per RV32 ABI: x1 (ra) and x5 (t0).
        var opcode = (int)(raw & 0x7F);
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        bool isJal = opcode == 0x6F;
        bool isJalr = opcode == 0x67;
        bool isBranch = opcode == 0x63 || isJal || isJalr;
        bool linkRd = rd == 1 || rd == 5;
        bool linkRs1 = rs1 == 1 || rs1 == 5;

        // Only consult the predictor for actual branch/jump instructions.
        // Non-branch instructions always continue to PC+4; feeding them to the
        // predictor would corrupt the BTB with non-branch addresses.
        BranchPrediction pred = isBranch
            ? predictor.Predict(Pc)
            : BranchPrediction.NotTaken(Pc + 4);

        // RAS override: detect JAL/JALR call and return patterns.

        if ((isJal || isJalr) && linkRd)
            // CALL: push the return address so a future RETURN can pop it.
            _ras.Push(Pc + 4);
        else if (isJalr && linkRs1 && !linkRd)
            // RETURN: override predictor target with the RAS top.
            if (_ras.TryPop(out ulong ret))
                pred = BranchPrediction.Taken(ret);

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