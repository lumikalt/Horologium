namespace Mechanism.BranchPred;

/// <summary>
///     BranchNet: a small per-branch convolutional predictor for hard-to-predict (H2P)
///     branches, layered on top of TAGE-SC-L. — Zangeneh, Sadrosadati, Ghose, Mutlu &amp;
///     Patt, "BranchNet: A Convolutional Neural Network to Predict Hard-To-Predict
///     Branches", MICRO 2020.
///     <para>
///         <b>Scoping.</b> The paper trains one small CNN per H2P branch <em>offline</em>
///         (via SimPoint-sampled profiling runs and full backpropagation with momentum,
///         learning-rate schedules, and dropout) and evaluates two deployment points:
///         iso-storage/iso-latency Mini-BranchNet, and an oracular upper-bound
///         Big-BranchNet. This class implements Mini-BranchNet's architecture (per-position
///         history-bit embeddings → 1D convolution → sum-pooling → a small quantization-free
///         floating-point MLP head) with plain single-pass SGD in place of the paper's fuller
///         training recipe, and a single fixed history length for every branch rather than a
///         per-branch length sweep. Big-BranchNet (which conditions on the true outcomes of
///         younger branches, unavailable at real fetch time) is not implemented.
///     </para>
///     <para>
///         <b>Offline training pre-pass.</b> <see cref="FromProfile" /> runs a functional
///         (<c>SingleCycleTrain</c>) pass over the workload, recording, for every dynamic
///         branch, the global history register value immediately before that branch (LSB =
///         most recent direction) and whether a freshly-constructed <see cref="TageScLBp" />
///         would have mispredicted it. Static branches that occur often enough and mispredict
///         often enough are classified H2P (paper Section 3.1's profiling-based selection,
///         without SimPoint sampling — the pre-pass here is already a single full functional
///         run, not a timed one, so sampling buys nothing). Each H2P branch's collected
///         (history, outcome) samples train one <see cref="BranchNetModel" /> via
///         backpropagation; models are frozen for the remainder of the object's lifetime —
///         there is no online fine-tuning, matching the paper's offline-training /
///         online-inference split.
///     </para>
///     <para>
///         <b>Online use.</b> A branch with a trained model is predicted by that model alone;
///         every other branch (the overwhelming majority) is predicted by an internal
///         <see cref="TageScLBp" /> baseline that this class keeps continuously trained
///         via delegation — the same "override a shared baseline for a known-hard subset"
///         layering <see cref="RunltsBp" /> and <see cref="LvcpBp" /> use,
///         chosen here via composition rather than inheritance since BranchNet's own working
///         state (a second global-history register sized for the CNN's input, and a
///         BranchNet-only BTB for branches the baseline never predicts taken for) is
///         independent of TAGE-SC-L's internals. The composed baseline's own global history
///         register is always a superset-width, bit-identical prefix of BranchNet's smaller
///         one (both are advanced by the same predicted-direction bit on every speculative
///         update), so <see cref="CaptureHistory" /> can forward directly to the baseline
///         and mask its wider snapshot down to reconstruct the model input.
///     </para>
/// </summary>
public sealed class BranchNetBp : IBranchPredictor {
    private const int HistoryLength = 24; // fixed per-branch history width, in bits

    // ── Offline training pre-pass ─────────────────────────────────────────────

    private const int MinOccurrences = 30;
    private const double MinMispredictRate = 0.08;
    private const int MaxH2PBranches = 32;
    private const int MinSamplesToTrain = 16;
    private const int MaxSamplesPerBranch = 4000;
    private const ulong HistoryMask = (1UL << BranchNetBp.HistoryLength) - 1;

    private readonly TageScLBp _baseline = new();
    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly Dictionary<ulong, BranchNetModel> _models;

    private ulong _committedGhr;
    private ulong _ghr; // working (speculative) global history, LSB = most recent
    private bool _speculative;

    /// <summary>Constructs a BranchNet predictor with no trained models (pure TAGE-SC-L fallback).</summary>
    public BranchNetBp() : this(new Dictionary<ulong, BranchNetModel>()) { }

    private BranchNetBp(Dictionary<ulong, BranchNetModel> models) => _models = models;

    /// <summary>Number of static branches with a trained CNN model.</summary>
    public int TrainedModelCount => _models.Count;

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (!_models.TryGetValue(pc, out BranchNetModel? model)) return _baseline.Predict(pc, knownTarget);

        bool taken = model.Forward(HistoryBits(_ghr)) > 0f;
        ulong target = taken ? _btb.GetValueOrDefault(pc, pc + 4) : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        _baseline.Update(pc, taken, actualTarget);
        if (taken) _btb[pc] = actualTarget;

        _committedGhr = ((_committedGhr << 1) | (taken ? 1UL : 0UL)) & BranchNetBp.HistoryMask;
        _ghr = _speculative ? _ghr : _committedGhr;
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        _baseline.SpeculativeHistoryUpdate(pc, predictedTaken);
        _speculative = true;
        _ghr = ((_ghr << 1) | (predictedTaken ? 1UL : 0UL)) & BranchNetBp.HistoryMask;
    }

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() {
        _baseline.RecoverSpeculativeHistory();
        _ghr = _committedGhr;
    }

    /// <inheritdoc />
    public BranchHistoryCheckpoint CaptureHistory(ulong pc) => _baseline.CaptureHistory(pc);

    /// <inheritdoc />
    public void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) {
        _baseline.RestoreHistory(checkpoint, pc, actualTaken);
        _speculative = true;
        _ghr = (((checkpoint.Global & BranchNetBp.HistoryMask) << 1) | (actualTaken ? 1UL : 0UL))
             & BranchNetBp.HistoryMask;
    }

    /// <summary>
    ///     Selects H2P branches from a completed <see cref="BranchProfiler" /> pass and trains
    ///     one model per branch, returning the ready-to-use predictor. The profiler itself must
    ///     be driven through a functional pre-pass (a <c>SingleCycleTrain</c> run) by the caller
    ///     — <see cref="Mechanism" /> has no dependency on the pipeline/memory layer that requires,
    ///     so that driving code lives at the config layer (mirroring how <c>TrueOracleConfig</c>
    ///     drives <see cref="BranchTraceRecorder" />).
    /// </summary>
    public static BranchNetBp FromProfile(BranchProfiler profiler) {
        List<ulong> h2P = profiler.Stats
                                  .Where(kv => kv.Value.Occurrences >= BranchNetBp.MinOccurrences
                                            && (double)kv.Value.Mispredicts / kv.Value.Occurrences
                                            >= BranchNetBp.MinMispredictRate
                                   )
                                  .OrderByDescending(kv => kv.Value.Mispredicts)
                                  .Take(BranchNetBp.MaxH2PBranches)
                                  .Select(kv => kv.Key)
                                  .ToList();

        var rng = new Random(0);
        var models = new Dictionary<ulong, BranchNetModel>();
        foreach (ulong pc in h2P) {
            List<(bool[] History, bool Taken)> samples = profiler.Samples[pc];
            if (samples.Count < BranchNetBp.MinSamplesToTrain) continue;
            models[pc] = BranchNetModel.Train(samples, rng);
        }

        return new BranchNetBp(models);
    }

    private static bool[] HistoryBits(ulong ghr) {
        var bits = new bool[BranchNetBp.HistoryLength];
        for (var i = 0; i < BranchNetBp.HistoryLength; i++) bits[i] = ((ghr >> i) & 1) != 0;
        return bits;
    }

    internal sealed class BranchStats {
        public int Mispredicts;
        public int Occurrences;
    }

    /// <summary>
    ///     Functional-run commit observer: classifies H2P branches against a scratch
    ///     TAGE-SC-L baseline and records (history-bits, outcome) samples for later offline
    ///     training. Attach to a <c>SingleCycleTrain</c> pre-pass; feed the result to
    ///     <see cref="BranchNetBp.FromProfile" />.
    /// </summary>
    public sealed class BranchProfiler(IDecoder decoder) : ICommitObserver {
        private readonly TageScLBp _scratchBaseline = new();
        private ulong _ghr;

        internal Dictionary<ulong, List<(bool[] History, bool Taken)>> Samples { get; } = new();
        internal Dictionary<ulong, BranchStats> Stats { get; } = new();

        /// <inheritdoc />
        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
            FetchHint hint = decoder.GetFetchHint(pc, rawEncoding);
            if (!hint.IsBranch) return;

            bool taken = state.Pc != pc + (ulong)hint.InstructionSize;
            BranchPrediction pred = _scratchBaseline.Predict(pc);
            bool mispredicted = pred.PredictedTaken != taken;
            _scratchBaseline.Update(pc, taken, state.Pc);

            if (!Stats.TryGetValue(pc, out BranchStats? st)) Stats[pc] = st = new BranchStats();
            st.Occurrences++;
            if (mispredicted) st.Mispredicts++;

            if (!Samples.TryGetValue(pc, out List<(bool[] History, bool Taken)>? list)) Samples[pc] = list = [];
            if (list.Count < BranchNetBp.MaxSamplesPerBranch) list.Add((HistoryBits(_ghr), taken));

            _ghr = ((_ghr << 1) | (taken ? 1UL : 0UL)) & BranchNetBp.HistoryMask;
        }
    }
}

/// <summary>
///     One branch's trained model: history-bit embeddings → 1D convolution → sum-pooling →
///     a two-layer tanh/linear head. Forward-only at inference time; <see cref="Train" />
///     performs the offline backpropagation pass (see <see cref="BranchNetBp" />'s
///     class doc comment for the scoping of this against the paper's full recipe).
/// </summary>
internal sealed class BranchNetModel {
    private const int EmbedDim = 4;
    private const int KernelSize = 4;
    private const int NumFilters = 6;
    private const int HiddenSize = 6;
    private const int Epochs = 40;
    private const float LearningRate = 0.05f;
    private readonly float[] _convB = new float[BranchNetModel.NumFilters];

    // [NumFilters][KernelSize][EmbedDim]
    private readonly float[][][] _convW;

    // [historyLength][bit=0/1][EmbedDim]
    private readonly float[][][] _embed;
    private readonly float[] _fc1B = new float[BranchNetModel.HiddenSize];

    // [HiddenSize][NumFilters]
    private readonly float[][] _fc1W;

    // [HiddenSize]
    private readonly float[] _fc2W = new float[BranchNetModel.HiddenSize];

    private readonly int _historyLength;
    private readonly int _numPositions;
    private float _fc2B;

    private BranchNetModel(int historyLength, Random rng) {
        _historyLength = historyLength;
        _numPositions = historyLength - BranchNetModel.KernelSize + 1;

        _embed = NewArray3(historyLength, 2, BranchNetModel.EmbedDim, rng);
        _convW = NewArray3(BranchNetModel.NumFilters, BranchNetModel.KernelSize, BranchNetModel.EmbedDim, rng);
        _fc1W = NewArray2(BranchNetModel.HiddenSize, BranchNetModel.NumFilters, rng);
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) _fc2W[h] = Init(rng);
    }

    /// <summary>Runs inference: returns a score whose sign is the predicted direction (positive = taken).</summary>
    public float Forward(bool[] historyBits) {
        float[][] embedded = EmbedSequence(historyBits);
        float[,] convOut = ConvForward(embedded);
        float[] pooled = SumPool(convOut);
        float[] hidden = Fc1Forward(pooled);
        return Fc2Forward(hidden);
    }

    /// <summary>Trains a fresh model on the given (history, outcome) samples via plain SGD.</summary>
    public static BranchNetModel Train(List<(bool[] History, bool Taken)> samples, Random rng) {
        int historyLength = samples[0].History.Length;
        var model = new BranchNetModel(historyLength, rng);

        for (var epoch = 0; epoch < BranchNetModel.Epochs; epoch++)
            foreach ((bool[] history, bool taken) in samples)
                model.TrainOne(history, taken ? 1f : -1f);

        return model;
    }

    private void TrainOne(bool[] historyBits, float target) {
        // ── Forward, keeping intermediates for backprop ──
        float[][] embedded = EmbedSequence(historyBits);
        var z = new float[_numPositions, BranchNetModel.NumFilters];
        var a = new float[_numPositions, BranchNetModel.NumFilters];
        for (var p = 0; p < _numPositions; p++)
        for (var f = 0; f < BranchNetModel.NumFilters; f++) {
            float sum = _convB[f];
            for (var k = 0; k < BranchNetModel.KernelSize; k++)
            for (var d = 0; d < BranchNetModel.EmbedDim; d++)
                sum += _convW[f][k][d] * embedded[p + k][d];
            z[p, f] = sum;
            a[p, f] = MathF.Max(0f, sum);
        }

        var pooled = new float[BranchNetModel.NumFilters];
        for (var f = 0; f < BranchNetModel.NumFilters; f++)
        for (var p = 0; p < _numPositions; p++)
            pooled[f] += a[p, f];

        var a1 = new float[BranchNetModel.HiddenSize];
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) {
            float sum = _fc1B[h];
            for (var f = 0; f < BranchNetModel.NumFilters; f++) sum += _fc1W[h][f] * pooled[f];
            a1[h] = MathF.Tanh(sum);
        }

        float output = _fc2B;
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) output += _fc2W[h] * a1[h];

        // ── Backward (squared-error loss against target in {-1,+1}) ──
        float dOut = output - target;

        var dA1 = new float[BranchNetModel.HiddenSize];
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) {
            dA1[h] = dOut * _fc2W[h];
            _fc2W[h] -= BranchNetModel.LearningRate * dOut * a1[h];
        }

        _fc2B -= BranchNetModel.LearningRate * dOut;

        var dZ1 = new float[BranchNetModel.HiddenSize];
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) dZ1[h] = dA1[h] * (1f - a1[h] * a1[h]);

        var dPooled = new float[BranchNetModel.NumFilters];
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) {
            for (var f = 0; f < BranchNetModel.NumFilters; f++) {
                dPooled[f] += dZ1[h] * _fc1W[h][f];
                _fc1W[h][f] -= BranchNetModel.LearningRate * dZ1[h] * pooled[f];
            }

            _fc1B[h] -= BranchNetModel.LearningRate * dZ1[h];
        }

        // Sum-pool spreads each filter's pooled gradient equally to every position.
        var dEmbedded = new float[_historyLength][];
        for (var i = 0; i < _historyLength; i++) dEmbedded[i] = new float[BranchNetModel.EmbedDim];

        for (var p = 0; p < _numPositions; p++)
        for (var f = 0; f < BranchNetModel.NumFilters; f++) {
            float dZ = dPooled[f] * (z[p, f] > 0f ? 1f : 0f); // ReLU derivative
            if (dZ == 0f) continue;

            for (var k = 0; k < BranchNetModel.KernelSize; k++)
            for (var d = 0; d < BranchNetModel.EmbedDim; d++) {
                dEmbedded[p + k][d] += dZ * _convW[f][k][d];
                _convW[f][k][d] -= BranchNetModel.LearningRate * dZ * embedded[p + k][d];
            }

            _convB[f] -= BranchNetModel.LearningRate * dZ;
        }

        for (var i = 0; i < _historyLength; i++) {
            int bit = historyBits[i] ? 1 : 0;
            for (var d = 0; d < BranchNetModel.EmbedDim; d++)
                _embed[i][bit][d] -= BranchNetModel.LearningRate * dEmbedded[i][d];
        }
    }

    private float[][] EmbedSequence(bool[] historyBits) {
        var embedded = new float[_historyLength][];
        for (var i = 0; i < _historyLength; i++) embedded[i] = _embed[i][historyBits[i] ? 1 : 0];
        return embedded;
    }

    private float[,] ConvForward(float[][] embedded) {
        var z = new float[_numPositions, BranchNetModel.NumFilters];
        for (var p = 0; p < _numPositions; p++)
        for (var f = 0; f < BranchNetModel.NumFilters; f++) {
            float sum = _convB[f];
            for (var k = 0; k < BranchNetModel.KernelSize; k++)
            for (var d = 0; d < BranchNetModel.EmbedDim; d++)
                sum += _convW[f][k][d] * embedded[p + k][d];
            z[p, f] = MathF.Max(0f, sum);
        }

        return z;
    }

    private float[] SumPool(float[,] a) {
        var pooled = new float[BranchNetModel.NumFilters];
        for (var f = 0; f < BranchNetModel.NumFilters; f++)
        for (var p = 0; p < _numPositions; p++)
            pooled[f] += a[p, f];
        return pooled;
    }

    private float[] Fc1Forward(float[] pooled) {
        var a1 = new float[BranchNetModel.HiddenSize];
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) {
            float sum = _fc1B[h];
            for (var f = 0; f < BranchNetModel.NumFilters; f++) sum += _fc1W[h][f] * pooled[f];
            a1[h] = MathF.Tanh(sum);
        }

        return a1;
    }

    private float Fc2Forward(float[] a1) {
        float output = _fc2B;
        for (var h = 0; h < BranchNetModel.HiddenSize; h++) output += _fc2W[h] * a1[h];
        return output;
    }

    private static float Init(Random rng) => (float)(rng.NextDouble() - 0.5) * 0.2f;

    private static float[][][] NewArray3(int d0, int d1, int d2, Random rng) {
        var arr = new float[d0][][];
        for (var i = 0; i < d0; i++) {
            arr[i] = new float[d1][];
            for (var j = 0; j < d1; j++) {
                arr[i][j] = new float[d2];
                for (var k = 0; k < d2; k++) arr[i][j][k] = Init(rng);
            }
        }

        return arr;
    }

    private static float[][] NewArray2(int d0, int d1, Random rng) {
        var arr = new float[d0][];
        for (var i = 0; i < d0; i++) {
            arr[i] = new float[d1];
            for (var j = 0; j < d1; j++) arr[i][j] = Init(rng);
        }

        return arr;
    }
}