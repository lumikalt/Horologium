namespace Orrery.Cache;

/// <summary>
///     SHiP (Signature-based Hit Predictor for High Performance Caching).
///     Layers a Signature History Counter Table (SHCT) on top of SRRIP-HP.
///     This variant uses the memory address as the signature (SHiP-Mem).
///     SHCT: 16K entries, 3-bit saturating counters (0–7), indexed by 14-bit signature.
///     SHCT[sig] == 0  → no observed reuse for this signature → insert at RRPV = distant (3)
///     SHCT[sig]  > 0  → reuse observed → insert at RRPV = long (2, the SRRIP default)
///     Per cache line:
///     signature_m — set once at installation, never updated on hits; used at eviction time
///     outcome     — false at installation, set true on first hit; gates eviction SHCT decrement
///     On hit:          set outcome=true; increment SHCT[signature_m] (saturating)
///     On eviction:     if !outcome: decrement SHCT[signature_m] (no reuse → weaken confidence)
///     On installation: outcome=false; signature_m=pending_sig; RRPV = 3 if SHCT==0, else 2
///     — Wu et al., "SHiP: Signature-based Hit Predictor for High Performance Caching", MICRO 2011.
/// </summary>
public sealed class ShipPolicy : RripPolicyBase {
    private const int ShctSize = 16384; // 16K entries, 14-bit index
    private const int MaxCounter = 7;   // 3-bit saturating counter
    private readonly bool[] _outcome;   // [set*ways+way]

    private readonly byte[] _shct;
    private readonly int[] _signature; // [set*ways+way]; -1 = slot not yet installed (cold)
    private int _pendingSignature;

    public ShipPolicy(int sets, int ways, int m = 2) : base(sets, ways, m) {
        _shct = new byte[ShipPolicy.ShctSize];
        _signature = new int[sets * ways];
        _outcome = new bool[sets * ways];
        Array.Fill(_signature, -1); // sentinel: cold slot
    }

    /// <summary>
    ///     Called by <see cref="SetAssociativeCache" /> with <c>address &gt;&gt; offsetBits</c> before
    ///     each fill. The lower 14 bits form the SHCT index (SHiP-Mem variant).
    /// </summary>
    public override void SetPendingSignature(ulong signature) =>
        _pendingSignature = (int)(signature & (ShipPolicy.ShctSize - 1));

    public override void RecordHit(int set, int way) {
        base.RecordHit(set, way); // RRIP-HP: RRPV → 0
        int idx = set * Ways + way;
        _outcome[idx] = true;
        int sig = _signature[idx];
        if (sig >= 0 && _shct[sig] < ShipPolicy.MaxCounter) _shct[sig]++;
    }

    public override void RecordInstall(int set, int way) {
        int idx = set * Ways + way;

        // Eviction step: penalise the outgoing line's signature if it was never reused.
        int oldSig = _signature[idx];
        if (oldSig >= 0 && !_outcome[idx] && _shct[oldSig] > 0) _shct[oldSig]--;

        // Install the new line.
        _signature[idx] = _pendingSignature;
        _outcome[idx] = false;
        Rrpv[set][way] = _shct[_pendingSignature] == 0 ? MaxRrpv : InsertionRrpv;
    }

    /// <summary>Exposes the raw SHCT counter for a given signature index (testing/inspection).</summary>
    public int GetShctCounter(int sigIndex) => _shct[sigIndex];

    /// <summary>
    ///     Serializes the RRPV array (base), the SHCT, and the per-way signature/outcome arrays.
    ///     Deliberately does not serialize <see cref="_pendingSignature" />: it is set by
    ///     <see cref="SetPendingSignature" /> immediately before <see cref="RecordInstall" /> is
    ///     called in the same fill operation, never observed across a drained boundary (no fill
    ///     is in flight when the pipeline is drained).
    /// </summary>
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        w.Write(_shct.Length);
        foreach (byte c in _shct) w.Write(c);
        w.Write(_signature.Length);
        foreach (int sig in _signature) w.Write(sig);
        foreach (bool o in _outcome) w.Write(o);
    }

    /// <inheritdoc />
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        int shctSize = r.ReadInt32();
        int shctN = Math.Min(shctSize, _shct.Length);
        for (var i = 0; i < shctSize; i++) {
            byte c = r.ReadByte();
            if (i < shctN) _shct[i] = c;
        }

        int slots = r.ReadInt32();
        int slotN = Math.Min(slots, _signature.Length);
        for (var i = 0; i < slots; i++) {
            int sig = r.ReadInt32();
            if (i < slotN) _signature[i] = sig;
        }

        for (var i = 0; i < slots; i++) {
            bool o = r.ReadBoolean();
            if (i < slotN) _outcome[i] = o;
        }
    }
}