namespace Pipeline.Ooo;

/// <summary>
///     Selective-checkpointing trigger for CPR (Akkary, Rajwar &amp; Srinivasan, MICRO 2003 §4.1.1),
///     using the JRS branch confidence estimator (Jacobsen, Rotenberg &amp; Smith, MICRO 1996) as
///     the paper specifies: a table of 4-bit saturating counters indexed by
///     <c>
///         branch PC XOR
///         global branch history
///     </c>
///     . A correct prediction increments the counter; a misprediction
///     resets it to zero. A counter value of 15 signals high confidence — every other value reads
///     as low confidence, and a low-confidence branch warrants opening a new map-table checkpoint
///     before it.
///     <para>
///         The paper's two further triggers — a forced checkpoint every N-th allocated instruction
///         (counter-overflow prevention / COVHD bound) and a forced checkpoint at the first branch
///         after a recovery — are the caller's responsibility (<c>CprTrain</c> tracks entry counts
///         and recovery events directly); this class only answers the per-branch confidence
///         question and owns the global-history register the index depends on.
///     </para>
/// </summary>
public sealed class CheckpointConfidencePredictor {
    private const byte Max = 15;
    private readonly byte[] _counters;
    private readonly ulong _mask;
    private ulong _globalHistory;

    public CheckpointConfidencePredictor(int tableSize = 4096) {
        ArgumentOutOfRangeException.ThrowIfLessThan(tableSize, 1);
        if ((tableSize & (tableSize - 1)) != 0)
            throw new ArgumentException("tableSize must be a power of two.", nameof(tableSize));
        _counters = new byte[tableSize];
        _mask = (ulong)(tableSize - 1);
    }

    /// <summary>True when the branch at <paramref name="pc" /> currently reads as low-confidence.</summary>
    public bool IsLowConfidence(ulong pc) => _counters[Index(pc)] < CheckpointConfidencePredictor.Max;

    /// <summary>
    ///     Updates the counter for the just-resolved branch at <paramref name="pc" /> and folds its
    ///     outcome into the global history register used to index future lookups.
    /// </summary>
    public void Update(ulong pc, bool correctlyPredicted, bool taken) {
        int idx = Index(pc);
        _counters[idx] = correctlyPredicted
            ? Math.Min(CheckpointConfidencePredictor.Max, (byte)(_counters[idx] + 1))
            : (byte)0;
        _globalHistory = (_globalHistory << 1) | (taken ? 1UL : 0UL);
    }

    public void Reset() {
        Array.Clear(_counters);
        _globalHistory = 0;
    }

    private int Index(ulong pc) => (int)((pc ^ _globalHistory) & _mask);
}