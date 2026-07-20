#region

using Mechanism;

#endregion

namespace Pipeline.Ooo;

/// <summary>
///     Token-passing critical-path predictor (Fields, Rubin &amp; Bodík, "Focusing Processor
///     Policies via Critical-Path Prediction", ISCA 2001).
///     <para>
///         Models each instruction as three dependence-graph nodes — D (dispatch), E (execute),
///         C (commit) — connected by seven edge types (Table 1 of the paper). The caller
///         (<c>OooeTrain</c>) resolves the paper's "last-arriving" rules (Table 2) into concrete
///         source references at commit time; this class only tracks which of a small set of
///         "tokens" have propagated forward from a seed instruction's E-node, and trains a
///         PC-indexed hysteresis table from whether a token survives <c>propagationDistance</c>
///         commits (Figure 5).
///     </para>
///     <para>
///         Token array: <c>robCapacity</c> slots (indexed by <c>InstrId % robCapacity</c>, valid
///         because the model guarantees no critical-path-edge spans more instructions than the
///         ROB — Section 2) × 3 nodes × <c>tokenCount</c> simultaneous tokens, one bit each
///         (Figure 6, Table 3 defaults: 8 tokens). A token freed (trained or newly planted) is
///         replanted at a random delay of 0–9 further commits, seeded for determinism.
///     </para>
/// </summary>
public sealed class TokenPassingCriticalityPredictor : ICriticalityPredictor {
    private readonly byte[] _cpTable; // PC-indexed 6-bit hysteresis (0-63); critical if > 8
    private readonly int _cpTableMask;
    private readonly ulong[] _plantedAt;         // commit counter value when this token was (re)planted
    private readonly ulong _propagationDistance; // 500 + robCapacity (paper's formula)
    private readonly ulong[] _replantAt;         // commit counter value at which a free token replants
    private readonly Random _rng;
    private readonly int _robCapacity;
    private readonly ulong[] _seedPc;
    private readonly bool[] _tokenInUse;
    private readonly byte[,] _tokens; // [slot, (int)CpNode] -> bitmask of tokens present
    private ulong _commitCount;

    public TokenPassingCriticalityPredictor(
        int robCapacity,
        int cpTableSize = 16384,
        int tokenCount = 8,
        int seed = 0
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(robCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokenCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenCount, 8); // one bit per token in a byte
        _robCapacity = robCapacity;
        _cpTable = new byte[cpTableSize];
        _cpTableMask = cpTableSize - 1;
        _propagationDistance = 500 + (ulong)robCapacity;
        _rng = new Random(seed);

        _tokens = new byte[robCapacity, 3];
        _tokenInUse = new bool[tokenCount];
        _seedPc = new ulong[tokenCount];
        _plantedAt = new ulong[tokenCount];
        _replantAt = new ulong[tokenCount];
        // Stagger initial planting across the first tokenCount commits instead of
        // seeding all tokens from commit #1.
        for (var k = 0; k < tokenCount; k++) _replantAt[k] = (ulong)k;
    }

    public bool PredictCritical(ulong pc) => _cpTable[CpTableIdx(pc)] > 8;

    public void OnCommit(in CriticalityCommitInfo info) {
        _commitCount++;
        var slot = (int)(info.InstrId % (ulong)_robCapacity);

        // Resolve this instruction's own D/E/C live-token bits before evicting the slot's
        // previous occupant: the CD edge (ROB-stall dispatch) reads exactly this slot's
        // stale C-bits, left behind by instrId - robCapacity's own commit.
        byte dBits = ResolveBits(info.DSourceNode, info.DSourceInstrId);
        byte eBits = info.ESourceNode == CpNode.D ? dBits : ResolveBits(CpNode.E, info.ESourceInstrId);
        byte cBits = info.CSourceNode == CpNode.E ? eBits : ResolveBits(CpNode.C, info.CSourceInstrId);

        _tokens[slot, (int)CpNode.D] = dBits;
        _tokens[slot, (int)CpNode.E] = eBits;
        _tokens[slot, (int)CpNode.C] = cBits;

        for (var k = 0; k < _tokenInUse.Length; k++) {
            if (!_tokenInUse[k]) {
                if (_commitCount < _replantAt[k]) continue;
                _tokenInUse[k] = true;
                _seedPc[k] = info.Pc;
                _plantedAt[k] = _commitCount;
                _tokens[slot, (int)CpNode.E] |= (byte)(1 << k);
                continue;
            }

            if (_commitCount - _plantedAt[k] < _propagationDistance) continue;
            bool alive = IsTokenLive(k);
            Train(_seedPc[k], alive);
            _tokenInUse[k] = false;
            _replantAt[k] = _commitCount + 1 + (ulong)_rng.Next(10);
        }
    }

    private byte ResolveBits(CpNode node, ulong instrId) {
        var slot = (int)(instrId % (ulong)_robCapacity);
        return _tokens[slot, (int)node];
    }

    private bool IsTokenLive(int token) {
        var bit = (byte)(1 << token);
        for (var slot = 0; slot < _robCapacity; slot++)
        for (var node = 0; node < 3; node++)
            if ((_tokens[slot, node] & bit) != 0)
                return true;
        return false;
    }

    private void Train(ulong pc, bool critical) {
        int idx = CpTableIdx(pc);
        _cpTable[idx] = critical
            ? (byte)Math.Min(63, _cpTable[idx] + 8)
            : (byte)Math.Max(0, _cpTable[idx] - 1);
    }

    private int CpTableIdx(ulong pc) => (int)((pc >> 2) & (uint)_cpTableMask);
}