namespace Mechanism.BranchPredictModels;

public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) =>
        BranchPrediction.NotTaken(pc + 4);

    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

public sealed class AlwaysTakenPredictor : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (knownTarget.HasValue) return BranchPrediction.Taken(knownTarget.Value);
        return _btb.TryGetValue(pc, out ulong target)
            ? BranchPrediction.Taken(target)
            : BranchPrediction.NotTaken(pc + 4);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}

public sealed class AlwaysBackwardNotForwards : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (knownTarget.HasValue) {
            if (knownTarget.Value < pc) return BranchPrediction.Taken(knownTarget.Value);
            return BranchPrediction.NotTaken(pc + 4);
        }

        if (_btb.TryGetValue(pc, out ulong t) && t < pc) return BranchPrediction.Taken(t);
        return BranchPrediction.NotTaken(pc + 4);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}