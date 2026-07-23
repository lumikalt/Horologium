namespace Mechanism.BranchPred;

/// <summary>
///     Perceptron branch predictor (Jimenez &amp; Lin, 2001).
///     <para>
///         Each of the <c>tableSize</c> entries holds a perceptron: a bias weight plus
///         one weight per history bit. Prediction is the sign of the dot product of
///         the weight vector with the history vector (history bits mapped to ±1).
///         Training fires when the prediction was wrong OR the output magnitude is
///         below threshold θ = ⌊1.93·H + 14⌋. Weights are clamped to [-128, 127].
///     </para>
/// </summary>
public sealed class PerceptronBp : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly SpeculativeGlobalHistory _hist; // bit history, LSB = most recent; 1=taken
    private readonly int _historyLength;
    private readonly int _tableMask;     // tableSize must be a power of 2
    private readonly int _threshold;     // θ = floor(1.93·H + 14)
    private readonly sbyte[][] _weights; // [tableSize][H+1]; [i][0] = bias

    /// <summary>
    ///     Constructs a Perceptron predictor.
    /// </summary>
    /// <param name="historyLength">
    ///     History length.
    /// </param>
    /// <param name="tableSize">
    ///     Entries in the table. Must be a power of 2.
    /// </param>
    public PerceptronBp(int historyLength = 24, int tableSize = 256) {
        _historyLength = historyLength;
        _threshold = (int)(1.93 * historyLength + 14);
        _tableMask = tableSize - 1;
        _weights = new sbyte[tableSize][];
        for (var i = 0; i < tableSize; i++) _weights[i] = new sbyte[historyLength + 1]; // zero = unbiased
        _hist = new SpeculativeGlobalHistory(historyLength);
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

        _hist.Commit(
            taken, () => {
                int y = DotProduct(pc);
                bool pred = y >= 0;
                if (pred != taken || Math.Abs(y) <= _threshold) Train(pc, taken);
            }
        );
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) => _hist.Speculate(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _hist.Recover();

    /// <summary>Serializes every per-PC weight vector, the BTB, and the speculative/committed history pair.</summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_weights.Length);
        foreach (sbyte[] vec in _weights) {
            w.Write(vec.Length);
            foreach (sbyte weight in vec) w.Write(weight);
        }

        w.Write(_btb.Count);
        foreach ((ulong pc, ulong target) in _btb) {
            w.Write(pc);
            w.Write(target);
        }

        _hist.WriteState(w);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table geometry must match.</summary>
    public void ReadState(BinaryReader r) {
        int numVecs = r.ReadInt32();
        int vecN = Math.Min(numVecs, _weights.Length);
        for (var i = 0; i < numVecs; i++) {
            int size = r.ReadInt32();
            int n = i < vecN ? Math.Min(size, _weights[i].Length) : 0;
            for (var j = 0; j < size; j++) {
                sbyte weight = r.ReadSByte();
                if (j < n) _weights[i][j] = weight;
            }
        }

        _btb.Clear();
        int btbCount = r.ReadInt32();
        for (var i = 0; i < btbCount; i++) {
            ulong pc = r.ReadUInt64();
            ulong target = r.ReadUInt64();
            _btb[pc] = target;
        }

        _hist.ReadState(r);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private int DotProduct(ulong pc) {
        sbyte[] w = _weights[TableIdx(pc)];
        int y = w[0]; // bias
        ulong ghr = _hist.Value;
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
        ulong ghr = _hist.Value;
        for (var i = 0; i < _historyLength; i++) {
            int xi = ((ghr >> i) & 1) == 1 ? 1 : -1;
            w[i + 1] = Clamp(w[i + 1] + t * xi);
        }
    }

    private int TableIdx(ulong pc) => (int)((pc >> 2) & (ulong)_tableMask);

    private static sbyte Clamp(int v) => (sbyte)Math.Clamp(v, sbyte.MinValue, sbyte.MaxValue);
}