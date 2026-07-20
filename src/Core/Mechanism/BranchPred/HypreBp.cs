#region

using System.Numerics;

#endregion

namespace Mechanism.BranchPred;

/// <summary>
///     HYPRE: hyperdimensional-computing / sparse-distributed-memory branch predictor.
///     — Vougioukas, Sandberg &amp; Nikoleris, "Branch Predicting with Sparse Distributed
///     Memories", arXiv:2110.09166, 2021.
///     <para>
///         Encodes a branch's context as a HyperVector (HV) — a long, effectively-random bit
///         vector generated deterministically from (PC, folded history) — and looks it up
///         against a pair of accumulator HVs (Taken / Not-Taken) per history length, one
///         geometric series per §3.1. Each accumulator element is a saturating counter (the
///         paper's "saturation" bits) whose sign is the stored bit; a match is decided by
///         Hamming similarity against a threshold derived from the binomial-distribution
///         argument in §2.2 (Fig. 2/3): for a D-bit vector, chance matches cluster tightly
///         around D/2, so a threshold of D/2 + 3σ (σ = √D⁄2) rejects them with high confidence.
///         The longest history length whose Taken or Not-Taken vector clears the threshold wins
///         (§3.1, Fig. 9); if none clears it, an HD-bimodal base predictor (§3.1, Fig. 8: PC
///         bits concatenated with a small per-PC local-history counter, same HV/threshold
///         machinery) supplies the fallback prediction.
///     </para>
///     <para>
///         Training (§3.1 "Update") adds the query HV to the correct-direction accumulator only
///         when that accumulator wasn't already confidently matching (SDM's one-shot-learning
///         property: confidently correct patterns aren't reinforced), and subtracts it from the
///         wrong-direction accumulator when that accumulator incorrectly cleared the threshold.
///     </para>
///     <para>
///         Simplifications from the paper, documented where they depart: (1) branch context is
///         encoded as PC hashed with a plain bit-outcome global-history register — the paper's
///         own "simpler way of creating branch patterns", explicitly offered as an alternative
///         to the full PC-sequence rotate/XOR chain (§3, "How do HVs capture branch patterns").
///         This bounds history length to a single 64-bit shift register, consistent with every
///         other predictor in this codebase (TAGE's 34 bits, HashedPerceptron's 32), rather than
///         the paper's up-to-4096-bit histories that specifically motivated the chain
///         construction — the chain's entire point is to avoid a register that wide. (2) Vector
///         dimension is fixed at 1024 bits for every history length, the size the paper itself
///         identifies as the accuracy plateau ("1024 bits are enough to deliver reliable
///         behavior that retains high accuracy" — §7), rather than widening to 4096 bits for
///         long histories — that widening exists in the paper only to avoid rotation wraparound
///         in the chain construction this implementation doesn't use. (3) The two
///         over-correction mitigations described in §3.1 ("Update") — randomizing a fraction of
///         bits instead of a full subtraction, and randomizing a fraction of bits on insert — are
///         omitted: both are explicitly probabilistic, and this codebase's predictors (and the
///         checkpoint/replay machinery built on them) are deterministic by convention. None of
///         these affect the core mechanism: per-length HV accumulators, longest-match-wins
///         arbitration, HD-bimodal fallback, and one-shot-learning training.
///     </para>
/// </summary>
public sealed class HypreBp : IBranchPredictor {
    private const int VectorBits = 1024;
    private const int VectorWords = HypreBp.VectorBits / 64; // 16, exact — no partial-word masking needed

    private const int LocalHistoryBits = 4;
    private const int LocalTableSize = 2048;

    private static readonly int[] DefaultHistLengths = [4, 8, 16, 32, 64,];

    private readonly HdVector _baseNotTaken = new(HypreBp.VectorBits);
    private readonly HdVector _baseTaken = new(HypreBp.VectorBits);
    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly SpeculativeGlobalHistory _hist;

    private readonly int[] _histLengths;
    private readonly byte[] _localHistory; // [LocalTableSize], LocalHistoryBits wide
    private readonly HdVector[] _notTaken;

    // Scratch buffer reused across calls — predictors in this codebase are accessed from a
    // single simulation thread, so no reentrancy concern.
    private readonly ulong[] _queryBuf = new ulong[HypreBp.VectorWords];

    private readonly HdVector[] _taken;
    private readonly int _threshold;

    /// <summary>
    ///     Constructs a HYPRE predictor.
    /// </summary>
    /// <param name="histLengths">
    ///     Geometric history lengths for the tagged accumulator tables, each ≤ 64 bits. If null,
    ///     a default 5-length series is used.
    /// </param>
    public HypreBp(int[]? histLengths = null) {
        _histLengths = histLengths ?? HypreBp.DefaultHistLengths;
        _hist = new SpeculativeGlobalHistory(_histLengths.Max());

        _taken = new HdVector[_histLengths.Length];
        _notTaken = new HdVector[_histLengths.Length];
        for (var i = 0; i < _histLengths.Length; i++) {
            _taken[i] = new HdVector(HypreBp.VectorBits);
            _notTaken[i] = new HdVector(HypreBp.VectorBits);
        }

        _localHistory = new byte[HypreBp.LocalTableSize];

        // D/2 + 3σ, σ = √D⁄2 (binomial(D, 0.5) standard deviation) — see class doc §2.2.
        var std = (int)Math.Round(Math.Sqrt(HypreBp.VectorBits) / 2.0);
        _threshold = HypreBp.VectorBits / 2 + 3 * std;
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool pred = Resolve(pc, _hist.Value);
        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;

        _hist.Commit(
            taken, () => {
                for (var i = 0; i < _histLengths.Length; i++) {
                    FillHv(pc, FoldedHist(_hist.Value, _histLengths[i]), _queryBuf);
                    TrainLevel(_taken[i], _notTaken[i], _queryBuf, taken);
                }

                int li = LocalIdx(pc);
                FillHv(pc, _localHistory[li], _queryBuf);
                TrainLevel(_baseTaken, _baseNotTaken, _queryBuf, taken);
                _localHistory[li] = (byte)(((_localHistory[li] << 1) | (taken ? 1 : 0)) &
                                           ((1 << HypreBp.LocalHistoryBits) - 1));
            }
        );
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) => _hist.Speculate(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _hist.Recover();

    // ── Internals ─────────────────────────────────────────────────────────────

    private bool Resolve(ulong pc, ulong hist) {
        for (int i = _histLengths.Length - 1; i >= 0; i--) {
            FillHv(pc, FoldedHist(hist, _histLengths[i]), _queryBuf);
            int matchT = MatchCount(_queryBuf, _taken[i]);
            int matchNt = MatchCount(_queryBuf, _notTaken[i]);
            if (matchT > _threshold || matchNt > _threshold) return matchT >= matchNt;
        }

        int li = LocalIdx(pc);
        FillHv(pc, _localHistory[li], _queryBuf);
        return MatchCount(_queryBuf, _baseTaken) >= MatchCount(_queryBuf, _baseNotTaken);
    }

    private void TrainLevel(HdVector taken, HdVector notTaken, ReadOnlySpan<ulong> query, bool outcome) {
        int matchT = MatchCount(query, taken);
        int matchNt = MatchCount(query, notTaken);

        if (outcome) {
            if (matchT <= _threshold) Add(taken, query);         // not yet confidently Taken: reinforce
            if (matchNt > _threshold) Subtract(notTaken, query); // wrongly confident NT: remove
        }
        else {
            if (matchNt <= _threshold) Add(notTaken, query);
            if (matchT > _threshold) Subtract(taken, query);
        }
    }

    private static int LocalIdx(ulong pc) => (int)((pc >> 2) & (HypreBp.LocalTableSize - 1));

    private static ulong FoldedHist(ulong hist, int len) => len >= 64 ? hist : hist & ((1UL << len) - 1);

    // Deterministic pseudo-random HV generation from (pc, history/local-history bits) — the
    // paper's noted alternative to a stored per-PC HV dictionary (§3, "How do HVs capture
    // branch patterns"): "it is possible to use a hashing mechanism that processes the branch
    // PC address directly into HVs... requires the hash to be processed every time [it's needed]".
    private static void FillHv(ulong pc, ulong feature, Span<ulong> dest) {
        ulong seed = (pc * 0x9E3779B97F4A7C15UL) ^ (feature * 0xC2B2AE3D27D4EB4FUL);
        for (var w = 0; w < dest.Length; w++) {
            seed ^= seed >> 33;
            seed *= 0xFF51AFD7ED558CCDUL;
            seed ^= seed >> 33;
            seed *= 0xC4CEB9FE1A85EC53UL;
            seed ^= seed >> 33;
            dest[w] = seed;
        }
    }

    private static int MatchCount(ReadOnlySpan<ulong> query, HdVector v) {
        var mismatches = 0;
        for (var w = 0; w < query.Length; w++) mismatches += BitOperations.PopCount(query[w] ^ v.SignBits[w]);
        return HypreBp.VectorBits - mismatches;
    }

    private static void Add(HdVector v, ReadOnlySpan<ulong> query) => Adjust(v, query, +1);
    private static void Subtract(HdVector v, ReadOnlySpan<ulong> query) => Adjust(v, query, -1);

    private static void Adjust(HdVector v, ReadOnlySpan<ulong> query, int sign) {
        for (var w = 0; w < query.Length; w++) {
            ulong bits = query[w];
            for (var b = 0; b < 64; b++) {
                int idx = w * 64 + b;
                bool bit = ((bits >> b) & 1) != 0;
                int delta = (bit ? 1 : -1) * sign;
                var c = (sbyte)Math.Clamp(v.Counters[idx] + delta, sbyte.MinValue, sbyte.MaxValue);
                v.Counters[idx] = c;
                if (c >= 0)
                    v.SignBits[w] |= 1UL << b;
                else
                    v.SignBits[w] &= ~(1UL << b);
            }
        }
    }

    // Saturating-counter HyperVector: Counters holds the raw per-element magnitude,
    // SignBits is the packed bit-vector of each counter's sign (>=0 → 1), kept incrementally
    // up to date so Hamming comparisons (MatchCount) never need to re-derive it.
    private sealed class HdVector(int bits) {
        public readonly sbyte[] Counters = new sbyte[bits];
        public readonly ulong[] SignBits = new ulong[bits / 64];
    }
}