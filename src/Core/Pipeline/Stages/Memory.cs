using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class MemoryStage : Gear {
    private ExMemLatch _current = ExMemLatch.Bubble;

    public MemoryStage(string name, SimNode parent, Escapement esc)
        : base(name, parent, esc) {
        Input = new InArbor<ExMemLatch>($"{name}.in");
        Output = new OutArbor<MemWbLatch>($"{name}.out", esc);

        Input.OnReceive = latch => _current = latch;
    }

    public MemWbLatch LastSent { get; private set; } = MemWbLatch.Bubble;

    public InArbor<ExMemLatch> Input { get; }
    public OutArbor<MemWbLatch> Output { get; }

    internal void Inject(ExMemLatch latch) => _current = latch;

    public void Cycle() {
        if (_current is not { IsValid: true, } latch || latch.Result is null ||
            (latch.Instruction is null && !latch.Result.HasTrap)) {
            _current = ExMemLatch.Bubble;
            LastSent = MemWbLatch.Bubble;
            return;
        }

        _current = ExMemLatch.Bubble;
        ExecuteResult result = latch.Result;

        ulong nextPc = result.BranchTaken && result.BranchTarget.HasValue
            ? result.BranchTarget.Value
            : latch.Pc + (ulong)(latch.Instruction?.SizeBytes ?? 4);

        var newLatch = new MemWbLatch {
            IsValid = true,
            Pc = latch.Pc,
            InstrId = latch.InstrId,
            NextPc = nextPc,
            Instruction = latch.Instruction,
            WritebackValue = result.RegisterResult.HasValue ? result.RegisterResult.Value : null,
            DestinationRegister = latch.DestinationRegister,
            HasTrap = result.HasTrap,
            Trap = result.Trap,
            IsHalt = result.IsHalt,
            RequestHalt = result.RequestHalt,
            IsReturnFromTrap = result.IsReturnFromTrap,
            ReturnPrivilege = result.ReturnPrivilege,
            SideEffect = result.SideEffect,
        };
        LastSent = newLatch;
    }
}