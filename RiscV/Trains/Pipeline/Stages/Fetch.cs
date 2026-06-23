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
        bool isCompressed = (raw & 0x3) != 0x3;
        int instrSize = isCompressed ? 2 : 4;

        bool isJal, isJalr, isBranch, linkRd, linkRs1;
        if (isCompressed) {
            var c = (ushort)(raw & 0xFFFF);
            var cfunct3 = (uint)(c >> 13);
            var q = (uint)(c & 0x3);
            int crs1 = (c >> 7) & 0x1F;
            int crs2 = (c >> 2) & 0x1F;
            bool inst12 = (c & 0x1000) != 0;
            if (q == 0x1 && cfunct3 == 0x1) {
                // C.JAL → JAL x1, imm
                isJal = true;
                isJalr = false;
                isBranch = true;
                linkRd = true;
                linkRs1 = false;
            }
            else if (q == 0x1 && cfunct3 == 0x5) {
                // C.J → JAL x0, imm
                isJal = true;
                isJalr = false;
                isBranch = true;
                linkRd = false;
                linkRs1 = false;
            }
            else if (q == 0x1 && (cfunct3 == 0x6 || cfunct3 == 0x7)) {
                // C.BEQZ / C.BNEZ
                isJal = false;
                isJalr = false;
                isBranch = true;
                linkRd = false;
                linkRs1 = false;
            }
            else if (q == 0x2 && cfunct3 == 0x4 && inst12 && crs2 == 0 && crs1 != 0) {
                // C.JALR → JALR x1, 0(rs1)
                isJal = false;
                isJalr = true;
                isBranch = true;
                linkRd = true;
                linkRs1 = crs1 == 1 || crs1 == 5;
            }
            else if (q == 0x2 && cfunct3 == 0x4 && !inst12 && crs2 == 0 && crs1 != 0) {
                // C.JR → JALR x0, 0(rs1) — return if rs1 is link register
                isJal = false;
                isJalr = true;
                isBranch = true;
                linkRd = false;
                linkRs1 = crs1 == 1 || crs1 == 5;
            }
            else {
                isJal = false;
                isJalr = false;
                isBranch = false;
                linkRd = false;
                linkRs1 = false;
            }
        }
        else {
            // Decode opcode fields needed for both predictor gating and RAS.
            // Link registers per RV32 ABI: x1 (ra) and x5 (t0).
            var opcode = (int)(raw & 0x7F);
            var rd = (int)((raw >> 7) & 0x1F);
            var rs1 = (int)((raw >> 15) & 0x1F);
            isJal = opcode == 0x6F;
            isJalr = opcode == 0x67;
            isBranch = opcode == 0x63 || isJal || isJalr;
            linkRd = rd == 1 || rd == 5;
            linkRs1 = rs1 == 1 || rs1 == 5;
        }

        // Only consult the predictor for actual branch/jump instructions.
        // Non-branch instructions always continue to PC+instrSize; feeding them to the
        // predictor would corrupt the BTB with non-branch addresses.
        BranchPrediction pred = isBranch
            ? predictor.Predict(Pc)
            : BranchPrediction.NotTaken(Pc + (ulong)instrSize);

        // RAS override: detect JAL/JALR call and return patterns.

        if ((isJal || isJalr) && linkRd)
            // CALL: push the return address so a future RETURN can pop it.
            _ras.Push(Pc + (ulong)instrSize);
        else if (isJalr && linkRs1 && !linkRd)
            // RETURN: override predictor target with the RAS top.
            if (_ras.TryPop(out ulong ret))
                pred = BranchPrediction.Taken(ret);

        ulong nextPc = pred.PredictedTaken ? pred.PredictedTarget : Pc + (ulong)instrSize;
        var latch = new IfIdLatch {
            IsValid = true, Pc = Pc, RawEncoding = raw, PredictedNextPc = nextPc,
        };
        _held = latch;
        LastSent = latch;
        Output.Send(latch);
        Pc = nextPc;
    }
}