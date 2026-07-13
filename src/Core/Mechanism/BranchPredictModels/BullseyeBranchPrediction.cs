namespace Mechanism.BranchPredictModels;

/// <summary>
///     Bullseye: H2P (hard-to-predict)-branch-targeted helper layered on TAGE-SC-L.
///     — Behrendt, Pun &amp; Nair, "Taming Wild Branches: Overcoming Hard-to-Predict Branches
///     using the Bullseye Predictor", CBP 2025.
///     <para>
///         Augments a <see cref="TageScLPredictor" /> baseline with a small H2P-targeted
///         subsystem. An H2P Identification Table (HIT, §4.1) tracks per-PC execution and
///         TAGE-SC-L-misprediction counts; a branch is flagged H2P-active once it clears
///         adaptive thresholds (Eq. 1a-1c) that tighten as more H2P branches are already
///         active. A flagged PC gets a dedicated local-history and global-history perceptron
///         (§4.3-4.4), each dual-hashed and trained with Seznec's O-GEHL dynamic-threshold
///         rule. A confidence-based arbiter (§4.5) picks a perceptron's output over
///         TAGE-SC-L only when the perceptron's running win-rate and output magnitude both
///         clear dynamic thresholds AND TAGE-SC-L itself is not already strongly confident
///         (usefulness saturated or the SC is already overriding). Branches that sustain 128
///         consecutive perceptron wins with no TAGE-SC-L win have their TAGE/loop/SC updates
///         suppressed (§4.6) via <see cref="LTagePredictor.SuppressTageUpdate" /> to stop
///         table pollution; a TAGE-SC-L win automatically un-suppresses.
///     </para>
///     <para>
///         Simplifications from the paper, documented where they depart: the HIT and H2P
///         cache are capacity-bounded dictionaries with simple LRU/weakest-entry eviction
///         rather than modeled set-associative RAMs (same simplification this codebase's
///         BTBs already make); the H2P cache's admission-time confidence/eviction counters
///         (§4.2.1-4.2.2) are folded into the same win-rate ratios the arbiter already
///         tracks, rather than kept as separate saturating counters; the piecewise accuracy
///         threshold f(N) (Eq. 1d) is reconstructed from the paper's prose (">95% when empty
///         down to 60% at saturation") since the typeset equation was not fully recoverable
///         from OCR. None of these affect the core mechanism: HIT-gated admission, per-branch
///         dual perceptrons, confidence arbitration, and selective TAGE-update suppression.
///     </para>
/// </summary>
public sealed class BullseyePredictor : TageScLPredictor {
    private const int HitCapacity = 64;
    private const int H2PCapacity = 8;
    private const uint TrialWindow = 512;   // dynamic occurrences before an entry is eviction-eligible
    private const uint StaleTimeout = 4096; // dynamic branches unreferenced before stale eviction
    private const uint FilterStreakLimit = 128;
    private const uint MinSamplesForWinRate = 16;
    private const int WinRatePercent = 55;
    private const int MaxUsefulness = 3;
    private const int LocalTableBits = 5; // 32-entry weight tables per window per hash
    private const int LocalTableSize = 1 << BullseyePredictor.LocalTableBits;

    // Global perceptron: folded-history classic perceptron (§4.4).
    private const int GlobalFoldBits = 16;

    private const int ThresholdTcBound = 63; // O-GEHL adaptive-threshold counter-saturation

    // Local perceptron: non-overlapping history windows of growing width (§4.3).
    private static readonly int[] LocalWindowWidths = [4, 8, 16, 32,];
    private static readonly int[] LocalWindowOffsets = [0, 4, 12, 28,]; // cumulative, non-overlapping
    private readonly Dictionary<ulong, H2PEntry> _h2P = new();

    private readonly Dictionary<ulong, HitEntry> _hit = new();
    private uint _clock;

    // ── Hooks ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        bool tageScPred = base.ResolvePrediction(pc, provider, tagePred);
        if (!_h2P.TryGetValue(pc, out H2PEntry? e)) return tageScPred;

        int localY = LocalOutput(e, pc);
        int globalY = GlobalOutput(e);
        bool localPred = localY >= 0;
        bool globalPred = globalY >= 0;

        bool localStrong = WinRateStrong(e.LocalWins, e.LocalTotal) && Math.Abs(localY) > e.LocalTheta;
        bool globalStrong = WinRateStrong(e.GlobalWins, e.GlobalTotal) && Math.Abs(globalY) > e.GlobalTheta;
        bool tageStrong = TageUsefulness(pc, provider) == BullseyePredictor.MaxUsefulness ||
                          Math.Abs(TageScore(pc, provider) + ScSum(pc)) > TageScLPredictor.ScThreshold;

        if (tageStrong || (!localStrong && !globalStrong)) return tageScPred;

        // Prefer whichever engaged perceptron has the larger normalized margin.
        bool useLocal = localStrong
                     && (!globalStrong || Math.Abs(localY) - e.LocalTheta >= Math.Abs(globalY) - e.GlobalTheta);
        return useLocal ? localPred : globalPred;
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident); // SC training (skipped if suppressed)
        _clock++;

        // §4.1: HIT bookkeeping. TAGE-SC-L's opinion, recomputed against the committed
        // (pre-training) state, the same trick TageScLPredictor.OnAfterUpdate uses for itself.
        int preTotal = preScore + ScSum(pc);
        bool tageScPred = Math.Abs(preTotal) > TageScLPredictor.ScThreshold ? preTotal >= 0 : provPred;

        HitEntry hit = TouchHit(pc);
        hit.Exec++;
        if (tageScPred != taken) hit.Mispred++;

        if (_h2P.TryGetValue(pc, out H2PEntry? e)) {
            UpdateH2PEntry(pc, e, taken, tageScPred);
            return;
        }

        TryAdmit(pc, hit);
    }

    /// <inheritdoc />
    protected override bool SuppressTageUpdate(ulong pc) =>
        _h2P.TryGetValue(pc, out H2PEntry? e) && e.Filtered;

    // ── HIT (§4.1) ────────────────────────────────────────────────────────────

    private HitEntry TouchHit(ulong pc) {
        if (_hit.TryGetValue(pc, out HitEntry? e)) {
            e.LastTouch = _clock;
            return e;
        }

        if (_hit.Count >= BullseyePredictor.HitCapacity) EvictLruHit();
        var fresh = new HitEntry { LastTouch = _clock, };
        _hit[pc] = fresh;
        return fresh;
    }

    private void EvictLruHit() {
        ulong victim = 0;
        var oldest = uint.MaxValue;
        foreach ((ulong key, HitEntry e) in _hit)
            if (e.LastTouch < oldest) {
                oldest = e.LastTouch;
                victim = key;
            }

        _hit.Remove(victim);
    }

    // Eq. 1a-1c: adaptive H2P-active admission thresholds.
    private void TryAdmit(ulong pc, HitEntry hit) {
        int n = _h2P.Count;
        double execThresh = 2048 + 16 * n;
        const double mispredThresh = 256;
        double acc = 1.0 - (double)hit.Mispred / hit.Exec;

        if (hit.Exec < execThresh || hit.Mispred < mispredThresh || acc >= AccuracyThreshold(n)) return;

        if (_h2P.Count >= BullseyePredictor.H2PCapacity && !EvictWeakestH2P()) return; // no room, nobody evictable
        _h2P[pc] = new H2PEntry();
    }

    // Reconstructed from the paper's prose (§4.1.2): >95% when the perceptron layer is
    // empty, tightening to 60% once N exceeds the empirically observed ~71-branch ceiling.
    // The typeset piecewise equation (Eq. 1d) was not fully OCR-recoverable.
    private static double AccuracyThreshold(int n) =>
        n switch {
            < 32  => 1.0 - 0.05 * n / 32.0,
            <= 71 => 0.95 - 0.01 * (n - 32),
            _     => 0.60,
        };

    // ── H2P cache: trial, arbitration bookkeeping, eviction (§4.2) ─────────────

    private void UpdateH2PEntry(ulong pc, H2PEntry e, bool taken, bool tageScPred) {
        e.LastTouch = _clock;
        e.TrialCount++;

        int localY = LocalOutput(e, pc);
        int globalY = GlobalOutput(e);
        bool localPred = localY >= 0;
        bool globalPred = globalY >= 0;

        // Win-rate bookkeeping (feeds both arbitration and eviction).
        e.LocalTotal++;
        if (localPred == taken) e.LocalWins++;
        e.GlobalTotal++;
        if (globalPred == taken) e.GlobalWins++;

        // O-GEHL dynamic-threshold training (§4.3-4.4): train on misprediction, or when
        // confidently correct but under threshold (keeps training activity near 50%).
        TrainPerceptron(ref e.LocalTheta, ref e.LocalTc, localPred != taken, Math.Abs(localY));
        if (localPred != taken || Math.Abs(localY) <= e.LocalTheta) TrainLocal(e, pc, taken);
        TrainPerceptron(ref e.GlobalTheta, ref e.GlobalTc, globalPred != taken, Math.Abs(globalY));
        if (globalPred != taken || Math.Abs(globalY) <= e.GlobalTheta) TrainGlobal(e, taken);

        e.LocalHistory = ((e.LocalHistory << 1) | (taken ? 1UL : 0UL)) &
                         ((1UL << (BullseyePredictor.LocalWindowOffsets[^1] + BullseyePredictor.LocalWindowWidths[^1]))
                        - 1);

        // §4.6: selective TAGE filtering. A TAGE-SC-L win always resets/un-suppresses;
        // a perceptron-only win (TAGE-SC-L wrong, a strong perceptron right) accrues streak.
        bool localStrong = WinRateStrong(e.LocalWins, e.LocalTotal) && Math.Abs(localY) > e.LocalTheta;
        bool globalStrong = WinRateStrong(e.GlobalWins, e.GlobalTotal) && Math.Abs(globalY) > e.GlobalTheta;
        bool perceptronOnlyWin = tageScPred != taken &&
                                 ((localStrong && localPred == taken) || (globalStrong && globalPred == taken));

        if (tageScPred == taken) {
            e.FilterStreak = 0;
            e.Filtered = false;
        }
        else if (perceptronOnlyWin) {
            e.FilterStreak++;
            if (e.FilterStreak >= BullseyePredictor.FilterStreakLimit) e.Filtered = true;
        }

        // §4.2.2: evict once past the warm-up trial window if neither perceptron ever
        // became confident, or if the entry has gone stale.
        if (e.TrialCount >= BullseyePredictor.TrialWindow && !localStrong && !globalStrong) _h2P.Remove(pc);
    }

    private bool EvictWeakestH2P() {
        ulong victim = 0;
        var found = false;
        var worstScore = double.MaxValue;
        foreach ((ulong key, H2PEntry e) in _h2P) {
            if (e.TrialCount < BullseyePredictor.TrialWindow) continue; // still in warm-up, protected
            double score = Math.Max(WinRate(e.LocalWins, e.LocalTotal), WinRate(e.GlobalWins, e.GlobalTotal));
            if (_clock - e.LastTouch > BullseyePredictor.StaleTimeout) score = -1; // stale entries evict first
            if (!found || score < worstScore) {
                worstScore = score;
                victim = key;
                found = true;
            }
        }

        if (!found) return false;
        _h2P.Remove(victim);
        return true;
    }

    private static bool WinRateStrong(uint wins, uint total) =>
        total >= BullseyePredictor.MinSamplesForWinRate &&
        100 * wins / total >= BullseyePredictor.WinRatePercent;

    private static double WinRate(uint wins, uint total) => total == 0 ? 0.0 : (double)wins / total;

    // Seznec's O-GEHL dynamic-threshold fitting: nudges theta so ~50% of predictions train.
    private static void TrainPerceptron(ref int theta, ref int tc, bool mispredicted, int magnitude) {
        if (mispredicted) {
            tc++;
            if (tc > BullseyePredictor.ThresholdTcBound) {
                theta++;
                tc = 0;
            }
        }
        else if (magnitude <= theta) {
            tc--;
            if (tc < -BullseyePredictor.ThresholdTcBound) {
                theta = Math.Max(0, theta - 1);
                tc = 0;
            }
        }
    }

    // ── Local-history perceptron (§4.3) ─────────────────────────────────────────

    private static int LocalOutput(H2PEntry e, ulong pc) {
        int y = e.LocalBias;
        for (var w = 0; w < BullseyePredictor.LocalWindowWidths.Length; w++) {
            ulong bits = WindowBits(e.LocalHistory, w);
            y += e.LocalWeightsA[w, Hash(pc, bits, 0)];
            y += e.LocalWeightsB[w, Hash(pc, bits, 1)];
        }

        return y;
    }

    private static void TrainLocal(H2PEntry e, ulong pc, bool taken) {
        int t = taken ? 1 : -1;
        for (var w = 0; w < BullseyePredictor.LocalWindowWidths.Length; w++) {
            ulong bits = WindowBits(e.LocalHistory, w);
            int idxA = Hash(pc, bits, 0);
            int idxB = Hash(pc, bits, 1);
            e.LocalWeightsA[w, idxA] = Clamp(e.LocalWeightsA[w, idxA] + t);
            e.LocalWeightsB[w, idxB] = Clamp(e.LocalWeightsB[w, idxB] + t);
        }

        e.LocalBias = Clamp(e.LocalBias + t);
    }

    private static ulong WindowBits(ulong localHistory, int window) {
        int offset = BullseyePredictor.LocalWindowOffsets[window];
        int width = BullseyePredictor.LocalWindowWidths[window];
        return (localHistory >> offset) & ((1UL << width) - 1);
    }

    // Dual hash (Behrendt et al. §4.3): two independent 64-bit finalizer mixes so the other
    //  usually resolves a collision under one hash.
    private static int Hash(ulong pc, ulong windowBits, int salt) {
        ulong x = pc ^ (windowBits * 0x9E3779B97F4A7C15UL) ^ (uint)(salt + 1);
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return (int)(x & (BullseyePredictor.LocalTableSize - 1));
    }

    // ── Global-history perceptron (§4.4) ─────────────────────────────────────────

    private int GlobalOutput(H2PEntry e) {
        int fold = FoldGlobal();
        int y = e.GlobalBias;
        for (var i = 0; i < BullseyePredictor.GlobalFoldBits; i++)
            y += ((fold >> i) & 1) != 0 ? e.GlobalWeights[i] : -e.GlobalWeights[i];
        return y;
    }

    // Classic perceptron update: each weight is nudged toward the sign its folded-history
    // bit contributed to the output (matching GlobalOutput's ±GlobalWeights[i] sum).
    private void TrainGlobal(H2PEntry e, bool taken) {
        int fold = FoldGlobal();
        int t = taken ? 1 : -1;
        for (var i = 0; i < BullseyePredictor.GlobalFoldBits; i++) {
            int sign = ((fold >> i) & 1) != 0 ? 1 : -1;
            e.GlobalWeights[i] = Clamp(e.GlobalWeights[i] + sign * t);
        }

        e.GlobalBias = Clamp(e.GlobalBias + t);
    }

    private int FoldGlobal() {
        ulong h = Ghr;
        var res = 0;
        for (var sh = 0; sh < LTagePredictor.MaxHist; sh += BullseyePredictor.GlobalFoldBits)
            res ^= (int)((h >> sh) & ((1 << BullseyePredictor.GlobalFoldBits) - 1));
        return res & ((1 << BullseyePredictor.GlobalFoldBits) - 1);
    }

    private static sbyte Clamp(int v) => (sbyte)Math.Clamp(v, sbyte.MinValue, sbyte.MaxValue);

    // ── Entry types ───────────────────────────────────────────────────────────

    private sealed class HitEntry {
        public uint Exec;
        public uint LastTouch;
        public uint Mispred;
    }

    private sealed class H2PEntry {
        public readonly sbyte[] GlobalWeights = new sbyte[BullseyePredictor.GlobalFoldBits];

        public readonly sbyte[,] LocalWeightsA
            = new sbyte[BullseyePredictor.LocalWindowWidths.Length, BullseyePredictor.LocalTableSize];

        public readonly sbyte[,] LocalWeightsB
            = new sbyte[BullseyePredictor.LocalWindowWidths.Length, BullseyePredictor.LocalTableSize];

        public bool Filtered;
        public uint FilterStreak;
        public sbyte GlobalBias;
        public int GlobalTc;
        public int GlobalTheta = 8;
        public uint GlobalTotal;
        public uint GlobalWins;
        public uint LastTouch;
        public sbyte LocalBias;

        public ulong LocalHistory;
        public int LocalTc;
        public int LocalTheta = 8;
        public uint LocalTotal;
        public uint LocalWins;
        public uint TrialCount;
    }
}