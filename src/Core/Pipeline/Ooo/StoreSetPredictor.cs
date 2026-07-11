namespace Pipeline.Ooo;

/// <summary>
/// Store-Set memory dependence predictor (Chrysos &amp; Emer, ISCA 1998).
///
/// SSIT (Store Set Identifier Table): PC-indexed, maps instruction → SSID.
/// LFST (Last Fetched Store Table): SSID-indexed, maps SSID → last-dispatched-store SeqNo.
///
/// At dispatch: stores write their SeqNo into LFST[SSID]; loads read LFST[SSID] to get
/// the predicted dependent store SeqNo.  At issue time, a load stalls until that store's
/// address is known.  When a memory-order violation is detected at commit, RecordViolation
/// assigns the store and load to a common SSID via one of four merge rules.
/// </summary>
internal sealed class StoreSetPredictor {
    private readonly int[] _ssit;   // SSIT: PC → SSID (0 = no set assigned)
    private readonly ulong[] _lfst; // LFST: (SSID-1) → last-dispatched-store SeqNo
    private readonly int _ssitMask;
    private int _nextSsid = 1;

    public StoreSetPredictor(int ssitSize = 4096, int lfstSize = 128) {
        _ssit = new int[ssitSize];
        _lfst = new ulong[lfstSize];
        _ssitMask = ssitSize - 1;
    }

    private int SsitIdx(ulong pc) => (int)((pc >> 2) & (uint)_ssitMask);

    /// <summary>
    /// Called at store dispatch: record this store's SeqNo in LFST so younger loads in
    /// the same set know which store to wait for.
    /// </summary>
    public void OnStoreDispatch(ulong pc, ulong seqNo) {
        int ssid = _ssit[SsitIdx(pc)];
        if (ssid == 0) return;
        _lfst[ssid - 1] = seqNo;
    }

    /// <summary>
    /// Called at load dispatch: returns the SeqNo of the predicted dependent store (0 = none).
    /// </summary>
    public ulong OnLoadDispatch(ulong pc) {
        int ssid = _ssit[SsitIdx(pc)];
        return ssid == 0 ? 0 : _lfst[ssid - 1];
    }

    /// <summary>
    /// Called when a store's address becomes known (AddressKnown = true): invalidate the
    /// LFST entry if it still refers to this store so younger loads can issue freely.
    /// </summary>
    public void OnStoreIssued(ulong pc, ulong seqNo) {
        int ssid = _ssit[SsitIdx(pc)];
        if (ssid == 0) return;
        if (_lfst[ssid - 1] == seqNo) _lfst[ssid - 1] = 0;
    }

    /// <summary>
    /// Called on a commit-time memory-order violation.  Applies the four SSID assignment
    /// rules from the paper to merge the store and load into a common store set.
    /// </summary>
    public void RecordViolation(ulong storePc, ulong loadPc) {
        if (storePc == 0) return;
        int storeIdx = SsitIdx(storePc);
        int loadIdx = SsitIdx(loadPc);
        int storeSsid = _ssit[storeIdx];
        int loadSsid = _ssit[loadIdx];

        if (storeSsid == 0 && loadSsid == 0) {
            // Rule 1: neither has a set — allocate a new SSID for both.
            int newSsid = AllocSsid();
            _ssit[storeIdx] = newSsid;
            _ssit[loadIdx] = newSsid;
        }
        else if (storeSsid == 0) {
            // Rule 2: only the load has a set — the store joins it.
            _ssit[storeIdx] = loadSsid;
        }
        else if (loadSsid == 0) {
            // Rule 3: only the store has a set — the load joins it.
            _ssit[loadIdx] = storeSsid;
        }
        else if (storeSsid != loadSsid) {
            // Rule 4: both have different sets — merge loser into winner (smaller SSID wins).
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
        // storeSsid == loadSsid: already in the same set, nothing to do.
    }

    private int AllocSsid() {
        if (_nextSsid > _lfst.Length) _nextSsid = 1; // wrap around
        return _nextSsid++;
    }
}