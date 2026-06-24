namespace Mechanism.BranchPredictModels;

/// <summary>
/// BATAGE: Bimodal-Augmented TAGE.
///
/// Extends L-TAGE with a per-PC bias table (a single statistical-corrector
/// table with no history folding). The bias table's signed weight is summed
/// with TAGE's signed confidence score; if the combined magnitude exceeds
/// BiasThreshold the combined sign overrides TAGE's direction prediction.
///
/// Note: this is the per-PC SC-table interpretation. Michaud's original BATAGE
/// paper uses a dual-counter bimodal structure; this simplified form follows
/// the single-weight-per-entry pattern used in TAGE-SC-L.
/// </summary>
public sealed class BatagePredictor : LTagePredictor {
    private const int BiasTableBits = 12; // 4096 entries
    private const int BiasThreshold = 8;

    private readonly sbyte[] _bias = new sbyte[1 << BatagePredictor.BiasTableBits];

    // ── LTagePredictor hooks ──────────────────────────────────────────────────

    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        int total = TageScore(pc, provider) + _bias[BiasIdx(pc)];
        if (Math.Abs(total) > BatagePredictor.BiasThreshold) return total >= 0;
        return tagePred;
    }

    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        int provider,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        if (loopWasConfident) return;

        int preTotal = preScore + _bias[BiasIdx(pc)];
        bool biasPred = Math.Abs(preTotal) > BatagePredictor.BiasThreshold ? preTotal >= 0 : provPred;

        if (biasPred != taken || Math.Abs(preTotal) <= BatagePredictor.BiasThreshold) {
            int idx = BiasIdx(pc);
            int delta = taken ? 1 : -1;
            _bias[idx] = (sbyte)Math.Clamp(_bias[idx] + delta, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int BiasIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << BatagePredictor.BiasTableBits) - 1));
}