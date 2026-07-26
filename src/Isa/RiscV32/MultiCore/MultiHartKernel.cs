#region

using Mechanism;

#endregion

namespace RiscV32.MultiCore;

/// <summary>
///     Drives N RISC-V harts round-robin against a shared physical memory.
///     Each call to <see cref="Step" /> advances every non-halted, non-dormant hart by one
///     instruction.
///     <para>
///         Callers are responsible for wrapping the shared backing memory in
///         <c>ReservationAwareMemory</c> and wiring each mechanism (<c>Rv32Mechanism</c>,
///         <c>Rv64Mechanism</c>, ...) with the same <c>ReservationTable</c> so that LR/SC
///         sequences are correctly cross-invalidated across harts.
///     </para>
///     <para>
///         This kernel operates entirely in physical address space. It does not apply
///         any fetch translation or cache hierarchy — it is suited for bare-metal
///         multi-hart workloads where harts share a flat physical memory.
///     </para>
///     <para>
///         <paramref name="activeHartCount" /> pre-allocates every hart's <see cref="IMechanism" />
///         and <see cref="IArchState" /> up front (mirroring every other constructor parameter,
///         which is always a fixed, known-at-construction-time array) but only the first
///         <paramref name="activeHartCount" /> start out running — the rest are <em>dormant</em>
///         until <see cref="SpawnHart" /> activates one, which is how <c>clone()</c>
///         (<see cref="LinuxSyscallEmulator" />, wired to this kernel via
///         <see cref="LinuxSyscallEmulator.Spawner" />) brings a thread to life. This kernel does
///         not grow the hart count dynamically — the caller must pre-size <c>mechanisms</c> to at
///         least as many harts as the workload will ever spawn; <see cref="SpawnHart" /> throws if
///         every pre-allocated slot is already active.
///     </para>
/// </summary>
public sealed class MultiHartKernel : IHartSpawner {
    private readonly bool[] _dormant;
    private readonly bool[] _halted;
    private readonly IMemory[] _hartMemory;
    private readonly IMechanism[] _mechanisms;
    private readonly IArchState[] _states;

    public MultiHartKernel(IMemory sharedMemory, int activeHartCount, params IMechanism[] mechanisms) {
        ArgumentNullException.ThrowIfNull(sharedMemory);
        if (mechanisms.Length == 0) throw new ArgumentException("At least one mechanism required.", nameof(mechanisms));

        _mechanisms = mechanisms;
        _states = new IArchState[mechanisms.Length];
        _halted = new bool[mechanisms.Length];
        _dormant = BuildDormantFlags(mechanisms.Length, activeHartCount);
        _hartMemory = new IMemory[mechanisms.Length];
        Array.Fill(_hartMemory, sharedMemory);

        for (var i = 0; i < mechanisms.Length; i++) _states[i] = mechanisms[i].CreateArchState();
    }

    /// <summary>Every hart starts active — the pre-<c>clone()</c>-support constructor shape.</summary>
    public MultiHartKernel(IMemory sharedMemory, params IMechanism[] mechanisms)
        : this(sharedMemory, mechanisms.Length, mechanisms) { }

    /// <summary>
    ///     Per-hart memory overload: each hart fetches and accesses its own <see cref="IMemory" />
    ///     (e.g. a <see cref="Orrery.Cache.MoesifCache" /> backed by a shared <see cref="Orrery.Cache.MoesifBus" />).
    ///     <paramref name="perHartMemory" /> must have the same length as <paramref name="mechanisms" />.
    /// </summary>
    public MultiHartKernel(IMemory[] perHartMemory, int activeHartCount, params IMechanism[] mechanisms) {
        ArgumentNullException.ThrowIfNull(perHartMemory);
        if (mechanisms.Length == 0) throw new ArgumentException("At least one mechanism required.", nameof(mechanisms));
        if (perHartMemory.Length != mechanisms.Length)
            throw new ArgumentException("perHartMemory.Length must equal mechanisms.Length.", nameof(perHartMemory));

        _mechanisms = mechanisms;
        _states = new IArchState[mechanisms.Length];
        _halted = new bool[mechanisms.Length];
        _dormant = BuildDormantFlags(mechanisms.Length, activeHartCount);
        _hartMemory = perHartMemory;

        for (var i = 0; i < mechanisms.Length; i++) _states[i] = mechanisms[i].CreateArchState();
    }

    /// <summary>Every hart starts active — the pre-<c>clone()</c>-support constructor shape.</summary>
    public MultiHartKernel(IMemory[] perHartMemory, params IMechanism[] mechanisms)
        : this(perHartMemory, mechanisms.Length, mechanisms) { }

    private static bool[] BuildDormantFlags(int hartCount, int activeHartCount) {
        if (activeHartCount < 0 || activeHartCount > hartCount) {
            throw new ArgumentOutOfRangeException(
                nameof(activeHartCount), activeHartCount, $"Must be between 0 and hart count ({hartCount})."
            );
        }

        var dormant = new bool[hartCount];
        for (var i = activeHartCount; i < hartCount; i++) dormant[i] = true;
        return dormant;
    }

    public long Ticks { get; private set; }
    public int HartCount => _mechanisms.Length;

    /// <summary>Returns the live architectural state of the given hart.</summary>
    public IArchState StateOf(int hartId) => _states[hartId];

    /// <summary>True if hart <paramref name="hartId" /> is pre-allocated but not yet spawned.</summary>
    public bool IsDormant(int hartId) => _dormant[hartId];

    public void SetEntryPoint(int hartId, ulong entryPoint) =>
        _states[hartId].Pc = entryPoint;

    /// <inheritdoc />
    public int SpawnHart(IArchState initialState) {
        int slot = Array.IndexOf(_dormant, true);
        if (slot < 0) {
            throw new InvalidOperationException(
                "MultiHartKernel.SpawnHart: no dormant hart slots remain — pre-allocate more mechanisms " +
                "than the workload's peak thread count."
            );
        }

        _states[slot] = initialState;
        _dormant[slot] = false;
        _halted[slot] = false;
        return slot;
    }

    /// <summary>
    ///     Advance every non-halted, non-dormant hart by one instruction.
    ///     Returns the number of harts that remain active after this tick.
    /// </summary>
    public int Step() {
        var active = 0;
        for (var id = 0; id < _mechanisms.Length; id++) {
            if (_halted[id] || _dormant[id]) continue;
            StepHart(id);
            if (!_halted[id]) active++;
        }

        Ticks++;
        return active;
    }

    /// <summary>Step until all harts are halted or <paramref name="maxTicks" /> is reached.</summary>
    public void Run(long maxTicks = 1_000_000) {
        while (Ticks < maxTicks && Step() > 0) { }
    }

    private void StepHart(int hartId) {
        IMechanism mech = _mechanisms[hartId];
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