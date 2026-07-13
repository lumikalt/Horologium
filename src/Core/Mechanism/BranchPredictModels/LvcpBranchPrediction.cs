namespace Mechanism.BranchPredictModels;

/// <summary>
///     LVCP: Load Value Correlated Predictor, layered on top of TAGE-SC-L. — Man, Gou, Liu,
///     Chen &amp; Bao, "LVCP: A Load Value Correlated Predictor for TAGE-SC-L", CBP 2025.
///     <para>
///         The full LVC-TAGE-SC-L submission is TAGE + a Multiperspective Statistical
///         Corrector (MPSC: bias tables, three global-history tables, IMLI/IMLI-OH,
///         per-set/per-address local history) + Loop Predictor + LVCP. This class
///         implements only LVCP, the paper's namesake contribution and the one the TODO
///         item this predictor was built for ("correlate on register values rather than
///         history") names; the MPSC elaborations are orthogonal baseline improvements
///         already covered qualitatively by <see cref="TageScLPredictor" />'s own SC.
///     </para>
///     <para>
///         <b>LVCP design (paper Section 3.1–3.3).</b> An H2P Branch Table (HBT) — a
///         4-way, 64-set, PC-tagged table of 2-bit saturating mispredict counters —
///         classifies a branch as hard-to-predict once its counter saturates; counters
///         decay by one every 20000 branch commits. A 16-entry Load Tracking Queue (LTQ)
///         records the (PC, value) of every load as it completes (via
///         <see cref="NotifyRegisterResult" />, using <c>isLoad</c> in place of the
///         paper's decode-time Load Marking Table — a structure that exists purely to
///         detect loads for hardware that doesn't already tag them). A direct-mapped
///         correlation table — the paper's is set-associative, simplified here the same
///         way <see cref="LTagePredictor" />'s own tagged tables are direct-mapped rather
///         than associative — is indexed and tagged by a hash of (branch PC, load PC, load
///         value); each entry holds a direction bit, a 5-bit confidence counter, and a
///         direction-changed bit that permanently retires the entry the first time it
///         predicts the wrong way (paper Section 3.3). The paper's distant load buffer
///         (captures load-branch dependencies beyond the LTQ's 16-entry reach) is not
///         implemented — the LTQ alone captures the common near-branch case.
///     </para>
///     <para>
///         At predict time, if the branch is H2P, every LTQ position is searched (newest
///         first) for a saturated-confidence correlation-table hit; the first one found
///         overrides the TAGE-SC-L baseline (mirroring how <see cref="LlbpPredictor" /> and
///         <see cref="VlaTagePredictor" /> layer their own structures on top of the same
///         shared base). Training, in contrast, only ever touches the entry keyed by the
///         single newest LTQ load: retraining every queue position against this dynamic
///         instance's outcome would corrupt correlations already learned against older,
///         still-valid loads sitting deeper in the same queue. A new correlation entry is
///         allocated from the newest load when the branch is H2P, the slot was never
///         previously allocated, and the baseline TAGE-SC-L prediction (cached from the
///         immediately preceding <see cref="IBranchPredictor.Predict" /> call for this PC,
///         an out-of-order residual, harmless the same way
///         <see cref="LlbpPredictor.TrainLlbp" /> documents) missed. A retired
///         (direction-changed) slot is never reclaimed by later allocation — retirement is
///         permanent, per paper Section 3.3.
///     </para>
/// </summary>
public sealed class LvcpPredictor : TageScLPredictor, IValueAwareBranchPredictor {
    // ── H2P Branch Table ──────────────────────────────────────────────────────
    private const int HbtIndexBits = 6; // 64 sets
    private const int HbtWays = 4;
    private const byte HbtSatMax = 3;         // 2-bit counter
    private const int HbtDecayPeriod = 20000; // branch commits between decay sweeps

    // ── Load Tracking Queue ───────────────────────────────────────────────────
    private const int LtqCapacity = 16;

    // ── Correlation table ─────────────────────────────────────────────────────
    private const int CorrIndexBits = 10; // 1024 entries
    private const int CorrTableSize = 1 << LvcpPredictor.CorrIndexBits;
    private const byte ConfMax = 31; // 5-bit confidence counter
    private readonly CorrEntry[] _corr = new CorrEntry[LvcpPredictor.CorrTableSize];

    private readonly HbtEntry[][] _hbt; // [1 << HbtIndexBits][HbtWays]
    private readonly (ulong Pc, ulong Value)[] _ltq = new (ulong, ulong)[LvcpPredictor.LtqCapacity];
    private bool _havePredicted;

    private int _hbtDecayClock;

    // Cached across Predict → Update for the same PC, to approximate "did the baseline
    // TAGE-SC-L prediction (before any LVCP override) miss" without touching TageScLPredictor's
    // private SC internals. An out-of-order residual if two dynamic instances of the same PC
    // are predicted before either commits; harmless (affects only allocation timing, not
    // correctness) — the same class of imprecision LlbpPredictor documents for TrainLlbp.
    private bool _lastBaselinePred;
    private ulong _lastPredictedPc;
    private int _ltqCount;
    private int _ltqHead;

    /// <summary>
    ///     Constructs an LVCP-augmented TAGE-SC-L predictor.
    /// </summary>
    public LvcpPredictor() {
        _hbt = new HbtEntry[1 << LvcpPredictor.HbtIndexBits][];
        for (var i = 0; i < _hbt.Length; i++) _hbt[i] = new HbtEntry[LvcpPredictor.HbtWays];
    }

    /// <summary>Number of predictions where LVCP overrode the TAGE-SC-L baseline.</summary>
    public int LvcpOverrides { get; private set; }

    // ── IValueAwareBranchPredictor ────────────────────────────────────────────

    /// <inheritdoc />
    public void NotifyRegisterResult(ulong pc, int destReg, ulong value, bool isLoad) {
        if (!isLoad) return;
        _ltq[_ltqHead] = (pc, value);
        _ltqHead = (_ltqHead + 1) % LvcpPredictor.LtqCapacity;
        if (_ltqCount < LvcpPredictor.LtqCapacity) _ltqCount++;
    }

    // ── IBranchPredictor overrides ────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        bool baseline = base.ResolvePrediction(pc, provider, tagePred);
        _lastBaselinePred = baseline;
        _lastPredictedPc = pc;
        _havePredicted = true;

        if (IsH2P(pc) && TryLvcpPredict(pc, out bool lvcpPred)) {
            LvcpOverrides++;
            return lvcpPred;
        }

        return baseline;
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident);

        bool baselineMispredicted = _havePredicted && pc == _lastPredictedPc && _lastBaselinePred != taken;
        TrainHbt(pc, baselineMispredicted);
        if (IsH2P(pc)) TrainCorrTable(pc, taken, baselineMispredicted);
    }

    // ── LVCP internals ────────────────────────────────────────────────────────

    // Searches the full LTQ window for a confident hit — a correlation may have been
    // learned against a load several positions back, not necessarily the newest one.
    private bool TryLvcpPredict(ulong pc, out bool dir) {
        for (var age = 0; age < _ltqCount; age++) {
            (ulong loadPc, ulong loadVal) = LtqAt(age);
            ushort tag = CorrTag(pc, loadPc, loadVal);
            ref CorrEntry e = ref _corr[CorrIdx(pc, loadPc, loadVal)];
            if (e is { Valid: true, DirChanged: false, } && e.Tag == tag && e.Conf >= LvcpPredictor.ConfMax) {
                dir = e.Dir;
                return true;
            }
        }

        dir = false;
        return false;
    }

    // Trains only the entry keyed by the single newest load in the LTQ. Training against
    // every queue position (as a search-then-train symmetry might suggest) would retrain
    // stale, unrelated entries with this dynamic instance's outcome every cycle — corrupting
    // correlations learned against older, still-valid loads in the same queue.
    private void TrainCorrTable(ulong pc, bool taken, bool baselineMispredicted) {
        if (_ltqCount == 0) return;
        (ulong loadPc, ulong loadVal) = LtqAt(0);
        ushort tag = CorrTag(pc, loadPc, loadVal);
        ref CorrEntry e = ref _corr[CorrIdx(pc, loadPc, loadVal)];

        if (e is { Valid: true, DirChanged: false, } && e.Tag == tag) {
            if (e.Dir == taken) {
                if (e.Conf < LvcpPredictor.ConfMax) e.Conf++;
            }
            else {
                e.DirChanged = true; // wrong-direction hit: permanently retire this entry
            }

            return;
        }

        // Never reclaim a retired (DirChanged) slot — retirement is permanent (paper Section
        // 3.3): only a never-allocated entry may be trained fresh.
        if (baselineMispredicted && !e.Valid) e = new CorrEntry { Tag = tag, Valid = true, Dir = taken, Conf = 1, };
    }

    private void TrainHbt(ulong pc, bool mispredicted) {
        _hbtDecayClock++;
        if (_hbtDecayClock >= LvcpPredictor.HbtDecayPeriod) {
            _hbtDecayClock = 0;
            DecayHbt();
        }

        if (!mispredicted) return;

        HbtEntry[] set = _hbt[HbtIdx(pc)];
        byte tag = HbtTag(pc);
        for (var w = 0; w < LvcpPredictor.HbtWays; w++) {
            ref HbtEntry e = ref set[w];
            if (!e.Valid || e.Tag != tag) continue;
            if (e.Ctr < LvcpPredictor.HbtSatMax) e.Ctr++;
            return;
        }

        for (var w = 0; w < LvcpPredictor.HbtWays; w++) {
            ref HbtEntry e = ref set[w];
            if (e.Ctr != 0) continue;
            e.Tag = tag;
            e.Ctr = 1;
            e.Valid = true;
            return;
        }
        // No free way (all counters useful/nonzero): skip allocation, as in the paper.
    }

    private void DecayHbt() {
        foreach (HbtEntry[] set in _hbt)
            for (var w = 0; w < LvcpPredictor.HbtWays; w++)
                if (set[w].Ctr > 0)
                    set[w].Ctr--;
    }

    private bool IsH2P(ulong pc) {
        HbtEntry[] set = _hbt[HbtIdx(pc)];
        byte tag = HbtTag(pc);
        return (from e in set where e.Valid && e.Tag == tag select e.Ctr >= LvcpPredictor.HbtSatMax).FirstOrDefault();
    }

    private (ulong Pc, ulong Value) LtqAt(int ageFromNewest) {
        int idx = ((_ltqHead - 1 - ageFromNewest) % LvcpPredictor.LtqCapacity + LvcpPredictor.LtqCapacity)
                % LvcpPredictor.LtqCapacity;
        return _ltq[idx];
    }

    private static int HbtIdx(ulong pc) => (int)((pc >> 2) & ((1u << LvcpPredictor.HbtIndexBits) - 1));

    private static byte HbtTag(ulong pc) => (byte)((pc >> (2 + LvcpPredictor.HbtIndexBits)) & 0xFF);

    private static int CorrIdx(ulong branchPc, ulong loadPc, ulong loadVal) =>
        (int)(Hash(branchPc, loadPc, loadVal) & (LvcpPredictor.CorrTableSize - 1));

    private static ushort CorrTag(ulong branchPc, ulong loadPc, ulong loadVal) =>
        (ushort)(Hash(branchPc, loadPc, loadVal) >> 32);

    private static ulong Hash(ulong a, ulong b, ulong c) {
        ulong h = a * 0x9E3779B97F4A7C15UL;
        h ^= b * 0xC2B2AE3D27D4EB4FUL;
        h ^= c * 0x165667B19E3779F9UL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return h;
    }

    private struct HbtEntry {
        public byte Tag;
        public byte Ctr;
        public bool Valid;
    }

    private struct CorrEntry {
        public ushort Tag;
        public bool Valid;
        public bool Dir;
        public byte Conf;
        public bool DirChanged;
    }
}