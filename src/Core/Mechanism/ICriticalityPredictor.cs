namespace Mechanism;

/// <summary>
///     Which node of an instruction's 3-node dependence-graph representation
///     (Fields, Rubin &amp; Bodík, ISCA 2001) a critical-path edge originates from.
/// </summary>
public enum CpNode : byte {
    /// <summary>Dispatch / window-entry node.</summary>
    D,

    /// <summary>Execute node.</summary>
    E,

    /// <summary>Commit node.</summary>
    C,
}

/// <summary>
///     Resolved last-arriving source for each of a committing instruction's three
///     dependence-graph nodes (Table 2 of Fields, Rubin &amp; Bodík, ISCA 2001).
///     <para>
///         The pipeline (which knows about ROB stalls, branch redirects, and CDB
///         arrival order) resolves the paper's rules into concrete node/instruction
///         references; the predictor only does token-array bookkeeping and training.
///     </para>
/// </summary>
public readonly struct CriticalityCommitInfo {
    /// <summary>Monotonic per-instruction age of the committing instruction.</summary>
    public required ulong InstrId { get; init; }

    /// <summary>Program counter of the committing instruction.</summary>
    public required ulong Pc { get; init; }

    /// <summary>D_{i-1} (default), E_{i-1} (post-misprediction redirect), or C_{i-w} (ROB stall).</summary>
    public required CpNode DSourceNode { get; init; }

    /// <summary>InstrId of the D-source instruction.</summary>
    public required ulong DSourceInstrId { get; init; }

    /// <summary>D_i (all operands ready at dispatch) or E_j (producer of the last-arriving operand).</summary>
    public required CpNode ESourceNode { get; init; }

    /// <summary>InstrId of the E-source instruction.</summary>
    public required ulong ESourceInstrId { get; init; }

    /// <summary>E_i (own execute delayed commit) or C_{i-1} (in-order commit was the bottleneck).</summary>
    public required CpNode CSourceNode { get; init; }

    /// <summary>InstrId of the C-source instruction.</summary>
    public required ulong CSourceInstrId { get; init; }
}

/// <summary>
///     Token-passing critical-path predictor (Fields, Rubin &amp; Bodík, "Focusing Processor
///     Policies via Critical-Path Prediction", ISCA 2001).
///     <para>
///         Queried at Issue to bias scheduling toward predicted-critical instructions when
///         there is functional-unit or port contention; trained at Commit from the resolved
///         dependence-graph sources of each retiring instruction. Purely a scheduling-priority
///         hint — it must never affect committed architectural results.
///     </para>
/// </summary>
public interface ICriticalityPredictor {
    /// <summary>Predicts whether the instruction at <paramref name="pc" /> lies on the critical path.</summary>
    bool PredictCritical(ulong pc);

    /// <summary>Trains the predictor from a retiring instruction's resolved dependence-graph sources.</summary>
    void OnCommit(in CriticalityCommitInfo info);
}