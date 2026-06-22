namespace Mechanism.BranchPredictModels;

public sealed class OneBitPredictor : IBranchPredictor {
    private readonly int _tableSize;
    private readonly bool[] _counters;
    private readonly ulong[] _btb;

    public OneBitPredictor(int tableSize = 1024) {
        _tableSize = tableSize;
        _counters = new bool[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, true);
    }

    public BranchPrediction Predict(ulong pc) {
        int idx = Index(pc);
        bool taken = _counters[idx];
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        _btb[idx] = actualTarget;
        _counters[idx] = taken;
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_tableSize);
}

public sealed class TwoBitPredictor : IBranchPredictor {
    private readonly int _tableSize;
    private readonly byte[] _counters;
    private readonly ulong[] _btb;

    public TwoBitPredictor(int tableSize = 1024) {
        _tableSize = tableSize;
        _counters = new byte[tableSize];
        _btb = new ulong[tableSize];
        Array.Fill(_counters, (byte)1);
    }

    public BranchPrediction Predict(ulong pc) {
        int idx = Index(pc);
        bool taken = _counters[idx] >= 2;
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = Index(pc);
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _counters[idx] < 3:  _counters[idx]++; break;
            case false when _counters[idx] > 0: _counters[idx]--; break;
        }
    }

    private int Index(ulong pc) => (int)((pc >> 2) % (ulong)_tableSize);
}