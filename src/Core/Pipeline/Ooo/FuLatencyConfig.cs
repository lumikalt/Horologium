#region

using Mechanism;

#endregion

namespace Pipeline.Ooo;

/// <summary>
///     Per-functional-unit issue count (parallel issue ports) and execution latency.
///     <para>
///         All functional units are modeled as fully pipelined: Count is the number of
///         independent issue ports for that class per cycle; Latency is the number of
///         cycles from issue until the result is broadcast on the CDB.
///     </para>
///     <para>
///         <b>LoadHitLatency</b> models the LSU pipeline depth on a cache hit (addr_calc →
///         MMU → cache_lookup → cache_read → complete = 4 cycles in Olympia). The MLP
///         miss-countdown adds the cache miss penalty on top of this base latency.
///         <b>LoadCount</b> and <b>StoreCount</b> are independent issue ports: a load and a store
///         may issue in the same cycle if both counters allow it (gem5 models this with separate
///         MemRead and MemWrite FU pools). Atomics share the load port (they occupy the LSU
///         read pipeline). <b>LoadStoreLatency</b> applies to stores and atomics only.
///         <b>BypassLatency</b> models the result-bypass network delay: cycles added between
///         a result broadcast and when a dependent instruction can issue. Real hardware
///         typically adds 1 cycle on the bypass path; 0 = zero-cycle (ideal) forwarding.
///         <b>ConservativeLoads</b> models Olympia's <c>allow_speculative_load_exec = false</c>:
///         a load may not issue while any older store in the SQ still has an unresolved
///         address. Matches store-to-load ordering policy in conservative LSU designs.
///         <b>DivLatency</b> overrides <b>MulDivLatency</b> for DIV/DIVU/REM/REMU instructions
///         specifically. 0 = inherit from MulDivLatency (default, backward-compatible).
///         Olympia models DIV at 23 cycles vs MUL at 3 cycles; setting DivLatency=23
///         enables that distinction without changing the MUL latency.
///     </para>
/// </summary>
public sealed record FuLatencyConfig(
    int IntAluCount = 2,
    int IntAluLatency = 1,
    int MulDivCount = 1,
    int MulDivLatency = 3,
    int DivLatency = 0,
    int LoadCount = 1,
    int StoreCount = 1,
    int LoadStoreLatency = 1,
    int LoadHitLatency = 1,
    int BranchCount = 1,
    int BranchLatency = 1,
    int FloatCount = 1,
    int FloatLatency = 4,
    int FloatDivSqrtCount = 1,
    int FloatDivSqrtLatency = 16,
    int SystemCount = 1,
    int SystemLatency = 1,
    int BypassLatency = 0,
    bool ConservativeLoads = false
) {
    public static FuLatencyConfig Default { get; } = new();

    /// <summary>
    ///     Maps a ToothClass to the issue-port counter slot used in StepIssue.
    ///     Classes that share a single FU pool (Atomic shares Load port; ConditionalBranch shares Branch)
    ///     must share the same slot so their combined issue count respects CountFor's limit.
    ///     Store has its own slot — it has an independent issue port (StoreCount) from loads.
    ///     Must be kept in sync with the groupings in CountFor.
    /// </summary>
    public static int BudgetSlot(ToothClass cls) => cls switch {
        ToothClass.Atomic            => (int)ToothClass.Load,
        ToothClass.ConditionalBranch => (int)ToothClass.Branch,
        _                            => (int)cls,
    };

    public int CountFor(ToothClass cls) => cls switch {
        ToothClass.IntegerAlu                             => IntAluCount,
        ToothClass.IntegerMulDiv                          => MulDivCount,
        ToothClass.Load or ToothClass.Atomic              => LoadCount,
        ToothClass.Store                                  => StoreCount,
        ToothClass.Branch or ToothClass.ConditionalBranch => BranchCount,
        ToothClass.FloatingPoint                          => FloatCount,
        ToothClass.FloatDivSqrt                           => FloatDivSqrtCount,
        ToothClass.Uve                                    => 1,
        _                                                 => SystemCount,
    };

    public int LatencyFor(ToothClass cls) => cls switch {
        ToothClass.IntegerAlu                             => IntAluLatency,
        ToothClass.IntegerMulDiv                          => MulDivLatency,
        ToothClass.Load                                   => LoadHitLatency,
        ToothClass.Store or ToothClass.Atomic             => LoadStoreLatency,
        ToothClass.Branch or ToothClass.ConditionalBranch => BranchLatency,
        ToothClass.FloatingPoint                          => FloatLatency,
        ToothClass.FloatDivSqrt                           => FloatDivSqrtLatency,
        ToothClass.Uve                                    => 1,
        _                                                 => SystemLatency,
    };

    // Instruction-aware overload: uses DivLatency for DIV/REM when it is set.
    public int LatencyFor(ITooth tooth) =>
        tooth is { Class: ToothClass.IntegerMulDiv, IsDiv: true, } && DivLatency > 0
            ? DivLatency
            : LatencyFor(tooth.Class);
}