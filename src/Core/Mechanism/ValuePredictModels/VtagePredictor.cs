using System.Numerics;

namespace Mechanism.ValuePredictModels;

/// <summary>
///     Value TAgged GEometric history length predictor (VTAGE — Perais &amp; Seznec, HPCA 2014,
///     "Practical Data Value Speculation for Future High-end Processors", §6). Adapts the
///     ITTAGE indirect-branch-target predictor (Seznec &amp; Michaud, JILP 2006) to value
///     prediction: a tagless <see cref="LvpPredictor" /> base component backed by N tagged
///     components indexed by a hash of the PC and a geometrically-increasing number of global
///     branch-history bits. The longest-history tagged component with a matching tag is the
///     "provider"; if none matches, the base component's (PC-only) prediction is used instead.
///     <para>
///         Unlike local-value-history predictors (FCM/D-FCM/DDISC), VTAGE's index depends only
///         on control flow, not on previous values of the same instruction — so it can predict
///         back-to-back occurrences of an instruction in a tight loop with no critical
///         same-cycle dependency (§3.2 of the paper).
///     </para>
/// </summary>
public sealed class VtagePredictor : IValuePredictor {
    private const int TagWidthBase = 12; // tag width for component rank r (1-based) is TagWidthBase + r

    /// <summary>Geometrically increasing history lengths, one per tagged component (paper §7.1.1, Table 1).</summary>
    private static readonly int[] HistLengths = [2, 4, 8, 16, 32, 64,];

    private readonly LvpPredictor _base;
    private readonly TaggedEntry[][] _components; // [component][index]
    private readonly int _entriesPerComponent;
    private readonly ForwardProbabilisticCounter _fpc;
    private readonly SpeculativeValueHistory _history;
    private readonly int _indexBits;
    private readonly Random _rng;

    /// <param name="baseEntries">Size of the tagless base (LVP) component. Must be a power of two.</param>
    /// <param name="entriesPerComponent">Size of each tagged component. Must be a power of two.</param>
    /// <param name="seed">Deterministic seed for FPC transitions and random component allocation.</param>
    public VtagePredictor(int baseEntries = 4096, int entriesPerComponent = 1024, int seed = 0) {
        _base = new LvpPredictor(baseEntries, seed);
        _entriesPerComponent = entriesPerComponent;
        _indexBits = BitOperations.Log2((uint)entriesPerComponent);
        _components = new TaggedEntry[VtagePredictor.HistLengths.Length][];
        for (var c = 0; c < _components.Length; c++) _components[c] = new TaggedEntry[entriesPerComponent];
        _history = new SpeculativeValueHistory(VtagePredictor.HistLengths[^1]);
        _fpc = new ForwardProbabilisticCounter(seed);
        _rng = new Random(seed);
    }

    private int NumComponents => _components.Length;

    /// <inheritdoc />
    public bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value) {
        ulong hist = history.Global;
        if (TryFindProvider(pc, hist, out int rank, out int idx)) {
            ref TaggedEntry provider = ref _components[rank][idx];
            if (ForwardProbabilisticCounter.IsSaturated(provider.Confidence)) {
                value = provider.Val;
                return true;
            }

            value = 0;
            return false; // a provider matched but isn't confident yet: no cascade to shorter history
        }

        return _base.TryPredict(pc, history, out value);
    }

    /// <inheritdoc />
    public void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue) {
        ulong hist = history.Global;
        if (TryFindProvider(pc, hist, out int rank, out int idx)) {
            ref TaggedEntry provider = ref _components[rank][idx];
            if (provider.Val == actualValue) {
                _fpc.OnCorrect(ref provider.Confidence);
                provider.Useful = true;
            }
            else {
                if (provider.Confidence == 0) provider.Val = actualValue;
                _fpc.OnMispredict(ref provider.Confidence);
                AllocateUpper(rank, pc, hist, actualValue);
            }
        }
        else { AllocateUpper(-1, pc, hist, actualValue); }

        _base.Update(pc, history, actualValue);
    }

    /// <inheritdoc />
    public void OnBranchFetched(bool predictedTaken) => _history.Speculate(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _history.Recover();

    /// <inheritdoc />
    public ValueHistoryCheckpoint CaptureHistory() => new(_history.Capture());

    /// <inheritdoc />
    public void RestoreHistory(in ValueHistoryCheckpoint checkpoint, bool actualTaken) =>
        _history.RestoreTo(checkpoint.Global, actualTaken);

    /// <summary>
    ///     Advances the committed history shadow with a branch's resolved outcome. Called once per
    ///     committing branch, in program order — not part of <see cref="IValuePredictor" /> since
    ///     it concerns branch commits, not value commits. The pipeline calls this on any
    ///     <see cref="IValuePredictor" /> that is also a <see cref="VtagePredictor" />.
    /// </summary>
    public void AdvanceCommittedHistory(bool taken) => _history.AdvanceCommitted(taken);

    /// <summary>
    ///     Searches components from longest to shortest history for a valid tag match. The first
    ///     (longest-history) match found is the provider.
    /// </summary>
    private bool TryFindProvider(ulong pc, ulong hist, out int rank, out int idx) {
        for (int c = NumComponents - 1; c >= 0; c--) {
            int i = Index(c, pc, hist);
            if (_components[c][i].Valid && _components[c][i].Tag == Tag(c, pc, hist)) {
                rank = c;
                idx = i;
                return true;
            }
        }

        rank = -1;
        idx = -1;
        return false;
    }

    /// <summary>
    ///     Allocates a new entry in a component using a longer history than <paramref name="providerRank" />
    ///     (-1 = no tagged provider, so every component is a candidate), following VTAGE's
    ///     replacement policy (paper §6): prefer a not-useful slot among the upper components,
    ///     chosen randomly if several qualify; if none qualify, age (clear the useful bit of)
    ///     every upper component's indexed slot instead, without allocating.
    /// </summary>
    private void AllocateUpper(int providerRank, ulong pc, ulong hist, ulong actualValue) {
        Span<int> candidates = stackalloc int[VtagePredictor.HistLengths.Length];
        var candidateCount = 0;
        for (int c = providerRank + 1; c < NumComponents; c++)
            if (!_components[c][Index(c, pc, hist)].Useful)
                candidates[candidateCount++] = c;

        if (candidateCount == 0) {
            for (int c = providerRank + 1; c < NumComponents; c++) _components[c][Index(c, pc, hist)].Useful = false;
            return;
        }

        int chosen = candidates[_rng.Next(candidateCount)];
        int chosenIdx = Index(chosen, pc, hist);
        ref TaggedEntry e = ref _components[chosen][chosenIdx];
        e.Valid = true;
        e.Tag = Tag(chosen, pc, hist);
        e.Val = actualValue;
        e.Confidence = 0;
        e.Useful = false;
    }

    private int Index(int component, ulong pc, ulong hist) {
        uint folded = Fold(hist, VtagePredictor.HistLengths[component], _indexBits);
        return (int)((folded ^ (uint)(pc >> 2)) & (uint)(_entriesPerComponent - 1));
    }

    private uint Tag(int component, ulong pc, ulong hist) {
        int tagBits = VtagePredictor.TagWidthBase + component + 1; // rank is 1-based
        uint folded = Fold(hist, VtagePredictor.HistLengths[component], tagBits);
        var pcMix = (uint)((pc >> 2) ^ (pc >> (2 + tagBits)));
        return (folded ^ pcMix) & ((1u << tagBits) - 1);
    }

    /// <summary>
    ///     XOR-folds the low <paramref name="bits" /> bits of <paramref name="value" /> down to
    ///     <paramref name="outBits" /> width.
    /// </summary>
    private static uint Fold(ulong value, int bits, int outBits) {
        ulong masked = bits >= 64 ? value : value & ((1UL << bits) - 1);
        uint result = 0;
        var shift = 0;
        while (shift < bits) {
            result ^= (uint)(masked >> shift) & (uint)((1UL << outBits) - 1);
            shift += outBits;
        }

        return result & (uint)((1UL << outBits) - 1);
    }

    private struct TaggedEntry {
        public bool Valid;
        public uint Tag;
        public ulong Val;
        public byte Confidence;
        public bool Useful;
    }
}

/// <summary>
///     A global value-history shift register with speculative and committed copies, private to
///     <see cref="VtagePredictor" />. Deliberately not shared with
///     <c>Mechanism.BranchPredictModels.SpeculativeGlobalHistory</c> (internal to that file and
///     architecturally a branch-predictor concern) — this is a distinct ~20-line utility for an
///     unrelated consumer.
///     <para>
///         <see cref="Value" /> is the speculative working history, advanced at fetch
///         (<see cref="Speculate" />) and rewound on a squash (<see cref="Recover" /> for a full
///         flush, <see cref="RestoreTo" /> for an execute-time partial squash).
///         <see cref="Committed" /> only ever advances at a branch's commit
///         (<see cref="AdvanceCommitted" />), in program order — since commit is in-order and only
///         branches fold history, <see cref="Committed" /> at any non-branch instruction's own
///         commit is exactly the history that instruction saw at its own (speculative) fetch time,
///         with no separate per-instruction checkpoint required for training.
///     </para>
/// </summary>
internal sealed class SpeculativeValueHistory {
    private readonly ulong _mask;

    public SpeculativeValueHistory(int bits) => _mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    /// <summary>Speculative working history, used for Predict-time indexing.</summary>
    public ulong Value { get; private set; }

    /// <summary>Committed history shadow, used for Update-time (training) indexing.</summary>
    public ulong Committed { get; private set; }

    /// <summary>Folds a predicted branch direction into the speculative history at fetch.</summary>
    public void Speculate(bool taken) => Value = ((Value << 1) | (taken ? 1UL : 0UL)) & _mask;

    /// <summary>Discards wrong-path speculation on a full flush, restoring committed history.</summary>
    public void Recover() => Value = Committed;

    /// <summary>Captures the working history as a per-branch checkpoint, before that branch speculates.</summary>
    public ulong Capture() => Value;

    /// <summary>Restores to a captured checkpoint and folds the redirecting branch's resolved direction.</summary>
    public void RestoreTo(ulong value, bool actualTaken) {
        Value = value;
        Speculate(actualTaken);
    }

    /// <summary>Advances the committed shadow with a branch's resolved outcome, at commit.</summary>
    public void AdvanceCommitted(bool taken) => Committed = ((Committed << 1) | (taken ? 1UL : 0UL)) & _mask;
}