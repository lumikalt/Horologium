using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class DecodeStage : Gear {
    private readonly IDecoder _decoder;
    private readonly IArchState _state;

    public bool Stall { get; set; }
    public bool Flush { get; set; }

    // Latch received from IF — set by InArbor callback
    private IfIdLatch _current = IfIdLatch.Bubble;

    public IdExLatch LastSent { get; private set; } = IdExLatch.Bubble;

    public InArbor<IfIdLatch> Input { get; }
    public OutArbor<IdExLatch> Output { get; }

    public DecodeStage(
        string name,
        SimNode parent,
        Escapement esc,
        IDecoder decoder,
        IArchState state
    )
        : base(name, parent, esc) {
        _decoder = decoder;
        _state = state;

        Input = new InArbor<IfIdLatch>($"{name}.in");
        Output = new OutArbor<IdExLatch>($"{name}.out", esc);

        Input.OnReceive = latch => _current = latch;
    }

    internal void Inject(IfIdLatch latch) => _current = latch;

    public void Cycle() {
        if (Flush) {
            _current = IfIdLatch.Bubble;
            LastSent = IdExLatch.Bubble;
            return;
        }

        if (Stall) {
            // Hold the current instruction; insert a bubble into EX.
            LastSent = IdExLatch.Bubble;
            return;
        }

        if (_current is not { IsValid: true, } latch) {
            _current = IfIdLatch.Bubble;
            LastSent = IdExLatch.Bubble;
            return;
        }

        _current = IfIdLatch.Bubble;

        // Propagate fetch page faults without decoding.
        if (latch.PreTrap is not null) {
            LastSent = new IdExLatch { IsValid = true, Pc = latch.Pc, PreTrap = latch.PreTrap };
            return;
        }

        ITooth instr;
        try { instr = _decoder.Decode(latch.Pc, latch.RawEncoding); }
        catch (IllegalInstructionException) {
            LastSent = IdExLatch.Bubble;
            return;
        }

        IRegisterFile regs = _state.IntegerRegisters;
        ulong rs1 = instr.SourceRegisters.Count > 0 ? regs.Read(instr.SourceRegisters[0]) : 0;
        ulong rs2 = instr.SourceRegisters.Count > 1 ? regs.Read(instr.SourceRegisters[1]) : 0;
        ulong rs3 = instr.SourceRegisters.Count > 2 ? regs.Read(instr.SourceRegisters[2]) : 0;

        var newLatch = new IdExLatch {
            IsValid = true,
            Pc = latch.Pc,
            Instruction = instr,
            Rs1Value = rs1,
            Rs2Value = rs2,
            Rs3Value = rs3,
            DestinationRegister = instr.DestinationRegister,
            PredictedNextPc = latch.PredictedNextPc,
        };
        LastSent = newLatch;
    }
}