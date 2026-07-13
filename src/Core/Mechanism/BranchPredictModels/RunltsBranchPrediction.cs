namespace Mechanism.BranchPredictModels;

/// <summary>
///     RUNLTS's sR component: register-value-correlated branch prediction, layered
///     on top of TAGE-SC-L. — Koizumi, Maekawa, Mizuno, Kuroki, Tsumura &amp; Shioya,
///     "RUNLTS: Register-value-aware Predictor Utilizing Nested Large Tables", CBP 2025.
///     <para>
///         RUNLTS extends the CBP-5 TAGE-SC-L baseline with three components (sC:
///         call-stack history, sI: BrIMLI/TaIMLI, sR: register-value correlation) plus
///         dynamic tagged-table allocation throttling. This class implements only sR —
///         the component the paper's own ablation (Fig. 7) attributes the largest and
///         broadest MPKI gain to, and the one named by the TODO item this predictor was
///         built for ("correlate on register values rather than history"). sC, sI, and
///         allocation throttling are independent, orthogonal improvements to the shared
///         TAGE-SC-L baseline and are not implemented here.
///     </para>
///     <para>
///         <b>sR design (paper Section 4.2).</b> A Tomasulo-like freshness table tracks,
///         per logical integer register, whether a 12-bit value digest is currently valid
///         (set when the producing instruction completes at execute; invalidated after 256
///         subsequent register-producing instructions — the paper's decode-based staleness
///         window, approximated here on notification count since Horologium does not expose
///         a decode-time hook to this predictor). The 64 registers of the paper's ISA (32
///         INT + 32 FP) become Horologium's 32 integer registers, split into the same eight
///         banks (four registers per bank instead of eight, since only integer writes reach
///         this predictor — see <see cref="IValueAwareBranchPredictor" />; FP/flag digest
///         formats from Fig. 6(b)/(c) are consequently not implemented, only the INT format).
///     </para>
///     <para>
///         Per bank, a PC-indexed weight vector picks the most useful <em>currently valid</em>
///         register in that bank (mirroring the SC's per-history-length table but keyed on
///         register slot instead of history length); that register's digest indexes a second,
///         PC-XOR-digest table of signed direction counters, exactly as
///         <see cref="TageScLPredictor" />'s own SC tables are indexed by PC XOR folded history.
///         The eight banks' counters sum to an sR score. Unlike SC (which is summed into TAGE's
///         score inside one shared threshold), sR is applied as a final override layer when its
///         own magnitude is confident — the same layering <see cref="LlbpPredictor" /> and
///         <see cref="VlaTagePredictor" /> use — since TAGE-SC-L's SC internals are private
///         to that class.
///     </para>
/// </summary>
public sealed class RunltsPredictor : TageScLPredictor, IValueAwareBranchPredictor {
    private const int NumBanks = 8;
    private const int RegsPerBank = 4;       // 32 integer registers / 8 banks
    private const int TableSize = 128;       // entries per bank's weight/direction tables
    private const int StalenessWindow = 256; // notifications before a digest goes stale
    private const int SrThreshold = 8;

    // Freshness table: one slot per integer register (Tomasulo-like valid/tag).
    private readonly int[] _digest = new int[RunltsPredictor.RegsPerBank * RunltsPredictor.NumBanks];

    // Second table: per-bank, per (PC XOR digest)-index, signed direction counter.
    private readonly sbyte[][] _dir; // [bank][idx]
    private readonly long[] _setAt = new long[RunltsPredictor.RegsPerBank * RunltsPredictor.NumBanks];
    private readonly bool[] _valid = new bool[RunltsPredictor.RegsPerBank * RunltsPredictor.NumBanks];

    // First table: per-bank, per-PC-index, weight of each of the bank's register slots.
    private readonly sbyte[][][] _weight; // [bank][pcIdx][slot]

    private long _clock;

    /// <summary>
    ///     Constructs an sR-augmented TAGE-SC-L predictor.
    /// </summary>
    public RunltsPredictor() {
        _weight = new sbyte[RunltsPredictor.NumBanks][][];
        _dir = new sbyte[RunltsPredictor.NumBanks][];
        for (var b = 0; b < RunltsPredictor.NumBanks; b++) {
            _dir[b] = new sbyte[RunltsPredictor.TableSize];
            _weight[b] = new sbyte[RunltsPredictor.TableSize][];
            for (var i = 0; i < RunltsPredictor.TableSize; i++) _weight[b][i] = new sbyte[RunltsPredictor.RegsPerBank];
        }
    }

    /// <summary>Number of predictions where sR's own confidence exceeded the override threshold.</summary>
    public int SrOverrides { get; private set; }

    // ── IValueAwareBranchPredictor ────────────────────────────────────────────

    /// <inheritdoc />
    public void NotifyRegisterResult(ulong pc, int destReg, ulong value, bool isLoad) {
        _clock++;
        if ((uint)destReg >= _valid.Length) return;
        _digest[destReg] = Digest(value);
        _setAt[destReg] = _clock;
        _valid[destReg] = true;
    }

    // ── IBranchPredictor overrides ────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        bool baseline = base.ResolvePrediction(pc, provider, tagePred);
        if (TryScoreSr(pc, out int srScore) && Math.Abs(srScore) >= RunltsPredictor.SrThreshold) {
            SrOverrides++;
            return srScore >= 0;
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
        TrainSr(pc, taken);
    }

    // ── sR internals ──────────────────────────────────────────────────────────

    // For each bank, pick the freshest/most-useful valid register slot and sum its
    // direction counter. Returns false only if no bank had a valid register (cold sR).
    private bool TryScoreSr(ulong pc, out int score) {
        score = 0;
        var any = false;
        int pcIdx = PcIdx(pc);
        for (var b = 0; b < RunltsPredictor.NumBanks; b++) {
            int slot = BestSlot(b, pcIdx);
            if (slot < 0) continue;
            any = true;
            int reg = b * RunltsPredictor.RegsPerBank + slot;
            int idx = SrIdx(pc, _digest[reg]);
            score += _dir[b][idx];
        }

        return any;
    }

    private void TrainSr(ulong pc, bool taken) {
        int t = taken ? 1 : -1;
        int pcIdx = PcIdx(pc);
        for (var b = 0; b < RunltsPredictor.NumBanks; b++) {
            int slot = BestSlot(b, pcIdx);
            if (slot < 0) continue;
            int reg = b * RunltsPredictor.RegsPerBank + slot;
            int idx = SrIdx(pc, _digest[reg]);

            bool agreed = _dir[b][idx] >= 0 == taken;
            ref sbyte w = ref _weight[b][pcIdx][slot];
            w = (sbyte)Math.Clamp(w + (agreed ? 1 : -1), sbyte.MinValue, sbyte.MaxValue);

            ref sbyte d = ref _dir[b][idx];
            d = (sbyte)Math.Clamp(d + t, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    // Among a bank's currently-valid (non-stale) registers, the one with the highest
    // per-PC usefulness weight — the "selection without extra memory ports" mechanism.
    private int BestSlot(int bank, int pcIdx) {
        int best = -1;
        var bestWeight = int.MinValue;
        for (var s = 0; s < RunltsPredictor.RegsPerBank; s++) {
            int reg = bank * RunltsPredictor.RegsPerBank + s;
            if (!IsFresh(reg)) continue;
            int w = _weight[bank][pcIdx][s];
            if (w <= bestWeight) continue;
            bestWeight = w;
            best = s;
        }

        return best;
    }

    private bool IsFresh(int reg) =>
        _valid[reg] && _clock - _setAt[reg] <= RunltsPredictor.StalenessWindow;

    private static int PcIdx(ulong pc) => (int)((pc >> 2) & (RunltsPredictor.TableSize - 1));

    private static int SrIdx(ulong pc, int digest) =>
        ((int)(pc >> 2) ^ digest) & (RunltsPredictor.TableSize - 1);

    // 12-bit digest: 3-bit leading run length, 3-bit trailing run length, 6 LSBs
    // (paper Fig. 6(a); the FP/flag formats of Fig. 6(b)/(c) do not apply — see class doc).
    private static int Digest(ulong v) {
        int lead = RunLength(v, true);
        int trail = RunLength(v, false);
        var low6 = (int)(v & 0x3F);
        return (lead << 9) | (trail << 6) | low6;
    }

    private static int RunLength(ulong v, bool fromMsb) {
        bool bit = fromMsb ? ((v >> 63) & 1) == 1 : (v & 1) == 1;
        var count = 0;
        for (var i = 0; i < 64 && count < 7; i++) {
            int shift = fromMsb ? 63 - i : i;
            bool b = ((v >> shift) & 1) == 1;
            if (b != bit) break;
            count++;
        }

        return count;
    }
}