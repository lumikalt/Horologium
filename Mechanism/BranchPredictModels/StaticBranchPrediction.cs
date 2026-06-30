namespace Mechanism.BranchPredictModels;

/// <summary>
/// Last-committed-value oracle: records the most recently committed branch outcome
/// and returns it as the next prediction. Correct when a branch repeats the same
/// direction (loop body, strongly biased branches). For volatile branches that
/// frequently alternate direction (e.g., sorting comparisons), mispredicts on every
/// direction change — potentially worse than a 2-bit counter, which has inertia.
///
/// This is NOT a true oracle predictor. A true oracle requires either two-pass
/// pre-simulation (record all outcomes in order, replay in the next run) or a
/// flush-aware interface to undo speculative predictions. Use ITTAGE/BATAGE for
/// a near-optimal realistic predictor on volatile-branch workloads.
/// </summary>
public sealed class OraclePredictor : IBranchPredictor {
    private readonly Dictionary<ulong, (bool Taken, ulong Target)> _table = new();

    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (_table.TryGetValue(pc, out (bool Taken, ulong Target) outcome))
            return outcome.Taken
                ? BranchPrediction.Taken(outcome.Target)
                : BranchPrediction.NotTaken(outcome.Target);
        return BranchPrediction.NotTaken(pc + 4); // cold miss: first encounter
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) =>
        _table[pc] = (taken, actualTarget);
}

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