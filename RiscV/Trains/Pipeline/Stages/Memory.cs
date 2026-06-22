using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace RiscV.Trains.Pipeline.Stages;

public sealed class MemoryStage : Gear {
    private readonly IMemory _memory;

    private ExMemLatch _current = ExMemLatch.Bubble;

    public MemWbLatch LastSent { get; private set; } = MemWbLatch.Bubble;

    public InArbor<ExMemLatch> Input { get; }
    public OutArbor<MemWbLatch> Output { get; }

    public MemoryStage(string name, SimNode parent, Escapement esc, IMemory memory)
        : base(name, parent, esc) {
        _memory = memory;
        Input = new InArbor<ExMemLatch>($"{name}.in");
        Output = new OutArbor<MemWbLatch>($"{name}.out", esc);

        Input.OnReceive = latch => _current = latch;
    }

    public void Cycle() {
        if (_current is not { IsValid: true, } latch ||
            latch.Instruction is null || latch.Result is null) {
            _current = ExMemLatch.Bubble;
            LastSent = MemWbLatch.Bubble;
            Output.Send(MemWbLatch.Bubble);
            return;
        }

        _current = ExMemLatch.Bubble;
        ExecuteResult result = latch.Result;

        if (result.HasTrap) {
            var trapLatch = new MemWbLatch {
                IsValid = true,
                Pc = latch.Pc,
                Instruction = latch.Instruction,
                HasTrap = true,
                Trap = result.Trap,
                DestinationRegister = -1,
            };
            LastSent = trapLatch;
            Output.Send(trapLatch);
            return;
        }

        var newLatch = new MemWbLatch {
            IsValid = true,
            Pc = latch.Pc,
            Instruction = latch.Instruction,
            WritebackValue = result.RegisterResult,
            DestinationRegister = latch.DestinationRegister,
        };
        Output.Send(newLatch);
        LastSent = newLatch;
    }
}