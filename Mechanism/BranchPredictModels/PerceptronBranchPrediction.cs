namespace Mechanism.BranchPredictModels;

/// <summary>
/// Perceptron branch predictor (Jimenez &amp; Lin, 2001).
/// <para>
/// Each of the <c>tableSize</c> entries holds a perceptron: a bias weight plus
/// one weight per history bit. Prediction is the sign of the dot product of
/// the weight vector with the history vector (history bits mapped to ±1).
/// Training fires when the prediction was wrong OR the output magnitude is
/// below threshold θ = ⌊1.93·H + 14⌋. Weights are clamped to [-128, 127].
/// </para>
/// </summary>
public sealed class PerceptronPredictor : IBranchPredictor {
    private readonly int _historyLength;
    private readonly int _threshold;     // θ = floor(1.93·H + 14)
    private readonly sbyte[][] _weights; // [tableSize][H+1]; [i][0] = bias
    private readonly int _tableMask;     // tableSize must be a power of 2
    private ulong _ghr;                  // bit history, LSB = most recent; 1=taken
    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <summary>
    /// Constructs a Perceptron predictor.
    /// </summary>
    /// <param name="historyLength">
    /// History length.
    /// </param>
    /// <param name="tableSize">
    /// Entries in the table. Must be a power of 2.
    /// </param>
    public PerceptronPredictor(int historyLength = 24, int tableSize = 256) {
        _historyLength = historyLength;
        _threshold = (int)(1.93 * historyLength + 14);
        _tableMask = tableSize - 1;
        _weights = new sbyte[tableSize][];
        for (var i = 0; i < tableSize; i++) _weights[i] = new sbyte[historyLength + 1]; // zero = unbiased
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool pred = DotProduct(pc) >= 0;
        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;

        int y = DotProduct(pc);
        bool pred = y >= 0;
        if (pred != taken || Math.Abs(y) <= _threshold) Train(pc, taken);

        _ghr = ((_ghr << 1) | (taken ? 1UL : 0UL)) & ((1UL << _historyLength) - 1);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private int DotProduct(ulong pc) {
        sbyte[] w = _weights[TableIdx(pc)];
        int y = w[0]; // bias
        ulong ghr = _ghr;
        for (var i = 0; i < _historyLength; i++) {
            int xi = ((ghr >> i) & 1) == 1 ? 1 : -1;
            y += w[i + 1] * xi;
        }

        return y;
    }

    private void Train(ulong pc, bool taken) {
        sbyte[] w = _weights[TableIdx(pc)];
        int t = taken ? 1 : -1;
        w[0] = Clamp(w[0] + t);
        ulong ghr = _ghr;
        for (var i = 0; i < _historyLength; i++) {
            int xi = ((ghr >> i) & 1) == 1 ? 1 : -1;
            w[i + 1] = Clamp(w[i + 1] + t * xi);
        }
    }

    private int TableIdx(ulong pc) => (int)((pc >> 2) & (ulong)_tableMask);

    private static sbyte Clamp(int v) => (sbyte)Math.Clamp(v, sbyte.MinValue, sbyte.MaxValue);
}