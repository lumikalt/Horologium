namespace Mechanism.BranchPredictModels;

/// <summary>
/// L-TAGE: TAGE branch predictor with a loop predictor overlay.
/// <para>
/// TAGE uses a bimodal base and N tagged tables with geometrically increasing
/// history lengths, each entry holding a partial tag, a 3-bit saturating
/// prediction counter, and a 2-bit usefulness counter. Longest-matching-history
/// wins. On misprediction an entry is allocated in the shortest longer-history
/// table with u=0; if none is available, usefulness bits are decayed.
/// </para>
/// <para>
/// The loop predictor tracks branches with a stable trip count. Once a branch
/// has exited a loop the same number of times on LoopConfThreshold consecutive
/// invocations, it takes over from TAGE and predicts taken/not-taken exactly.
/// </para>
/// </summary>
public class LTagePredictor : IBranchPredictor {
    // ── TAGE parameters ───────────────────────────────────────────────────────
    /// <summary>Number of tagged TAGE history tables (excluding the bimodal base).</summary>
    protected const int NumTables = 4;

    private const int TableIndexBits = 9; // 512 entries per tagged table
    private const int BaseIndexBits = 12; // 4096-entry bimodal base

    /// <summary>Bit width of the partial tag stored in each TAGE entry.</summary>
    protected const int TagWidth = 9; // bits of partial tag per entry

    private const int MaxHist = 34;

    // Geometrically increasing history lengths (~1.6×)
    private static readonly int[] HistLengths = [8, 13, 21, 34,];

    // ── Loop predictor parameters ─────────────────────────────────────────────
    private const int LoopIndexBits = 5; // 32-entry loop table
    private const int LoopTagWidth = 10;
    private const int LoopConfidence = 4; // consistent exits before confident

    // ── Storage ───────────────────────────────────────────────────────────────
    private readonly byte[] _base;        // 2-bit counters (taken ≥ 2)
    private readonly TageEntry[][] _tage; // [NumTables][1 << TableIndexBits]
    private readonly LoopEntry[] _loop;   // [1 << LoopIndexBits]

    /// <summary>
    /// Working global history register used for indexing at Predict time. In an
    /// out-of-order pipeline this is the *speculative* history: advanced at fetch by
    /// <see cref="SpeculativeHistoryUpdate"/> with predicted directions, and restored from
    /// <see cref="_committedGhr"/> on a flush. When no speculative updates arrive (in-order
    /// pipelines) it stays in lock-step with <see cref="_committedGhr"/>.
    /// </summary>
    protected ulong Ghr; // global history, LSB = most recent

    // Architectural history shadow: advanced only when a branch retires (in Update, with the
    // true outcome). Because commit and fetch are in-order, a branch that commits had every
    // older branch on its path predicted correctly, so this equals that branch's predict-time
    // history — Update swaps it into Ghr to train tables against the history the branch saw.
    private ulong _committedGhr;

    // Latches true once the pipeline drives speculative history (out-of-order). Until then
    // Update keeps Ghr == _committedGhr so the commit-time-history behaviour is bit-identical.
    private bool _speculative;

    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <summary>
    /// Constructs an L-TAGE predictor.
    /// </summary>
    public LTagePredictor() {
        _base = new byte[1 << LTagePredictor.BaseIndexBits];
        Array.Fill(_base, (byte)1); // weakly not-taken

        _tage = new TageEntry[LTagePredictor.NumTables][];
        for (var t = 0; t < LTagePredictor.NumTables; t++) {
            _tage[t] = new TageEntry[1 << LTagePredictor.TableIndexBits];
            for (var i = 0; i < _tage[t].Length; i++) _tage[t][i].Ctr = 3; // weakly not-taken (3-bit: taken ≥ 4)
        }

        _loop = new LoopEntry[1 << LTagePredictor.LoopIndexBits];
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        TageLookup(pc, out int provider, out bool tagePred, out _);
        bool pred = ResolvePrediction(pc, provider, tagePred);

        ref LoopEntry le = ref _loop[LoopIdx(pc)];
        if (le.Tag == (ushort)LoopTag(pc) && le.Confident)
            pred = le.CurrentIter < le.LearnedIter; // taken while not yet at trip count

        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    /// <inheritdoc />
    public virtual void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;

        // Train against committed (predict-time) history: swap it into Ghr for the table
        // lookups, then restore the working history afterwards. In non-speculative mode the
        // two are equal, so this is a no-op swap and behaviour is unchanged.
        ulong working = Ghr;
        Ghr = _committedGhr;

        TageLookup(pc, out int provider, out bool provPred, out bool altPred);
        int preScore = TageScore(pc, provider);
        bool preLoopConfident = _loop[LoopIdx(pc)].Tag == (ushort)LoopTag(pc) && _loop[LoopIdx(pc)].Confident;
        UpdateTage(pc, taken, provider, provPred, altPred);
        UpdateLoop(pc, taken);
        OnAfterUpdate(pc, taken, provPred, preScore, preLoopConfident);

        _committedGhr = ((_committedGhr << 1) | (taken ? 1UL : 0UL)) & ((1UL << LTagePredictor.MaxHist) - 1);
        // Non-speculative: keep the working history in lock-step with committed (old
        // behaviour). Speculative: leave it as fetch advanced it; flush restores it.
        Ghr = _speculative ? working : _committedGhr;
    }

    /// <inheritdoc />
    public virtual void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        _speculative = true;
        Ghr = ((Ghr << 1) | (predictedTaken ? 1UL : 0UL)) & ((1UL << LTagePredictor.MaxHist) - 1);
    }

    /// <inheritdoc />
    public virtual void RecoverSpeculativeHistory() => Ghr = _committedGhr;

    // ── Extension points for subclasses ──────────────────────────────────────

    /// <summary>
    /// Returns the score of the prediction for the given PC.
    /// </summary>
    /// <param name="pc">Program counter.</param>
    /// <param name="provider">
    /// Index of the provider table, or -1 if the PC is in the base table.
    /// </param>
    /// <returns></returns>
    protected int TageScore(ulong pc, int provider) {
        if (provider >= 0) {
            byte c = _tage[provider][TageIdx(pc, provider)].Ctr;
            return c * 2 - 7; // 0..7 → -7..+7
        }

        byte b = _base[BaseIdx(pc)];
        return b * 2 - 3; // 0..3 → -3..+3
    }

    /// <summary>
    /// Returns the prediction for the given PC.
    /// </summary>
    /// <param name="pc">
    /// Program counter.
    /// </param>
    /// <param name="provider">
    /// Index of the provider table, or -1 if the PC is in the base table.
    /// </param>
    /// <param name="tagePred">
    /// TAGE prediction, or false if the PC is in the base table.
    /// </param>
    /// <returns></returns>
    protected virtual bool ResolvePrediction(ulong pc, int provider, bool tagePred) => tagePred;

    /// <summary>
    /// Called after a branch update.
    /// </summary>
    /// <param name="pc">Program counter.</param>
    /// <param name="taken">Branch was taken.</param>
    /// <param name="provPred">
    /// TAGE prediction, or false if the PC is in the base table.
    /// </param>
    /// <param name="preScore">
    /// Score of the prediction before the update.
    /// </param>
    /// <param name="loopWasConfident">
    /// True if the loop predictor was confident before the update.
    /// </param>
    protected virtual void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) { }

    // ── TAGE internals ────────────────────────────────────────────────────────

    // Scan tables shortest→longest to find provider (longest match) and alt provider.
    private void TageLookup(
        ulong pc,
        out int provider,
        out bool provPred,
        out bool altPred
    ) {
        bool basePred = _base[BaseIdx(pc)] >= 2;
        provider = -1;
        provPred = basePred;
        altPred = basePred;

        for (var t = 0; t < LTagePredictor.NumTables; t++) {
            ref TageEntry e = ref _tage[t][TageIdx(pc, t)];
            if (!e.Valid || e.Tag != (ushort)TageTag(pc, t)) continue;
            altPred = provPred;
            provider = t;
            provPred = e.Ctr >= 4;
        }
    }

    private void UpdateTage(
        ulong pc,
        bool taken,
        int provider,
        bool provPred,
        bool altPred
    ) {
        // Update provider counter and usefulness.
        if (provider >= 0) {
            ref TageEntry e = ref _tage[provider][TageIdx(pc, provider)];
            Sat3(ref e.Ctr, taken);
            if (provPred != altPred) {
                if (provPred == taken) {
                    if (e.U < 3) e.U++;
                }
                else {
                    if (e.U > 0) e.U--;
                }
            }
        }
        else { Sat2(ref _base[BaseIdx(pc)], taken); }

        // On misprediction, allocate in shortest eligible longer-history table.
        if (provPred == taken) return;

        var alloc = false;
        for (int t = provider + 1; t < LTagePredictor.NumTables; t++) {
            ref TageEntry e = ref _tage[t][TageIdx(pc, t)];
            if (e.U == 0) {
                e.Tag = (ushort)TageTag(pc, t);
                e.Ctr = taken ? (byte)4 : (byte)3; // weakly in correct direction
                e.U = 0;
                e.Valid = true;
                alloc = true;
                break;
            }
        }

        // If no slot was free, decay usefulness to free one in a future miss.
        if (!alloc)
            for (int t = provider + 1; t < LTagePredictor.NumTables; t++) {
                ref TageEntry e = ref _tage[t][TageIdx(pc, t)];
                if (e.U > 0) e.U--;
            }
    }

    // ── Loop predictor internals ──────────────────────────────────────────────

    private void UpdateLoop(ulong pc, bool taken) {
        int li = LoopIdx(pc);
        var lt = (ushort)LoopTag(pc);
        ref LoopEntry le = ref _loop[li];

        if (le.Tag == lt) {
            if (taken) { le.CurrentIter++; }
            else {
                if (!le.Confident) {
                    if (le.LearnedIter == 0 || le.LearnedIter == le.CurrentIter) {
                        le.LearnedIter = le.CurrentIter;
                        if (le.ConfCount < 255) le.ConfCount++;
                        if (le.ConfCount >= LTagePredictor.LoopConfidence) le.Confident = true;
                    }
                    else {
                        // Inconsistent trip count — restart tracking at this slot.
                        le = new LoopEntry { Tag = lt, };
                    }
                }
                else if (le.CurrentIter != le.LearnedIter) {
                    // Trip count changed — lose confidence and re-learn.
                    le.Confident = false;
                    le.ConfCount = 0;
                    le.LearnedIter = le.CurrentIter;
                }

                le.CurrentIter = 0;
            }
        }
        else if (taken) {
            // First time seeing a taken branch at this slot: start tracking.
            le = new LoopEntry { Tag = lt, CurrentIter = 1, };
        }
    }

    // ── Index / tag helpers ───────────────────────────────────────────────────

    private static int BaseIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << LTagePredictor.BaseIndexBits) - 1));

    /// <summary>Returns the bimodal (T0) prediction for <paramref name="pc"/>.</summary>
    protected bool BimodalPrediction(ulong pc) => _base[BaseIdx(pc)] >= 2;

    private int TageIdx(ulong pc, int t) {
        int folded = FoldHist(LTagePredictor.HistLengths[t], LTagePredictor.TableIndexBits);
        return ((int)(pc >> 2) ^ folded) & ((1 << LTagePredictor.TableIndexBits) - 1);
    }

    // Two independent folds of different history lengths produce two tag halves,
    // reducing aliasing between branches that share the same PC index.
    /// <summary>Computes the partial TAGE tag for branch <paramref name="pc"/> in table <paramref name="t"/>.</summary>
    protected int TageTag(ulong pc, int t) {
        int f1 = FoldHist(LTagePredictor.HistLengths[t], LTagePredictor.TagWidth);
        int f2 = FoldHist(LTagePredictor.HistLengths[t] - 1, LTagePredictor.TagWidth - 1);
        return ((int)(pc >> 2) ^ f1 ^ (f2 << 1)) & ((1 << LTagePredictor.TagWidth) - 1);
    }

    private static int LoopIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << LTagePredictor.LoopIndexBits) - 1));

    // Mix high PC bits into low bits to separate tag from index.
    private static int LoopTag(ulong pc) {
        ulong x = pc >> 2;
        return (int)((x ^ (x >> LTagePredictor.LoopIndexBits)) & ((1u << LTagePredictor.LoopTagWidth) - 1));
    }

    // XOR-fold the lowest `histLen` bits of _ghr into `outBits` bits.
    // Masking _ghr to histLen bits first ensures no garbage above.
    private int FoldHist(int histLen, int outBits) {
        ulong hist = Ghr & ((1UL << histLen) - 1);
        int mask = (1 << outBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += outBits) res ^= (int)((hist >> sh) & (ulong)mask);
        return res;
    }

    // ── Saturating counter helpers ────────────────────────────────────────────

    private static void Sat2(ref byte c, bool taken) {
        switch (taken) {
            case true when c < 3:  c++; break;
            case false when c > 0: c--; break;
        }
    }

    private static void Sat3(ref byte c, bool taken) {
        switch (taken) {
            case true when c < 7:  c++; break;
            case false when c > 0: c--; break;
        }
    }

    // ── Entry structs ─────────────────────────────────────────────────────────

    private struct TageEntry {
        public ushort Tag;
        public byte Ctr; // 3-bit prediction counter (taken ≥ 4)
        public byte U;   // 2-bit usefulness counter
        public bool Valid;
    }

    private struct LoopEntry {
        public ushort Tag;
        public ushort LearnedIter; // stable trip count
        public ushort CurrentIter; // iterations since last exit
        public byte ConfCount;     // consistent exits seen
        public bool Confident;
    }
}