namespace Mechanism.BranchPredictModels;

/// <summary>
/// Tournament branch predictor (Alpha 21264 style).
/// <para>
/// Combines a local predictor (per-branch 10-bit BHT → 3-bit local PHT) with
/// a global gshare predictor (2-bit PHT indexed by GHR XOR PC). A 2-bit
/// chooser table indexed by GHR selects between them; ≥2 → global, &lt;2 → local.
/// Both predictors are updated on every branch; the chooser is updated only
/// when they disagree.
/// </para>
/// </summary>
public sealed class TournamentPredictor : IBranchPredictor {
    // Local predictor
    private readonly SpeculativeLocalHistory _local; // per-PC branch history shift register
    private readonly byte[] _localPht;               // 3-bit counters; taken ≥ 4
    private readonly int _bhtMask;
    private readonly int _localPhtMask;

    // Global predictor (gshare)
    private readonly byte[] _globalPht; // 2-bit counters; taken ≥ 2
    private readonly int _globalPhtMask;

    // Chooser
    private readonly byte[] _chooser; // 2-bit counters; ≥2 → prefer global
    private readonly int _chooserMask;

    private readonly SpeculativeGlobalHistory _hist; // global history register

    private readonly Dictionary<ulong, ulong> _btb = new();

    /// <summary>
    /// Constructs a Tournament predictor.
    /// </summary>
    /// <param name="localHistoryBits">
    /// Bits in the local BHT.
    /// </param>
    /// <param name="localTableSize">
    /// Entries in the local BHT.
    /// </param>
    /// <param name="globalHistoryBits">
    /// Bits in the global PHT.
    /// </param>
    public TournamentPredictor(
        int localHistoryBits = 10,
        int localTableSize = 1024,
        int globalHistoryBits = 12
    ) {
        int bhtSize = localTableSize;
        _bhtMask = bhtSize - 1;
        _local = new SpeculativeLocalHistory(bhtSize, localHistoryBits);

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

        _hist = new SpeculativeGlobalHistory(globalHistoryBits);
    }

    // ── IBranchPredictor ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool pred = PreferGlobal(pc) ? GlobalPred(pc) : LocalPred(pc);
        ulong target = pred
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(pred, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;

        int bhtIdx = BhtIdx(pc);
        // Swap both histories to their committed (predict-time) values for the whole update:
        // the chooser must train on what each component predicted at fetch, and the global
        // PHT/chooser index off committed global history.
        _hist.Commit(
            taken, () =>
                _local.Commit(
                    bhtIdx, taken, () => {
                        bool local = LocalPred(pc);
                        bool global = GlobalPred(pc);
                        int ci = ChooserIdx();

                        // Update both predictors unconditionally (history advance is handled by the
                        // enclosing Commit calls).
                        UpdateLocalPht(bhtIdx, taken);
                        UpdateGlobal(pc, taken);

                        // Update chooser only when they disagree.
                        if (local != global) {
                            if (global == taken && _chooser[ci] < 3)
                                _chooser[ci]++;
                            else if (local == taken && _chooser[ci] > 0) _chooser[ci]--;
                        }
                    }
                )
        );
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        _hist.Speculate(predictedTaken);
        _local.Speculate(BhtIdx(pc), predictedTaken);
    }

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() {
        _hist.Recover();
        _local.Recover();
    }

    /// <inheritdoc />
    public BranchHistoryCheckpoint CaptureHistory(ulong pc) {
        int idx = BhtIdx(pc);
        return new BranchHistoryCheckpoint(_hist.Capture(), idx, _local.Capture(idx));
    }

    /// <inheritdoc />
    public void RestoreLocalEntry(in BranchHistoryCheckpoint checkpoint) {
        if (checkpoint.LocalIdx >= 0) _local.RestoreEntry(checkpoint.LocalIdx, checkpoint.LocalValue);
    }

    /// <inheritdoc />
    public void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) {
        _hist.RestoreTo(checkpoint.Global, actualTaken);
        if (checkpoint.LocalIdx >= 0)
            _local.RestoreEntryAndFold(checkpoint.LocalIdx, checkpoint.LocalValue, actualTaken);
    }

    // ── Local predictor ───────────────────────────────────────────────────────

    private bool LocalPred(ulong pc) {
        var phtIdx = (int)(_local.Value(BhtIdx(pc)) & (ulong)_localPhtMask);
        return _localPht[phtIdx] >= 4;
    }

    // Trains the local PHT against the (committed) local history; the history shift is
    // handled by the enclosing _local.Commit in Update.
    private void UpdateLocalPht(int bhtIdx, bool taken) {
        var phtIdx = (int)(_local.Value(bhtIdx) & (ulong)_localPhtMask);
        switch (taken) {
            case true when _localPht[phtIdx] < 7:  _localPht[phtIdx]++; break;
            case false when _localPht[phtIdx] > 0: _localPht[phtIdx]--; break;
        }
    }

    // ── Global predictor (gshare) ─────────────────────────────────────────────

    private bool GlobalPred(ulong pc) => _globalPht[GlobalIdx(pc)] >= 2;

    private void UpdateGlobal(ulong pc, bool taken) {
        int idx = GlobalIdx(pc);
        switch (taken) {
            case true when _globalPht[idx] < 3:  _globalPht[idx]++; break;
            case false when _globalPht[idx] > 0: _globalPht[idx]--; break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool PreferGlobal(ulong _) => _chooser[ChooserIdx()] >= 2;

    private int BhtIdx(ulong pc) => (int)((pc >> 2) & (ulong)_bhtMask);

    private int GlobalIdx(ulong pc) =>
        (int)((_hist.Value ^ (pc >> 2)) & (ulong)_globalPhtMask);

    private int ChooserIdx() => (int)(_hist.Value & (ulong)_chooserMask);
}