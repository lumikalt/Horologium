using System.Numerics;

namespace Orrery.Cache;

/// <summary>
///     Perceptron-based Prefetch Filter over a de-throttled Signature Path Prefetcher
///     (Bhatia, Chacon, Teran, Gratz &amp; Jiménez, "Perceptron-Based Prefetch Filtering",
///     ISCA 2019). PPF is presented in the paper as a modification of SPP, not a generic
///     wrapper around an independent prefetcher: SPP's own confidence thresholds (T_P/T_F)
///     are discarded so its lookahead walk runs to exhaustion (bounded only by "no more
///     Pattern Table information" or a hardware safety cap), and every delta candidate the
///     walk produces — accurate or not — is passed through a hashed-perceptron filter that
///     decides to admit/reject. This lets the underlying SPP speculate far deeper than its own
///     throttling would allow, while the perceptron (trained online from real outcomes)
///     suppresses the resulting flood of low-value candidates.
///     <para>
///         <b>De-throttled SPP core.</b> Reimplements the same Signature Table (256 entries,
///         64 sets × 4-way LRU) / Pattern Table (512 entries × 4 deltas, 12-bit signature,
///         4-bit saturating counters) / Global History Register (8 entries, page-boundary
///         bootstrap) machinery as <see cref="SppPrefetcher" /> — see that class for the
///         signature-compression and GHR-bootstrap mechanics, which PPF leaves untouched.
///         The difference: the walk does not stop on low-path confidence. It continues
///         while the Pattern Table has any information for the current signature
///         (C_sig &gt; 0), up to <see cref="MaxLookahead" /> depth (an engineering safety
///         valve, not a paper value — the paper's own hardware still needs *some* bound).
///         Path confidence P_d = α·C_d·P_(d−1) is still computed at every depth — the paper
///         keeps it purely as a perceptron *feature*, no longer as a gate.
///     </para>
///     <para>
///         <b>Perceptron filter.</b> Nine hashed features per candidate (address, cache
///         line, page, PC⊕depth, PC-path-hash, PC⊕delta, confidence, page⊕confidence,
///         signature⊕delta — Section 4.2 of the paper) each index an independent weight
///         table of signed 5-bit saturating counters (range −16..+15); the nine partial
///         weights sum to a single confidence score. The paper's Table 3 gives table-count
///         totals by size (4×4096-entry, 2×2048-entry, 2×1024-entry, 1×128-entry — the
///         113,280-bit weight total cross-checks exactly) and states the *policy* for
///         assigning features to sizes ("features with higher correlation... were given
///         most importance and allowed full 12-bit indexing... low P-value [features] were
///         allocated fewer entries") but the PDF's per-feature table did not survive
///         extraction. The mapping below applies that stated policy using the paper's own
///         correlation ranking (page⊕confidence has the highest reported Pearson
///         coefficient, 0.904; PC⊕delta and PC⊕depth are named as the low-P-value features)
///         — it is a documented engineering reconstruction, not a verbatim table.
///     </para>
///     <para>
///         <b>Fill-level split not modeled.</b> The paper thresholds the perceptron sum
///         against two values, τ_hi (fill L2) and τ_lo (fill LLC; below it, reject) — the
///         same simplification <see cref="SppPrefetcher" /> already documents for SPP's own
///         T_F applies here: prefetches insert at one level in this simulator, so the two
///         thresholds collapse into a single admit threshold (<see cref="_admitThreshold" />,
///         default 0 — the natural zero-sum midpoint of symmetric ±16/+15 weights).
///     </para>
///     <para>
///         <b>Training.</b> Two of the paper's three training triggers map directly onto
///         <see cref="IPrefetcher.OnAccess" />, mirroring <see cref="SppPrefetcher" />'s own
///         Prefetch-Filter usefulness tracking: (1) a demand access hitting a line this
///         filter admitted trains the contributing weights toward "useful" (retrieved from
///         the 1024-entry direct-mapped Prefetch Table); (2) a demand access hitting a line
///         this filter *rejected* is a false negative — the weights are trained toward
///         "should have admitted" (retrieved from the 1024-entry Reject Table). The paper's
///         third trigger, an L2 eviction of a still-unused prefetched line, has no analogue
///         in <see cref="IPrefetcher" /> (no eviction callback reaches the prefetcher).
///         It is approximated the same way the direct-mapped Prefetch Table itself already
///         behaves in hardware: when a new admitted candidate's slot collision evicts an
///         existing entry never marked useful, that departing entry is trained
///         toward "should have rejected" before being overwritten. This is a documented
///         fidelity limit — a table-pressure proxy for genuine L1D/L2 capacity pressure —
///         not the paper's literal mechanism.
///     </para>
///     <para>
///         Per-candidate training uses a threshold-gated update (Jiménez-style dynamic
///         perceptron training, matching the paper's description of θ_p/θ_n "training
///         saturation" thresholds, whose numeric values are likewise not given in the
///         extracted text): weights move by ±1 only while the perceptron sum has not
///         already cleared <see cref="TrainMargin" /> in the correct direction, avoiding
///         needless re-training of an already-confident correct decision.
///     </para>
/// </summary>
public sealed class PpfPrefetcher : IPrefetcher {
    // ── De-throttled SPP core sizing (matches SppPrefetcher) ──────────────────
    private const int StEntries = 256;
    private const int PtEntries = 512;
    private const int PtWays = 4;
    private const int GhrEntries = 8;
    private const int SigMask = 0xFFF;
    private const int SigShift = 3;
    private const int CounterMax = 15;
    private const int AlphaMax = 99;
    private const int AlphaCold = 90;
    private const int AccuracyCounterMax = 1023;
    private const int MaxLookahead = 64;

    // ── Perceptron filter sizing (paper Table 3: 4×4096 + 2×2048 + 2×1024 + 1×128) ──
    private const int BigBits = 12;   // 4096 entries
    private const int MidBits = 11;   // 2048 entries
    private const int SmallBits = 10; // 1024 entries
    private const int TinyBits = 7;   // 128 entries
    private const int WeightMin = -16;
    private const int WeightMax = 15;

    private const int FilterEntries = 1024; // Prefetch Table / Reject Table
    private const int TrainMargin = 32;     // θp/θn: retrain while |sum| hasn't cleared this

    private readonly int _admitThreshold;
    private readonly int _deltaSignBit;
    private readonly GhrEntry[] _ghr = new GhrEntry[PpfPrefetcher.GhrEntries];
    private readonly int _lineShift;
    private readonly int _offsetBits;
    private readonly int _offsetMask;

    // ── Prefetch Table (admitted candidates) / Reject Table (rejected candidates) ──
    private readonly FilterEntry[] _prefetchTable = new FilterEntry[PpfPrefetcher.FilterEntries];
    private readonly byte[,] _ptCdelta = new byte[PpfPrefetcher.PtEntries, PpfPrefetcher.PtWays];
    private readonly byte[] _ptCsig = new byte[PpfPrefetcher.PtEntries];
    private readonly short[,] _ptDelta = new short[PpfPrefetcher.PtEntries, PpfPrefetcher.PtWays];
    private readonly FilterEntry[] _rejectTable = new FilterEntry[PpfPrefetcher.FilterEntries];

    // ── SPP core state ────────────────────────────────────────────────────────
    private readonly StEntry[] _st = new StEntry[PpfPrefetcher.StEntries];
    private readonly sbyte[] _wCacheLine = new sbyte[1 << PpfPrefetcher.BigBits];
    private readonly sbyte[] _wConfidence = new sbyte[1 << PpfPrefetcher.SmallBits];
    private readonly sbyte[] _wPage = new sbyte[1 << PpfPrefetcher.BigBits];
    private readonly sbyte[] _wPageXorConf = new sbyte[1 << PpfPrefetcher.BigBits];
    private readonly sbyte[] _wPcPathHash = new sbyte[1 << PpfPrefetcher.MidBits];
    private readonly sbyte[] _wPcXorDelta = new sbyte[1 << PpfPrefetcher.SmallBits];
    private readonly sbyte[] _wPcXorDepth = new sbyte[1 << PpfPrefetcher.TinyBits];

    // ── Perceptron weight tables (indexed by feature ordinal) ────────────────
    // 0 PhysAddr, 1 CacheLine, 2 Page, 3 PageXorConf  → 12-bit (paper's 4 highest-importance)
    // 4 SigXorDelta, 5 PcPathHash                     → 11-bit
    // 6 PcXorDelta, 7 Confidence                       → 10-bit
    // 8 PcXorDepth                                     → 7-bit (paper's stated low-P-value feature)
    private readonly sbyte[] _wPhysAddr = new sbyte[1 << PpfPrefetcher.BigBits];
    private readonly sbyte[] _wSigXorDelta = new sbyte[1 << PpfPrefetcher.MidBits];
    private long _age;
    private int _cTotal;
    private int _cUseful;

    // ── Rolling 3-PC history for the PC-path feature ─────────────────────────
    private ulong _pc1, _pc2, _pc3;

    public PpfPrefetcher(int blockBytes = 32, int pageBytes = 4096, int admitThreshold = 0) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes) || pageBytes <= blockBytes)
            throw new ArgumentException("pageBytes must be a power of 2 greater than blockBytes.", nameof(pageBytes));
        _lineShift = BitOperations.Log2((uint)blockBytes);
        _offsetBits = BitOperations.Log2((uint)pageBytes) - _lineShift;
        _offsetMask = (1 << _offsetBits) - 1;
        _deltaSignBit = 1 << _offsetBits;
        _admitThreshold = admitThreshold;
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong line = address >> _lineShift;
        ulong page = line >> _offsetBits;
        var offset = (int)(line & (uint)_offsetMask);
        _age++;

        // Feedback for candidates suggested by prior calls, before generating new ones.
        FeedbackDemandAccess(line);

        int sig;
        int stIdx = StFind(page);
        if (stIdx >= 0) {
            ref StEntry st = ref _st[stIdx];
            int delta = offset - st.LastOffset;
            if (delta != 0) {
                PtTrain(st.Sig, delta);
                st.Sig = ExtendSignature(st.Sig, delta);
                st.LastOffset = offset;
            }

            st.Age = _age;
            sig = st.Sig;
        }
        else {
            sig = GhrBootstrap(offset);
            StAllocate(page, offset, sig);
        }

        _pc3 = _pc2;
        _pc2 = _pc1;
        _pc1 = pc;

        return Walk(pc, sig, line, page, targets);
    }

    // ── De-throttled lookahead walk: emits every candidate to the perceptron ──

    private int Walk(ulong pc, int sig, ulong line, ulong basePage, Span<ulong> targets) {
        int alpha = _cTotal > 0
            ? Math.Min(PpfPrefetcher.AlphaMax, _cUseful * 100 / _cTotal)
            : PpfPrefetcher.AlphaCold;

        var count = 0;
        int curSig = sig;
        var curLine = (long)line;
        var pathConf = 100;
        // The triggering address/cache-line/page stay fixed for the whole walk — this is
        // exactly why the paper needs "PC XOR Depth" to disambiguate depths, since address
        // and PC alone are otherwise identical across every candidate in one OnAccess call.
        ulong triggerAddr = line << _lineShift;

        for (var depth = 0; depth < PpfPrefetcher.MaxLookahead; depth++) {
            int idx = PtIndex(curSig);
            int csig = _ptCsig[idx];
            if (csig == 0) break;

            int bestConf = -1;
            var bestDelta = 0;

            for (var w = 0; w < PpfPrefetcher.PtWays; w++) {
                int cdelta = _ptCdelta[idx, w];
                if (cdelta == 0) continue;
                int cd = cdelta * 100 / csig;
                int pd = depth == 0 ? cd : alpha * cd * pathConf / 10_000;
                int delta = _ptDelta[idx, w];

                // Recursion picks the highest path-confidence delta, matching
                // SppPrefetcher's convention (pd, not the local cd, since pd already
                // folds in how well depth-0..depth-1 predicted so far).
                if (pd > bestConf) {
                    bestConf = pd;
                    bestDelta = delta;
                }

                long target = curLine + delta;
                if (target < 0 || (ulong)target >> _offsetBits != basePage) {
                    // Crossing prediction: SPP's own boundary-learning path, unaffected by
                    // the perceptron filter (Section 4.1 keeps this machinery untouched).
                    GhrInsert(curSig, pd, (int)(curLine & _offsetMask), delta);
                    continue;
                }

                if (count >= targets.Length) continue;
                var targetLine = (ulong)target;
                ulong targetAddr = targetLine << _lineShift;
                if (AlreadyQueued(targets[..count], targetAddr)) continue;

                var cand = new Candidate {
                    Pc = pc, Address = triggerAddr, TargetLine = targetLine, Signature = curSig,
                    PcHash = PcPathHash(), Delta = delta, Confidence = pd, Depth = depth,
                };
                if (Admit(cand)) targets[count++] = targetAddr;
            }

            if (bestConf < 0) break;
            curSig = ExtendSignature(curSig, bestDelta);
            curLine += bestDelta;
            pathConf = bestConf;
        }

        return count;
    }

    private static bool AlreadyQueued(ReadOnlySpan<ulong> issued, ulong targetAddr) {
        foreach (ulong a in issued)
            if (a == targetAddr)
                return true;
        return false;
    }

    // ── Perceptron inference + candidate recording ────────────────────────────

    private bool Admit(in Candidate c) {
        // Feature values derive from the ORIGINAL triggering access (paper's "physical
        // address / cache line / page of the demand access that triggers the prefetch").
        ulong physAddr = c.Address;
        ulong cacheLine = c.Address >> _lineShift;
        ulong page = cacheLine >> _offsetBits;
        var pageXorConf = (int)(page ^ (ulong)c.Confidence);
        int sigXorDelta = c.Signature ^ EncodeDelta(c.Delta);
        var pcXorDelta = (int)(c.Pc ^ (ulong)EncodeDelta(c.Delta));
        var pcXorDepth = (int)(c.Pc ^ (ulong)c.Depth);

        int i0 = Hash(physAddr, PpfPrefetcher.BigBits);
        int i1 = Hash(cacheLine, PpfPrefetcher.BigBits);
        int i2 = Hash(page, PpfPrefetcher.BigBits);
        int i3 = Hash((ulong)pageXorConf, PpfPrefetcher.BigBits);
        int i4 = Hash((ulong)sigXorDelta, PpfPrefetcher.MidBits);
        int i5 = Hash(c.PcHash, PpfPrefetcher.MidBits);
        int i6 = Hash((ulong)pcXorDelta, PpfPrefetcher.SmallBits);
        int i7 = Hash((ulong)c.Confidence, PpfPrefetcher.SmallBits);
        int i8 = Hash((ulong)pcXorDepth, PpfPrefetcher.TinyBits);

        int sum = _wPhysAddr[i0] + _wCacheLine[i1] + _wPage[i2] + _wPageXorConf[i3]
                + _wSigXorDelta[i4] + _wPcPathHash[i5] + _wPcXorDelta[i6] + _wConfidence[i7]
                + _wPcXorDepth[i8];

        bool admit = sum > _admitThreshold;
        var meta = new FilterMeta {
            I0 = i0, I1 = i1, I2 = i2, I3 = i3, I4 = i4, I5 = i5, I6 = i6, I7 = i7, I8 = i8,
        };

        // Table indexing uses the candidate's TARGET line — the address a later demand
        // access or eviction will actually match against — not the trigger address above.
        FilterEntry[] table = admit ? _prefetchTable : _rejectTable;
        int slot = FilterIndex(c.TargetLine);
        byte tag = FilterTag(c.TargetLine);

        if (admit && table[slot].Valid && !table[slot].Useful && table[slot].Tag != tag)
            // Slot-overwrite proxy for the paper's eviction-triggered negative signal
            // (see class docs): the departing entry never saw a demand hit, so it is
            // trained toward "should have rejected" before being replaced.
            TrainNegative(table[slot].Meta);

        table[slot] = new FilterEntry { Valid = true, Tag = tag, Useful = false, Meta = meta, };
        return admit;
    }

    /// <summary>
    ///     Every OnAccess call is a stand-in for "L2 demand access": check whether this
    ///     line was previously admitted (positive feedback) or previously rejected
    ///     (false-negative correction).
    /// </summary>
    private void FeedbackDemandAccess(ulong line) {
        int slot = FilterIndex(line);
        byte tag = FilterTag(line);

        if (_prefetchTable[slot].Valid && _prefetchTable[slot].Tag == tag && !_prefetchTable[slot].Useful) {
            _prefetchTable[slot].Useful = true;
            TrainPositive(_prefetchTable[slot].Meta);
            if (++_cUseful >= PpfPrefetcher.AccuracyCounterMax) {
                _cTotal >>= 1;
                _cUseful >>= 1;
            }
        }

        if (_rejectTable[slot].Valid && _rejectTable[slot].Tag == tag) {
            TrainPositive(_rejectTable[slot].Meta); // false negative: should have admitted
            _rejectTable[slot].Valid = false;       // consume — this specific miss is resolved
        }
    }

    private void TrainPositive(in FilterMeta m) {
        int sum = SumOf(m);
        if (sum > PpfPrefetcher.TrainMargin) return; // already confidently correct
        Bump(m, +1);
    }

    private void TrainNegative(in FilterMeta m) {
        int sum = SumOf(m);
        if (sum < -PpfPrefetcher.TrainMargin) return; // already confidently correct
        Bump(m, -1);
    }

    private int SumOf(in FilterMeta m) =>
        _wPhysAddr[m.I0] + _wCacheLine[m.I1] + _wPage[m.I2] + _wPageXorConf[m.I3]
      + _wSigXorDelta[m.I4] + _wPcPathHash[m.I5] + _wPcXorDelta[m.I6] + _wConfidence[m.I7]
      + _wPcXorDepth[m.I8];

    private void Bump(in FilterMeta m, int step) {
        _wPhysAddr[m.I0] = Sat(_wPhysAddr[m.I0] + step);
        _wCacheLine[m.I1] = Sat(_wCacheLine[m.I1] + step);
        _wPage[m.I2] = Sat(_wPage[m.I2] + step);
        _wPageXorConf[m.I3] = Sat(_wPageXorConf[m.I3] + step);
        _wSigXorDelta[m.I4] = Sat(_wSigXorDelta[m.I4] + step);
        _wPcPathHash[m.I5] = Sat(_wPcPathHash[m.I5] + step);
        _wPcXorDelta[m.I6] = Sat(_wPcXorDelta[m.I6] + step);
        _wConfidence[m.I7] = Sat(_wConfidence[m.I7] + step);
        _wPcXorDepth[m.I8] = Sat(_wPcXorDepth[m.I8] + step);
    }

    private static sbyte Sat(int v) =>
        (sbyte)Math.Clamp(v, PpfPrefetcher.WeightMin, PpfPrefetcher.WeightMax);

    private ulong PcPathHash() => _pc1 ^ (_pc2 >> 1) ^ (_pc3 >> 2);

    private static int EncodeDelta(int delta) => delta & 0xFF; // small signed deltas, low byte

    private static int Hash(ulong value, int bits) {
        ulong h = value * 0x9E3779B97F4A7C15UL;
        h ^= h >> 32;
        return (int)(h & ((1UL << bits) - 1));
    }

    private static int FilterIndex(ulong line) => (int)((line ^ (line >> 10)) & (PpfPrefetcher.FilterEntries - 1));
    private static byte FilterTag(ulong line) => (byte)((line >> 10) & 0x3F);

    // ── SPP core: Pattern Table, signature compression, ST, GHR (mirrors SppPrefetcher) ──

    private static int PtIndex(int sig) => sig & (PpfPrefetcher.PtEntries - 1);

    private void PtTrain(int sig, int delta) {
        int idx = PtIndex(sig);
        if (_ptCsig[idx] >= PpfPrefetcher.CounterMax) {
            _ptCsig[idx] >>= 1;
            for (var w = 0; w < PpfPrefetcher.PtWays; w++) _ptCdelta[idx, w] >>= 1;
        }

        _ptCsig[idx]++;

        var victim = 0;
        for (var w = 0; w < PpfPrefetcher.PtWays; w++) {
            if (_ptCdelta[idx, w] > 0 && _ptDelta[idx, w] == delta) {
                _ptCdelta[idx, w]++;
                return;
            }

            if (_ptCdelta[idx, w] < _ptCdelta[idx, victim]) victim = w;
        }

        _ptDelta[idx, victim] = (short)delta;
        _ptCdelta[idx, victim] = 1;
    }

    private int ExtendSignature(int sig, int delta) {
        int mag = Math.Abs(delta) & (_deltaSignBit - 1);
        int enc = delta < 0 ? _deltaSignBit | mag : mag;
        return ((sig << PpfPrefetcher.SigShift) ^ enc) & PpfPrefetcher.SigMask;
    }

    private int StFind(ulong page) {
        var tag = (ushort)(page & 0xFFFF);
        var set = (int)(page & (PpfPrefetcher.StEntries / 4 - 1));
        for (var w = 0; w < 4; w++) {
            int i = set * 4 + w;
            if (_st[i].Valid && _st[i].Tag == tag) return i;
        }

        return -1;
    }

    private void StAllocate(ulong page, int offset, int sig) {
        var tag = (ushort)(page & 0xFFFF);
        var set = (int)(page & (PpfPrefetcher.StEntries / 4 - 1));
        int victim = set * 4;
        for (var w = 1; w < 4; w++) {
            int i = set * 4 + w;
            if (!_st[i].Valid) {
                victim = i;
                break;
            }

            if (_st[i].Age < _st[victim].Age) victim = i;
        }

        _st[victim] = new StEntry { Valid = true, Tag = tag, LastOffset = offset, Sig = sig, Age = _age, };
    }

    private void GhrInsert(int sig, int confidence, int lastOffset, int delta) {
        var victim = 0;
        for (var i = 0; i < PpfPrefetcher.GhrEntries; i++) {
            if (!_ghr[i].Valid) {
                victim = i;
                break;
            }

            if (_ghr[i].Age < _ghr[victim].Age) victim = i;
        }

        _ghr[victim] = new GhrEntry {
            Valid = true, Sig = sig, Confidence = confidence, LastOffset = lastOffset, Delta = delta, Age = _age,
        };
    }

    private int GhrBootstrap(int offset) {
        int linesPerPage = _offsetMask + 1;
        int best = -1;
        for (var i = 0; i < PpfPrefetcher.GhrEntries; i++) {
            if (!_ghr[i].Valid) continue;
            int landing = ((_ghr[i].LastOffset + _ghr[i].Delta) % linesPerPage + linesPerPage) % linesPerPage;
            if (landing != offset) continue;
            if (best < 0 || _ghr[i].Confidence > _ghr[best].Confidence) best = i;
        }

        return best >= 0 ? ExtendSignature(_ghr[best].Sig, _ghr[best].Delta) : 0;
    }

    private struct StEntry {
        public bool Valid;
        public ushort Tag;
        public int LastOffset;
        public int Sig;
        public long Age;
    }

    private struct GhrEntry {
        public bool Valid;
        public int Sig;
        public int Confidence;
        public int LastOffset;
        public int Delta;
        public long Age;
    }

    private struct Candidate {
        public ulong Pc;

        public ulong
            Address; // byte address of the ORIGINAL triggering access (feature input, fixed for the whole walk)

        public ulong
            TargetLine; // line address this candidate predicts (table index — what a later demand access matches against)

        public int Signature;
        public ulong PcHash;
        public int Delta;
        public int Confidence;
        public int Depth;
    }

    private struct FilterMeta {
        public int I0, I1, I2, I3, I4, I5, I6, I7, I8;
    }

    private struct FilterEntry {
        public bool Valid;
        public byte Tag;
        public bool Useful;
        public FilterMeta Meta;
    }
}