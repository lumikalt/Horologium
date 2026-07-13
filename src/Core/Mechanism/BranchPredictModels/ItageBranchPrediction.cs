namespace Mechanism.BranchPredictModels;

/// <summary>
///     ITTAGE: Indirect Target predictor using Tagged tables with Geometric History lengths.
///     — Seznec, "A 64-Kbytes ITTAGE Indirect Branch Predictor", CBP-3/JWAC-2, 2007.
///     <para>
///         Applies the TAGE philosophy to indirect-branch target prediction. N tagged tables
///         at geometrically increasing history lengths each store a full predicted target
///         address plus a 2-bit confidence counter (Ctr) and a 2-bit usefulness counter (U).
///         The longest-matching history entry provides the target. On a misprediction the
///         provider entry's Ctr is decremented first (hysteresis); only once Ctr reaches zero
///         does a further miss overwrite the stored Target. A new entry is allocated in the
///         next eligible longer-history table (U == 0 or invalid) with Ctr weak and U cleared;
///         if no table slot is free, usefulness counters are decayed to open one on a future
///         miss. U is incremented when the provider's target beat the alternate (shorter- or
///         un-tagged-history) prediction, decremented when it lost — same discipline as
///         <see cref="LTagePredictor" />'s usefulness bits.
///     </para>
///     <para>
///         Direction is predicted from a bimodal base (2-bit saturating counters). Real
///         deployments overlay ITTAGE on top of a direction predictor; here the bimodal is
///         bundled so this class satisfies IBranchPredictor standalone. ITTAGE's primary
///         advantage is distinguishing different targets for the same PC based on execution
///         history (e.g. virtual dispatch, computed gotos).
///     </para>
/// </summary>
public sealed class IttagePredictor : IBranchPredictor {
    private const int NumTables = 5;
    private const int TableIndexBits = 9; // 512 entries per tagged table
    private const int TagWidth = 10;
    private const int MaxHist = 55;
    private const int BaseIndexBits = 12; // 4096-entry bimodal base for direction

    private static readonly int[] HistLengths = [8, 13, 21, 34, 55,];

    private readonly byte[] _base; // 2-bit bimodal for direction (taken ≥ 2)
    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly SpeculativeGlobalHistory _hist;
    private readonly IttageEntry[][] _tables; // target-storing ITTAGE tables

    /// <summary>
    ///     Constructs an ITTAGE predictor.
    /// </summary>
    public IttagePredictor() {
        _base = new byte[1 << IttagePredictor.BaseIndexBits];
        Array.Fill(_base, (byte)1); // weakly not-taken

        _tables = new IttageEntry[IttagePredictor.NumTables][];
        for (var t = 0; t < IttagePredictor.NumTables; t++)
            _tables[t] = new IttageEntry[1 << IttagePredictor.TableIndexBits];

        _hist = new SpeculativeGlobalHistory(IttagePredictor.MaxHist);
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool dir = _base[BaseIdx(pc)] >= 2;
        if (knownTarget.HasValue) return new BranchPrediction(dir, knownTarget.Value);
        IttageLookup(pc, out _, out ulong target, out _);
        return new BranchPrediction(dir, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) =>
        _hist.Commit(
            taken, () => {
                // Capture state before any writes.
                IttageLookup(pc, out int provider, out ulong providerTarget, out ulong altTarget);
                bool targetCorrect = providerTarget == actualTarget;
                bool providerBeatAlt = providerTarget != altTarget;

                if (taken) _btb[pc] = actualTarget;
                Sat2(ref _base[BaseIdx(pc)], taken);

                if (provider >= 0) {
                    ref IttageEntry e = ref _tables[provider][TableIdx(pc, provider)];
                    if (targetCorrect) {
                        if (e.Ctr < 3) e.Ctr++;
                        if (providerBeatAlt && e.U < 3) e.U++;
                    }
                    else {
                        // Hysteresis: weaken confidence first; only overwrite the stored
                        // target once confidence is exhausted (Seznec 2007, §1.1.2).
                        if (e.Ctr > 0)
                            e.Ctr--;
                        else
                            e.Target = actualTarget;
                        if (providerBeatAlt && e.U > 0) e.U--;
                    }
                }

                if (!targetCorrect) AllocateOrDecay(pc, actualTarget, provider + 1);
            }
        );

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) => _hist.Speculate(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _hist.Recover();

    // ── Internals ─────────────────────────────────────────────────────────────

    // Scan tables shortest→longest; the longest tag match is the provider, the
    // second-longest is the alternate (mirrors LTagePredictor.TageLookup).
    private void IttageLookup(ulong pc, out int provider, out ulong providerTarget, out ulong altTarget) {
        ulong baseTarget = _btb.TryGetValue(pc, out ulong bt) ? bt : pc + 4;
        provider = -1;
        providerTarget = baseTarget;
        altTarget = baseTarget;

        for (var t = 0; t < IttagePredictor.NumTables; t++) {
            ref IttageEntry e = ref _tables[t][TableIdx(pc, t)];
            if (!e.Valid || e.Tag != (ushort)TableTag(pc, t)) continue;
            altTarget = providerTarget;
            provider = t;
            providerTarget = e.Target;
        }
    }

    private void AllocateOrDecay(ulong pc, ulong target, int startTable) {
        for (int t = startTable; t < IttagePredictor.NumTables; t++) {
            ref IttageEntry e = ref _tables[t][TableIdx(pc, t)];
            if (!e.Valid || e.U == 0) {
                e.Tag = (ushort)TableTag(pc, t);
                e.Target = target;
                e.Ctr = 1; // weak confidence (Seznec 2007, §1.1.2: "confidence counter is set to weak")
                e.U = 0;
                e.Valid = true;
                return;
            }
        }

        // No free slot — decay usefulness to open one on the next miss.
        for (int t = startTable; t < IttagePredictor.NumTables; t++) {
            ref IttageEntry e = ref _tables[t][TableIdx(pc, t)];
            if (e.U > 0) e.U--;
        }
    }

    // ── Index / tag helpers (same folding scheme as LTagePredictor) ───────────

    private int BaseIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << IttagePredictor.BaseIndexBits) - 1));

    private int TableIdx(ulong pc, int t) {
        int folded = FoldHist(IttagePredictor.HistLengths[t], IttagePredictor.TableIndexBits);
        return ((int)(pc >> 2) ^ folded) & ((1 << IttagePredictor.TableIndexBits) - 1);
    }

    private int TableTag(ulong pc, int t) {
        int f1 = FoldHist(IttagePredictor.HistLengths[t], IttagePredictor.TagWidth);
        int f2 = FoldHist(IttagePredictor.HistLengths[t] - 1, IttagePredictor.TagWidth - 1);
        return ((int)(pc >> 2) ^ f1 ^ (f2 << 1)) & ((1 << IttagePredictor.TagWidth) - 1);
    }

    private int FoldHist(int histLen, int outBits) {
        ulong hist = _hist.Value & ((1UL << histLen) - 1);
        int mask = (1 << outBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += outBits) res ^= (int)((hist >> sh) & (ulong)mask);
        return res;
    }

    private static void Sat2(ref byte c, bool taken) {
        switch (taken) {
            case true when c < 3:  c++; break;
            case false when c > 0: c--; break;
        }
    }

    // ── Entry struct ──────────────────────────────────────────────────────────

    private struct IttageEntry {
        public ushort Tag;
        public ulong Target;
        public byte Ctr; // 2-bit confidence in Target (hysteresis before overwrite)
        public byte U;   // 2-bit usefulness (provider beat alternate)
        public bool Valid;
    }
}