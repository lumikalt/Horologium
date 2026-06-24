using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class WritebackStage : Gear {
    private readonly IArchState _state;
    private readonly ITrapController _trap;

    private MemWbLatch _current = MemWbLatch.Bubble;

    public InArbor<MemWbLatch> Input { get; }

    public bool Halted { get; private set; }
    public ulong? TrapRedirect { get; private set; }
    public long RetiredCount { get; private set; }

    internal Histogram? OpcodeHistogram { get; set; }

    public WritebackStage(
        string name,
        SimNode parent,
        Escapement esc,
        IArchState state,
        ITrapController trap
    )
        : base(name, parent, esc) {
        _state = state;
        _trap = trap;
        Input = new InArbor<MemWbLatch>($"{name}.in") {
            OnReceive = latch => _current = latch,
        };
    }

    internal void Inject(MemWbLatch latch) => _current = latch;

    public void Cycle() {
        TrapRedirect = null;

        if (_current is not { IsValid: true, } latch || latch.Instruction is null) {
            _current = MemWbLatch.Bubble;
            return;
        }

        _current = MemWbLatch.Bubble;

        if (latch.IsHalt) {
            Halted = true;
            return;
        }

        if (latch.HasTrap && latch.Trap is not null) { TrapRedirect = _trap.RaiseTrap(latch.Trap, _state); }
        else if (latch.IsReturnFromTrap && latch.ReturnPrivilege.HasValue) {
            TrapRedirect = _trap.ReturnFromTrap(latch.ReturnPrivilege.Value, _state);
        }
        else {
            latch.SideEffect?.Invoke(_state);
            if (latch.WritebackValue.HasValue && latch.DestinationRegister > 0)
                _state.IntegerRegisters.Write(latch.DestinationRegister, latch.WritebackValue.Value);
            else if (latch.DestinationRegister > 0 && latch.SideEffect is null)
                throw new InvalidOperationException(
                    $"WB: instruction {latch.Instruction.Payload?.GetType().Name} " +
                    $"has rd={latch.DestinationRegister} but WritebackValue is null."
                );
        }

        var instrType = latch.Instruction.Payload?.GetType();
        if (instrType is not null) OpcodeHistogram?.Observe(instrType);
        RetiredCount++;
    }
}