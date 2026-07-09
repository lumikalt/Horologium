namespace Mechanism.BranchPredictModels;

/// <summary>
/// Local predictor with a single counter per PC.
/// </summary>
public sealed class NBitPredictor : IBranchPredictor {
    private readonly int _satMax;
    private readonly int _satThreshold;
    private readonly byte[] _counters;
    private readonly ulong[] _btb;

    /// <summary>
    /// Constructs a predictor with the given number of states and BTB size.
    /// </summary>
    /// <param name="bits">
    /// Number of states.
    /// </param>
    /// <param name="tableSize">
    /// Entries in the BTB.
    /// </param>
    public NBitPredictor(int bits = 2, int tableSize = 1024) {
        _satMax = (1 << bits) - 1;
        _satThreshold = 1 << (bits - 1); // taken if counter >= satThreshold
        _counters = new byte[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, (byte)(_satThreshold - 1)); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int idx = Index(pc);
        bool taken = _counters[idx] >= _satThreshold;
        return new BranchPrediction(taken, taken ? _btb[idx] : pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _counters[idx] < _satMax: _counters[idx]++; break;
            case false when _counters[idx] > 0:      _counters[idx]--; break;
        }
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_counters.Length);
}