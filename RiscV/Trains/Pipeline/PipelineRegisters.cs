using Mechanism;

namespace RiscV.Trains.Pipeline;

/// <summary>
/// The IF/ID pipeline register.
/// Carries a fetched instruction word into the Decode stage.
/// </summary>
public sealed record IfIdLatch {
    public static readonly IfIdLatch Bubble = new();

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public uint RawEncoding { get; init; }

    // The PC Fetch speculatively went to after this instruction. Carried
    // downstream so EX can detect a misprediction once the branch resolves.
    public ulong PredictedNextPc { get; init; }
}

/// <summary>
/// The ID/EX pipeline register.
/// Carries a fully decoded instruction into the Execute stage.
/// </summary>
public sealed record IdExLatch {
    public static readonly IdExLatch Bubble = new();

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public ITooth? Instruction { get; init; }
    public ulong Rs1Value { get; init; }
    public ulong Rs2Value { get; init; }
    public int DestinationRegister { get; init; } = -1;
    public ulong PredictedNextPc { get; init; }
}

/// <summary>
/// The EX/MEM pipeline register.
/// Carries an executed result into the Memory stage.
/// </summary>
public sealed record ExMemLatch {
    public static readonly ExMemLatch Bubble = new();

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public ITooth? Instruction { get; init; }
    public ExecuteResult? Result { get; init; }
    public int DestinationRegister { get; init; } = -1;
    public ulong Rs2Value { get; init; } // for stores
    public ulong PredictedNextPc { get; init; }
}

/// <summary>
/// The MEM/WB pipeline register.
/// Carries a memory result into the Writeback stage.
/// </summary>
public sealed record MemWbLatch {
    public static readonly MemWbLatch Bubble = new();

    public bool IsValid { get; init; }
    public ulong Pc { get; init; }
    public ITooth? Instruction { get; init; }
    public ulong? WritebackValue { get; init; }
    public int DestinationRegister { get; init; } = -1;
    public bool HasTrap { get; init; }
    public TrapInfo? Trap { get; init; }
}