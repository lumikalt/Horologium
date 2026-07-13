namespace Mechanism;

/// <summary>
///     A branch predictor — pluggable policy for speculative fetch.
///     <para>
///         The predictor is queried at Fetch time and updated at Commit time.
///         It has no access to architectural state — it operates purely on
///         PC values and observed outcomes.
///     </para>
/// </summary>
public interface IBranchPredictor {
    /// <summary>
    ///     Predicts whether the branch at <paramref name="pc" /> will be taken,
    ///     and if so, what the target address will be.
    /// </summary>
    /// <param name="pc">Calculated program counter.</param>
    /// <param name="knownTarget">
    ///     Statically decoded branch target for PC-relative instructions, or HasValue=false for
    ///     register-indirect branches (JALR) where the target is unknown at fetch time.
    ///     Dynamic predictors ignore this; static predictors use it to avoid a cold BTB miss.
    /// </param>
    BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default);

    /// <summary>
    ///     Updates the predictor with the actual outcome of a branch.
    ///     Called at commit, in program order.
    /// </summary>
    void Update(ulong pc, bool taken, ulong actualTarget);

    /// <summary>
    ///     Folds the <paramref name="predictedTaken" /> direction of the branch at
    ///     <paramref name="pc" /> into the predictor's speculative global history. Called at
    ///     fetch, immediately after <see cref="Predict" />, so that younger in-flight branches
    ///     index fresh history instead of stale commit-only history — the difference that lets
    ///     a history-based predictor track tight recursion and loops inside the ROB window.
    ///     <para>
    ///         Default no-op: predictors that keep no global history, and in-order pipelines that
    ///         never call this, retain the commit-time history behaviour unchanged.
    ///     </para>
    /// </summary>
    void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) { }

    /// <summary>
    ///     Discards wrong-path speculative history on a pipeline flush, restoring the working
    ///     history to the last committed state. Default no-op. Pairs with
    ///     <see cref="SpeculativeHistoryUpdate" />: an out-of-order train calls this from its
    ///     flush handler after <see cref="Update" /> has applied the redirecting branch's true
    ///     outcome, so fetch resumes with correct history.
    /// </summary>
    void RecoverSpeculativeHistory() { }

    /// <summary>
    ///     Captures a checkpoint of the predictor's speculative history <em>before</em> the branch
    ///     at the current fetch has folded its own predicted direction in. Called at fetch,
    ///     immediately before <see cref="SpeculativeHistoryUpdate" />, and stored per in-flight
    ///     branch so an out-of-order train can recover exact history on an execute-time partial
    ///     squash (redirect at Execute rather than a full flush at commit).
    ///     <para>Default returns <c>default</c>: predictors with no speculative history need nothing.</para>
    /// </summary>
    BranchHistoryCheckpoint CaptureHistory(ulong pc) => default;

    /// <summary>
    ///     Rewinds one branch's per-PC <em>local</em> history entry to its pre-branch value, used
    ///     when walking the squashed (younger-than-redirect) branches youngest-to-oldest during a
    ///     partial squash. Global history is a single register recovered once via
    ///     <see cref="RestoreHistory" />, so this touches only local tables. Default no-op.
    /// </summary>
    void RestoreLocalEntry(in BranchHistoryCheckpoint checkpoint) { }

    /// <summary>
    ///     Restores the predictor's speculative history to the redirecting branch's checkpoint and
    ///     folds that branch's <em>resolved</em> direction, so fetch resumes from the correct path
    ///     with history as-of-the-branch. Called once per partial squash, after the younger branches
    ///     have been rewound via <see cref="RestoreLocalEntry" />.
    ///     <para>
    ///         The default falls back to <see cref="RecoverSpeculativeHistory" /> (restore to the committed
    ///         shadow): a predictor that keeps speculative history but does not checkpoint per branch still
    ///         drops its wrong-path bits rather than carrying them onto the correct path — a
    ///         prediction-accuracy approximation, never an architectural inaccuracy. Predictors that
    ///         checkpoint (the TAGE family and Tournament) override this for exact as-of-the-branch recovery.
    ///     </para>
    /// </summary>
    void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) =>
        RecoverSpeculativeHistory();
}

/// <summary>
///     A value-type snapshot of a predictor's speculative history at one branch's fetch, stored per
///     in-flight branch for exact recovery on an execute-time partial squash. <see cref="Global" />
///     is the global shift-register value before the branch folded its direction; <see cref="LocalIdx" />
///     / <see cref="LocalValue" /> capture the single per-PC local-history entry the branch indexed
///     (<see cref="LocalIdx" /> = -1 when the predictor keeps no local history).
/// </summary>
public readonly record struct BranchHistoryCheckpoint(
    ulong Global = 0,
    int LocalIdx = -1,
    ulong LocalValue = 0
);

/// <summary>
///     Optional extension for predictors that benefit from knowing which
///     instructions are vector operations and which branches are loop back-edges.
///     The pipeline checks for this interface and calls it when available.
/// </summary>
public interface IVectorAwareBranchPredictor : IBranchPredictor {
    /// <summary>
    ///     Called at execute time for every vector instruction.
    ///     <paramref name="pc" /> is the instruction address.
    /// </summary>
    void NotifyVectorInstruction(ulong pc);

    /// <summary>
    ///     Called at execute time for every taken backward conditional branch
    ///     (a loop back-edge). Provides the branch PC, the taken target
    ///     (= loop head), and the two comparison-register values so the
    ///     Loop Monitor can estimate remaining iterations.
    /// </summary>
    void NotifyLoopBranchExecute(ulong branchPc, ulong loopTarget, ulong rs1, ulong rs2);
}

/// <summary>
///     Optional extension for predictors that correlate branch outcomes with
///     register or load values produced by other instructions, rather than
///     (or in addition to) branch history. The pipeline checks for this
///     interface and calls it when available.
/// </summary>
public interface IValueAwareBranchPredictor : IBranchPredictor {
    /// <summary>
    ///     Called at execute time for every instruction that writes an integer
    ///     destination register, giving the architectural register index and
    ///     the produced value. <paramref name="isLoad" /> distinguishes values
    ///     that came from memory (a load) from values computed by ALU/branch/etc.,
    ///     since some predictors correlate specifically on load values.
    ///     <para>
    ///         Fires for every dynamic instance, including ones that later turn out
    ///         to be on the wrong (mispredicted) path — real hardware cannot tell the
    ///         difference at execute time either.
    ///     </para>
    /// </summary>
    void NotifyRegisterResult(ulong pc, int destReg, ulong value, bool isLoad);
}

/// <summary>
///     The prediction made for a branch instruction.
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