namespace Mechanism.BranchPredictModels;

public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc, ulong? knownTarget = null) => BranchPrediction.NotTaken(pc + 4);
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

public sealed class AlwaysTakenPredictor : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    // knownTarget is set for PC-relative branches; null for register-indirect (jalr).
    public BranchPrediction Predict(ulong pc, ulong? knownTarget = null) {
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

    // knownTarget is set for PC-relative branches; null for register-indirect (jalr).
    public BranchPrediction Predict(ulong pc, ulong? knownTarget = null) {
        ulong? target = knownTarget ?? (_btb.TryGetValue(pc, out ulong t) ? t : null);
        if (target.HasValue && target.Value < pc) return BranchPrediction.Taken(target.Value);
        return BranchPrediction.NotTaken(pc + 4);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}