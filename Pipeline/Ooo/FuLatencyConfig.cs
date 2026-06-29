using Mechanism;

namespace Pipeline.Ooo;

/// <summary>
/// Per-functional-unit issue count (parallel issue ports) and execution latency.
///
/// All functional units are modeled as fully pipelined: Count is the number of
/// independent issue ports for that class per cycle; Latency is the number of
/// cycles from issue until the result is broadcast on the CDB.
///
/// <b>LoadStore latency</b> should remain 1 — the lump-sum cache-miss stall model
/// already owns miss penalty cycles, and giving loads a multi-cycle FU latency
/// would double-count the stall.
/// </summary>
public sealed record FuLatencyConfig(
    int IntAluCount = 2,
    int IntAluLatency = 1,
    int MulDivCount = 1,
    int MulDivLatency = 3,
    int LoadStoreCount = 1,
    int LoadStoreLatency = 1,
    int BranchCount = 1,
    int BranchLatency = 1,
    int FloatCount = 1,
    int FloatLatency = 4,
    int FloatDivSqrtCount = 1,
    int FloatDivSqrtLatency = 16,
    int SystemCount = 1,
    int SystemLatency = 1
) {
    public static FuLatencyConfig Default { get; } = new();

    /// <summary>
    /// Maps a ToothClass to the issue-port counter slot used in StepIssue.
    /// Classes that share a single FU pool (Load/Store/Atomic; Branch/ConditionalBranch)
    /// must share the same slot so their combined issue count respects CountFor's limit.
    /// Must be kept in sync with the groupings in CountFor.
    /// </summary>
    public static int BudgetSlot(ToothClass cls) => cls switch {
        ToothClass.Store or ToothClass.Atomic => (int)ToothClass.Load,
        ToothClass.ConditionalBranch          => (int)ToothClass.Branch,
        _                                     => (int)cls,
    };

    public int CountFor(ToothClass cls) => cls switch {
        ToothClass.IntegerAlu                                    => IntAluCount,
        ToothClass.IntegerMulDiv                                 => MulDivCount,
        ToothClass.Load or ToothClass.Store or ToothClass.Atomic => LoadStoreCount,
        ToothClass.Branch or ToothClass.ConditionalBranch        => BranchCount,
        ToothClass.FloatingPoint                                 => FloatCount,
        ToothClass.FloatDivSqrt                                  => FloatDivSqrtCount,
        ToothClass.Uve                                           => 1,
        _                                                        => SystemCount,
    };

    public int LatencyFor(ToothClass cls) => cls switch {
        ToothClass.IntegerAlu                                    => IntAluLatency,
        ToothClass.IntegerMulDiv                                 => MulDivLatency,
        ToothClass.Load or ToothClass.Store or ToothClass.Atomic => LoadStoreLatency,
        ToothClass.Branch or ToothClass.ConditionalBranch        => BranchLatency,
        ToothClass.FloatingPoint                                 => FloatLatency,
        ToothClass.FloatDivSqrt                                  => FloatDivSqrtLatency,
        ToothClass.Uve                                           => 1,
        _                                                        => SystemLatency,
    };
}