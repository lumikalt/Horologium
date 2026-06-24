namespace Mechanism.BranchPredictModels;

/// <summary>
/// Tournament branch predictor (Alpha 21264 style).
///
/// Combines a local predictor (per-branch 10-bit BHT → 3-bit local PHT) with
/// a global gshare predictor (2-bit PHT indexed by GHR XOR PC). A 2-bit
/// chooser table indexed by GHR selects between them; ≥2 → global, &lt;2 → local.
/// Both predictors are updated on every branch; the chooser is updated only
/// when they disagree.
/// </summary>
public sealed class TournamentPredictor : IBranchPredictor {
    private readonly int _localHistoryBits;
    private readonly int _globalHistoryBits;

    // Local predictor
    private readonly ulong[] _bht;     // per-PC branch history shift register
    private readonly byte[] _localPht; // 3-bit counters; taken ≥ 4
    private readonly int _bhtMask;
    private readonly int _localPhtMask;

    // Global predictor (gshare)
    private readonly byte[] _globalPht; // 2-bit counters; taken ≥ 2
    private readonly int _globalPhtMask;

    // Chooser
    private readonly byte[] _chooser; // 2-bit counters; ≥2 → prefer global
    private readonly int _chooserMask;

    private ulong _ghr; // global history register

    private readonly Dictionary<ulong, ulong> _btb = new();

    public TournamentPredictor(
        int localHistoryBits = 10,
        int localTableSize = 1024,
        int globalHistoryBits = 12
    ) {
        _localHistoryBits = localHistoryBits;
        _globalHistoryBits = globalHistoryBits;

        int bhtSize = localTableSize;
        _bhtMask = bhtSize - 1;
        _bht = new ulong[bhtSize];

        int localPhtSize = 1 << localHistoryBits;
        _localPhtMask = localPhtSize - 1;
        _localPht = new byte[localPhtSize];
        Array.Fill(_localPht, (byte)3); // weakly not-taken (3-bit: taken ≥ 4)

        int globalPhtSize = 1 << globalHistoryBits;
        _globalPhtMask = globalPhtSize - 1;
        _globalPht = new byte[globalPhtSize];
        Array.Fill(_globalPht, (byte)1); // weakly not-taken

        int chooserSize = 1 << globalHistoryBits;
        _chooserMask = chooserSize - 1;
        _chooser = new byte[chooserSize];
        Array.Fill(_chooser, (byte)1); // weakly prefer local
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    public BranchPrediction Predict(ulong pc) {
        bool pred = PreferGlobal(pc) ? GlobalPred(pc) : LocalPred(pc);
        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;

        bool local = LocalPred(pc);
        bool global = GlobalPred(pc);
        int ci = ChooserIdx();

        // Update both predictors unconditionally.
        UpdateLocal(pc, taken);
        UpdateGlobal(pc, taken);

        // Update chooser only when they disagree.
        if (local != global) {
            if (global == taken && _chooser[ci] < 3)
                _chooser[ci]++;
            else if (local == taken && _chooser[ci] > 0) _chooser[ci]--;
        }

        _ghr = ((_ghr << 1) | (taken ? 1UL : 0UL)) & ((1UL << _globalHistoryBits) - 1);
    }

    // ── Local predictor ───────────────────────────────────────────────────────

    private bool LocalPred(ulong pc) {
        int bhtIdx = BhtIdx(pc);
        var phtIdx = (int)(_bht[bhtIdx] & (ulong)_localPhtMask);
        return _localPht[phtIdx] >= 4;
    }

    private void UpdateLocal(ulong pc, bool taken) {
        int bhtIdx = BhtIdx(pc);
        var phtIdx = (int)(_bht[bhtIdx] & (ulong)_localPhtMask);
        if (taken && _localPht[phtIdx] < 7)
            _localPht[phtIdx]++;
        else if (!taken && _localPht[phtIdx] > 0) _localPht[phtIdx]--;
        _bht[bhtIdx] = ((_bht[bhtIdx] << 1) | (taken ? 1UL : 0UL)) & (ulong)_localPhtMask;
    }

    // ── Global predictor (gshare) ─────────────────────────────────────────────

    private bool GlobalPred(ulong pc) => _globalPht[GlobalIdx(pc)] >= 2;

    private void UpdateGlobal(ulong pc, bool taken) {
        int idx = GlobalIdx(pc);
        if (taken && _globalPht[idx] < 3)
            _globalPht[idx]++;
        else if (!taken && _globalPht[idx] > 0) _globalPht[idx]--;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool PreferGlobal(ulong pc) => _chooser[ChooserIdx()] >= 2;

    private int BhtIdx(ulong pc) => (int)((pc >> 2) & (ulong)_bhtMask);

    private int GlobalIdx(ulong pc) =>
        (int)((_ghr ^ (pc >> 2)) & (ulong)_globalPhtMask);

    private int ChooserIdx() => (int)(_ghr & (ulong)_chooserMask);
}