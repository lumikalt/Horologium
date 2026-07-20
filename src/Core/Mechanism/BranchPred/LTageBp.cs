namespace Mechanism.BranchPred;

/// <summary>
///     L-TAGE: TAGE branch predictor with a loop predictor overlay.
///     <para>
///         TAGE uses a bimodal base and N tagged tables with geometrically increasing
///         history lengths, each entry holding a partial tag, a 3-bit saturating
///         prediction counter, and a 2-bit usefulness counter. Longest-matching-history
///         wins. On misprediction an entry is allocated in the shortest longer-history
///         table with u=0; if none is available, usefulness bits are decayed.
///     </para>
///     <para>
///         The loop predictor tracks branches with a stable trip count. Once a branch
///         has exited a loop the same number of times on LoopConfThreshold consecutive
///         invocations, it takes over from TAGE and predicts taken/not-taken exactly.
///     </para>
/// </summary>
public class LTageBp : IBranchPredictor {
    // ── TAGE parameters ───────────────────────────────────────────────────────
    /// <summary>Number of tagged TAGE history tables (excluding the bimodal base).</summary>
    protected const int NumTables = 4;

    private const int TableIndexBits = 9; // 512 entries per tagged table
    private const int BaseIndexBits = 12; // 4096-entry bimodal base

    /// <summary>Bit width of the partial tag stored in each TAGE entry.</summary>
    private const int TagWidth = 9; // bits of partial tag per entry

    /// <summary>Width, in bits, of the global history register.</summary>
    protected const int MaxHist = 34;

    // ── Loop predictor parameters ─────────────────────────────────────────────
    private const int LoopIndexBits = 5; // 32-entry loop table
    private const int LoopTagWidth = 10;
    private const int LoopConfidence = 4; // consistent exits before confident

    // Geometrically increasing history lengths (~1.6×)
    private static readonly int[] HistLengths = [8, 13, 21, 34,];

    // ── Storage ───────────────────────────────────────────────────────────────
    private readonly byte[] _base; // 2-bit counters (taken ≥ 2)

    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly LoopEntry[] _loop;   // [1 << LoopIndexBits]
    private readonly TageEntry[][] _tage; // [NumTables][1 << TableIndexBits]

    // Architectural history shadow: advanced only when a branch retires (in Update, with the
    // true outcome). Because commit and fetch are in-order, a branch that commits had every
    // older branch on its path predicted correctly, so this equals that branch's predict-time
    // history. Update swaps it into Ghr to train tables against the history the branch saw.
    private ulong _committedGhr;

    // Latches true once the pipeline drives speculative history (out-of-order). Until then
    // Update keeps Ghr == _committedGhr, so the commit-time-history behavior is bit-identical.
    private bool _speculative;

    /// <summary>
    ///     Working global history register used for indexing at Predict time. In an
    ///     out-of-order pipeline this is the *speculative* history: advanced at fetch by
    ///     <see cref="SpeculativeHistoryUpdate" /> with predicted directions, and restored from
    ///     <see cref="_committedGhr" /> on a flush. When no speculative updates arrive (in-order
    ///     pipelines), it stays in lock-step with <see cref="_committedGhr" />.
    /// </summary>
    protected ulong Ghr; // global history, LSB = most recent

    /// <summary>
    ///     Constructs an L-TAGE predictor.
    /// </summary>
    public LTageBp() {
        _base = new byte[1 << LTageBp.BaseIndexBits];
        Array.Fill(_base, (byte)1); // weakly not-taken

        _tage = new TageEntry[LTageBp.NumTables][];
        for (var t = 0; t < LTageBp.NumTables; t++) {
            _tage[t] = new TageEntry[1 << LTageBp.TableIndexBits];
            for (var i = 0; i < _tage[t].Length; i++) _tage[t][i].Ctr = 3; // weakly not-taken (3-bit: taken ≥ 4)
        }

        _loop = new LoopEntry[1 << LTageBp.LoopIndexBits];
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
        // lookups, then restore the working history afterward. In non-speculative mode the
        // two are equal, so this is a no-op swap, and behavior is unchanged.
        ulong working = Ghr;
        Ghr = _committedGhr;

        TageLookup(pc, out int provider, out bool provPred, out bool altPred);
        int preScore = TageScore(pc, provider);
        bool preLoopConfident = _loop[LoopIdx(pc)].Tag == (ushort)LoopTag(pc) && _loop[LoopIdx(pc)].Confident;
        if (!SuppressTageUpdate(pc)) {
            UpdateTage(pc, taken, provider, provPred, altPred);
            UpdateLoop(pc, taken);
        }

        OnAfterUpdate(pc, taken, provPred, preScore, preLoopConfident);

        _committedGhr = ((_committedGhr << 1) | (taken ? 1UL : 0UL)) & ((1UL << LTageBp.MaxHist) - 1);
        // Non-speculative: keep the working history in lock-step with committed (old
        // behavior). Speculative: leave it as fetch advanced it; flush restores it.
        Ghr = _speculative ? working : _committedGhr;
    }

    /// <inheritdoc />
    public virtual void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        _speculative = true;
        Ghr = ((Ghr << 1) | (predictedTaken ? 1UL : 0UL)) & ((1UL << LTageBp.MaxHist) - 1);
    }

    /// <inheritdoc />
    public virtual void RecoverSpeculativeHistory() => Ghr = _committedGhr;

    /// <inheritdoc />
    // The whole TAGE family indexes off Ghr (folded histories are recomputed on the fly from it),
    // so a single ulong checkpoint of Ghr is a complete history snapshot. No local component.
    public virtual BranchHistoryCheckpoint CaptureHistory(ulong pc) => new(Ghr);

    /// <inheritdoc />
    public virtual void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) {
        _speculative = true;
        Ghr = ((checkpoint.Global << 1) | (actualTaken ? 1UL : 0UL)) & ((1UL << LTageBp.MaxHist) - 1);
    }

    // ── Extension points for subclasses ──────────────────────────────────────

    /// <summary>
    ///     Returns the score of the prediction for the given PC.
    /// </summary>
    /// <param name="pc">Program counter.</param>
    /// <param name="provider">
    ///     Index of the provider table, or -1 if the PC is in the base table.
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
    ///     Returns the prediction for the given PC.
    /// </summary>
    /// <param name="pc">
    ///     Program counter.
    /// </param>
    /// <param name="provider">
    ///     Index of the provider table, or -1 if the PC is in the base table.
    /// </param>
    /// <param name="tagePred">
    ///     TAGE prediction, or false if the PC is in the base table.
    /// </param>
    /// <returns></returns>
    protected virtual bool ResolvePrediction(ulong pc, int provider, bool tagePred) => tagePred;

    /// <summary>
    ///     Called after a branch update.
    /// </summary>
    /// <param name="pc">Program counter.</param>
    /// <param name="taken">Branch was taken.</param>
    /// <param name="provPred">
    ///     TAGE prediction, or false if the PC is in the base table.
    /// </param>
    /// <param name="preScore">
    ///     Score of the prediction before the update.
    /// </param>
    /// <param name="loopWasConfident">
    ///     True if the loop predictor was confident before the update.
    /// </param>
    protected virtual void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) { }

    /// <summary>
    ///     When true, skips the TAGE table and loop table writes (and, transitively, any
    ///     subclass's <see cref="OnAfterUpdate" /> training that itself checks this hook — see
    ///     <see cref="TageScLBp.OnAfterUpdate" />) for this branch, while global history
    ///     still advances normally. Used by <see cref="BullseyeBp" /> to stop polluting
    ///     the TAGE-SC-L substrate for branches its perceptron layer has taken over, without
    ///     breaking history-based indexing for every other branch. Default false.
    /// </summary>
    protected virtual bool SuppressTageUpdate(ulong pc) => false;

    /// <summary>
    ///     Usefulness counter of the given PC's provider-table entry, or 0 if the PC resolved to
    ///     the untagged base predictor. Exposed for subclasses (e.g. <see cref="BullseyeBp" />'s
    ///     TAGE confidence gate) that need the raw usefulness bit, not just <see cref="TageScore" />.
    /// </summary>
    protected byte TageUsefulness(ulong pc, int provider) =>
        provider >= 0 ? _tage[provider][TageIdx(pc, provider)].U : (byte)0;

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

        for (var t = 0; t < LTageBp.NumTables; t++) {
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
        for (int t = provider + 1; t < LTageBp.NumTables; t++) {
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
            for (int t = provider + 1; t < LTageBp.NumTables; t++) {
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
                        if (le.ConfCount >= LTageBp.LoopConfidence) le.Confident = true;
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
        (int)((pc >> 2) & ((1u << LTageBp.BaseIndexBits) - 1));

    /// <summary>Returns the bimodal (T0) prediction for <paramref name="pc" />.</summary>
    protected bool BimodalPrediction(ulong pc) => _base[BaseIdx(pc)] >= 2;

    private int TageIdx(ulong pc, int t) {
        int folded = FoldHist(LTageBp.HistLengths[t], LTageBp.TableIndexBits);
        return ((int)(pc >> 2) ^ folded) & ((1 << LTageBp.TableIndexBits) - 1);
    }

    // Two independent folds of different history lengths produce two tag halves,
    // reducing aliasing between branches that share the same PC index.
    /// <summary>Computes the partial TAGE tag for branch <paramref name="pc" /> in table <paramref name="t" />.</summary>
    protected int TageTag(ulong pc, int t) {
        int f1 = FoldHist(LTageBp.HistLengths[t], LTageBp.TagWidth);
        int f2 = FoldHist(LTageBp.HistLengths[t] - 1, LTageBp.TagWidth - 1);
        return ((int)(pc >> 2) ^ f1 ^ (f2 << 1)) & ((1 << LTageBp.TagWidth) - 1);
    }

    private static int LoopIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << LTageBp.LoopIndexBits) - 1));

    // Mix high PC bits into low bits to separate tag from index.
    private static int LoopTag(ulong pc) {
        ulong x = pc >> 2;
        return (int)((x ^ (x >> LTageBp.LoopIndexBits)) & ((1u << LTageBp.LoopTagWidth) - 1));
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