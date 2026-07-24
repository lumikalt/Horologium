namespace Mechanism.ValuePred;

/// <summary>
///     Combines a context-based predictor (e.g. <see cref="IValuePredictor" />) with a computational
///     predictor (e.g. <see cref="OnBranchFetched" />) into a single <see cref="CaptureHistory" />,
///     per Perais &amp; Seznec, HPCA 2014 §7.1.2's hybrid combination rule: if only one part has a
///     confident prediction, it is used; if both do, and they agree, the (shared) value is used; if
///     both do, and they disagree, no prediction is made at all — disagreement between two
///     independent, otherwise-confident predictors is itself a signal not to trust either. Both
///     components are updated with the committed value at every retire, regardless of which one (if
///     either) supplied the prediction that was used, since a predictor must be trained on every
///     outcome to converge.
///     <para>
///         Context and computational predictors are complementary rather than redundant (see
///         <see cref="TryPredict" />'s doc comment): a monotonic counter defeats value-repetition
///         predictors but is trivial for a stride predictor, while a value that depends on control
///         flow but never forms a fixed arithmetic progression is the reverse.
///     </para>
///     <para>
///         Branch-history bookkeeping (<see cref="Update" />, <see cref="TryPredict" />,
///         etc.) is forwarded only to the <c>context</c> component — only a history-based predictor
///         needs any of it; a computational predictor like <see cref="StrideVp" /> ignores
///         the checkpoint it's handed either way. Not
///         modeled: the paper's further optimization of feeding one part's speculative
///         prediction to the other as an ersatz "last occurrence" value to resolve back-to-back
///         same-PC occurrences within a single cycle (§7.1.2) — Horologium's pipeline only calls
///         <see cref="StrideVp" />/<see cref="StrideVp" /> once per instruction, at Rename/Commit, so
///         there is no equivalent intra-cycle chaining to hook into.
///     </para>
///     <para>
///         Known limitation, not chased: <see cref="StrideVp" /> queries both components
///         unconditionally even when they end up disagreeing — a confident
///         <see cref="VtageVp" /> component still advances its own in-flight-depth counter
///         on such a call even though the disagreement means no prediction is actually used. This
///         perturbs that counter's accuracy but never correctness (a real misprediction is still
///         always caught and squashed regardless). It's off-path for the workloads this hybrid was
///         built and measured against, where the context component is silent (never confident)
///         whenever the computational one is, so no disagreement — and thus no phantom advance —
///         actually occurs.
///     </para>
/// </summary>
public sealed class HybridVp : IValuePredictor {
    private readonly IValuePredictor _computational;
    private readonly IValuePredictor _context;

    /// <param name="context">The context-based (history-driven) component, e.g. <see cref="VtageVp" />.</param>
    /// <param name="computational">The computational component, e.g. <see cref="StrideVp" />.</param>
    public HybridVp(IValuePredictor context, IValuePredictor computational) {
        _context = context;
        _computational = computational;
    }

    /// <inheritdoc />
    public bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value) {
        bool contextHit = _context.TryPredict(pc, history, out ulong contextValue);
        bool computationalHit = _computational.TryPredict(pc, history, out ulong computationalValue);

        switch (contextHit) {
            case true when computationalHit: {
                if (contextValue == computationalValue) {
                    value = contextValue;
                    return true;
                }

                value = 0;
                return false; // disagreement between two confident predictors: trust neither
            }
            case true:
                value = contextValue;
                return true;
        }

        if (computationalHit) {
            value = computationalValue;
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc />
    public void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue) {
        _context.Update(pc, history, actualValue);
        _computational.Update(pc, history, actualValue);
    }

    /// <inheritdoc />
    public void OnBranchFetched(bool predictedTaken) => _context.OnBranchFetched(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() {
        // Forwarded to both components, not just context: a computational predictor can have its
        // own squash-sensitive speculative state to reset (e.g., StrideVp's in-flight-depth
        // counter, which would otherwise leak upward forever across repeated squashes if this
        // only reached the history-tracking component).
        _context.RecoverSpeculativeHistory();
        _computational.RecoverSpeculativeHistory();
    }

    /// <inheritdoc />
    public ValueHistoryCheckpoint CaptureHistory() => _context.CaptureHistory();

    /// <inheritdoc />
    public void RestoreHistory(in ValueHistoryCheckpoint checkpoint, bool actualTaken) {
        _context.RestoreHistory(checkpoint, actualTaken);
        _computational.RestoreHistory(checkpoint, actualTaken);
    }

    /// <inheritdoc />
    public void AdvanceCommittedHistory(bool taken) => _context.AdvanceCommittedHistory(taken);

    /// <summary>Holds no state of its own; delegates to both composed components.</summary>
    public void WriteState(BinaryWriter w) {
        _context.WriteState(w);
        _computational.WriteState(w);
    }

    /// <inheritdoc cref="WriteState" />
    public void ReadState(BinaryReader r) {
        _context.ReadState(r);
        _computational.ReadState(r);
    }
}