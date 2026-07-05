namespace Orrery.Cache;

/// <summary>
/// Shared base for SRRIP, BRRIP, and DRRIP.
/// Holds the RRPV array and implements the two invariant operations:
/// — hit promotion (RRIP-HP: RRPV ← 0)
/// — victim selection (find first way with RRPV == max; if none, increment all and retry)
/// Subclasses differ only in <see cref="RecordInstall"/>.
/// — Jaleel et al., "High Performance Cache Replacement Using Re-Reference Interval
///   Prediction (RRIP)", ISCA 2010.
/// </summary>
public abstract class RripPolicyBase : IReplacementPolicy {
    protected readonly int[][] _rrpv;
    protected readonly int _ways;
    protected readonly int _maxRrpv;        // 2^M − 1  (= 3 for M=2)
    protected readonly int _insertionRrpv;  // 2^M − 2  (= 2 for M=2, "long re-reference")

    protected RripPolicyBase(int sets, int ways, int m = 2) {
        _ways = ways;
        _maxRrpv = (1 << m) - 1;
        _insertionRrpv = _maxRrpv - 1;
        _rrpv = new int[sets][];
        for (int s = 0; s < sets; s++) {
            _rrpv[s] = new int[ways];
            for (int w = 0; w < ways; w++)
                _rrpv[s][w] = _maxRrpv; // invalid ways treated as distant
        }
    }

    // RRIP-HP: a hit predicts near-immediate re-reference.
    public void RecordHit(int set, int way) => _rrpv[set][way] = 0;

    public int ChooseVictim(int set) {
        while (true) {
            for (int w = 0; w < _ways; w++)
                if (_rrpv[set][w] == _maxRrpv)
                    return w;
            // No distant entry: age all RRPVs by one, then retry.
            for (int w = 0; w < _ways; w++)
                _rrpv[set][w]++;
        }
    }

    public abstract void RecordInstall(int set, int way);

    public int GetMetadata(int set, int way) => _rrpv[set][way];
}

/// <summary>
/// Static RRIP (SRRIP-HP): all inserts at RRPV = 2^M−2 ("long re-reference interval").
/// Scan-resistant: scan blocks cannot immediately evict the active working set.
/// </summary>
public sealed class SrripPolicy : RripPolicyBase {
    public SrripPolicy(int sets, int ways, int m = 2) : base(sets, ways, m) { }

    public override void RecordInstall(int set, int way) => _rrpv[set][way] = _insertionRrpv;
}

/// <summary>
/// Bimodal RRIP (BRRIP-HP): inserts at RRPV = 2^M−1 ("distant") with probability 1−ε,
/// and at RRPV = 2^M−2 ("long") with probability ε = 1/bimodalDenominator (default 1/32).
/// Thrash-resistant: when the working set exceeds capacity, most inserts are immediately
/// evictable, preserving a fraction of the working set between thrashing waves.
/// </summary>
public sealed class BrripPolicy : RripPolicyBase {
    private readonly int _denominator;
    private int _counter;

    public BrripPolicy(int sets, int ways, int m = 2, int bimodalDenominator = 32)
        : base(sets, ways, m) => _denominator = bimodalDenominator;

    public override void RecordInstall(int set, int way) {
        _counter++;
        if (_counter >= _denominator) {
            _counter = 0;
            _rrpv[set][way] = _insertionRrpv; // long (1/denominator probability)
        } else {
            _rrpv[set][way] = _maxRrpv;       // distant (most inserts)
        }
    }
}

/// <summary>
/// Dynamic RRIP (DRRIP-HP): uses Set Dueling to choose between SRRIP and BRRIP.
/// A small number of dedicated SDM sets permanently follow SRRIP or BRRIP; a 10-bit
/// PSEL counter tracks which policy causes fewer misses; the remaining follower sets
/// use whichever policy is currently winning.
/// Parameters follow the paper: 32-entry SDMs, 10-bit PSEL, ε = 1/32.
/// </summary>
public sealed class DrripPolicy : RripPolicyBase {
    private readonly int _sdmSets;       // SDM sets per policy (sets [0, sdmSets) = SDM_SRRIP)
    private readonly int _pselMax;       // 2^pselBits − 1  (= 1023 for 10-bit)
    private readonly int _pselThreshold; // pselMax/2 + 1   (= 512 for 10-bit)
    private readonly int _denominator;  // BRRIP bimodal denominator
    private int _psel;                  // policy selection counter
    private int _bimodalCounter;        // BRRIP insertion counter

    public DrripPolicy(int sets, int ways, int m = 2, int sdmSets = 32, int pselBits = 10, int bimodalDenominator = 32)
        : base(sets, ways, m) {
        // Guard: keep SDM size sane for small caches.
        _sdmSets     = Math.Max(1, Math.Min(sdmSets, sets / 4));
        _pselMax     = (1 << pselBits) - 1;
        _pselThreshold = _pselMax / 2 + 1; // 512 for 10-bit
        _denominator = bimodalDenominator;
        _psel        = _pselThreshold - 1; // start with SRRIP winning
    }

    /// <summary>Current PSEL value (0..pselMax). &lt; threshold → SRRIP wins; ≥ threshold → BRRIP wins.</summary>
    public int Psel => _psel;

    private bool IsSdmSrrip(int set) => set < _sdmSets;
    private bool IsSdmBrrip(int set) => set >= _sdmSets && set < _sdmSets * 2;

    public override void RecordInstall(int set, int way) {
        bool useSrrip;
        if (IsSdmSrrip(set)) {
            // SRRIP SDM missed → SRRIP loses a point → increment PSEL (votes for BRRIP).
            if (_psel < _pselMax) _psel++;
            useSrrip = true;
        } else if (IsSdmBrrip(set)) {
            // BRRIP SDM missed → BRRIP loses a point → decrement PSEL (votes for SRRIP).
            if (_psel > 0) _psel--;
            useSrrip = false;
        } else {
            // Follower: use whichever policy is winning.
            useSrrip = _psel < _pselThreshold;
        }

        if (useSrrip) {
            _rrpv[set][way] = _insertionRrpv;
        } else {
            // BRRIP: distant most of the time, long every 1/denominator inserts.
            _bimodalCounter++;
            if (_bimodalCounter >= _denominator) {
                _bimodalCounter = 0;
                _rrpv[set][way] = _insertionRrpv; // long
            } else {
                _rrpv[set][way] = _maxRrpv;       // distant
            }
        }
    }
}
