#region

using Mechanism;
using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

#endregion

namespace Pipeline.Stages;

public sealed class ExecuteStage : Gear {
    private readonly IExecutor _executor;
    private readonly ForwardingOverlay _forwardOverlay = new();
    private readonly HazardUnit _hazard;
    private readonly IMemory _memory;
    private readonly IArchState _state;

    private IdExLatch _current = IdExLatch.Bubble;
    private IReadOnlyList<PipelineResident> _forwardProviders = [];

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

    public InArbor<IdExLatch> Input { get; }
    public OutArbor<ExMemLatch> Output { get; }

    public ExMemLatch LastSent { get; private set; } = ExMemLatch.Bubble;

    // Set for one cycle when a branch resolves taken: the instruction currently
    // in EX is on the wrong path (fetched after the branch) and must be killed.
    public bool Squash { get; set; }

    // The pipeline controller pushes forwarding providers each tick (oldest-first).
    public void SetForwardingContext(IReadOnlyList<PipelineResident> providers) { _forwardProviders = providers; }

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

        int s0 = instr.SourceRegisters.Count > 0 ? instr.SourceRegisters[0] : -1;
        int s1 = instr.SourceRegisters.Count > 1 ? instr.SourceRegisters[1] : -1;
        int s2 = instr.SourceRegisters.Count > 2 ? instr.SourceRegisters[2] : -1;

        // Shadow the forwarded operands over the real register file for the duration of this
        // Execute call, rather than physically writing them in and clobber-restoring afterward.
        // The real regfile is never mutated for forwarding bookkeeping — Writeback still owns
        // the only real write, later, at commit.
        _forwardOverlay.Rewire(regs, s0, rs1, s1, rs2, s2, rs3);
        _state.IntegerRegisters = _forwardOverlay;
        _memory.SetRequestPc(latch.Pc);
        ExecuteResult result;
        try { result = _executor.Execute(instr, _state, _memory); }
        finally { _state.IntegerRegisters = regs; }

        // A blocking syscall (e.g. futex(FUTEX_WAIT)) that hasn't cleared: result carries
        // RequestBlock through to WB via ExMemLatch/MemWbLatch unchanged — WritebackStage
        // (WriteBack.cs) is what actually redirects Fetch back to this instruction's own Pc
        // instead of retiring it (see WritebackStage.BlockRedirect and the squash-and-refetch
        // logic in FiveStageTrain.RunCycle).
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

    /// <summary>
    ///     Redirects reads of up to three register indices to captured forwarded values for the
    ///     duration of one <see cref="IExecutor.Execute" /> call. Writes always pass through to
    ///     the real register file untouched (the executor never writes registers directly — see
    ///     <see cref="IExecutor" />). Reused across cycles via <see cref="Rewire" /> rather than
    ///     reallocated, since it replaces a per-cycle save/write/execute/restore dance rather than
    ///     adding one.
    /// </summary>
    private sealed class ForwardingOverlay : IRegisterFile {
        private IRegisterFile _inner = null!;
        private int _r0 = -1;
        private int _r1 = -1;
        private int _r2 = -1;
        private ulong _v0;
        private ulong _v1;
        private ulong _v2;

        public int Count => _inner.Count;
        public int Width => _inner.Width;

        // Checked highest-index-first so that a repeated register index (e.g. `add x1, x1, x1`,
        // or a repeated R4-type FMADD source) resolves the same way the old write-in-order
        // clobber did: the last write wins.
        public ulong Read(int index) =>
            index == _r2 ? _v2 : index == _r1 ? _v1 : index == _r0 ? _v0 : _inner.Read(index);

        public void Write(int index, ulong value) => _inner.Write(index, value);
        public void Reset() => _inner.Reset();

        public void Rewire(IRegisterFile inner, int r0, ulong v0, int r1, ulong v1, int r2, ulong v2) {
            _inner = inner;
            (_r0, _v0, _r1, _v1, _r2, _v2) = (r0, v0, r1, v1, r2, v2);
        }
    }
}