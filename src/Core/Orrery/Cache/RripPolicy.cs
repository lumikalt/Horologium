namespace Orrery.Cache;

/// <summary>
///     Shared base for SRRIP, BRRIP, and DRRIP.
///     Holds the RRPV array and implements the two invariant operations:
///     — hit promotion (RRIP-HP: RRPV ← 0)
///     — victim selection (find first way with RRPV == max; if none, increment all and retry)
///     Subclasses differ only in <see cref="RecordInstall" />.
///     — Jaleel et al., "High Performance Cache Replacement Using Re-Reference Interval
///     Prediction (RRIP)", ISCA 2010.
/// </summary>
public abstract class RripPolicyBase : IReplacementPolicy {
    protected readonly int InsertionRrpv; // 2^M − 2  (= 2 for M=2, "long re-reference")
    protected readonly int MaxRrpv;       // 2^M − 1  (= 3 for M=2)
    protected readonly int[][] Rrpv;
    protected readonly int Ways;

    protected RripPolicyBase(int sets, int ways, int m = 2) {
        Ways = ways;
        MaxRrpv = (1 << m) - 1;
        InsertionRrpv = MaxRrpv - 1;
        Rrpv = new int[sets][];
        for (var s = 0; s < sets; s++) {
            Rrpv[s] = new int[ways];
            for (var w = 0; w < ways; w++) Rrpv[s][w] = MaxRrpv; // invalid ways treated as distant
        }
    }

    // RRIP-HP: a hit predicts near-immediate re-reference.
    public virtual void RecordHit(int set, int way) => Rrpv[set][way] = 0;

    /// <summary>No-op for all RRIP variants; overridden by SHiP.</summary>
    public virtual void SetPendingSignature(ulong signature) { }

    public int ChooseVictim(int set) {
        while (true) {
            for (var w = 0; w < Ways; w++)
                if (Rrpv[set][w] == MaxRrpv)
                    return w;
            // No distant entry: age all RRPVs by one, then retry.
            for (var w = 0; w < Ways; w++) Rrpv[set][w]++;
        }
    }

    public abstract void RecordInstall(int set, int way);

    public int GetMetadata(int set, int way) => Rrpv[set][way];

    /// <summary>Serializes the shared RRPV array. Subclasses override to append their own state.</summary>
    public virtual void WriteState(BinaryWriter w) {
        w.Write(Rrpv.Length);
        foreach (int[] set in Rrpv)
        foreach (int rrpv in set)
            w.Write(rrpv);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Subclasses override to append their own state.</summary>
    public virtual void ReadState(BinaryReader r) {
        int sets = r.ReadInt32();
        int n = Math.Min(sets, Rrpv.Length);
        for (var s = 0; s < sets; s++)
        for (var w2 = 0; w2 < Ways; w2++) {
            int rrpv = r.ReadInt32();
            if (s < n) Rrpv[s][w2] = rrpv;
        }
    }
}

/// <summary>
///     Static RRIP (SRRIP-HP): all inserts at RRPV = 2^M−2 ("long re-reference interval").
///     Scan-resistant: scan blocks cannot immediately evict the active working set.
/// </summary>
public sealed class SrripPolicy : RripPolicyBase {
    public SrripPolicy(int sets, int ways, int m = 2) : base(sets, ways, m) { }

    public override void RecordInstall(int set, int way) => Rrpv[set][way] = InsertionRrpv;
}

/// <summary>
///     Bimodal RRIP (BRRIP-HP): inserts at RRPV = 2^M−1 ("distant") with probability 1−ε,
///     and at RRPV = 2^M−2 ("long") with probability ε = 1/bimodalDenominator (default 1/32).
///     Thrash-resistant: when the working set exceeds capacity, most inserts are immediately
///     evictable, preserving a fraction of the working set between thrashing waves.
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
            Rrpv[set][way] = InsertionRrpv; // long (1/denominator probability)
        }
        else {
            Rrpv[set][way] = MaxRrpv; // distant (most inserts)
        }
    }

    /// <inheritdoc />
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        w.Write(_counter);
    }

    /// <inheritdoc />
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        _counter = r.ReadInt32();
    }
}

/// <summary>
///     Dynamic RRIP (DRRIP-HP): uses Set Dueling to choose between SRRIP and BRRIP.
///     A small number of dedicated SDM sets permanently follow SRRIP or BRRIP; a 10-bit
///     PSEL counter tracks which policy causes fewer misses; the remaining follower sets
///     use whichever policy is currently winning.
///     Parameters follow the paper: 32-entry SDMs, 10-bit PSEL, ε = 1/32.
/// </summary>
public sealed class DrripPolicy : RripPolicyBase {
    private readonly int _denominator;   // BRRIP bimodal denominator
    private readonly int _pselMax;       // 2^pselBits − 1  (= 1023 for 10-bit)
    private readonly int _pselThreshold; // pselMax/2 + 1   (= 512 for 10-bit)
    private readonly int _sdmSets;       // SDM sets per policy (sets [0, sdmSets) = SDM_SRRIP)
    private int _bimodalCounter;         // BRRIP insertion counter

    public DrripPolicy(int sets, int ways, int m = 2, int sdmSets = 32, int pselBits = 10, int bimodalDenominator = 32)
        : base(sets, ways, m) {
        // Guard: keep SDM size sane for small caches.
        _sdmSets = Math.Max(1, Math.Min(sdmSets, sets / 4));
        _pselMax = (1 << pselBits) - 1;
        _pselThreshold = _pselMax / 2 + 1; // 512 for 10-bit
        _denominator = bimodalDenominator;
        Psel = _pselThreshold - 1; // start with SRRIP winning
    }

    /// <summary>Current PSEL value (0..pselMax). &lt; threshold → SRRIP wins; ≥ threshold → BRRIP wins.</summary>
    public int Psel { get; private set; }

    private bool IsSdmSrrip(int set) => set < _sdmSets;
    private bool IsSdmBrrip(int set) => set >= _sdmSets && set < _sdmSets * 2;

    public override void RecordInstall(int set, int way) {
        bool useSrrip;
        if (IsSdmSrrip(set)) {
            // SRRIP SDM missed → SRRIP loses a point → increment PSEL (votes for BRRIP).
            if (Psel < _pselMax) Psel++;
            useSrrip = true;
        }
        else if (IsSdmBrrip(set)) {
            // BRRIP SDM missed → BRRIP loses a point → decrement PSEL (votes for SRRIP).
            if (Psel > 0) Psel--;
            useSrrip = false;
        }
        else {
            // Follower: use whichever policy is winning.
            useSrrip = Psel < _pselThreshold;
        }

        if (useSrrip) { Rrpv[set][way] = InsertionRrpv; }
        else {
            // BRRIP: distant most of the time, long every 1/denominator inserts.
            _bimodalCounter++;
            if (_bimodalCounter >= _denominator) {
                _bimodalCounter = 0;
                Rrpv[set][way] = InsertionRrpv; // long
            }
            else {
                Rrpv[set][way] = MaxRrpv; // distant
            }
        }
    }

    /// <inheritdoc />
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        w.Write(Psel);
        w.Write(_bimodalCounter);
    }

    /// <inheritdoc />
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        Psel = r.ReadInt32();
        _bimodalCounter = r.ReadInt32();
    }
}