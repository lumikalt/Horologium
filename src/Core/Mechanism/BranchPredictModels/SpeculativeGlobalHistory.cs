namespace Mechanism.BranchPredictModels;

/// <summary>
/// A global branch-history shift register with speculative and committed copies.
/// <para>
/// <see cref="Value"/> is the working history used for Predict-time indexing. In an
/// out-of-order pipeline it is advanced speculatively at fetch (<see cref="Speculate"/>)
/// and restored from the committed shadow on a flush (<see cref="Recover"/>).
/// <see cref="Commit"/> advances the committed shadow with a true outcome at commit and
/// runs the predictor's table training against that shadow — the predict-time history of
/// the committing branch (valid because in-order commit + in-order fetch means any branch
/// that commits had its path predicted correctly).
/// </para>
/// <para>
/// When the pipeline never drives speculation (in-order trains never call
/// <see cref="Speculate"/>), a latch keeps <see cref="Value"/> in lock-step with the
/// committed shadow, so the behaviour is bit-identical to a plain single-register history.
/// </para>
/// </summary>
internal sealed class SpeculativeGlobalHistory {
    private readonly ulong _mask;
    private bool _speculative;
    private ulong _committed;

    /// <param name="bits">Number of history bits retained (LSB = most recent).</param>
    public SpeculativeGlobalHistory(int bits) =>
        _mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    /// <summary>Working history for Predict-time indexing.</summary>
    public ulong Value { get; private set; }

    /// <summary>Folds a predicted direction into the speculative history at fetch.</summary>
    public void Speculate(bool taken) {
        _speculative = true;
        Value = ((Value << 1) | (taken ? 1UL : 0UL)) & _mask;
    }

    /// <summary>Discards wrong-path speculation on a flush, restoring committed history.</summary>
    public void Recover() => Value = _committed;

    /// <summary>
    /// The current working history, captured (before this branch's <see cref="Speculate"/>) as a
    /// per-branch checkpoint for exact recovery on an execute-time partial squash.
    /// </summary>
    public ulong Capture() => Value;

    /// <summary>
    /// Restores the working history to a captured checkpoint and folds <paramref name="actualTaken"/>
    /// — the redirecting branch's resolved direction — so fetch resumes with history as-of-the-branch.
    /// </summary>
    public void RestoreTo(ulong value, bool actualTaken) {
        Value = value;
        Speculate(actualTaken);
    }

    /// <summary>
    /// Runs <paramref name="train"/> with <see cref="Value"/> swapped to the committed
    /// (predict-time) history, then advances the committed shadow with
    /// <paramref name="taken"/> and restores the working history. In non-speculative mode
    /// the working history ends equal to the advanced committed shadow (old behaviour).
    /// </summary>
    public void Commit(bool taken, Action train) {
        ulong working = Value;
        Value = _committed;
        train();
        _committed = ((_committed << 1) | (taken ? 1UL : 0UL)) & _mask;
        Value = _speculative ? working : _committed;
    }
}