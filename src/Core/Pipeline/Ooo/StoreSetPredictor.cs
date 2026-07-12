namespace Pipeline.Ooo;

/// <summary>
/// Store-Set memory dependence predictor (Chrysos &amp; Emer, ISCA 1998).
///
/// SSIT (Store Set Identifier Table): PC-indexed, maps instruction → SSID.
/// LFST (Last Fetched Store Table): SSID-indexed, maps SSID → last-dispatched-store SeqNo.
///
/// Each SSIT slot stores both the SSID and the owning PC so that hash-colliding instructions
/// do not inherit each other's store sets.
///
/// A confidence threshold (default: 2) prevents one-shot violations — e.g. initialization
/// stores that race a load only once — from poisoning a hot loop.  A load is promoted to a
/// store set only on its Nth violation; the first N-1 are absorbed without adding stalls.
/// Rules 2–4 (one or both parties already have SSIDs) bypass the threshold: the pair has
/// already earned confidence via a prior violation.
///
/// The SSIT is PC-only (no address information), so once a load/store PC pair earns an
/// SSID, every future dynamic instance of that pair is serialized — including independent
/// ones a generic/recursive function reuses across many addresses (e.g. a sort's swap
/// site). The periodic clear (default: every 4096 loads) is the mitigation: it decays
/// stale pairings so a false dependency costs a bounded window of lost ILP rather than
/// the rest of the run. 4096 was picked by sweeping the full gem5-compare benchmark suite
/// (see docs/gem5-comparison.md "Store sets false-dependency mitigation"): below ~4096, towers loses
/// its store-set benefit (0.924 → 0.819 H/G at 3072) because real recurring dependencies
/// get cleared before they matter; at and above 4096, rsort/qsort recover most of their
/// regression while towers/treesum keep their gains.
/// </summary>
internal sealed class StoreSetPredictor {
    private readonly int[] _ssit;      // SSIT slot → SSID (0 = unassigned)
    private readonly ulong[] _ssitPc;  // PC that owns the slot; valid only when _ssit[slot] != 0
    private readonly ulong[] _lfst;    // LFST: (SSID-1) → last-dispatched-store SeqNo
    private readonly ulong[] _seen1Pc; // load PC seen in one violation but not yet assigned SSID
    private readonly int _ssitMask;
    private readonly uint _clearPeriod;
    private readonly int _threshold; // violations before SSID assignment (1 = original paper)
    private int _nextSsid = 1;
    private uint _loadCount;

    public StoreSetPredictor(
        int ssitSize = 1024,
        int lfstSize = 1024,
        uint clearPeriod = 4_096,
        int threshold = 2
    ) {
        _ssit = new int[ssitSize];
        _ssitPc = new ulong[ssitSize];
        _lfst = new ulong[lfstSize];
        _seen1Pc = new ulong[ssitSize];
        _ssitMask = ssitSize - 1;
        _clearPeriod = clearPeriod;
        _threshold = threshold;
    }

    private int SsitIdx(ulong pc) => (int)((pc >> 2) & (uint)_ssitMask);

    private int GetSsid(ulong pc) {
        int idx = SsitIdx(pc);
        return _ssit[idx] != 0 && _ssitPc[idx] == pc ? _ssit[idx] : 0;
    }

    private void SetSsid(ulong pc, int ssid) {
        int idx = SsitIdx(pc);
        _ssit[idx] = ssid;
        _ssitPc[idx] = pc;
    }

    /// <summary>Called at store dispatch: record SeqNo in LFST for its set.</summary>
    public void OnStoreDispatch(ulong pc, ulong seqNo) {
        int ssid = GetSsid(pc);
        if (ssid == 0) return;
        _lfst[ssid - 1] = seqNo;
    }

    /// <summary>
    /// Called at load dispatch: returns the predicted dependent store SeqNo (0 = none).
    /// Periodically clears both tables to flush stale predictions.
    /// </summary>
    public ulong OnLoadDispatch(ulong pc) {
        if (_clearPeriod > 0 && ++_loadCount % _clearPeriod == 0) ClearAll();
        int ssid = GetSsid(pc);
        return ssid == 0 ? 0 : _lfst[ssid - 1];
    }

    /// <summary>Called when a store's address becomes known: release stalled loads.</summary>
    public void OnStoreIssued(ulong pc, ulong seqNo) {
        int ssid = GetSsid(pc);
        if (ssid == 0) return;
        if (_lfst[ssid - 1] == seqNo) _lfst[ssid - 1] = 0;
    }

    /// <summary>
    /// Called on a commit-time memory-order violation.
    /// Rule 1 (neither party has an SSID) requires <see cref="_threshold"/> violations
    /// before assigning a store set — absorbing one-shot violations without adding stalls.
    /// Rules 2–4 bypass the threshold.
    /// </summary>
    public void RecordViolation(ulong storePc, ulong loadPc) {
        if (storePc == 0) return;
        int storeSsid = GetSsid(storePc);
        int loadSsid = GetSsid(loadPc);

        if (storeSsid == 0 && loadSsid == 0) {
            // Rule 1: neither has a set yet.
            if (_threshold <= 1) {
                // Original paper behaviour: assign on first violation.
                int s = AllocSsid();
                SetSsid(storePc, s);
                SetSsid(loadPc, s);
            }
            else {
                // Confidence gate: promote to SSID only on the second violation.
                int pendIdx = SsitIdx(loadPc);
                if (_seen1Pc[pendIdx] == loadPc) {
                    _seen1Pc[pendIdx] = 0;
                    int s = AllocSsid();
                    SetSsid(storePc, s);
                    SetSsid(loadPc, s);
                }
                else { _seen1Pc[pendIdx] = loadPc; }
            }
        }
        else if (storeSsid == 0) {
            // Rule 2: load has a set, store joins it.
            SetSsid(storePc, loadSsid);
        }
        else if (loadSsid == 0) {
            // Rule 3: store has a set, load joins it.
            SetSsid(loadPc, storeSsid);
        }
        else if (storeSsid != loadSsid) {
            // Rule 4: merge — smaller SSID wins, remap all loser entries.
            int winner = Math.Min(storeSsid, loadSsid);
            int loser = Math.Max(storeSsid, loadSsid);
            for (var i = 0; i < _ssit.Length; i++)
                if (_ssit[i] == loser)
                    _ssit[i] = winner;
            int wi = winner - 1, li = loser - 1;
            if (wi < _lfst.Length && li < _lfst.Length) {
                if (_lfst[li] > _lfst[wi]) _lfst[wi] = _lfst[li];
                _lfst[li] = 0;
            }
        }
        // storeSsid == loadSsid: already in the same set.
    }

    private void ClearAll() {
        Array.Clear(_ssit);
        Array.Clear(_ssitPc);
        Array.Clear(_lfst);
        Array.Clear(_seen1Pc);
        _nextSsid = 1;
    }

    private int AllocSsid() {
        if (_nextSsid > _lfst.Length) _nextSsid = 1;
        return _nextSsid++;
    }
}