namespace Mechanism.BranchPredictModels;

/// <summary>
/// LLBP-X: The Last-Level Branch Predictor Revisited — Schall et al., HPCA 2026.
/// <para>
/// Extends LLBP with dynamic context-depth adaptation driven by a Context Tracking
/// Table (CTT). Each context starts shallow (W=2 hashed PCs, tables 0–1 in our
/// 4-table TAGE). When the 16-slot pattern set fills and the running history-length
/// average saturates at 7, the CTT promotes the context to deep (W=64, tables 2–3).
/// Deep contexts revert to shallow once the average drains back to 0.
/// </para>
/// <para>
/// Shallow contexts index only TAGE tables 0–1 (histories 8, 13); deep contexts
/// index only tables 2–3 (histories 21, 34). This mirrors the paper's short/long
/// history split across LLBP-X's 21-entry history range.
/// </para>
/// <para>
/// Branch-type distinction (unconditional vs conditional) is not surfaced by
/// IBranchPredictor, so RCR update uses all taken branches — identical to LLBP.
/// </para>
/// </summary>
public sealed class LlbpXPredictor : LlbpPredictor {
    private readonly Ctt _ctt = new();

    // Tables 0-1 = short history (8, 13 bits); tables 2-3 = long history (21, 34 bits).
    private const int DeepTableThreshold = 2;

    private bool _usedDeep;

    /// <summary>Number of predictions made using a deep (W=64) context.</summary>
    private int DeepContextPredictions { get; set; }

    /// <inheritdoc />
    protected override bool TryLlbpPredict(ulong pc, int provider, out bool pred) {
        uint cid2 = Rcr.CidShallow;
        _usedDeep = _ctt.IsDeep(cid2);
        LlbpCtxKey = _usedDeep ? Rcr.CidDeep : cid2;

        PatternMap? pm = Storage.Get(LlbpCtxKey);
        if (pm != null)
            for (int t = LTagePredictor.NumTables - 1; t >= 0; t--) {
                bool tableIsLong = t >= LlbpXPredictor.DeepTableThreshold;
                if (_usedDeep != tableIsLong) continue; // history-range restriction
                int key = PatternKey(pc, t);
                if (!pm.TryGet(key, out sbyte ctr)) continue;
                LlbpHistIdx = t;
                LlbpPatternKey = key;
                if (t >= provider) {
                    if (_usedDeep) DeepContextPredictions++;
                    pred = ctr >= 0;
                    return true;
                }

                break;
            }

        pred = false;
        return false;
    }

    /// <inheritdoc />
    protected override void TrainLlbp(ulong pc, bool taken, bool provPred) {
        // Key off the committed RCR (the committing branch's predict-time context), not the
        // working RCR, which has already run ahead speculatively by commit time.
        uint cid2 = CommittedRcr.CidShallow;

        if (LlbpIsProvider && LlbpHistIdx >= 0) {
            // Recompute the depth-appropriate context key from the committed RCR rather than
            // the predict-time LlbpCtxKey (which a younger in-flight branch may have clobbered).
            uint ctxKey = _ctt.IsDeep(cid2) ? CommittedRcr.CidDeep : cid2;
            PatternMap pm = Storage.GetOrCreate(ctxKey);
            pm.SatUpdate(LlbpPatternKey, taken);
            if (pm.IsFull()) _ctt.NotifyOverflow(cid2);
        }
        else if (provPred != taken) {
            int allocTable = LastProvider + 1;
            if ((uint)allocTable < LTagePredictor.NumTables) {
                bool isLong = allocTable >= LlbpXPredictor.DeepTableThreshold;
                _ctt.NotifyAllocation(cid2, isLong);
                // Route into the depth-appropriate storage directly, independent of
                // the current _usedDeep flag (which reflects the last predict, not this update).
                uint allocCtxKey = isLong ? CommittedRcr.CidDeep : cid2;
                PatternMap pm = Storage.GetOrCreate(allocCtxKey);
                pm.AllocateIfAbsent(PatternKey(pc, allocTable), taken);
                if (pm.IsFull()) _ctt.NotifyOverflow(cid2);
            }
        }
    }
}

/// <summary>
/// Context Tracking Table: monitors contended pattern sets and promotes/demotes
/// contexts between shallow (W=2) and deep (W=64) history depth.
/// </summary>
internal sealed class Ctt {
    private const int Capacity = 6144;
    private const int AvgHistMax = 7;

    private readonly Dictionary<uint, CttEntry> _map = new(Ctt.Capacity + 1);
    private readonly Queue<uint> _order = new(Ctt.Capacity + 1);

    public bool IsDeep(uint cid2) =>
        _map.TryGetValue(cid2, out CttEntry e) && e.IsDeep;

    /// <summary>Called when a pattern set becomes full — triggers CTT allocation if not present.</summary>
    public void NotifyOverflow(uint cid2) {
        if (!_map.ContainsKey(cid2)) Alloc(cid2);
    }

    /// <summary>Called on each misprediction allocation; adjusts the avg-history-length counter.</summary>
    public void NotifyAllocation(uint cid2, bool isLong) {
        if (!_map.TryGetValue(cid2, out CttEntry e)) return;
        if (isLong) {
            if (e.AvgHistLen < Ctt.AvgHistMax) e.AvgHistLen++;
        }
        else {
            if (e.AvgHistLen > 0) e.AvgHistLen--;
        }

        e.IsDeep = e.AvgHistLen >= Ctt.AvgHistMax;
        _map[cid2] = e;
    }

    private void Alloc(uint cid2) {
        if (_map.Count >= Ctt.Capacity)
            while (_order.TryDequeue(out uint old) && !_map.Remove(old)) { }

        _map[cid2] = new CttEntry();
        _order.Enqueue(cid2);
    }
}

internal struct CttEntry {
    public byte AvgHistLen; // 3-bit saturating counter (0–7)
    public bool IsDeep;
}