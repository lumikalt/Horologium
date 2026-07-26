#region

using Mechanism;

#endregion

namespace Pipeline;

/// <summary>
///     The IF/ID pipeline register.
///     Carries a fetched instruction word into the Decode stage.
/// </summary>
public readonly record struct IfIdLatch {
    public static readonly IfIdLatch Bubble = new();

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public uint RawEncoding { get; init; }
    public ulong InstrId { get; init; }

    // The PC Fetch speculatively went to after this instruction. Carried
    // downstream so EX can detect a misprediction once the branch resolves.
    public ulong PredictedNextPc { get; init; }

    // Non-null when Fetch detected a page fault; downstream stages propagate
    // this to WB without decoding or executing.
    public TrapInfo? PreTrap { get; init; }
}

/// <summary>
///     The ID/EX pipeline register.
///     Carries a fully decoded instruction into the Execute stage.
/// </summary>
public readonly record struct IdExLatch {
    public static readonly IdExLatch Bubble = new();

    // Required because DestinationRegister below has a field initializer: a struct with
    // field initializers must declare its parameterless constructor explicitly, otherwise
    // `default(IdExLatch)` and `new IdExLatch()` would silently disagree on its value.
    public IdExLatch() { }

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public ulong InstrId { get; init; }
    public ITooth? Instruction { get; init; }
    public ulong Rs1Value { get; init; }
    public ulong Rs2Value { get; init; }
    public ulong Rs3Value { get; init; } // R4-type (FMADD family) third source
    public int DestinationRegister { get; init; } = -1;
    public ulong PredictedNextPc { get; init; }

    // Propagated from IfIdLatch when a fetch page fault was detected.
    public TrapInfo? PreTrap { get; init; }
}

/// <summary>
///     The EX/MEM pipeline register.
///     Carries an executed result into the Memory stage.
/// </summary>
public readonly record struct ExMemLatch {
    public static readonly ExMemLatch Bubble = new();

    // See IdExLatch — required by the DestinationRegister field initializer below.
    public ExMemLatch() { }

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public ulong InstrId { get; init; }
    public ITooth? Instruction { get; init; }
    public ExecuteResult? Result { get; init; }
    public int DestinationRegister { get; init; } = -1;
    public ulong Rs1Value { get; init; } // first source (for loop-branch LM wiring)
    public ulong Rs2Value { get; init; } // second source / store data
    public ulong PredictedNextPc { get; init; }
}

/// <summary>
///     The MEM/WB pipeline register.
///     Carries a memory result into the Writeback stage.
/// </summary>
public readonly record struct MemWbLatch {
    public static readonly MemWbLatch Bubble = new();

    // See IdExLatch — required by the DestinationRegister field initializer below.
    public MemWbLatch() { }

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }

    public ulong InstrId { get; init; }

    // The actual committed next PC: branch target for taken branches/jumps,
    // PC+size otherwise. Used by WritebackStage to set state.Pc for interrupt
    // mepc precision.
    public ulong NextPc { get; init; }
    public ITooth? Instruction { get; init; }
    public ulong? WritebackValue { get; init; }
    public int DestinationRegister { get; init; } = -1;
    public bool HasTrap { get; init; }
    public TrapInfo? Trap { get; init; }
    public bool IsHalt { get; init; }
    public bool RequestHalt { get; init; }
    public bool IsReturnFromTrap { get; init; }
    public PrivilegeLevel? ReturnPrivilege { get; init; }
    public Action<IArchState>? SideEffect { get; init; }

    // A blocking syscall (e.g. futex(FUTEX_WAIT)) that hasn't cleared yet — see
    // ExecuteResult.RequestBlock. WritebackStage must not retire this instruction, apply its
    // SideEffect, or advance state.Pc; instead it redirects Fetch back to Pc (this same
    // instruction) so it's re-decoded and re-executed next time around, mirroring
    // MultiHartKernel's functional retry-in-place.
    public bool RequestBlock { get; init; }
}