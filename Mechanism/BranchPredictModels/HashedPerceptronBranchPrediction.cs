namespace Mechanism.BranchPredictModels;

/// <summary>
/// Hashed / path-based perceptron predictor (Jimenez, 2005).
/// <para>
/// Extends the classic perceptron by spreading weights across multiple tables
/// at geometrically increasing history lengths. Each table has one weight per
/// entry, indexed by PC XOR XOR-folded history of that length (table 0 is a
/// bias table indexed by PC only). Prediction is the sign of the sum of all
/// looked-up weights. Training fires when wrong OR |sum| ≤ θ = ⌊1.93·H + 14⌋.
/// </para>
/// <para>
/// Compared to the classic perceptron: no per-PC weight vector — different PCs
/// that share the same history path contribute to the same table entries, giving
/// better generalization across correlated branches with less storage.
/// </para>
/// </summary>
public sealed class HashedPerceptronPredictor : IBranchPredictor {
    // Default: 8 tables with geometric history lengths.
    private static readonly int[] DefaultHistLengths = [0, 2, 4, 8, 11, 16, 23, 32,];

    private readonly int[] _histLengths;
    private readonly int _maxHist;
    private readonly int _threshold;    // θ = floor(1.93·H + 14)
    private readonly sbyte[][] _tables; // [numTables][tableSize]
    private readonly int _tableMask;
    private readonly int _indexBits; // log2(tableSize), used for XOR-folding
    private ulong _ghr;
    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <summary>
    /// Hashed / path-based perceptron predictor.
    /// </summary>
    /// <param name="tableSize">
    /// Entries in each table. Must be a power of 2.
    /// </param>
    /// <param name="histLengths">
    /// History lengths for each table. If null, the default geometric is used.
    /// </param>
    public HashedPerceptronPredictor(int tableSize = 512, int[]? histLengths = null) {
        _histLengths = histLengths ?? HashedPerceptronPredictor.DefaultHistLengths;
        _maxHist = _histLengths.Max();
        _threshold = (int)(1.93 * _maxHist + 14);
        _tableMask = tableSize - 1;
        _indexBits = BitWidth(tableSize);
        _tables = new sbyte[_histLengths.Length][];
        for (var i = 0; i < _histLengths.Length; i++) _tables[i] = new sbyte[tableSize];
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool pred = Sum(pc) >= 0;
        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
        int y = Sum(pc);
        bool pred = y >= 0;
        if (pred != taken || Math.Abs(y) <= _threshold) Train(pc, taken);
        _ghr = ((_ghr << 1) | (taken ? 1UL : 0UL)) & ((1UL << _maxHist) - 1);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private int Sum(ulong pc) {
        var y = 0;
        for (var i = 0; i < _histLengths.Length; i++) y += _tables[i][TableIdx(pc, i)];
        return y;
    }

    private void Train(ulong pc, bool taken) {
        int t = taken ? 1 : -1;
        for (var i = 0; i < _histLengths.Length; i++) {
            int idx = TableIdx(pc, i);
            _tables[i][idx] = (sbyte)Math.Clamp(_tables[i][idx] + t, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    private int TableIdx(ulong pc, int tableIdx) {
        int histLen = _histLengths[tableIdx];
        var pcIdx = (int)((pc >> 2) & (ulong)_tableMask);
        if (histLen == 0) return pcIdx;
        return (pcIdx ^ FoldHist(histLen)) & _tableMask;
    }

    private int FoldHist(int histLen) {
        ulong hist = _ghr & ((1UL << histLen) - 1);
        int outMask = (1 << _indexBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += _indexBits) res ^= (int)((hist >> sh) & (ulong)outMask);
        return res;
    }

    // Number of bits needed to index tableSize entries (tableSize must be a power of 2).
    private static int BitWidth(int n) {
        var bits = 0;
        while (n > 1) {
            n >>= 1;
            bits++;
        }

        return bits;
    }
}