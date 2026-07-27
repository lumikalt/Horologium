#region

using Mechanism;
using Pipeline;
using RiscV32.Syscalls;

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
///         <c>activeHartCount</c> pre-allocates every hart's <see cref="IMechanism" />
///         and <see cref="IArchState" /> up front (mirroring every other constructor parameter,
///         which is always a fixed, known-at-construction-time array) but only the first
///         <c>activeHartCount</c> start out running — the rest are <em>dormant</em>
///         until <see cref="SpawnHart" /> activates one, which is how <c>clone()</c>
///         (<see cref="LinuxSyscallEmulator" />, wired to this kernel via
///         <see cref="LinuxSyscallEmulator.Spawner" />) brings a thread to life. This kernel does
///         not grow the hart count dynamically — the caller must pre-size <c>mechanisms</c> to at
///         least as many harts as the workload will ever spawn; <see cref="SpawnHart" /> throws if
///         every pre-allocated slot is already active.
///     </para>
///     <para>
///         <c>futex(FUTEX_WAIT)</c> blocking (<see cref="LinuxSyscallEmulator" />) uses no separate
///         "parked" state: a blocked hart's <c>ecall</c> returns <see cref="ExecuteResult.RequestBlock" />,
///         and <see cref="StepHart" /> simply returns without advancing PC, so the same instruction is
///         re-decoded and re-executed next tick until the futex word no longer matches the expected
///         value. This is sound at this kernel's per-instruction round-robin granularity: every write
///         to the futex word from any hart happens on some tick before the waiter's next poll, so no
///         store can be missed between checks.
///     </para>
/// </summary>
public sealed class MultiHartKernel : IHartSpawner {
    private readonly bool[] _dormant;
    private readonly bool[] _halted;
    private readonly IMemory[] _hartMemory;
    private readonly IMechanism[] _mechanisms;
    private readonly ICommitObserver?[] _observers;
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
        _observers = new ICommitObserver?[mechanisms.Length];

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
        _observers = new ICommitObserver?[mechanisms.Length];

        for (var i = 0; i < mechanisms.Length; i++) _states[i] = mechanisms[i].CreateArchState();
    }

    /// <summary>Every hart starts active — the pre-<c>clone()</c>-support constructor shape.</summary>
    public MultiHartKernel(IMemory[] perHartMemory, params IMechanism[] mechanisms)
        : this(perHartMemory, mechanisms.Length, mechanisms) { }

    public long Ticks { get; private set; }
    public int HartCount => _mechanisms.Length;

    /// <inheritdoc />
    public int SpawnHart(IArchState initialState) {
        int slot = Array.IndexOf(_dormant, true);
        if (slot < 0)
            throw new InvalidOperationException(
                "MultiHartKernel.SpawnHart: no dormant hart slots remain — pre-allocate more mechanisms " +
                "than the workload's peak thread count."
            );

        _states[slot] = initialState;
        _dormant[slot] = false;
        _halted[slot] = false;
        return slot;
    }

    private static bool[] BuildDormantFlags(int hartCount, int activeHartCount) {
        if (activeHartCount < 0 || activeHartCount > hartCount)
            throw new ArgumentOutOfRangeException(
                nameof(activeHartCount), activeHartCount, $"Must be between 0 and hart count ({hartCount})."
            );

        var dormant = new bool[hartCount];
        for (int i = activeHartCount; i < hartCount; i++) dormant[i] = true;
        return dormant;
    }

    /// <summary>Returns the live architectural state of the given hart.</summary>
    public IArchState StateOf(int hartId) => _states[hartId];

    /// <summary>True if hart <paramref name="hartId" /> is pre-allocated but not yet spawned.</summary>
    public bool IsDormant(int hartId) => _dormant[hartId];

    /// <summary>True if hart <paramref name="hartId" /> has halted (exit/ebreak/self-loop).</summary>
    public bool IsHalted(int hartId) => _halted[hartId];

    /// <summary>
    ///     True if hart <paramref name="hartId" /> was actually stepping as of the last <see cref="Step" />
    ///     — neither still dormant (never <c>clone()</c>d into) nor already halted. A checkpoint captured
    ///     at a region boundary must only drive detailed-pipeline trains for harts that are live at that
    ///     instant; a dormant hart's <see cref="IArchState" /> is whatever <see cref="IMechanism.CreateArchState" />
    ///     produced at construction (PC 0, all-zero registers) and was never touched, so restoring and
    ///     running it fetches and executes whatever bytes happen to sit at that stale PC as if it were
    ///     real code.
    /// </summary>
    public bool IsActive(int hartId) => !_dormant[hartId] && !_halted[hartId];

    public void SetEntryPoint(int hartId, ulong entryPoint) =>
        _states[hartId].Pc = entryPoint;

    /// <summary>
    ///     Attaches a per-hart commit observer (e.g. <see cref="MultiHartLoopPointProfiler.HartObserver" />),
    ///     notified for every instruction hart <paramref name="hartId" /> commits — the same commit
    ///     semantics <c>SingleCycleTrain</c> already guarantees (not called for halt, trap, or
    ///     trap-return).
    /// </summary>
    public void SetObserver(int hartId, ICommitObserver observer) => _observers[hartId] = observer;

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

        if (result.RequestBlock) return; // futex(FUTEX_WAIT) still blocked — retry the same ecall next tick

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
            _observers[hartId]?.OnCommit(pc, instr.RawEncoding, state);
        }

        state.OnRetire();

        if (result.RequestHalt) {
            _halted[hartId] = true;
            if (result.RequestHaltAll) Array.Fill(_halted, true); // SYS_exit_group — whole process exits
            return;
        }

        if (state.Pc == pc && instr.Class == ToothClass.Branch) _halted[hartId] = true;
    }
}