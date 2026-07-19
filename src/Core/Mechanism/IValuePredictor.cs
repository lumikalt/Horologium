namespace Mechanism;

/// <summary>
///     A value predictor — pluggable policy for speculatively predicting a register value
///     before the instruction that produces it has executed (Lipasti &amp; Shen, MICRO 1996;
///     Perais &amp; Seznec, HPCA 2014).
///     <para>
///         The predictor is queried at Fetch time and updated at Commit time, in program
///         order — the predicted value is used to let dependent instructions issue and
///         execute early, but the producing instruction always executes for real too, so a
///         misprediction is caught (not merely mispredicted architecture) and recovered via a
///         pipeline squash. It has no access to architectural state — it operates purely on
///         PC values and observed outcomes.
///     </para>
/// </summary>
public interface IValuePredictor {
    /// <summary>
    ///     Predicts the value that the instruction at <paramref name="pc" /> will write to its
    ///     destination register, given <paramref name="history" /> — the predictor's own
    ///     speculative-history checkpoint captured at this same instruction's fetch (see
    ///     <see cref="CaptureHistory" />). Returns <c>false</c> (with <paramref name="value" />
    ///     undefined) when the predictor has no confident prediction — the instruction then
    ///     proceeds through the ordinary (non-speculative) dataflow path.
    ///     <para>
    ///         Passing the fetch-time checkpoint explicitly (rather than reading live internal
    ///         state) matters for history-based predictors: it guarantees that whichever slot this
    ///         call indexes is the exact same slot <see cref="Update" /> will later train for this
    ///         same dynamic instruction, even if the live speculative history has moved on by the
    ///         time this instruction reaches Commit (e.g. after other instructions' flush/refetch
    ///         cycles). A predictor with no history component (e.g. a tagless LVPT) ignores it.
    ///     </para>
    /// </summary>
    bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value);

    /// <summary>
    ///     Trains the predictor with the actual value produced by the instruction at
    ///     <paramref name="pc" />, indexed by <paramref name="history" /> — the <em>same</em>
    ///     fetch-time checkpoint passed to <see cref="TryPredict" /> for this dynamic instruction
    ///     (not a live or committed-shadow snapshot re-read at commit time, which could have
    ///     drifted from what was used to predict). Called at commit, in program order, for every
    ///     eligible instruction — regardless of whether its value was predicted, or predicted but
    ///     not used (low confidence): predictors must be trained on every outcome to converge.
    /// </summary>
    void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue);

    /// <summary>
    ///     Folds the <paramref name="predictedTaken" /> direction of a branch into the
    ///     predictor's speculative global history. Called at fetch, immediately after the
    ///     branch predictor's own <see cref="IBranchPredictor.SpeculativeHistoryUpdate" />, so a
    ///     history-based value predictor (e.g. VTAGE) indexes fresh history for younger
    ///     in-flight instructions rather than stale commit-only history.
    ///     <para>Default no-op: predictors that keep no global history need nothing here.</para>
    /// </summary>
    void OnBranchFetched(bool predictedTaken) { }

    /// <summary>
    ///     Discards wrong-path speculative history on a full pipeline flush, restoring the
    ///     working history to the last committed state. Default no-op. Pairs with
    ///     <see cref="OnBranchFetched" />.
    /// </summary>
    void RecoverSpeculativeHistory() { }

    /// <summary>
    ///     Captures a checkpoint of the predictor's speculative history <em>before</em> the
    ///     branch at the current fetch has folded its own predicted direction in. Called at
    ///     fetch for every fetched instruction (not just branches), immediately before
    ///     <see cref="OnBranchFetched" />, and stored per in-flight instruction: an out-of-order
    ///     train uses it both to recover exact history on an execute-time partial squash
    ///     (redirect at Execute rather than a full flush at commit) and to pass the same snapshot
    ///     to <see cref="TryPredict" /> and <see cref="Update" /> for a single dynamic instruction.
    ///     <para>Default returns <c>default</c>: predictors with no speculative history need nothing.</para>
    /// </summary>
    ValueHistoryCheckpoint CaptureHistory() => default;

    /// <summary>
    ///     Restores the predictor's speculative history to a branch's checkpoint and folds that
    ///     branch's <em>resolved</em> direction, so fetch resumes from the correct path with
    ///     history as-of-the-branch. Called once per partial squash, on the redirecting branch's
    ///     checkpoint.
    ///     <para>
    ///         The default falls back to <see cref="RecoverSpeculativeHistory" /> (restore to the
    ///         committed shadow): a predictor that keeps speculative history but does not
    ///         checkpoint per branch still drops its wrong-path bits rather than carrying them
    ///         onto the correct path — a prediction-accuracy approximation, never an
    ///         architectural inaccuracy (the producing instruction always executes for real).
    ///     </para>
    /// </summary>
    void RestoreHistory(in ValueHistoryCheckpoint checkpoint, bool actualTaken) =>
        RecoverSpeculativeHistory();
}

/// <summary>
///     A value-type snapshot of a value predictor's speculative history at one branch's fetch,
///     stored per in-flight branch for exact recovery on an execute-time partial squash. Unlike
///     <see cref="BranchHistoryCheckpoint" />, a value predictor keeps only a single global
///     shift register — no per-PC local-history component.
/// </summary>
public readonly record struct ValueHistoryCheckpoint(ulong Global = 0);