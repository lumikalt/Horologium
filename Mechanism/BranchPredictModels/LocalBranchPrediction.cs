namespace Mechanism.BranchPredictModels;

public sealed class NBitPredictor : IBranchPredictor {
    private readonly int _satMax;
    private readonly int _satThreshold;
    private readonly byte[] _counters;
    private readonly ulong[] _btb;

    public NBitPredictor(int bits = 2, int tableSize = 1024) {
        _satMax = (1 << bits) - 1;
        _satThreshold = 1 << (bits - 1); // taken if counter >= satThreshold
        _counters = new byte[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, (byte)(_satThreshold - 1)); // weakly not-taken
    }

    public BranchPrediction Predict(ulong pc) {
        int idx = Index(pc);
        bool taken = _counters[idx] >= _satThreshold;
        return new BranchPrediction(taken, taken ? _btb[idx] : pc + 4);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        _btb[idx] = actualTarget;
        if (taken && _counters[idx] < _satMax) _counters[idx]++;
        if (!taken && _counters[idx] > 0) _counters[idx]--;
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_counters.Length);
}