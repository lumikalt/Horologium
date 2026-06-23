using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;
using RiscV.Decode;
using RiscV.State;

namespace RiscV.Trains.Pipeline.Stages;

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

    public void Cycle() {
        TrapRedirect = null;

        if (_current is not { IsValid: true, } latch || latch.Instruction is null) {
            _current = MemWbLatch.Bubble;
            return;
        }

        _current = MemWbLatch.Bubble;

        switch (latch) {
            case { HasTrap: true, Trap: not null, } when latch.Instruction.Payload is RvEbreak:
                Halted = true;
                return;
            case { HasTrap: true, Trap: not null, }:
                TrapRedirect = latch.Instruction.Payload is RvMret
                    ? _trap.ReturnFromTrap(PrivilegeLevel.Machine, _state)
                    : _trap.RaiseTrap(latch.Trap, _state);
                break;
            case { VectorResult: not null, VectorDestRegister: >= 0, }:
                ((RvArchState)_state).VectorRegisters.Write(latch.VectorDestRegister, latch.VectorResult);
                break;
            case { WritebackValue: not null, DestinationRegister: > 0, }:
                _state.IntegerRegisters.Write(
                    latch.DestinationRegister, latch.WritebackValue.Value
                );
                break;
            case { Instruction.DestinationRegister: > 0, }:
                // This fires if the instruction has a destination but WritebackValue is null
                throw new InvalidOperationException(
                    $"WB: instruction {latch.Instruction.Payload?.GetType().Name} " +
                    $"has rd={latch.DestinationRegister} but WritebackValue is null. " +
                    $"Result was: {latch.WritebackValue}"
                );
        }

        OpcodeHistogram?.Observe(latch.Instruction.Payload?.GetType().Name ?? "unknown");
        RetiredCount++;
    }
}