namespace Mechanism.BranchPred;

/// <summary>
///     TAGE-SC-L: TAGE + Statistical Corrector + Loop predictor.
///     <para>
///         The Statistical Corrector keeps several small tables indexed by PC XOR
///         folded-history at a set of history lengths. Each table holds sbyte-valued
///         signed weights. During prediction the SC sums TAGE's signed confidence
///         score with all SC table weights; if the magnitude exceeds ScThreshold AND
///         the sign disagrees with TAGE, it overrides TAGE's direction.
///         The loop predictor (inherited from LTageBp) is still the final hard
///         override and is applied after SC.
///     </para>
/// </summary>
public class TageScLBp : LTageBp {
    private const int ScTableSize = 128;

    /// <summary>Magnitude beyond which the statistical corrector overrides TAGE's direction.</summary>
    protected const int ScThreshold = 10;

    private static readonly int[] ScHistLengths = [0, 8, 16, 24,];

    // SC tables: one per history length; indexed by (pc >> 2) XOR folded history.
    private readonly sbyte[][] _sc;

    /// <summary>
    ///     Constructs a TAGE-SC-L predictor.
    /// </summary>
    public TageScLBp() {
        _sc = new sbyte[TageScLBp.ScHistLengths.Length][];
        for (var i = 0; i < TageScLBp.ScHistLengths.Length; i++) _sc[i] = new sbyte[TageScLBp.ScTableSize];
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        int total = TageScore(pc, provider) + ScSum(pc);
        if (Math.Abs(total) > TageScLBp.ScThreshold) return total >= 0;
        return tagePred;
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        if (SuppressTageUpdate(pc)) return; // TAGE substrate frozen for this PC (e.g., Bullseye filtering)
        if (loopWasConfident) return;       // loop result was authoritative; SC doesn't train on it

        int preTotal = preScore + ScSum(pc);
        bool scPred = Math.Abs(preTotal) > TageScLBp.ScThreshold ? preTotal >= 0 : provPred;

        if (scPred != taken || Math.Abs(preTotal) <= TageScLBp.ScThreshold) TrainSc(pc, taken);
    }

    // ── SC internals ──────────────────────────────────────────────────────────

    /// <summary>Sum of all statistical-corrector table weights for <paramref name="pc" />.</summary>
    protected int ScSum(ulong pc) {
        var sum = 0;
        for (var i = 0; i < TageScLBp.ScHistLengths.Length; i++) sum += _sc[i][ScIdx(pc, i)];
        return sum;
    }

    private void TrainSc(ulong pc, bool taken) {
        int t = taken ? 1 : -1;
        for (var i = 0; i < TageScLBp.ScHistLengths.Length; i++) {
            int idx = ScIdx(pc, i);
            _sc[i][idx] = (sbyte)Math.Clamp(_sc[i][idx] + t, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    private int ScIdx(ulong pc, int tableIdx) {
        int hist = TageScLBp.ScHistLengths[tableIdx];
        int folded = hist == 0 ? 0 : FoldScHist(hist);
        return ((int)(pc >> 2) ^ folded) & (TageScLBp.ScTableSize - 1);
    }

    private int FoldScHist(int histLen) {
        ulong mask = (1UL << histLen) - 1;
        ulong h = Ghr & mask;
        const int outBits = 7; // log2(ScTableSize)
        const int outMask = (1 << outBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += outBits) res ^= (int)((h >> sh) & outMask);
        return res;
    }

    /// <summary>Serializes the inherited TAGE/loop/history state (via <c>base</c>) plus the SC tables.</summary>
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        w.Write(_sc.Length);
        foreach (sbyte[] table in _sc) {
            w.Write(table.Length);
            foreach (sbyte weight in table) w.Write(weight);
        }
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table geometry must match.</summary>
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        int numTables = r.ReadInt32();
        int tableN = Math.Min(numTables, _sc.Length);
        for (var i = 0; i < numTables; i++) {
            int size = r.ReadInt32();
            int n = i < tableN ? Math.Min(size, _sc[i].Length) : 0;
            for (var j = 0; j < size; j++) {
                sbyte weight = r.ReadSByte();
                if (j < n) _sc[i][j] = weight;
            }
        }
    }
}