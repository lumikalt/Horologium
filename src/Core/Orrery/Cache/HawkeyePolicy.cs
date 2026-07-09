namespace Orrery.Cache;

/// <summary>
/// Hawkeye cache replacement policy (Jain &amp; Lin, ISCA 2016 — "Back to the Future:
/// Leveraging Belady's Algorithm for Improved Cache Replacement").
///
/// OPTgen reconstructs Belady's optimal decisions for the observed access stream and
/// trains a PC-indexed 3-bit saturating-counter predictor. At insertion the predictor
/// labels the line cache-friendly (RRPV=0) or cache-averse (RRPV=7). Hits decrement
/// RRPV toward 0. ChooseVictim selects RRPV=7, aging all lines (SRRIP-style) if none.
///
/// OPTgen uses a circular occupancy vector of length 8×ways per set (absolute-time
/// indexed) to reconstruct which accesses would have been OPT hits.
/// </summary>
public sealed class HawkeyePolicy : IReplacementPolicy {
    // ── RRPV state ───────────────────────────────────────────────────────────
    private const int MaxRrpv = 7; // 3-bit RRPV (0–7)
    private readonly int _ways;
    private readonly int[][] _rrpv; // [set][way]

    // ── OPTgen ───────────────────────────────────────────────────────────────
    // Circular occupancy vector: each slot counts how many lines are "live"
    // (within their liveness interval) at that absolute time step.
    // Length = 8×ways; an interval longer than this is treated as a cold miss.
    private readonly int _optLen;     // = 8 * ways
    private readonly int[][] _optOcc; // [set][slot] occupancy count
    private readonly long[] _absTime; // [set] absolute access counter

    // Per-way: absolute time of last install or hit (for liveness interval computation).
    private readonly long[][] _absLineTime; // [set][way]; -1 = no history
    private readonly ulong[][] _lineTag;    // [set][way] tag of current occupant

    // ── Hawkeye predictor ────────────────────────────────────────────────────
    // 8K-entry table of 3-bit saturating counters, indexed by load PC.
    // Counter ≥ Threshold → cache-friendly (RRPV=0); otherwise cache-averse (RRPV=7).
    private const int PredictorSize = 8192;
    private const int PredictorMask = HawkeyePolicy.PredictorSize - 1;
    private const int PredictorMax = 7; // 3-bit saturation ceiling
    private const int Threshold = 4;    // ≥4 → cache-friendly

    private readonly byte[] _predictor;

    // ── Pending install metadata ─────────────────────────────────────────────
    private ulong _pendingTag;
    private ulong _pendingPc;

    public HawkeyePolicy(int sets, int ways) {
        _ways = ways;
        _optLen = 8 * ways;

        _rrpv = new int[sets][];
        _optOcc = new int[sets][];
        _absTime = new long[sets];
        _absLineTime = new long[sets][];
        _lineTag = new ulong[sets][];

        for (var s = 0; s < sets; s++) {
            _rrpv[s] = new int[ways];
            _optOcc[s] = new int[_optLen];
            _absLineTime[s] = new long[ways];
            _lineTag[s] = new ulong[ways];
            for (var w = 0; w < ways; w++) {
                _rrpv[s][w] = HawkeyePolicy.MaxRrpv;
                _absLineTime[s][w] = -1;
            }
        }

        _predictor = new byte[HawkeyePolicy.PredictorSize];
        for (var i = 0; i < HawkeyePolicy.PredictorSize; i++) _predictor[i] = HawkeyePolicy.Threshold;
    }

    // ── OPTgen ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Records one access at absolute time <c>now</c> in the OPTgen occupancy vector.
    /// <paramref name="prevAbsTime"/> is the last time this tag was accessed in this set
    /// (-1 = first access). Returns true if OPT would have been a hit (the cache had
    /// room to keep the line from prevAbsTime to now). Advances the time pointer.
    /// </summary>
    private bool OpTgenAccess(int set, long prevAbsTime, out long now) {
        now = _absTime[set]++;
        int[] occ = _optOcc[set];

        // Reset the slot being claimed for this time step.
        occ[now % _optLen] = 0;

        if (prevAbsTime < 0) return false; // cold access
        long dist = now - prevAbsTime;
        if (dist >= _optLen) return false; // interval too long → stale

        // Check whether the cache had room to hold the line during this interval.
        var len = (int)dist;
        var maxOcc = 0;
        for (var i = 0; i < len; i++) {
            var slot = (int)((prevAbsTime + i) % _optLen);
            if (occ[slot] > maxOcc) maxOcc = occ[slot];
        }

        bool hit = maxOcc < _ways;

        if (hit)
            // Line stayed live: mark its occupancy for each slot in the interval.
            for (var i = 0; i < len; i++)
                occ[(int)((prevAbsTime + i) % _optLen)]++;
        return hit;
    }

    // ── Predictor training ───────────────────────────────────────────────────

    private void Train(ulong pc, bool optHit) {
        var idx = (int)(pc & HawkeyePolicy.PredictorMask);
        if (optHit) {
            if (_predictor[idx] < HawkeyePolicy.PredictorMax) _predictor[idx]++;
        }
        else {
            if (_predictor[idx] > 0) _predictor[idx]--;
        }
    }

    // ── IReplacementPolicy ───────────────────────────────────────────────────

    public void SetPendingAddress(ulong lineTag, ulong pc) {
        _pendingTag = lineTag;
        _pendingPc = pc;
    }

    public void SetPendingSignature(ulong signature) { } // unused by Hawkeye

    public void RecordHitPc(int set, int way, ulong lineTag, ulong pc) {
        long prevAbs = _absLineTime[set][way];
        bool optHit = OpTgenAccess(set, prevAbs, out long now);
        _absLineTime[set][way] = now;
        _lineTag[set][way] = lineTag;
        Train(pc, optHit);
    }

    public void RecordHit(int set, int way) {
        if (_rrpv[set][way] > 0) _rrpv[set][way]--;
    }

    public int ChooseVictim(int set) {
        while (true) {
            for (var w = 0; w < _ways; w++)
                if (_rrpv[set][w] == HawkeyePolicy.MaxRrpv)
                    return w;
            for (var w = 0; w < _ways; w++) _rrpv[set][w]++;
        }
    }

    public void RecordInstall(int set, int way) {
        ulong newTag = _pendingTag;
        ulong pc = _pendingPc;

        // Find the last time this tag was resident in this set (for OPTgen interval).
        long prevAbs = -1;
        for (var w = 0; w < _ways; w++)
            if (_lineTag[set][w] == newTag && _absLineTime[set][w] >= 0) {
                prevAbs = _absLineTime[set][w];
                break;
            }

        bool optHit = OpTgenAccess(set, prevAbs, out long now);
        _absLineTime[set][way] = now;
        _lineTag[set][way] = newTag;
        Train(pc, optHit);

        // Insert based on predictor decision.
        bool friendly = _predictor[(int)(pc & HawkeyePolicy.PredictorMask)] >= HawkeyePolicy.Threshold;
        _rrpv[set][way] = friendly ? 0 : HawkeyePolicy.MaxRrpv;
    }

    public int GetMetadata(int set, int way) => _rrpv[set][way];
}