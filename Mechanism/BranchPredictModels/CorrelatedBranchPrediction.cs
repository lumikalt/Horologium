namespace Mechanism.BranchPredictModels;

/// Two-level adaptive (m, n) predictor — per-branch local history (PAg).
/// Each PC has its own m-bit Branch History Register (BHR); the BHR indexes a
/// shared Pattern History Table (PHT) of n-bit saturating counters.
/// Distinct from Gselect/Gshare because history is local, not global.
public sealed class CorrelatedPredictor : IBranchPredictor {
    private readonly int _satMax;
    private readonly int _satThreshold;
    private readonly int _bhtMask;
    private readonly int _phtMask;
    private readonly int[] _bht;  // per-branch history registers
    private readonly byte[] _pht; // shared pattern history table (2^m entries)
    private readonly ulong[] _btb;

    /// <summary>
    /// Constructs a CorrelatedPredictor with the given history length.
    /// </summary>
    /// <param name="m">
    /// Bits in the BHR.
    /// </param>
    /// <param name="n"></param>
    /// Bits in the PHT.
    /// <param name="bhtSize">
    /// Entries in the BHT.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="m"/>, <paramref name="n"/> ≤ 0.</exception>
    public CorrelatedPredictor(int m = 2, int n = 2, int bhtSize = 1024) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        int phtSize = 1 << m;
        _satMax = (1 << n) - 1;
        _satThreshold = 1 << (n - 1);
        _bhtMask = bhtSize - 1;
        _phtMask = phtSize - 1;
        _bht = new int[bhtSize];
        _pht = new byte[phtSize];
        _btb = new ulong[bhtSize];
        Array.Fill(_pht, (byte)(_satThreshold - 1)); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int idx = BhtIndex(pc);
        int history = _bht[idx];
        bool taken = _pht[history] >= _satThreshold;
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = BhtIndex(pc);
        int history = _bht[idx];
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _pht[history] < _satMax: _pht[history]++; break;
            case false when _pht[history] > 0:      _pht[history]--; break;
        }

        _bht[idx] = ((history << 1) | (taken ? 1 : 0)) & _phtMask;
    }

    private int BhtIndex(ulong pc) => (int)((pc >> 2) & (uint)_bhtMask);
}

/// Gselect: global history register; PHT index = concat(GHR, lower PC bits).
/// PHT size = 2^(historyBits + pcBits). Each dimension contributes independently.
public sealed class GselectPredictor : IBranchPredictor {
    private readonly int _pcBits;
    private readonly int _ghrMask;
    private readonly int _pcMask;
    private readonly int _satThreshold;
    private readonly int _satMax;
    private readonly byte[] _pht;
    private readonly ulong[] _btb;
    private int _ghr;

    /// <summary>
    /// Constructs a Gselect predictor with the given history and PC lengths.
    /// </summary>
    /// <param name="historyBits">History length.</param>
    /// <param name="pcBits">
    /// Bits in the lower PC.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="historyBits"/>, <paramref name="pcBits"/> ≤ 0.
    /// </exception>
    public GselectPredictor(int historyBits = 4, int pcBits = 4) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyBits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pcBits);
        int phtSize = 1 << (historyBits + pcBits);
        _pcBits = pcBits;
        _ghrMask = (1 << historyBits) - 1;
        _pcMask = (1 << pcBits) - 1;
        _satThreshold = 2; // 2-bit counters
        _satMax = 3;
        _pht = new byte[phtSize];
        _btb = new ulong[phtSize];
        Array.Fill(_pht, (byte)1); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int idx = PhtIndex(pc);
        bool taken = _pht[idx] >= _satThreshold;
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = PhtIndex(pc);
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _pht[idx] < _satMax: _pht[idx]++; break;
            case false when _pht[idx] > 0:      _pht[idx]--; break;
        }

        _ghr = ((_ghr << 1) | (taken ? 1 : 0)) & _ghrMask;
    }

    // index = GHR occupies the upper historyBits; PC occupies the lower pcBits
    private int PhtIndex(ulong pc) =>
        ((_ghr & _ghrMask) << _pcBits) | ((int)(pc >> 2) & _pcMask);
}

/// Gshare: global history register; PHT index = GHR XOR lower PC bits.
/// XOR spreads aliasing more evenly than concatenation.
public sealed class GsharePredictor : IBranchPredictor {
    private readonly int _ghrMask;
    private readonly int _satThreshold;
    private readonly int _satMax;
    private readonly byte[] _pht;
    private readonly ulong[] _btb;
    private int _ghr;

    /// <summary>
    /// Constructs a Gshare predictor with the given history length.
    /// </summary>
    /// <param name="historyBits">History length.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyBits"/> ≤ 0</exception>
    public GsharePredictor(int historyBits = 8) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyBits);
        int phtSize = 1 << historyBits;
        _ghrMask = phtSize - 1;
        _satThreshold = 2; // 2-bit counters
        _satMax = 3;
        _pht = new byte[phtSize];
        _btb = new ulong[phtSize];
        Array.Fill(_pht, (byte)1); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int idx = PhtIndex(pc);
        bool taken = _pht[idx] >= _satThreshold;
        ulong target = taken ? _btb[idx] : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        int idx = PhtIndex(pc);
        _btb[idx] = actualTarget;
        switch (taken) {
            case true when _pht[idx] < _satMax: _pht[idx]++; break;
            case false when _pht[idx] > 0:      _pht[idx]--; break;
        }

        _ghr = ((_ghr << 1) | (taken ? 1 : 0)) & _ghrMask;
    }

    private int PhtIndex(ulong pc) => ((int)(pc >> 2) ^ _ghr) & _ghrMask;
}