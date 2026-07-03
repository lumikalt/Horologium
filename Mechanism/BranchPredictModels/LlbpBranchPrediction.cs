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
public sealed class LlbpPredictor : TageScLPredictor {
    private readonly RollingContextReg _rcr = new();
    private readonly LlbpStorage _storage = new();

    private bool _llbpIsProvider;
    private int  _llbpHistIdx = -1;
    private int  _llbpPatternKey;
    private uint _llbpCtxKey;
    private int  _lastProvider = -1;

    /// <summary>Number of times LLBP overrode TAGE's direction.</summary>
    public int LlbpOverrides { get; private set; }

    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        _lastProvider = provider;
        _llbpIsProvider = false;
        _llbpHistIdx = -1;
        _llbpCtxKey = _rcr.ContextId;

        PatternMap? pm = _storage.Get(_llbpCtxKey);
        if (pm != null) {
            for (int t = NumTables - 1; t >= 0; t--) {
                int key = PatternKey(pc, t);
                if (!pm.TryGet(key, out sbyte ctr)) continue;
                _llbpHistIdx = t;
                _llbpPatternKey = key;
                if (t >= provider) {
                    _llbpIsProvider = true;
                    LlbpOverrides++;
                    return base.ResolvePrediction(pc, provider, ctr >= 0);
                }
                break;
            }
        }
        return base.ResolvePrediction(pc, provider, tagePred);
    }

    protected override void OnAfterUpdate(ulong pc, bool taken, bool provPred, int preScore, bool loopWasConfident) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident);

        if (_llbpIsProvider && _llbpHistIdx >= 0) {
            _storage.GetOrCreate(_llbpCtxKey).SatUpdate(_llbpPatternKey, taken);
        }
        else if (provPred != taken) {
            int allocTable = _lastProvider + 1;
            if ((uint)allocTable < (uint)NumTables)
                _storage.GetOrCreate(_llbpCtxKey).AllocateIfAbsent(PatternKey(pc, allocTable), taken);
        }

        if (taken) _rcr.Update(pc);
    }

    private int PatternKey(ulong pc, int t) => (TageTag(pc, t) << 2) | t;
}

internal sealed class RollingContextReg {
    private const int MaxWindow = 120;
    private const int W = 8;
    private const int D = 8;
    private const int S = 2;
    private const int CtWidth = 14;

    private readonly ulong[] _window = new ulong[MaxWindow];
    private int _head;
    private int _count;
    private uint _ccid;

    public uint ContextId => _ccid;

    public void Update(ulong pc) {
        _window[_head] = pc;
        _head = (_head + 1) % MaxWindow;
        if (_count < MaxWindow) _count++;
        if (_count == MaxWindow) _ccid = CalcHash(W, D);
    }

    private uint CalcHash(int n, int start) {
        const uint mask = (1u << CtWidth) - 1;
        uint hash = 0;
        int sh = 0;
        int collected = 0;
        for (int i = start; i < _count && collected < n; i++, collected++) {
            int idx = (_head - 1 - i + MaxWindow) % MaxWindow;
            hash ^= (uint)(_window[idx] << sh);
            sh = (sh + S) % CtWidth;
        }
        return hash & mask;
    }
}

internal sealed class PatternMap {
    private const int Cap = 16;
    private const int CtrMin = -4;
    private const int CtrMax = 3;

    private readonly (int Key, sbyte Ctr, bool Valid)[] _e = new (int, sbyte, bool)[Cap];
    private int _clock;

    public bool TryGet(int key, out sbyte ctr) {
        for (int i = 0; i < Cap; i++) {
            if (_e[i].Valid && _e[i].Key == key) { ctr = _e[i].Ctr; return true; }
        }
        ctr = 0;
        return false;
    }

    public void SatUpdate(int key, bool taken) {
        for (int i = 0; i < Cap; i++) {
            if (!_e[i].Valid || _e[i].Key != key) continue;
            sbyte c = taken ? (sbyte)Math.Min(_e[i].Ctr + 1, CtrMax)
                            : (sbyte)Math.Max(_e[i].Ctr - 1, CtrMin);
            _e[i] = (_e[i].Key, c, true);
            return;
        }
    }

    public void AllocateIfAbsent(int key, bool taken) {
        for (int i = 0; i < Cap; i++) {
            if (_e[i].Valid && _e[i].Key == key) return;
        }
        int slot = -1;
        for (int i = 0; i < Cap; i++) {
            if (!_e[i].Valid) { slot = i; break; }
        }
        if (slot < 0) { slot = _clock; _clock = (_clock + 1) % Cap; }
        _e[slot] = (key, taken ? (sbyte)0 : (sbyte)-1, true);
    }
}

internal sealed class LlbpStorage {
    private const int Capacity = 14336;
    private readonly Dictionary<uint, PatternMap> _map = new(Capacity + 1);
    private readonly Queue<uint> _order = new(Capacity + 1);

    public PatternMap? Get(uint key) => _map.TryGetValue(key, out PatternMap? pm) ? pm : null;

    public PatternMap GetOrCreate(uint key) {
        if (_map.TryGetValue(key, out PatternMap? pm)) return pm;
        if (_map.Count >= Capacity) {
            while (_order.TryDequeue(out uint old) && !_map.Remove(old)) { }
        }
        pm = new PatternMap();
        _map[key] = pm;
        _order.Enqueue(key);
        return pm;
    }
}
