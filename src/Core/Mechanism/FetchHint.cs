namespace Mechanism;

/// <summary>
///     Lightweight pre-decode result used by the fetch stage to classify an instruction
///     before full decode, without embedding ISA opcode knowledge in the pipeline.
/// </summary>
public readonly record struct FetchHint {
    /// <summary>Initializes a <see cref="FetchHint" /> with default values (4-byte, non-branch).</summary>
    public FetchHint() { }

    /// <summary>Size of the instruction in bytes.</summary>
    public int InstructionSize { get; init; } = 4;

    /// <summary>True if this instruction may change control flow (branch, jump, call, return).</summary>
    public bool IsBranch { get; init; }

    /// <summary>True if this is a call that pushes a return address (used to push RAS).</summary>
    public bool IsCall { get; init; }

    /// <summary>True if this is a return that pops a return address (used to pop RAS).</summary>
    public bool IsReturn { get; init; }

    /// <summary>
    ///     True if this branch is unconditional (jump, call, or return) — always taken.
    ///     The fetch stage resolves direct unconditional branches straight to
    ///     <see cref="BranchTarget" /> without consulting the direction predictor; only
    ///     conditional branches (and indirect targets) need the predictor.
    /// </summary>
    public bool IsUnconditional { get; init; }

    /// <summary>
    ///     Statically decoded branch target (PC + offset) for PC-relative instructions.
    ///     HasValue=false for register-indirect branches (e.g. JALR) where the target is unknown at fetch time.
    /// </summary>
    public (ulong Value, bool HasValue) BranchTarget { get; init; }
}