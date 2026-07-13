using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class WritebackStage : Gear {
    private readonly ICommitObserver? _commitObserver;
    private readonly RdipPrefetcher? _rdip;
    private readonly IArchState _state;
    private readonly ITrapController _trap;

    private MemWbLatch _current = MemWbLatch.Bubble;

    public WritebackStage(
        string name,
        SimNode parent,
        Escapement esc,
        IArchState state,
        ITrapController trap,
        ICommitObserver? commitObserver = null,
        RdipPrefetcher? rdip = null
    )
        : base(name, parent, esc) {
        _state = state;
        _trap = trap;
        _commitObserver = commitObserver;
        _rdip = rdip;
        Input = new InArbor<MemWbLatch>($"{name}.in") {
            OnReceive = latch => _current = latch,
        };
    }

    public InArbor<MemWbLatch> Input { get; }

    public bool Halted { get; private set; }
    public (ulong Value, bool HasValue) TrapRedirect { get; private set; }
    public long RetiredCount { get; private set; }

    internal Histogram? OpcodeHistogram { get; set; }

    internal void Inject(MemWbLatch latch) => _current = latch;

    public void Cycle() {
        TrapRedirect = default((ulong Value, bool HasValue));

        if (_current is not { IsValid: true, } latch ||
            (latch.Instruction is null && !latch.HasTrap)) {
            _current = MemWbLatch.Bubble;
            return;
        }

        _current = MemWbLatch.Bubble;

        if (latch.IsHalt) {
            Halted = true;
            return;
        }

        switch (latch) {
            case { HasTrap: true, Trap: not null, }: TrapRedirect = (_trap.RaiseTrap(latch.Trap, _state), true); break;
            case { IsReturnFromTrap: true, ReturnPrivilege: not null, }:
                TrapRedirect = (_trap.ReturnFromTrap(latch.ReturnPrivilege.Value, _state), true);
                break;
            default: {
                latch.SideEffect?.Invoke(_state);
                switch (latch) {
                    case { WritebackValue: not null, DestinationRegister: > 0, }:
                        _state.IntegerRegisters.Write(latch.DestinationRegister, latch.WritebackValue.Value);
                        break;
                    case { DestinationRegister: > 0, SideEffect: null, }:
                        throw new InvalidOperationException(
                            $"WB: instruction {latch.Instruction?.Payload?.GetType().Name} " +
                            $"has rd={latch.DestinationRegister} but WritebackValue is null."
                        );
                }

                break;
            }
        }

        Type? instrType = latch.Instruction?.Payload?.GetType();
        if (instrType is not null) OpcodeHistogram?.Observe(instrType);
        RetiredCount++;

        // Check for pending interrupts after a normal retire (not if trap/MRET/SRET already redirected).
        if (!TrapRedirect.HasValue) {
            // Update state.Pc to the committed next PC so mepc is correct.
            _state.Pc = latch.NextPc;

            // Co-sim notification — a normal retire (trap/return set TrapRedirect
            // above and skip this block). Fire before the interrupt peek so an
            // instruction that triggers a following interrupt still commits.
            if (latch.Instruction is not null) {
                _commitObserver?.OnCommit(latch.Pc, latch.Instruction.RawEncoding, _state);
                _rdip?.OnCommit(latch.Pc, latch.Instruction.RawEncoding);
            }

            // First-class HTIF tohost exit: the store flagged a post-commit halt.
            // The instruction has committed above; stop before any further retire.
            if (latch.RequestHalt) {
                Halted = true;
                return;
            }

            // Backstop: halt on an unconditional jump-to-self (the conventional
            // bare-metal terminator, e.g. the spin after a non-HTIF program ends).
            // Gated on Branch (jal/jalr) so a conditional spin-wait — which may be
            // waiting on an interrupt — is not mistaken for a halt. Mirrors the
            // single-cycle train.
            if (latch.NextPc == latch.Pc && latch.Instruction?.Class == ToothClass.Branch) {
                Halted = true;
                return;
            }

            TrapInfo? interrupt = _trap.PeekInterrupt(_state);
            if (interrupt is not null) TrapRedirect = (_trap.RaiseTrap(interrupt, _state), true);
        }
    }
}