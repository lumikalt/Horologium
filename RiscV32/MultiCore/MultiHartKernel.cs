using Mechanism;

namespace RiscV32.MultiCore;

/// <summary>
/// Drives N RISC-V harts round-robin against a shared physical memory.
/// Each call to <see cref="Step"/> advances every non-halted hart by one instruction.
/// <para>
/// Callers are responsible for wrapping the shared backing memory in
/// <c>ReservationAwareMemory</c> and wiring each <see cref="Rv32Mechanism"/>
/// with the same <c>ReservationTable</c> so that LR/SC sequences are correctly
/// cross-invalidated across harts.
/// </para>
/// <para>
/// This kernel operates entirely in physical address space. It does not apply
/// any fetch translation or cache hierarchy — it is suited for bare-metal
/// multi-hart workloads where harts share a flat physical memory.
/// </para>
/// </summary>
public sealed class MultiHartKernel {
    private readonly Rv32Mechanism[] _mechanisms;
    private readonly IArchState[] _states;
    private readonly IMemory[] _hartMemory;
    private readonly bool[] _halted;

    public long Ticks { get; private set; }
    public int HartCount => _mechanisms.Length;

    /// <summary>Returns the live architectural state of the given hart.</summary>
    public IArchState StateOf(int hartId) => _states[hartId];

    public MultiHartKernel(IMemory sharedMemory, params Rv32Mechanism[] mechanisms) {
        ArgumentNullException.ThrowIfNull(sharedMemory);
        if (mechanisms.Length == 0) throw new ArgumentException("At least one mechanism required.", nameof(mechanisms));

        _mechanisms = mechanisms;
        _states = new IArchState[mechanisms.Length];
        _halted = new bool[mechanisms.Length];
        _hartMemory = new IMemory[mechanisms.Length];
        Array.Fill(_hartMemory, sharedMemory);

        for (var i = 0; i < mechanisms.Length; i++) _states[i] = mechanisms[i].CreateArchState();
    }

    /// <summary>
    /// Per-hart memory overload: each hart fetches and accesses its own <see cref="IMemory"/>
    /// (e.g. a <see cref="Orrery.Cache.MoesiCache"/> backed by a shared <see cref="Orrery.Cache.MoesiBus"/>).
    /// <paramref name="perHartMemory"/> must have the same length as <paramref name="mechanisms"/>.
    /// </summary>
    public MultiHartKernel(IMemory[] perHartMemory, params Rv32Mechanism[] mechanisms) {
        ArgumentNullException.ThrowIfNull(perHartMemory);
        if (mechanisms.Length == 0) throw new ArgumentException("At least one mechanism required.", nameof(mechanisms));
        if (perHartMemory.Length != mechanisms.Length)
            throw new ArgumentException("perHartMemory.Length must equal mechanisms.Length.", nameof(perHartMemory));

        _mechanisms = mechanisms;
        _states = new IArchState[mechanisms.Length];
        _halted = new bool[mechanisms.Length];
        _hartMemory = perHartMemory;

        for (var i = 0; i < mechanisms.Length; i++) _states[i] = mechanisms[i].CreateArchState();
    }

    public void SetEntryPoint(int hartId, ulong entryPoint) =>
        _states[hartId].Pc = entryPoint;

    /// <summary>
    /// Advance every non-halted hart by one instruction.
    /// Returns the number of harts that remain active after this tick.
    /// </summary>
    public int Step() {
        var active = 0;
        for (var id = 0; id < _mechanisms.Length; id++) {
            if (_halted[id]) continue;
            StepHart(id);
            if (!_halted[id]) active++;
        }

        Ticks++;
        return active;
    }

    /// <summary>Step until all harts are halted or <paramref name="maxTicks"/> is reached.</summary>
    public void Run(long maxTicks = 1_000_000) {
        while (Ticks < maxTicks && Step() > 0) { }
    }

    private void StepHart(int hartId) {
        Rv32Mechanism mech = _mechanisms[hartId];
        IArchState state = _states[hartId];
        IMemory memory = _hartMemory[hartId];
        ulong pc = state.Pc;

        ITooth instr;
        try { instr = mech.Decoder.Decode(pc, memory); }
        catch (IllegalInstructionException ex) {
            state.Pc = mech.TrapController.RaiseTrap(
                new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), state
            );
            return;
        }

        ExecuteResult result = mech.Executor.Execute(instr, state, memory);

        if (result.IsHalt) {
            _halted[hartId] = true;
            return;
        }

        if (result.HasTrap) { state.Pc = mech.TrapController.RaiseTrap(result.Trap!, state); }
        else if (result.IsReturnFromTrap) {
            state.Pc = mech.TrapController.ReturnFromTrap(result.ReturnPrivilege!.Value, state);
        }
        else {
            result.SideEffect?.Invoke(state);
            if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
                state.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);
            state.Pc = result is { BranchTaken: true, BranchTarget: not null, }
                ? result.BranchTarget.Value
                : pc + (ulong)instr.SizeBytes;
        }

        state.OnRetire();

        if (result.RequestHalt) {
            _halted[hartId] = true;
            return;
        }

        if (state.Pc == pc && instr.Class == ToothClass.Branch) _halted[hartId] = true;
    }
}