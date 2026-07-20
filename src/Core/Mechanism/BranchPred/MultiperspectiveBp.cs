namespace Mechanism.BranchPred;

/// <summary>
///     MPP: Multiperspective Perceptron Predictor — Jiménez, "Multiperspective Perceptron
///     Predictor", CBP 2025 (updates the CBP 2016 original).
///     <para>
///         The CBP2025 report (a 4-page summary) names ~15 candidate history "perspectives"
///         conceptually but withholds the tuned configuration, deferring to unpublished source
///         code. That source turned out to be publicly available: gem5 ships a line-for-line
///         port of Jiménez's submission (<c>src/cpu/pred/multiperspective_perceptron*.{hh,cc}</c>,
///         adapted by Javier Bueno Hedo), including the concrete per-feature parameters used by
///         the actual CBP-winning configurations. This implementation follows the
///         <c>MultiperspectivePerceptronTAGE8KB</c> configuration — the variant that layers MPP
///         features onto TAGE-SC-L (matching this predictor's own TAGE-SC-L-subclass shape) as
///         opposed to gem5's standalone-perceptron variants.
///     </para>
///     <para>
///         <b>Combination mechanism.</b> In that configuration MPP is not a separately arbitrated
///         predictor: its per-feature weighted sum (<see cref="MppSum" />) is injected as one more
///         additive term into the same signed sum TAGE-SC-L already thresholds
///         (<c>gem5::MultiperspectivePerceptronTAGE::lookup</c> seeds the Statistical Corrector's
///         <c>lsum</c> with <c>±22 + computePartialSum()</c> before the SC adds its own GEHL
///         contributions and thresholds the total). Here that becomes one extra summand added to
///         <see cref="TageScLBp.ResolvePrediction" />'s existing
///         <c>TageScore(pc,provider) + ScSum(pc)</c>, reusing the same
///         <see cref="TageScLBp.ScThreshold" /> magnitude gate — the natural translation of
///         "another additive term in the corrector sum" onto this codebase's existing TAGE-SC-L
///         shape.
///     </para>
///     <para>
///         <b>Feature set</b> (<c>MultiperspectivePerceptronTAGE8KB::createSpecs()</c>, verbatim):
///         BLURRYPATH(5,15) — the sum of the last 15 *distinct* values of <c>pc&gt;&gt;5</c> (a
///         coarse-grained, deduplicated path history); RECENCYPOS(31) — position of this PC's
///         truncated address in a 31-entry MRU recency stack (a dedicated "miss" bucket when
///         absent); GHISTMODPATH(3,7,1) — a 7-deep history of (address, direction) pairs recorded
///         only for branches whose hashed PC is divisible by 5; and two IMLI (inner-most-loop
///         iteration) counters — a backward-branch taken-streak and a forward-branch
///         not-taken-streak, each reset on the opposite outcome. (gem5's own base-class report
///         notes standalone GHIST is unused and backward-IMLI's own table is present despite the
///         CBP2025 report's claim that only forward-IMLI adds value beyond TAGE-SC-L — the actual
///         tuned submission keeps both, so both are implemented here.) Per-feature weights are
///         plain saturating 6-bit signed counters ([-32,31], increment on taken / decrement on
///         not-taken — no sign-magnitude representation or non-linear transfer function; those
///         exist only in gem5's *non*-TAGE-combined variants), each scaled by a fixed per-feature
///         coefficient (2.25 / 3.5 / 2.24 / 2.23 / 1.98) before summing.
///     </para>
///     <para>
///         Also confirmed directly from source, not assumed: the TAGE-combined variant has no
///         Bloom-filter trivial-branch bypass at all (<c>num_filter_entries=0</c>, and its own
///         <c>lookup()</c>/<c>update()</c> never touch the filter table — that machinery exists
///         only in gem5's standalone-perceptron variants), and speculative history update is
///         explicitly unimplemented (<c>fatal_if(speculative_update, ...)</c>; the shipped configs
///         all set <c>speculativeHistUpdate=false</c>) — so MPP's auxiliary histories updating only
///         at commit, as this file does, is not a simplification but the documented real behavior.
///     </para>
///     <para>
///         Simplifications from the reference source: (1) each feature table is a fixed 4096
///         entries rather than gem5's automatic hardware-bit-budget solver (<c>computeBits</c>) —
///         this codebase does not model bit budgets for any branch predictor, TAGE-SC-L included.
///         (2) The magnitude threshold gating MPP's contribution reuses
///         <see cref="TageScLBp.ScThreshold" /> (a fixed constant) rather than gem5's
///         per-PC dynamically-adapted <c>pUpdateThreshold</c> table — the same simplification this
///         codebase's <see cref="TageScLBp" /> already makes for its own SC threshold. None
///         of these affect the core mechanism: the five hashed-history features, their exact
///         weighted-sum combination into the TAGE-SC-L decision, and commit-time-only history
///         maintenance.
///     </para>
/// </summary>
public sealed class MultiperspectivePerceptronBp : TageScLBp {
    private const int TableSize = 4096;
    private const int CounterMin = -32;
    private const int CounterMax = 31; // 6-bit saturating counter, [-32, 31]

    private const int NumFeatures = 5;
    private const int FBlurryPath = 0;
    private const int FRecencyPos = 1;
    private const int FGhistModPath = 2;
    private const int FImli1 = 3; // backward-branch taken-streak
    private const int FImli4 = 4; // forward-branch not-taken-streak

    private const int BlurryScale = 5;
    private const int BlurryDepth = 15;
    private const int RecencyAssoc = 31;
    private const int ModHistDivisor = 5; // GHISTMODPATH's a=3 => modulus a+2
    private const int ModHistDepth = 7;

    // BLURRYPATH(5, 15, -1, 2.25, ...), RECENCYPOS(31, 3.5, ...), GHISTMODPATH(3, 7, 1, 2.24, ...),
    // IMLI(1, 2.23, ...), IMLI(4, 1.98, ...) — coefficients verbatim from createSpecs().
    private static readonly double[] Coeff = [2.25, 3.5, 2.24, 2.23, 1.98,];

    private readonly uint[] _blurryPath = new uint[MultiperspectivePerceptronBp.BlurryDepth]; // dedup'd pc>>5
    private readonly uint[] _imliCounter = new uint[4]; // only [0] (backward) and [3] (forward) are read
    private readonly bool[] _modHist = new bool[MultiperspectivePerceptronBp.ModHistDepth];
    private readonly ushort[] _modPath = new ushort[MultiperspectivePerceptronBp.ModHistDepth];
    private readonly ushort[] _recency = new ushort[MultiperspectivePerceptronBp.RecencyAssoc]; // MRU stack

    private readonly short[][] _tables; // [NumFeatures][TableSize]

    private ulong _pendingTarget;

    /// <summary>
    ///     Constructs a Multiperspective Perceptron predictor layered on TAGE-SC-L.
    /// </summary>
    public MultiperspectivePerceptronBp() {
        _tables = new short[MultiperspectivePerceptronBp.NumFeatures][];
        for (var f = 0; f < MultiperspectivePerceptronBp.NumFeatures; f++)
            _tables[f] = new short[MultiperspectivePerceptronBp.TableSize];
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        int total = TageScore(pc, provider) + ScSum(pc) + MppSum(pc);
        if (Math.Abs(total) > TageScLBp.ScThreshold) return total >= 0;
        return tagePred;
    }

    /// <inheritdoc />
    public override void Update(ulong pc, bool taken, ulong actualTarget) {
        _pendingTarget = actualTarget;
        base.Update(pc, taken, actualTarget);
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident); // SC training

        int preTotal = preScore + ScSum(pc) + MppSum(pc);
        bool scPred = preTotal >= 0;
        if (scPred != taken || Math.Abs(preTotal) < TageScLBp.ScThreshold) TrainMpp(pc, taken);

        AdvanceHistories(pc, taken, _pendingTarget);
    }

    // ── Perceptron core ──────────────────────────────────────────────────────

    private int MppSum(ulong pc) {
        Span<int> idx = stackalloc int[MultiperspectivePerceptronBp.NumFeatures];
        ComputeIndices(pc, idx);
        var y = 0;
        for (var f = 0; f < MultiperspectivePerceptronBp.NumFeatures; f++)
            y += (int)(MultiperspectivePerceptronBp.Coeff[f] * _tables[f][idx[f]]);
        return y;
    }

    private void TrainMpp(ulong pc, bool taken) {
        Span<int> idx = stackalloc int[MultiperspectivePerceptronBp.NumFeatures];
        ComputeIndices(pc, idx);
        for (var f = 0; f < MultiperspectivePerceptronBp.NumFeatures; f++) {
            ref short c = ref _tables[f][idx[f]];
            if (taken) {
                if (c < MultiperspectivePerceptronBp.CounterMax) c++;
            }
            else {
                if (c > MultiperspectivePerceptronBp.CounterMin) c--;
            }
        }
    }

    // ── Feature indexing ─────────────────────────────────────────────────────

    private void ComputeIndices(ulong pc, Span<int> idx) {
        var hpc = (uint)(pc ^ (pc >> 2));

        idx[MultiperspectivePerceptronBp.FBlurryPath] = Index(BlurryHash(), hpc, false);
        idx[MultiperspectivePerceptronBp.FRecencyPos] = Index(RecencyHash((ushort)(pc >> 2)), hpc, false);
        idx[MultiperspectivePerceptronBp.FGhistModPath] = Index(GhistModPathHash(), hpc, false);
        idx[MultiperspectivePerceptronBp.FImli1] = Index(_imliCounter[0], hpc, false);
        // imli_mask1 (0x70) also XORs table 4's index with the backward IMLI counter.
        idx[MultiperspectivePerceptronBp.FImli4] = Index(_imliCounter[3], hpc, true);
    }

    private int Index(ulong hash, uint hpc, bool extraImli0) {
        ulong h = hash;
        h <<= 20;
        h ^= hpc;
        if (extraImli0) h += _imliCounter[0];
        return (int)(h % MultiperspectivePerceptronBp.TableSize);
    }

    private uint BlurryHash() {
        uint x = 0;
        for (var i = 0; i < MultiperspectivePerceptronBp.BlurryDepth; i++) x += _blurryPath[i];
        return x;
    }

    private ulong RecencyHash(ushort pc2) {
        for (var i = 0; i < MultiperspectivePerceptronBp.RecencyAssoc; i++)
            if (_recency[i] == pc2)
                return (ulong)(i * MultiperspectivePerceptronBp.TableSize
                             / MultiperspectivePerceptronBp.RecencyAssoc);
        return MultiperspectivePerceptronBp.TableSize - 1; // miss bucket
    }

    private ulong GhistModPathHash() {
        ulong x = 0;
        for (var i = 0; i < MultiperspectivePerceptronBp.ModHistDepth; i++)
            x = (x << 1) + (((ulong)_modPath[i] << 1) | (_modHist[i] ? 1UL : 0UL));
        return x;
    }

    // ── History advance (commit-time only — real behavior, not a simplification; see class doc) ──

    private void AdvanceHistories(ulong pc, bool taken, ulong target) {
        var z = (uint)(pc >> MultiperspectivePerceptronBp.BlurryScale);
        if (_blurryPath[0] != z) {
            for (int i = MultiperspectivePerceptronBp.BlurryDepth - 1; i > 0; i--) _blurryPath[i] = _blurryPath[i - 1];
            _blurryPath[0] = z;
        }

        InsertRecency((ushort)(pc >> 2));

        var hpc = (uint)(pc ^ (pc >> 2));
        if (hpc % MultiperspectivePerceptronBp.ModHistDivisor == 0) {
            for (int i = MultiperspectivePerceptronBp.ModHistDepth - 1; i > 0; i--) {
                _modPath[i] = _modPath[i - 1];
                _modHist[i] = _modHist[i - 1];
            }

            _modPath[0] = (ushort)(pc >> 2);
            _modHist[0] = taken;
        }

        bool backward = target < pc;
        if (backward) {
            if (!taken)
                _imliCounter[0] = 0;
            else
                _imliCounter[0]++;
        }
        else {
            if (taken)
                _imliCounter[3] = 0;
            else
                _imliCounter[3]++;
        }
    }

    private void InsertRecency(ushort pc2) {
        int i;
        for (i = 0; i < MultiperspectivePerceptronBp.RecencyAssoc; i++)
            if (_recency[i] == pc2)
                break;
        if (i == MultiperspectivePerceptronBp.RecencyAssoc) {
            i = MultiperspectivePerceptronBp.RecencyAssoc - 1;
            _recency[i] = pc2;
        }

        ushort b = _recency[i];
        for (int j = i; j >= 1; j--) _recency[j] = _recency[j - 1];
        _recency[0] = b;
    }
}