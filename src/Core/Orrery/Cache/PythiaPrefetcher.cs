using System.Numerics;

namespace Orrery.Cache;

/// <summary>
///     Pythia: RL-based prefetcher using online SARSA with a tile-coded Q-value store
///     (Bera et al., MICRO 2021).
///     <para>
///         For every demand request Pythia extracts two program features (PC+Delta and a
///         rolling hash of the last-4 deltas), looks up the Q-value store (QVStore) to
///         select a prefetch offset action, and issues one prefetch.  Rewards are assigned
///         eagerly when the prefetched address is later demanded (RAT / RAL, depending on
///         whether the demand was a cache hit) or on EQ eviction when the prefetch was never
///         demanded (RIN).  The SARSA update fires once per EQ eviction, updating the
///         winning vault's tile planes for the evicted state-action pair.
///     </para>
///     <para>
///         Bandwidth feedback is approximated with the low-BW reward variants throughout —
///         the simulation has no direct DRAM-BW monitor.  The <see cref="IPrefetcher" />
///         interface has no fill callback, so <c>wasHit=true</c> is used as a proxy for
///         "prefetch was installed before the demand" (RAT); <c>wasHit=false</c> with a
///         matching EQ entry is treated as a late prefetch (RAL).
///     </para>
/// </summary>
public sealed class PythiaPrefetcher : IPrefetcher {
    private const int NumActions = 16;
    private const int NoPrefetchAction = 3; // Offsets[3] == 0

    // ── Hyperparameters (Table 2, Bera et al., MICRO 2021) ───────────────────
    private const float Alpha = 0.0065f;
    private const float Gamma = 0.556f;
    private const float Eps = 0.002f;

    // ── Reward levels (Table 2; low-BW RIN/RNP used — no BW monitor) ─────────
    private const float Rat = 20f;
    private const float Ral = 12f;
    private const float Rcl = -12f;
    private const float RinL = -8f;
    private const float RnpL = -4f;

    // ── QVStore: 2 vaults × 3 planes × 128 feature-entries × 16 actions ──────
    // Q(phi_i, A)  = SUM of partial Q-values across all planes in vault i.
    // Q(S, A)      = MAX over vaults of Q(phi_i, A).
    private const int Vaults = 2;
    private const int Planes = 3;
    private const int FEntries = 128;

    // ── EQ (Evaluation Queue): 256-entry FIFO ─────────────────────────────────
    private const int EqSize = 256;

    private const int IpSize = 64;

    private const int IpIndexBits = 6;

    // ── Action space (Table 2, pruned from [−63, 63]) ─────────────────────────
    private static readonly int[] Offsets = [-6, -3, -1, 0, 1, 3, 4, 5, 10, 11, 12, 16, 22, 23, 30, 32,];

    // Shift constants for tile coding (randomly fixed at design time — §4.2.1).
    private static readonly int[,] Shifts = { { 0, 3, 6, }, { 1, 4, 7, }, };

    // ── Geometry ──────────────────────────────────────────────────────────────
    private readonly int _blockBytes;

    private readonly EqEntry[] _eq = new EqEntry[PythiaPrefetcher.EqSize];
    private readonly IpState[] _ip = new IpState[PythiaPrefetcher.IpSize];
    private readonly int _lineShift;
    private readonly int _pageShift;

    private readonly float[,,,] _qvs = new float[PythiaPrefetcher.Vaults, PythiaPrefetcher.Planes,
        PythiaPrefetcher.FEntries, PythiaPrefetcher.NumActions];

    private int _eqCount;
    private int _eqHead; // index of oldest entry
    private int _eqTail; // next-write slot

    // ── XOR-shift RNG for ε-greedy exploration ────────────────────────────────
    private uint _rng = 0xDEADBEEFu;

    public PythiaPrefetcher(int blockBytes = 32, int pageBytes = 4096) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes))
            throw new ArgumentException("pageBytes must be a power of 2.", nameof(pageBytes));
        _blockBytes = blockBytes;
        _lineShift = BitOperations.Log2((uint)blockBytes);
        _pageShift = BitOperations.Log2((uint)pageBytes);

        // Initialize Q(S,A) = 1/(1-γ), distributed evenly across planes (§4.1).
        const float initPerPlane = 1f / (1f - PythiaPrefetcher.Gamma) / PythiaPrefetcher.Planes;
        for (var v = 0; v < PythiaPrefetcher.Vaults; v++)
        for (var p = 0; p < PythiaPrefetcher.Planes; p++)
        for (var f = 0; f < PythiaPrefetcher.FEntries; f++)
        for (var a = 0; a < PythiaPrefetcher.NumActions; a++)
            _qvs[v, p, f, a] = initPerPlane;
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong lineAddr = address >> _lineShift;
        ulong lineBase = lineAddr << _lineShift;
        ulong page = address >> _pageShift;

        // ── 1. Search EQ for this demand address and assign reward (Algorithm 1 ❶) ─
        for (var i = 0; i < _eqCount; i++) {
            int slot = (_eqHead + i) & (PythiaPrefetcher.EqSize - 1);
            ref EqEntry e = ref _eq[slot];
            if (e is { Valid: true, HasReward: false, } && e.PrefetchAddr == lineBase) {
                // wasHit proxies the filled bit: hit → prefetch installed before demand (RAT).
                e.Reward = wasHit ? PythiaPrefetcher.Rat : PythiaPrefetcher.Ral;
                e.HasReward = true;
                break;
            }
        }

        // ── 2. Compute delta from IP table (Algorithm 1 ❷) ───────────────────
        var ipIdx = (int)((pc >> 2) & (PythiaPrefetcher.IpSize - 1));
        ulong ipTag = pc >> (PythiaPrefetcher.IpIndexBits + 2);
        ref IpState ip = ref _ip[ipIdx];

        var delta = 0;
        var prevHistory = 0;
        if (ip.Valid && ip.Tag == ipTag) {
            delta = (int)((long)lineAddr - (long)ip.LastLineAddr);
            prevHistory = ip.DeltaHistory;
        }

        // ── 3. Compute QVStore plane indices for current state ─────────────────
        // Vault 0: PC+Delta — multiplicative hash of the load PC and current delta.
        uint f0 = ((uint)(pc >> 2) * 2654435761u) ^ (uint)(delta * 1013904223);
        // Vault 1: last-4 deltas — rolling hash of delta history.
        var f1 = (uint)prevHistory;

        var v0P0 = (int)((f0 >> PythiaPrefetcher.Shifts[0, 0]) & (PythiaPrefetcher.FEntries - 1));
        var v0P1 = (int)((f0 >> PythiaPrefetcher.Shifts[0, 1]) & (PythiaPrefetcher.FEntries - 1));
        var v0P2 = (int)((f0 >> PythiaPrefetcher.Shifts[0, 2]) & (PythiaPrefetcher.FEntries - 1));
        var v1P0 = (int)((f1 >> PythiaPrefetcher.Shifts[1, 0]) & (PythiaPrefetcher.FEntries - 1));
        var v1P1 = (int)((f1 >> PythiaPrefetcher.Shifts[1, 1]) & (PythiaPrefetcher.FEntries - 1));
        var v1P2 = (int)((f1 >> PythiaPrefetcher.Shifts[1, 2]) & (PythiaPrefetcher.FEntries - 1));

        // ── 4. Update IP state ────────────────────────────────────────────────
        ip.Valid = true;
        ip.Tag = ipTag;
        ip.LastLineAddr = lineAddr;
        ip.DeltaHistory = (prevHistory << 3) ^ (delta & 0x1FF);

        // ── 5. Select action via ε-greedy argmax Q(S, a) (Algorithm 1 ❸) ──────
        int action;
        if ((XorShift32() & 0xFFFFu) < (uint)(PythiaPrefetcher.Eps * 65536f)) {
            action = (int)(XorShift32() % PythiaPrefetcher.NumActions);
        }
        else {
            float bestQ = float.NegativeInfinity;
            action = 0;
            for (var a = 0; a < PythiaPrefetcher.NumActions; a++) {
                float q0 = _qvs[0, 0, v0P0, a] + _qvs[0, 1, v0P1, a] + _qvs[0, 2, v0P2, a];
                float q1 = _qvs[1, 0, v1P0, a] + _qvs[1, 1, v1P1, a] + _qvs[1, 2, v1P2, a];
                float q = q0 > q1 ? q0 : q1;
                if (q > bestQ) {
                    bestQ = q;
                    action = a;
                }
            }
        }

        // ── 6. Determine prefetch address and immediate reward (Algorithm 1 ❹) ─
        ulong prefetchAddr = 0;
        float immediateReward = float.NaN; // NaN = no immediate reward; assigned via EQ residency
        var issuedPrefetch = false;

        if (action == PythiaPrefetcher.NoPrefetchAction) { immediateReward = PythiaPrefetcher.RnpL; }
        else {
            long offset = (long)PythiaPrefetcher.Offsets[action] * _blockBytes;
            var pAddr = (ulong)((long)lineBase + offset);
            if (pAddr >> _pageShift != page) {
                immediateReward = PythiaPrefetcher.Rcl; // out-of-page prefetch (Algorithm 1 line 22)
            }
            else if (targets.Length > 0) {
                prefetchAddr = pAddr;
                targets[0] = pAddr;
                issuedPrefetch = true;
            }
        }

        // ── 7. Evict oldest EQ entry if queue is full ─────────────────────────
        EqEntry evicted = default;
        var hasEvicted = false;
        if (_eqCount == PythiaPrefetcher.EqSize) {
            evicted = _eq[_eqHead];
            hasEvicted = true;
            _eqHead = (_eqHead + 1) & (PythiaPrefetcher.EqSize - 1);
            _eqCount--;
        }

        // ── 8. Insert new EQ entry (Algorithm 1 ❺) ───────────────────────────
        _eq[_eqTail] = new EqEntry {
            V0P0 = v0P0, V0P1 = v0P1, V0P2 = v0P2,
            V1P0 = v1P0, V1P1 = v1P1, V1P2 = v1P2,
            Action = action,
            PrefetchAddr = prefetchAddr,
            Reward = float.IsNaN(immediateReward) ? 0f : immediateReward,
            HasReward = !float.IsNaN(immediateReward),
            Valid = true,
        };
        _eqTail = (_eqTail + 1) & (PythiaPrefetcher.EqSize - 1);
        _eqCount++;

        // ── 9. SARSA update for evicted entry (Algorithm 1 ❻) ────────────────
        if (hasEvicted && evicted.Valid) {
            float r = evicted.HasReward ? evicted.Reward : PythiaPrefetcher.RinL;
            // S2,A2 = EQ.head after eviction (Algorithm 1, line 28).
            SarsaUpdate(in evicted, r, in _eq[_eqHead]);
        }

        return issuedPrefetch ? 1 : 0;
    }

    private void SarsaUpdate(in EqEntry e1, float r, in EqEntry e2) {
        float q1V0 = _qvs[0, 0, e1.V0P0, e1.Action]
                   + _qvs[0, 1, e1.V0P1, e1.Action]
                   + _qvs[0, 2, e1.V0P2, e1.Action];
        float q1V1 = _qvs[1, 0, e1.V1P0, e1.Action]
                   + _qvs[1, 1, e1.V1P1, e1.Action]
                   + _qvs[1, 2, e1.V1P2, e1.Action];
        float q1 = q1V0 > q1V1 ? q1V0 : q1V1;

        float q2V0 = _qvs[0, 0, e2.V0P0, e2.Action]
                   + _qvs[0, 1, e2.V0P1, e2.Action]
                   + _qvs[0, 2, e2.V0P2, e2.Action];
        float q2V1 = _qvs[1, 0, e2.V1P0, e2.Action]
                   + _qvs[1, 1, e2.V1P1, e2.Action]
                   + _qvs[1, 2, e2.V1P2, e2.Action];
        float q2 = q2V0 > q2V1 ? q2V0 : q2V1;

        float tdError = r + PythiaPrefetcher.Gamma * q2 - q1;

        // Apply gradient to all planes of the winning vault (tile coding — §4.2.1).
        if (q1V0 >= q1V1) {
            _qvs[0, 0, e1.V0P0, e1.Action] += PythiaPrefetcher.Alpha * tdError;
            _qvs[0, 1, e1.V0P1, e1.Action] += PythiaPrefetcher.Alpha * tdError;
            _qvs[0, 2, e1.V0P2, e1.Action] += PythiaPrefetcher.Alpha * tdError;
        }
        else {
            _qvs[1, 0, e1.V1P0, e1.Action] += PythiaPrefetcher.Alpha * tdError;
            _qvs[1, 1, e1.V1P1, e1.Action] += PythiaPrefetcher.Alpha * tdError;
            _qvs[1, 2, e1.V1P2, e1.Action] += PythiaPrefetcher.Alpha * tdError;
        }
    }

    private uint XorShift32() {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng;
    }

    private struct EqEntry {
        // Precomputed QVStore plane indices for the state at issue time.
        public int V0P0, V0P1, V0P2; // vault 0, planes 0–2
        public int V1P0, V1P1, V1P2; // vault 1, planes 0–2
        public int Action;
        public ulong PrefetchAddr; // byte-aligned line base address; 0 if none issued
        public float Reward;
        public bool HasReward;
        public bool Valid;
    }

    // ── Per-IP state for delta computation (64-entry direct-mapped) ───────────
    private struct IpState {
        public ulong Tag;
        public ulong LastLineAddr;
        public int DeltaHistory; // rolling hash of recent deltas (vault 1 input)
        public bool Valid;
    }
}