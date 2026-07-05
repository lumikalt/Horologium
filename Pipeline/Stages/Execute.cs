using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Pipeline.Stages;

public sealed class ExecuteStage : Gear {
    private readonly IExecutor _executor;
    private readonly IArchState _state;
    private readonly IMemory _memory;
    private readonly HazardUnit _hazard;

    private IdExLatch _current = IdExLatch.Bubble;
    private IReadOnlyList<PipelineResident> _forwardProviders = [];

    public InArbor<IdExLatch> Input { get; }
    public OutArbor<ExMemLatch> Output { get; }

    public ExMemLatch LastSent { get; private set; } = ExMemLatch.Bubble;

    // Set for one cycle when a branch resolves taken: the instruction currently
    // in EX is on the wrong path (fetched after the branch) and must be killed.
    public bool Squash { get; set; }

    // The pipeline controller pushes forwarding providers each tick (oldest-first).
    public void SetForwardingContext(IReadOnlyList<PipelineResident> providers) { _forwardProviders = providers; }

    public ExecuteStage(
        string name,
        SimNode parent,
        Escapement esc,
        IExecutor executor,
        IArchState state,
        IMemory memory,
        HazardUnit hazard
    )
        : base(name, parent, esc) {
        _executor = executor;
        _state = state;
        _memory = memory;
        _hazard = hazard;

        Input = new InArbor<IdExLatch>($"{name}.in");
        Output = new OutArbor<ExMemLatch>($"{name}.out", esc);

        Input.OnReceive = latch => _current = latch;
    }

    internal void Inject(IdExLatch latch) => _current = latch;

    public void Cycle() {
        if (Squash) {
            Squash = false;
            _current = IdExLatch.Bubble;
            LastSent = ExMemLatch.Bubble;
            return;
        }

        if (_current is not { IsValid: true, } latch) {
            _current = IdExLatch.Bubble;
            LastSent = ExMemLatch.Bubble;
            return;
        }

        _current = IdExLatch.Bubble;

        // Fetch page fault: bypass execution and forward the pre-baked trap result.
        if (latch.PreTrap is not null) {
            LastSent = new ExMemLatch {
                IsValid = true, Pc = latch.Pc, InstrId = latch.InstrId,
                Result = ExecuteResult.WithTrap(latch.PreTrap),
            };
            return;
        }

        if (latch.Instruction is null) {
            LastSent = ExMemLatch.Bubble;
            return;
        }

        IRegisterFile regs = _state.IntegerRegisters;
        ITooth instr = latch.Instruction;
        (ulong rs1, ulong rs2, ulong rs3) = _hazard.Forward(
            latch.Rs1Value, latch.Rs2Value, latch.Rs3Value, instr.SourceRegisters, _forwardProviders
        );

        // The executor reads operands from the register file, so inject the
        // forwarded values, then restore — otherwise a forwarded operand would
        // clobber a value the Writeback stage just committed this cycle.
        int s0 = instr.SourceRegisters.Count > 0 ? instr.SourceRegisters[0] : -1;
        int s1 = instr.SourceRegisters.Count > 1 ? instr.SourceRegisters[1] : -1;
        int s2 = instr.SourceRegisters.Count > 2 ? instr.SourceRegisters[2] : -1;
        ulong save0 = s0 >= 0 ? regs.Read(s0) : 0;
        ulong save1 = s1 >= 0 ? regs.Read(s1) : 0;
        ulong save2 = s2 >= 0 ? regs.Read(s2) : 0;

        if (s0 >= 0) regs.Write(s0, rs1);
        if (s1 >= 0) regs.Write(s1, rs2);
        if (s2 >= 0) regs.Write(s2, rs3);

        _memory.SetRequestPc(latch.Pc);
        ExecuteResult result = _executor.Execute(instr, _state, _memory);

        if (s0 >= 0) regs.Write(s0, save0);
        if (s1 >= 0) regs.Write(s1, save1);
        if (s2 >= 0) regs.Write(s2, save2);

        var newLatch = new ExMemLatch {
            IsValid = true,
            Pc = latch.Pc,
            InstrId = latch.InstrId,
            Instruction = instr,
            Result = result,
            DestinationRegister = latch.DestinationRegister,
            Rs1Value = rs1,
            Rs2Value = rs2,
            PredictedNextPc = latch.PredictedNextPc,
        };
        LastSent = newLatch;
    }
}