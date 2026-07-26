namespace Mechanism;

/// <summary>
///     The result produced by IExecutor after executing one instruction.
///     Immutable — the executor returns a new result, never mutates state directly.
///     The pipeline applies the result to IArchState at the appropriate stage.
/// </summary>
public sealed record ExecuteResult {
    /// <summary>
    ///     The value to write to the destination register,
    ///     or HasValue=false if this instruction does not write a register.
    /// </summary>
    public (ulong Value, bool HasValue) RegisterResult { get; init; }

    /// <summary>Whether a branch was taken.</summary>
    public bool BranchTaken { get; init; }

    /// <summary>
    ///     The branch target address, if a branch was taken or a jump was executed.
    ///     Null if control flow is sequential.
    /// </summary>
    public ulong? BranchTarget { get; init; }

    /// <summary>
    ///     The trap raised by this instruction, or null if execution was clean.
    ///     The pipeline passes this to ITrapController at commit.
    /// </summary>
    public TrapInfo? Trap { get; private init; }

    /// <summary>True if this result carries a trap.</summary>
    public bool HasTrap => Trap is not null;

    /// <summary>Convenience: a clean result with no register write and sequential flow.</summary>
    public static ExecuteResult Clean => new();

    /// <summary>True if this instruction halts the simulation (e.g. EBREAK).</summary>
    public bool IsHalt { get; init; }

    /// <summary>
    ///     True if the simulation should halt <em>after</em> this instruction commits
    ///     (in contrast to <see cref="IsHalt" />, which halts without committing the
    ///     instruction). Set by an HTIF tohost-exit store so the engine terminates at
    ///     the exit write itself rather than the spin-loop that conventionally follows
    ///     it. ISA-agnostic to the trains: they act on the flag without knowing why.
    /// </summary>
    public bool RequestHalt { get; init; }

    /// <summary>
    ///     True alongside <see cref="RequestHalt" /> when every hart of a multi-hart run should stop,
    ///     not just the calling one (e.g. <c>SYS_exit_group</c> — a real process-wide exit — as
    ///     opposed to <c>SYS_exit</c>, which ends only the calling thread). Single-hart drivers have
    ///     no other hart to distinguish, so <see cref="RequestHalt" /> alone already means "stop" for
    ///     them; only a multi-hart driver (<c>MultiHartKernel</c>) needs to act on this flag.
    /// </summary>
    public bool RequestHaltAll { get; init; }

    /// <summary>
    ///     True if this instruction has not completed and must be retried unchanged next tick
    ///     (e.g. a <c>futex(FUTEX_WAIT)</c> whose condition still holds). The pipeline must not
    ///     advance PC, apply <see cref="SideEffect" />, write <see cref="RegisterResult" />, or call
    ///     <c>IArchState.OnRetire</c> when this is set — the instruction contributes zero committed
    ///     work this tick, purely a scheduling artifact, so it must not appear in retired-instruction
    ///     counts or basic-block-vector profiling. ISA-agnostic to the trains: they act on the flag
    ///     without knowing why.
    /// </summary>
    public bool RequestBlock { get; init; }

    /// <summary>True if this instruction returns from a trap (e.g. MRET).</summary>
    public bool IsReturnFromTrap { get; init; }

    /// <summary>The privilege level to return to when IsReturnFromTrap is true.</summary>
    public PrivilegeLevel? ReturnPrivilege { get; init; }

    /// <summary>
    ///     Optional ISA-specific state mutation to apply at writeback (e.g. vector register write).
    ///     Invoked by the pipeline after the standard register writeback.
    /// </summary>
    public Action<IArchState>? SideEffect { get; init; }

    /// <summary>
    ///     Stream configuration command emitted by a stream-setup instruction (ss.*).
    ///     When non-null, the pipeline calls StreamingEngine.Configure with the given
    ///     stream ID and descriptor. Null for all non-stream-setup instructions.
    /// </summary>
    public (int StreamId, StreamDescriptor Descriptor)? StreamConfig { get; init; }

    /// <summary>
    ///     Per-instruction FU latency in cycles, reported by the unit that executed the
    ///     instruction — set by units with data-dependent timing (e.g. an RTL-backed
    ///     divider via <see cref="RtlFu.RtlBackedExecutor" />). When non-null, timing
    ///     pipelines use this instead of the static per-class FuLatencyConfig entry.
    ///     Null for units whose latency is fully described by the static config.
    /// </summary>
    public int? LatencyOverride { get; init; }

    /// <summary>Convenience: a result that writes a register value.</summary>
    public static ExecuteResult WithResult(ulong value) =>
        new() { RegisterResult = (value, true), };

    /// <summary>Convenience: a result that redirects control flow.</summary>
    public static ExecuteResult WithBranch(bool taken, ulong target) =>
        new() { BranchTaken = taken, BranchTarget = target, };

    /// <summary>Convenience: a result that raises a trap.</summary>
    public static ExecuteResult WithTrap(TrapInfo trap) =>
        new() { Trap = trap, };
}

/// <summary>
///     Describes a trap (exception or interrupt) raised during execution.
///     The numeric Cause value is ISA-defined; use ISA-specific constants (e.g. RvTrapCause)
///     to construct and interpret it.
/// </summary>
public sealed record TrapInfo(
    int Cause,
    ulong TrapValue,
    ulong Pc
);

/// <summary>ISA-agnostic trap cause codes. Numeric values match RISC-V mcause by convention.</summary>
public static class TrapCause {
    /// <summary>Instruction address not naturally aligned.</summary>
    public const int InstructionAddressMisaligned = 0;

    /// <summary>Instruction fetch from a faulting physical address.</summary>
    public const int InstructionAccessFault = 1;

    /// <summary>Unrecognised or privilege-violating instruction encoding.</summary>
    public const int IllegalInstruction = 2;

    /// <summary>EBREAK or debugger breakpoint hit.</summary>
    public const int Breakpoint = 3;

    /// <summary>Load address not naturally aligned.</summary>
    public const int LoadAddressMisaligned = 4;

    /// <summary>Load from a faulting physical address.</summary>
    public const int LoadAccessFault = 5;

    /// <summary>Store/AMO address not naturally aligned.</summary>
    public const int StoreAddressMisaligned = 6;

    /// <summary>Store/AMO to a faulting physical address.</summary>
    public const int StoreAccessFault = 7;

    /// <summary>Instruction fetch page fault (Sv32/Sv39 translation failure).</summary>
    public const int InstructionPageFault = 12;

    /// <summary>Load page fault (Sv32/Sv39 translation failure).</summary>
    public const int LoadPageFault = 13;

    /// <summary>Store/AMO page fault (Sv32/Sv39 translation failure).</summary>
    public const int StorePageFault = 15;
}