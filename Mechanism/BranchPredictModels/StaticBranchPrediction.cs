namespace Mechanism.BranchPredictModels;

public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc) => BranchPrediction.NotTaken(pc + 4);
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

public sealed class AlwaysTakenPredictor : IBranchPredictor {
    public BranchPrediction Predict(ulong pc) => BranchPrediction.Taken(pc + 4);
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}