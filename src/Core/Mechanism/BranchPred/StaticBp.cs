namespace Mechanism.BranchPred;

/// <summary>
///     Always predicts not-taken.
/// </summary>
public sealed class AlwaysNotTakenPredictor : IBranchPredictor {
    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) =>
        BranchPrediction.NotTaken(pc + 4);

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}

/// <summary>
///     Predicts the last-committed target.
/// </summary>
public sealed class AlwaysTakenPredictor : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (knownTarget.HasValue) return BranchPrediction.Taken(knownTarget.Value);
        return _btb.TryGetValue(pc, out ulong target)
            ? BranchPrediction.Taken(target)
            : BranchPrediction.NotTaken(pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}

/// <summary>
///     Predicts Taken for backwards branches and NotTaken for forwards branches.
/// </summary>
public sealed class AlwaysBackwardNotForwards : IBranchPredictor {
    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (knownTarget.HasValue)
            return knownTarget.Value < pc
                ? BranchPrediction.Taken(knownTarget.Value)
                : BranchPrediction.NotTaken(pc + 4);

        if (_btb.TryGetValue(pc, out ulong t) && t < pc) return BranchPrediction.Taken(t);
        return BranchPrediction.NotTaken(pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
    }
}