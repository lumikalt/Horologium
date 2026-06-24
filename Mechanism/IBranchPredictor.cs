namespace Mechanism;

/// <summary>
/// A branch predictor — pluggable policy for speculative fetch.
///
/// The predictor is queried at Fetch time and updated at Commit time.
/// It has no access to architectural state — it operates purely on
/// PC values and observed outcomes.
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