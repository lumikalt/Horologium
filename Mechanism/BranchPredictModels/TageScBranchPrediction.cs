namespace Mechanism.BranchPredictModels;

/// <summary>
/// TAGE-SC-L: TAGE + Statistical Corrector + Loop predictor.
///
/// The Statistical Corrector keeps several small tables indexed by PC XOR
/// folded-history at a set of history lengths. Each table holds sbyte-valued
/// signed weights. During prediction the SC sums TAGE's signed confidence
/// score with all SC table weights; if the magnitude exceeds ScThreshold AND
/// the sign disagrees with TAGE it overrides TAGE's direction.
/// The loop predictor (inherited from LTagePredictor) is still the final hard
/// override and is applied after SC.
/// </summary>
public sealed class TageScLPredictor : LTagePredictor {
    private static readonly int[] ScHistLengths = [0, 8, 16, 24,];
    private const int ScTableSize = 128;
    private const int ScThreshold = 10;

    // SC tables: one per history length; indexed by (pc >> 2) XOR folded history.
    private readonly sbyte[][] _sc;

    public TageScLPredictor() {
        _sc = new sbyte[TageScLPredictor.ScHistLengths.Length][];
        for (var i = 0; i < TageScLPredictor.ScHistLengths.Length; i++)
            _sc[i] = new sbyte[TageScLPredictor.ScTableSize];
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────

    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        int total = TageScore(pc, provider) + ScSum(pc);
        if (Math.Abs(total) > TageScLPredictor.ScThreshold) return total >= 0;
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
        if (loopWasConfident) return; // loop result was authoritative; SC doesn't train on it

        int preTotal = preScore + ScSum(pc);
        bool scPred = Math.Abs(preTotal) > TageScLPredictor.ScThreshold ? preTotal >= 0 : provPred;

        if (scPred != taken || Math.Abs(preTotal) <= TageScLPredictor.ScThreshold) TrainSc(pc, taken);
    }

    // ── SC internals ──────────────────────────────────────────────────────────

    private int ScSum(ulong pc) {
        var sum = 0;
        for (var i = 0; i < TageScLPredictor.ScHistLengths.Length; i++) sum += _sc[i][ScIdx(pc, i)];
        return sum;
    }

    private void TrainSc(ulong pc, bool taken) {
        int t = taken ? 1 : -1;
        for (var i = 0; i < TageScLPredictor.ScHistLengths.Length; i++) {
            int idx = ScIdx(pc, i);
            _sc[i][idx] = (sbyte)Math.Clamp(_sc[i][idx] + t, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    private int ScIdx(ulong pc, int tableIdx) {
        int hist = TageScLPredictor.ScHistLengths[tableIdx];
        int folded = hist == 0 ? 0 : FoldScHist(hist);
        return ((int)(pc >> 2) ^ folded) & (TageScLPredictor.ScTableSize - 1);
    }

    private int FoldScHist(int histLen) {
        ulong mask = (1UL << histLen) - 1;
        ulong h = Ghr & mask;
        var outBits = 7; // log2(ScTableSize)
        int outMask = (1 << outBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += outBits) res ^= (int)((h >> sh) & (ulong)outMask);
        return res;
    }
}