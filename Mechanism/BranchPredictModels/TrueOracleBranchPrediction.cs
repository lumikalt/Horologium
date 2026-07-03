namespace Mechanism.BranchPredictModels;

public readonly record struct BranchOutcome(ulong Pc, bool Taken, ulong Target);

/// <summary>
/// Commit observer that records all branch outcomes in dynamic execution order.
/// Attach to a <c>SingleCycleTrain</c> pre-pass; feed the resulting
/// <see cref="Trace"/> to <see cref="TrueOraclePredictor"/>.
/// </summary>
public sealed class BranchTraceRecorder(IDecoder decoder) : ICommitObserver {
    private readonly List<BranchOutcome> _trace = [];

    public IReadOnlyList<BranchOutcome> Trace => _trace;

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        FetchHint hint = decoder.GetFetchHint(pc, rawEncoding);
        if (!hint.IsBranch) return;
        bool taken = state.Pc != pc + (ulong)hint.InstructionSize;
        _trace.Add(new BranchOutcome(pc, taken, state.Pc));
    }
}

/// <summary>
/// Zero-mispredict oracle predictor. Replays a branch outcome trace collected by a
/// functional pre-pass (<see cref="BranchTraceRecorder"/>). Accuracy is perfect when
/// the pre-pass and main run execute the same instruction stream in the same order.
/// <para>
/// Limitations: index drift occurs if speculative branches are Predicted-then-flushed
/// for reasons other than branch misprediction (e.g., traps, interrupts, RAS overflow).
/// For bare-metal compute workloads without mid-run interrupts this does not occur.
/// </para>
/// </summary>
public sealed class TrueOraclePredictor(IReadOnlyList<BranchOutcome> trace) : IBranchPredictor {
    private int _nextIdx;

    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        if (_nextIdx >= trace.Count) return BranchPrediction.NotTaken(pc + 4);
        BranchOutcome outcome = trace[_nextIdx++];
        return outcome.Taken
            ? BranchPrediction.Taken(outcome.Target)
            : BranchPrediction.NotTaken(outcome.Target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) { }
}