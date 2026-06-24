namespace Mechanism.BranchPredictModels;

/// <summary>
/// ITTAGE: Indirect Target predictor using Tagged tables with Geometric History lengths
/// (Michaud 2011, refined by Seznec).
///
/// Applies the TAGE philosophy to indirect-branch target prediction. N tagged tables
/// at geometrically increasing history lengths each store a full predicted target
/// address. The longest-matching history entry wins; on a target misprediction a new
/// entry is allocated in the next eligible longer-history table. If no table slot is
/// free, usefulness counters are decayed to open one on the next miss.
///
/// Direction is predicted from a bimodal base (2-bit saturating counters). Real
/// deployments overlay ITTAGE on top of a direction predictor; here the bimodal is
/// bundled so this class satisfies IBranchPredictor standalone. ITTAGE's primary
/// advantage is distinguishing different targets for the same PC based on execution
/// history (e.g. virtual dispatch, computed gotos).
/// </summary>
public sealed class IttagePredictor : IBranchPredictor {
    private const int NumTables = 5;
    private const int TableIndexBits = 9; // 512 entries per tagged table
    private const int TagWidth = 10;
    private const int MaxHist = 55;
    private const int BaseIndexBits = 12; // 4096-entry bimodal base for direction

    private static readonly int[] HistLengths = [8, 13, 21, 34, 55,];

    private readonly byte[] _base;            // 2-bit bimodal for direction (taken ≥ 2)
    private readonly IttageEntry[][] _tables; // target-storing ITTAGE tables
    private readonly Dictionary<ulong, ulong> _btb = new();
    private ulong _ghr;

    public IttagePredictor() {
        _base = new byte[1 << IttagePredictor.BaseIndexBits];
        Array.Fill(_base, (byte)1); // weakly not-taken

        _tables = new IttageEntry[IttagePredictor.NumTables][];
        for (var t = 0; t < IttagePredictor.NumTables; t++)
            _tables[t] = new IttageEntry[1 << IttagePredictor.TableIndexBits];
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool dir = _base[BaseIdx(pc)] >= 2;
        ulong target = knownTarget.HasValue ? knownTarget.Value : PredictTarget(pc);
        return new BranchPrediction(dir, target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        // Capture state before any writes.
        int provider = FindProvider(pc);
        ulong prevTarget = provider >= 0
            ? _tables[provider][TableIdx(pc, provider)].Target
            : _btb.TryGetValue(pc, out ulong bt)
                ? bt
                : pc + 4;
        bool targetCorrect = prevTarget == actualTarget;

        if (taken) _btb[pc] = actualTarget;
        Sat2(ref _base[BaseIdx(pc)], taken);

        if (provider >= 0) {
            ref IttageEntry e = ref _tables[provider][TableIdx(pc, provider)];
            e.Target = actualTarget;
            if (!targetCorrect && e.U > 0) e.U--;
        }

        if (!targetCorrect) AllocateOrDecay(pc, actualTarget, provider + 1);

        _ghr = ((_ghr << 1) | (taken ? 1UL : 0UL)) & ((1UL << IttagePredictor.MaxHist) - 1);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private ulong PredictTarget(ulong pc) {
        ulong target = _btb.TryGetValue(pc, out ulong t) ? t : pc + 4;
        for (var i = 0; i < IttagePredictor.NumTables; i++) {
            ref IttageEntry e = ref _tables[i][TableIdx(pc, i)];
            if (e.Valid && e.Tag == (ushort)TableTag(pc, i)) target = e.Target; // longer history overrides shorter
        }

        return target;
    }

    private int FindProvider(ulong pc) {
        int provider = -1;
        for (var t = 0; t < IttagePredictor.NumTables; t++) {
            ref IttageEntry e = ref _tables[t][TableIdx(pc, t)];
            if (e.Valid && e.Tag == (ushort)TableTag(pc, t)) provider = t;
        }

        return provider;
    }

    private void AllocateOrDecay(ulong pc, ulong target, int startTable) {
        for (int t = startTable; t < IttagePredictor.NumTables; t++) {
            ref IttageEntry e = ref _tables[t][TableIdx(pc, t)];
            if (!e.Valid || e.U == 0) {
                e.Tag = (ushort)TableTag(pc, t);
                e.Target = target;
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
        ulong hist = _ghr & ((1UL << histLen) - 1);
        int mask = (1 << outBits) - 1;
        var res = 0;
        for (var sh = 0; sh < histLen; sh += outBits) res ^= (int)((hist >> sh) & (ulong)mask);
        return res;
    }

    private static void Sat2(ref byte c, bool taken) {
        if (taken && c < 3)
            c++;
        else if (!taken && c > 0) c--;
    }

    // ── Entry struct ──────────────────────────────────────────────────────────

    private struct IttageEntry {
        public ushort Tag;
        public ulong Target;
        public byte U; // 2-bit usefulness
        public bool Valid;
    }
}