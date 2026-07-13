namespace Mechanism.BranchPredictModels;

/// <summary>
///     Last-committed-value oracle: records the most recently committed branch outcome
///     and returns it as the next prediction. Correct when a branch repeats the same
///     direction (loop body, strongly biased branches). For volatile branches that
///     frequently alternate direction (e.g., sorting comparisons), mispredicts on every
///     direction change — potentially worse than a 2-bit counter, which has inertia.
///     <para>
///         This is NOT a true oracle predictor — it misses on first encounters and on
///         direction changes. For a true oracle that achieves zero mispredictions, use
///         <see cref="TrueOraclePredictor" /> (configured via <c>TrueOracleConfig</c>),
///         which runs a functional pre-pass to collect the complete branch trace.
///     </para>
/// </summary>
public sealed class OraclePredictor : IBranchPredictor {
    private readonly Dictionary<ulong, (bool Taken, ulong Target)> _table = new();

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (_table.TryGetValue(pc, out (bool Taken, ulong Target) outcome))
            return outcome.Taken
                ? BranchPrediction.Taken(outcome.Target)
                : BranchPrediction.NotTaken(outcome.Target);
        return BranchPrediction.NotTaken(pc + 4); // cold miss: first encounter
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) =>
        _table[pc] = (taken, actualTarget);
}

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