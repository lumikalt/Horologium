namespace Mechanism.BranchPredictModels;

/// <summary>
///     Wraps <see cref="CbpNgFfiPredictor" /> so a harcom-based CBP2025/CBP-NG submission can be
///     driven safely from a pipeline with many concurrently outstanding, unresolved predictions
///     (<c>OooeTrain</c>'s ROB window, or <c>FiveStageTrain</c>'s IF/ID/EX overlap) — see README.md
///     "Extend CBP2025/CBP-NG integration to OoOE".
///     <para>
///         Harcom's <c>reg</c>/<c>ram</c> primitives (<c>vendor/harcom.hpp</c>) are a genuinely
///         cycle-accurate hardware model, not just a couple of scratch fields: every register/RAM
///         enforces "one write (and, for RAM, one read) per cycle" against a single shared clock
///         (<c>panel.cycle</c>), and <c>predict1</c>/<c>predict2</c> stage values into registers that
///         <c>update_condbr</c>/<c>update_cycle</c> read back for the <em>same</em> branch. There is
///         no snapshot/restore surface in the shim or in harcom itself, and submissions declare
///         arbitrary <c>reg</c>/<c>ram</c> members with no reflection hook, so a generic per-branch
///         checkpoint (the mechanism <see cref="BranchHistoryCheckpoint" /> gives TAGE-family
///         predictors) cannot be built for harcom state. The only universally safe strategy is to
///         never let two blocks be open at once.
///     </para>
///     <para>
///         This adapter gets that for free from <see cref="IBranchPredictor.Update" />'s existing
///         contract: "called at commit, in program order." <see cref="Update" /> is therefore the
///         only place the wrapped <see cref="CbpNgFfiPredictor" /> is ever touched — it calls the
///         native <c>cbpng_predict</c> immediately followed by <c>cbpng_update</c> for the
///         <em>same</em> branch, back to back, exactly reproducing harcom's own reference execution
///         model ("always fully resolves one prediction block before starting the next") regardless
///         of how deeply the surrounding pipeline speculates. Because <c>predict1</c>/<c>predict2</c>
///         only ever observe state trained by <em>earlier-in-program-order</em> committed branches —
///         never this branch's own outcome — the resulting prediction (compared against the actual
///         outcome) is exactly as faithful as if it had been queried live at fetch.
///     </para>
///     <para>
///         The tradeoff: harcom's own prediction never steers real speculative fetch under this
///         adapter — an internal fetch-side predictor (a plain, always-reentrant C# predictor;
///         <see cref="GsharePredictor" /> by default) does, via <see cref="Predict" /> and
///         the full <see cref="IBranchPredictor" /> speculative-history surface. Harcom is trained and
///         scored purely as an observer of the committed instruction stream, which is also exactly
///         how CBP itself evaluates a submission (trace-replay, one resolved block at a time) — so
///         nothing about the submission's own accuracy measurement is lost, only its ability to
///         redirect fetch on its own say-so.
///     </para>
///     <para>
///         Not suitable for <c>CprTrain</c>: that train trains predictors speculatively at execute,
///         out of program order (MICRO 2003 §5.1), which violates the program-order assumption this
///         adapter depends on for safety.
///     </para>
/// </summary>
public sealed class CbpNgCommitDrivenPredictor : IBranchKindAwareBranchPredictor, IDisposable {
    private readonly IBranchPredictor _fetchPredictor;
    private readonly CbpNgFfiPredictor _harcom;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> (see
    ///     <see cref="CbpNgFfiPredictor" />) and wraps it for reentrant-safe use.
    /// </summary>
    /// <param name="libraryPath">Path to a shared library built by <c>native/CbpNgShim/build.sh</c>.</param>
    /// <param name="fetchPredictor">
    ///     The predictor that actually steers speculative fetch. Defaults to a fresh
    ///     <see cref="GsharePredictor" />. Must be safe under speculation (support
    ///     <see cref="IBranchPredictor.SpeculativeHistoryUpdate" /> /
    ///     <see cref="IBranchPredictor.RecoverSpeculativeHistory" />, as all built-in predictors do).
    /// </param>
    public CbpNgCommitDrivenPredictor(string libraryPath, IBranchPredictor? fetchPredictor = null) {
        _harcom = new CbpNgFfiPredictor(libraryPath);
        _fetchPredictor = fetchPredictor ?? new GsharePredictor();
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) =>
        _fetchPredictor.Predict(pc, knownTarget);

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        // Resolve harcom's own block for this branch — predict then update, back to back, with
        // nothing else touching the native predictor in between. Called at commit, in program
        // order, so this is the only point at which two blocks could ever overlap, and it never
        // happens here.
        _harcom.Predict(pc);
        _harcom.Update(pc, taken, actualTarget);
        _fetchPredictor.Update(pc, taken, actualTarget);
    }

    /// <inheritdoc />
    public void NotifyBranchKind(ulong pc, BranchKind kind) => _harcom.NotifyBranchKind(pc, kind);

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) =>
        _fetchPredictor.SpeculativeHistoryUpdate(pc, predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _fetchPredictor.RecoverSpeculativeHistory();

    /// <inheritdoc />
    public BranchHistoryCheckpoint CaptureHistory(ulong pc) => _fetchPredictor.CaptureHistory(pc);

    /// <inheritdoc />
    public void RestoreLocalEntry(in BranchHistoryCheckpoint checkpoint) =>
        _fetchPredictor.RestoreLocalEntry(checkpoint);

    /// <inheritdoc />
    public void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) =>
        _fetchPredictor.RestoreHistory(checkpoint, pc, actualTaken);

    /// <inheritdoc />
    public void Dispose() => _harcom.Dispose();
}