namespace Mechanism;

/// <summary>
/// A branch predictor — pluggable policy for speculative fetch.
/// <para>
/// The predictor is queried at Fetch time and updated at Commit time.
/// It has no access to architectural state — it operates purely on
/// PC values and observed outcomes.
/// </para>
/// </summary>
public interface IBranchPredictor {
    /// <summary>
    /// Predicts whether the branch at <paramref name="pc"/> will be taken,
    /// and if so, what the target address will be.
    /// </summary>
    /// <param name="pc">Calculated program counter.</param>
    /// <param name="knownTarget">
    /// Statically decoded branch target for PC-relative instructions, or HasValue=false for
    /// register-indirect branches (JALR) where the target is unknown at fetch time.
    /// Dynamic predictors ignore this; static predictors use it to avoid a cold BTB miss.
    /// </param>
    BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default);

    /// <summary>
    /// Updates the predictor with the actual outcome of a branch.
    /// Called at commit, in program order.
    /// </summary>
    void Update(ulong pc, bool taken, ulong actualTarget);
}

/// <summary>
/// Optional extension for predictors that benefit from knowing which
/// instructions are vector operations and which branches are loop back-edges.
/// The pipeline checks for this interface and calls it when available.
/// </summary>
public interface IVectorAwareBranchPredictor : IBranchPredictor {
    /// <summary>
    /// Called at execute time for every vector instruction.
    /// <paramref name="pc"/> is the instruction address.
    /// </summary>
    void NotifyVectorInstruction(ulong pc);

    /// <summary>
    /// Called at execute time for every taken backward conditional branch
    /// (a loop back-edge). Provides the branch PC, the taken target
    /// (= loop head), and the two comparison-register values so the
    /// Loop Monitor can estimate remaining iterations.
    /// </summary>
    void NotifyLoopBranchExecute(ulong branchPc, ulong loopTarget, ulong rs1, ulong rs2);
}

/// <summary>
/// The prediction made for a branch instruction.
/// </summary>
public readonly record struct BranchPrediction(
    bool PredictedTaken,
    ulong PredictedTarget
) {
    /// <summary>Convenience: predict not taken, sequential PC.</summary>
    public static BranchPrediction NotTaken(ulong sequentialPc) =>
        new(false, sequentialPc);

    /// <summary>Convenience: predict taken to a specific target.</summary>
    public static BranchPrediction Taken(ulong target) =>
        new(true, target);
}