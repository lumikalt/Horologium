namespace Mechanism.BranchPredictModels;

/// <summary>
/// LLBP: The Last-Level Branch Predictor — Schall et al., MICRO 2024.
/// <para>
/// Extends TAGE-SC-L with a context-addressed backing store keyed by the RCR
/// (Rolling Context Register). The RCR hashes the PCs of the last W=8 taken
/// branches (offset D=8 into the window) into a 14-bit context ID. Within each
/// context, patterns are indexed by the same PC×GHR tag as the TAGE tables.
/// When LLBP finds a match at history-table index t >= TAGE's current provider,
/// it overrides TAGE's direction (the SC layer still applies on top).
/// </para>
/// <para>
/// Branch-type distinction (unconditional vs conditional) is not surfaced by
/// IBranchPredictor. RCR update therefore uses all taken branches (T=4 mode in
/// the original paper's taxonomy).
/// </para>
/// </summary>
public class LlbpPredictor : TageScLPredictor {
    // Working RCR: advanced speculatively at fetch, used for Predict-time context lookups.
    private protected readonly RollingContextReg Rcr = new();

    // Architectural RCR shadow: advanced only when a taken branch retires. By the in-order
    // invariant it equals a committing branch's predict-time context, so training keys off it
    // (dissolving the predict-time LlbpCtxKey raciness); the speculative RCR restores from it
    // on flush.
    private protected readonly RollingContextReg CommittedRcr = new();
    private protected readonly LlbpStorage Storage = new();

    // Latches once the pipeline drives speculative history (out-of-order). Until then the
    // working RCR is advanced at commit alongside the shadow, so in-order pipelines behave
    // exactly as a single commit-time RCR (bit-identical).
    private bool _rcrSpeculative;

    /// <summary>True when LLBP (not TAGE) was the final prediction provider for the current branch.</summary>
    protected bool LlbpIsProvider;

    /// <summary>History-table index of the LLBP match, or -1 when LLBP had no entry.</summary>
    protected int LlbpHistIdx = -1;

    /// <summary>Pattern key used in the LLBP match, retained for training.</summary>
    protected int LlbpPatternKey;

    /// <summary>Context key used for the current LLBP lookup.</summary>
    protected uint LlbpCtxKey;

    /// <summary>TAGE provider index from the most recent prediction (-1 if unset).</summary>
    protected int LastProvider = -1;

    /// <summary>Number of times LLBP overrode TAGE's direction.</summary>
    public int LlbpOverrides { get; private set; }

    /// <inheritdoc/>
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        LastProvider = provider;
        LlbpIsProvider = false;
        LlbpHistIdx = -1;

        if (TryLlbpPredict(pc, provider, out bool llbpPred)) {
            LlbpIsProvider = true;
            LlbpOverrides++;
            return base.ResolvePrediction(pc, provider, llbpPred);
        }

        return base.ResolvePrediction(pc, provider, tagePred);
    }

    /// <summary>Searches the LLBP context store for a match at a history level ≥ <paramref name="provider"/>.</summary>
    protected virtual bool TryLlbpPredict(ulong pc, int provider, out bool pred) {
        LlbpCtxKey = Rcr.ContextId;
        PatternMap? pm = Storage.Get(LlbpCtxKey);
        if (pm != null)
            for (int t = LTagePredictor.NumTables - 1; t >= 0; t--) {
                int key = PatternKey(pc, t);
                if (!pm.TryGet(key, out sbyte ctr)) continue;
                LlbpHistIdx = t;
                LlbpPatternKey = key;
                if (t >= provider) {
                    pred = ctr >= 0;
                    return true;
                }

                break;
            }

        pred = false;
        return false;
    }

    /// <inheritdoc/>
    protected override void OnAfterUpdate(ulong pc, bool taken, bool provPred, int preScore, bool loopWasConfident) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident);
        TrainLlbp(pc, taken, provPred);
        if (taken) {
            // Advance the committed shadow (predict-time context of the committing branch).
            CommittedRcr.Update(pc);
            // In-order: no fetch speculation ran, so keep the working RCR in lock-step.
            if (!_rcrSpeculative) Rcr.Update(pc);
        }
    }

    /// <inheritdoc/>
    public override void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        base.SpeculativeHistoryUpdate(pc, predictedTaken);
        _rcrSpeculative = true;
        if (predictedTaken) Rcr.Update(pc);
    }

    /// <inheritdoc/>
    public override void RecoverSpeculativeHistory() {
        base.RecoverSpeculativeHistory();
        Rcr.CopyFrom(CommittedRcr);
    }

    /// <summary>Updates LLBP counters or allocates a new entry on misprediction. Keys the
    /// context off the committed RCR (the branch's predict-time context); which pattern within
    /// the context is trained still uses the predict-time LlbpHistIdx/LastProvider, a residual
    /// out-of-order imprecision that does not affect correctness.</summary>
    protected virtual void TrainLlbp(ulong pc, bool taken, bool provPred) {
        if (LlbpIsProvider && LlbpHistIdx >= 0) {
            Storage.GetOrCreate(CommittedRcr.ContextId).SatUpdate(LlbpPatternKey, taken);
        }
        else if (provPred != taken) {
            int allocTable = LastProvider + 1;
            if ((uint)allocTable < LTagePredictor.NumTables)
                Storage.GetOrCreate(CommittedRcr.ContextId).AllocateIfAbsent(PatternKey(pc, allocTable), taken);
        }
    }

    /// <summary>Computes the LLBP pattern key for branch <paramref name="pc"/> at history-table index <paramref name="t"/>.</summary>
    protected int PatternKey(ulong pc, int t) => (TageTag(pc, t) << 2) | t;
}

internal sealed class RollingContextReg {
    private const int MaxWindow = 120;
    private const int W = 8;
    private const int WShallow = 2;
    private const int WDeep = 64;
    private const int D = 8;
    private const int S = 2;
    private const int CtWidth = 14;

    private readonly ulong[] _window = new ulong[RollingContextReg.MaxWindow];
    private int _head;
    private int _count;
    private uint _ccid;
    private uint _cidShallow;
    private uint _cidDeep;

    public uint ContextId => _ccid;
    public uint CidShallow => _cidShallow;
    public uint CidDeep => _cidDeep;

    public void Update(ulong pc) {
        _window[_head] = pc;
        _head = (_head + 1) % RollingContextReg.MaxWindow;
        if (_count < RollingContextReg.MaxWindow) _count++;
        if (_count == RollingContextReg.MaxWindow) {
            _ccid = CalcHash(RollingContextReg.W, RollingContextReg.D);
            _cidShallow = CalcHash(RollingContextReg.WShallow, RollingContextReg.D);
            _cidDeep = CalcHash(RollingContextReg.WDeep, RollingContextReg.D);
        }
    }

    /// <summary>
    /// Overwrites this register with a copy of <paramref name="other"/>. Used to restore the
    /// speculative RCR from the committed shadow on a pipeline flush.
    /// </summary>
    public void CopyFrom(RollingContextReg other) {
        Array.Copy(other._window, _window, _window.Length);
        _head = other._head;
        _count = other._count;
        _ccid = other._ccid;
        _cidShallow = other._cidShallow;
        _cidDeep = other._cidDeep;
    }

    private uint CalcHash(int n, int start) {
        const uint mask = (1u << RollingContextReg.CtWidth) - 1;
        uint hash = 0;
        var sh = 0;
        var collected = 0;
        for (int i = start; i < _count && collected < n; i++, collected++) {
            int idx = (_head - 1 - i + RollingContextReg.MaxWindow) % RollingContextReg.MaxWindow;
            hash ^= (uint)(_window[idx] << sh);
            sh = (sh + RollingContextReg.S) % RollingContextReg.CtWidth;
        }

        return hash & mask;
    }
}

internal sealed class PatternMap {
    private const int Cap = 16;
    private const int CtrMin = -4;
    private const int CtrMax = 3;

    private readonly (int Key, sbyte Ctr, bool Valid)[] _e = new (int, sbyte, bool)[PatternMap.Cap];
    private int _clock;

    public bool IsFull() {
        for (var i = 0; i < PatternMap.Cap; i++)
            if (!_e[i].Valid)
                return false;
        return true;
    }

    public bool TryGet(int key, out sbyte ctr) {
        for (var i = 0; i < PatternMap.Cap; i++)
            if (_e[i].Valid && _e[i].Key == key) {
                ctr = _e[i].Ctr;
                return true;
            }

        ctr = 0;
        return false;
    }

    public void SatUpdate(int key, bool taken) {
        for (var i = 0; i < PatternMap.Cap; i++) {
            if (!_e[i].Valid || _e[i].Key != key) continue;
            sbyte c = taken
                ? (sbyte)Math.Min(_e[i].Ctr + 1, PatternMap.CtrMax)
                : (sbyte)Math.Max(_e[i].Ctr - 1, PatternMap.CtrMin);
            _e[i] = (_e[i].Key, c, true);
            return;
        }
    }

    public void AllocateIfAbsent(int key, bool taken) {
        for (var i = 0; i < PatternMap.Cap; i++)
            if (_e[i].Valid && _e[i].Key == key)
                return;
        int slot = -1;
        for (var i = 0; i < PatternMap.Cap; i++)
            if (!_e[i].Valid) {
                slot = i;
                break;
            }

        if (slot < 0) {
            slot = _clock;
            _clock = (_clock + 1) % PatternMap.Cap;
        }

        _e[slot] = (key, taken ? (sbyte)0 : (sbyte)-1, true);
    }
}

internal sealed class LlbpStorage {
    private const int Capacity = 14336;
    private readonly Dictionary<uint, PatternMap> _map = new(LlbpStorage.Capacity + 1);
    private readonly Queue<uint> _order = new(LlbpStorage.Capacity + 1);

    public PatternMap? Get(uint key) => _map.TryGetValue(key, out PatternMap? pm) ? pm : null;

    public PatternMap GetOrCreate(uint key) {
        if (_map.TryGetValue(key, out PatternMap? pm)) return pm;
        if (_map.Count >= LlbpStorage.Capacity)
            while (_order.TryDequeue(out uint old) && !_map.Remove(old)) { }

        pm = new PatternMap();
        _map[key] = pm;
        _order.Enqueue(key);
        return pm;
    }
}