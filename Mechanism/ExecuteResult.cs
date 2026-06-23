namespace Mechanism;

/// <summary>
/// The result produced by IExecutor after executing one instruction.
/// Immutable — the executor returns a new result, never mutates state directly.
/// The pipeline applies the result to IArchState at the appropriate stage.
/// </summary>
public sealed record ExecuteResult {
    /// <summary>
    /// The value to write to the destination register,
    /// or null if this instruction does not write a register.
    /// </summary>
    public ulong? RegisterResult { get; init; }

    /// <summary>Whether a branch was taken.</summary>
    public bool BranchTaken { get; init; }

    /// <summary>
    /// The branch target address, if a branch was taken or a jump was executed.
    /// Null if control flow is sequential.
    /// </summary>
    public ulong? BranchTarget { get; init; }

    /// <summary>
    /// The trap raised by this instruction, or null if execution was clean.
    /// The pipeline passes this to ITrapController at commit.
    /// </summary>
    public TrapInfo? Trap { get; init; }

    /// <summary>True if this result carries a trap.</summary>
    public bool HasTrap => Trap is not null;

    /// <summary>Convenience: a clean result with no register write and sequential flow.</summary>
    public static ExecuteResult Clean => new();

    /// <summary>Convenience: a result that writes a register value.</summary>
    public static ExecuteResult WithResult(ulong value) =>
        new() { RegisterResult = value, };

    /// <summary>Convenience: a result that redirects control flow.</summary>
    public static ExecuteResult WithBranch(bool taken, ulong target) =>
        new() { BranchTaken = taken, BranchTarget = target, };

    /// <summary>Convenience: a result that raises a trap.</summary>
    public static ExecuteResult WithTrap(TrapInfo trap) =>
        new() { Trap = trap, };

    /// <summary>True if this instruction halts the simulation (e.g. EBREAK).</summary>
    public bool IsHalt { get; init; }

    /// <summary>True if this instruction returns from a trap (e.g. MRET).</summary>
    public bool IsReturnFromTrap { get; init; }

    /// <summary>The privilege level to return to when IsReturnFromTrap is true.</summary>
    public PrivilegeLevel? ReturnPrivilege { get; init; }

    /// <summary>
    /// Optional ISA-specific state mutation to apply at writeback (e.g. vector register write).
    /// Invoked by the pipeline after the standard register writeback.
    /// </summary>
    public Action<IArchState>? SideEffect { get; init; }
}

/// <summary>
/// Describes a trap (exception or interrupt) raised during execution.
/// </summary>
public sealed record TrapInfo(
    TrapCause Cause,
    ulong TrapValue,
    ulong Pc
);

/// <summary>The cause of a trap.</summary>
public enum TrapCause {
    // Exceptions
    InstructionAddressMisaligned = 0,
    InstructionAccessFault = 1,
    IllegalInstruction = 2,
    Breakpoint = 3,
    LoadAddressMisaligned = 4,
    LoadAccessFault = 5,
    StoreAddressMisaligned = 6,
    StoreAccessFault = 7,
    EnvironmentCallFromU = 8,
    EnvironmentCallFromS = 9,
    EnvironmentCallFromM = 11,
    InstructionPageFault = 12,
    LoadPageFault = 13,
    StorePageFault = 15,
}