namespace Mechanism.BranchPredictModels;

public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc) => BranchPrediction.NotTaken(pc + 4);
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

public sealed class AlwaysTakenPredictor : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    public BranchPrediction Predict(ulong pc) =>
        _btb.TryGetValue(pc, out ulong target)
            ? BranchPrediction.Taken(target)
            : BranchPrediction.NotTaken(pc + 4); // BTB cold miss: fall through

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}

public sealed class AlwaysBackwardNotForwards : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();
    
    public BranchPrediction Predict(ulong pc) =>
        _btb.TryGetValue(pc, out ulong target)
            ? BranchPrediction.Taken(target)
            : BranchPrediction.NotTaken(pc - 4);
    
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
    
    public void Update(ulong pc, bool taken, ulong actualTarget, bool isBackward) {
        if (isBackward) _btb[pc] = actualTarget;
    }
}