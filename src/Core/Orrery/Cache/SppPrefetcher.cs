using System.Numerics;

namespace Orrery.Cache;

/// <summary>
///     Signature Path Prefetcher (Kim et al., "Path Confidence based Lookahead Prefetching",
///     MICRO 2016). A PC-free lookahead prefetcher: per-page delta history is compressed into
///     a 12-bit signature (<c>sig = (sig &lt;&lt; 3) XOR delta</c>, sign+magnitude deltas), which
///     indexes a global Pattern Table of (delta, C_delta) predictions with a per-signature
///     occurrence counter C_sig. Prediction walks a <em>signature path</em>: the best delta
///     extends the signature speculatively and the walk recurses, with path confidence
///     P_d = α · C_d · P_(d−1) (P_0 = C_d, all fixed-point percent); candidates whose P_d
///     clears the prefetch threshold T_P (25) are issued, and the walk stops when confidence
///     falls below T_P. α is the measured global prefetch accuracy (C_useful / C_total from
///     the Prefetch Filter), throttling lookahead depth per program phase.
///     <para>
///         Page-boundary learning: a prediction that crosses the 4KB page is not issued but
///         recorded in an 8-entry Global History Register (signature, confidence, last
///         offset, delta). The first access to a page not tracked by the Signature Table
///         searches the GHR for an entry whose <c>(lastOffset + delta) mod linesPerPage</c>
///         matches the new offset; a match bootstraps the new page's signature so complex
///         patterns continue across physical pages with no per-page warmup. The lookahead
///         walk itself also continues past a crossing (a later delta can fold back into the
///         current page and is then issued, per the paper's Page A/B example).
///     </para>
///     <para>
///         Structures follow Table II: 256-entry ST (LRU), 512-entry PT × 4 deltas (4-bit
///         saturating counters, halved together when C_sig saturates), 1024-entry
///         direct-mapped Prefetch Filter with a per-entry useful bit feeding 10-bit
///         C_total/C_useful accuracy counters, 8-entry GHR.
///     </para>
///     <para>
///         Adaptations to this simulator: SPP trains at whatever cache level hosts the
///         prefetcher (the paper's L2 becomes the innermost cache here) and every
///         <see cref="OnAccess" /> call is a training + prediction event; the T_F fill-level
///         split (L2 vs LLC insertion by confidence) is not modeled since prefetches insert
///         at one level; PF entries age out by direct-mapped overwrite instead of L2
///         eviction callbacks; the delta sign+magnitude width follows the configured
///         page/block geometry (7 bits for 64 lines/page, as in the paper). The paper's
///         "stop on low L2 read-queue resources" condition maps to the caller's finite
///         target span. GHR bootstrap restores the signature only; path confidence restarts
///         from the PT counters.
///     </para>
/// </summary>
public sealed class SppPrefetcher : IPrefetcher {
    private const int StEntries = 256;
    private const int PtEntries = 512;
    private const int PtWays = 4;
    private const int PfEntries = 1024;
    private const int GhrEntries = 8;

    private const int SigMask = 0xFFF; // 12-bit signature
    private const int SigShift = 3;    // bits shifted per delta (paper's sensitivity optimum)
    private const int CounterMax = 15; // 4-bit C_sig / C_delta

    private const int PrefetchThreshold = 25;    // T_P, percent
    private const int AlphaMax = 99;             // α < 1 strictly, so confidence always decays
    private const int AlphaCold = 90;            // α before any accuracy feedback exists
    private const int AccuracyCounterMax = 1023; // 10-bit C_total / C_useful
    private const int MaxLookahead = 64;         // engineering guard on the walk
    private readonly int _deltaSignBit;          // sign bit position for sign+magnitude encoding
    private readonly GhrEntry[] _ghr = new GhrEntry[SppPrefetcher.GhrEntries];

    private readonly int _lineShift;
    private readonly int _offsetBits; // log2(lines per page)
    private readonly int _offsetMask;
    private readonly PfEntry[] _pf = new PfEntry[SppPrefetcher.PfEntries];
    private readonly byte[,] _ptCdelta = new byte[SppPrefetcher.PtEntries, SppPrefetcher.PtWays];
    private readonly byte[] _ptCsig = new byte[SppPrefetcher.PtEntries];
    private readonly short[,] _ptDelta = new short[SppPrefetcher.PtEntries, SppPrefetcher.PtWays];

    private readonly StEntry[] _st = new StEntry[SppPrefetcher.StEntries];
    private long _age; // monotone counter driving ST/GHR LRU

    private int _cTotal;
    private int _cUseful;

    public SppPrefetcher(int blockBytes = 32, int pageBytes = 4096) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes) || pageBytes <= blockBytes)
            throw new ArgumentException("pageBytes must be a power of 2 greater than blockBytes.", nameof(pageBytes));
        _lineShift = BitOperations.Log2((uint)blockBytes);
        _offsetBits = BitOperations.Log2((uint)pageBytes) - _lineShift;
        _offsetMask = (1 << _offsetBits) - 1;
        _deltaSignBit = 1 << _offsetBits; // magnitude uses _offsetBits bits, sign one more
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong line = address >> _lineShift;
        ulong page = line >> _offsetBits;
        var offset = (int)(line & (uint)_offsetMask);
        _age++;

        // Track usefulness: a demand access landing on a filter-recorded line means the
        // prefetch that installed it was useful (counted once per entry).
        PfDemandCheck(line);

        // ── Signature Table lookup / training ────────────────────────────────
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
            // New page: try to inherit a signature from a boundary-crossing prediction.
            sig = GhrBootstrap(offset);
            StAllocate(page, offset, sig);
        }

        // ── Path-confidence lookahead walk ────────────────────────────────────
        return Predict(sig, line, page, targets);
    }

    // ── Pattern Table ─────────────────────────────────────────────────────────

    private static int PtIndex(int sig) => sig & (SppPrefetcher.PtEntries - 1);

    private void PtTrain(int sig, int delta) {
        int idx = PtIndex(sig);

        // C_delta can never exceed C_sig (they increment together and halve together),
        // so C_sig saturates first; halving all counters on its saturation covers both.
        if (_ptCsig[idx] >= SppPrefetcher.CounterMax) {
            _ptCsig[idx] >>= 1;
            for (var w = 0; w < SppPrefetcher.PtWays; w++) _ptCdelta[idx, w] >>= 1;
        }

        _ptCsig[idx]++;

        var victim = 0;
        for (var w = 0; w < SppPrefetcher.PtWays; w++) {
            if (_ptCdelta[idx, w] > 0 && _ptDelta[idx, w] == delta) {
                _ptCdelta[idx, w]++;
                return;
            }

            if (_ptCdelta[idx, w] < _ptCdelta[idx, victim]) victim = w;
        }

        _ptDelta[idx, victim] = (short)delta;
        _ptCdelta[idx, victim] = 1;
    }

    // ── Prediction walk ───────────────────────────────────────────────────────

    private int Predict(int sig, ulong line, ulong basePage, Span<ulong> targets) {
        int alpha = _cTotal > 0
            ? Math.Min(SppPrefetcher.AlphaMax, _cUseful * 100 / _cTotal)
            : SppPrefetcher.AlphaCold;

        var count = 0;
        int curSig = sig;
        var curLine = (long)line;
        var pathConf = 100;

        for (var depth = 0; depth < SppPrefetcher.MaxLookahead; depth++) {
            int idx = PtIndex(curSig);
            int csig = _ptCsig[idx];
            if (csig == 0) break;

            int bestConf = -1;
            var bestDelta = 0;
            for (var w = 0; w < SppPrefetcher.PtWays; w++) {
                int cdelta = _ptCdelta[idx, w];
                if (cdelta == 0) continue;
                int cd = cdelta * 100 / csig;
                // P_0 = C_d for the direct prediction; deeper levels decay by α.
                int pd = depth == 0 ? cd : alpha * cd * pathConf / 10_000;
                if (pd < SppPrefetcher.PrefetchThreshold) continue;

                int delta = _ptDelta[idx, w];
                long target = curLine + delta;
                if (target >= 0 && (ulong)target >> _offsetBits == basePage) {
                    if (count < targets.Length && PfCheckAndInsert((ulong)target)) {
                        targets[count++] = (ulong)target << _lineShift;
                        if (++_cTotal >= SppPrefetcher.AccuracyCounterMax) {
                            _cTotal >>= 1;
                            _cUseful >>= 1;
                        }
                    }
                }
                else {
                    // Crossing prediction: not issued (physical adjacency unknown) —
                    // recorded so the next page's first touch can inherit the pattern.
                    GhrInsert(curSig, pd, (int)(curLine & _offsetMask), delta);
                }

                if (pd > bestConf) {
                    bestConf = pd;
                    bestDelta = delta;
                }
            }

            if (bestConf < SppPrefetcher.PrefetchThreshold) break;

            // Single lookahead signature: extend down the highest-confidence path. The
            // walk continues even past a page crossing — a later delta may fold back
            // into the base page and issue there.
            curSig = ExtendSignature(curSig, bestDelta);
            curLine += bestDelta;
            pathConf = bestConf; // bestConf is already P_d = α·C_d·P_(d−1)
        }

        return count;
    }

    // ── Signature compression (Equation 2) ────────────────────────────────────

    private int ExtendSignature(int sig, int delta) {
        int mag = Math.Abs(delta) & (_deltaSignBit - 1);
        int enc = delta < 0 ? _deltaSignBit | mag : mag;
        return ((sig << SppPrefetcher.SigShift) ^ enc) & SppPrefetcher.SigMask;
    }

    // ── Signature Table (256-entry, LRU) ──────────────────────────────────────

    private int StFind(ulong page) {
        var tag = (ushort)(page & 0xFFFF);
        var set = (int)(page & (SppPrefetcher.StEntries / 4 - 1)); // 64 sets × 4 ways
        for (var w = 0; w < 4; w++) {
            int i = set * 4 + w;
            if (_st[i].Valid && _st[i].Tag == tag) return i;
        }

        return -1;
    }

    private void StAllocate(ulong page, int offset, int sig) {
        var tag = (ushort)(page & 0xFFFF);
        var set = (int)(page & (SppPrefetcher.StEntries / 4 - 1));
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

    // ── Prefetch Filter (1024-entry direct-mapped) ────────────────────────────

    private static int PfIndex(ulong line) =>
        (int)((line ^ (line >> 10)) & (SppPrefetcher.PfEntries - 1));

    private static byte PfTag(ulong line) => (byte)((line >> 10) & 0x3F);

    /// <summary>False if the line was already prefetched (redundant → drop).</summary>
    private bool PfCheckAndInsert(ulong line) {
        int idx = PfIndex(line);
        byte tag = PfTag(line);
        if (_pf[idx].Valid && _pf[idx].Tag == tag) return false;
        _pf[idx] = new PfEntry { Valid = true, Tag = tag, Counted = false, };
        return true;
    }

    private void PfDemandCheck(ulong line) {
        int idx = PfIndex(line);
        if (!_pf[idx].Valid || _pf[idx].Tag != PfTag(line) || _pf[idx].Counted) return;
        _pf[idx].Counted = true;
        if (++_cUseful >= SppPrefetcher.AccuracyCounterMax) {
            _cTotal >>= 1;
            _cUseful >>= 1;
        }
    }

    // ── Global History Register (8-entry, page-boundary learning) ─────────────

    private void GhrInsert(int sig, int confidence, int lastOffset, int delta) {
        var victim = 0;
        for (var i = 0; i < SppPrefetcher.GhrEntries; i++) {
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

    /// <summary>
    ///     First touch of an untracked page: find a crossing prediction whose landing
    ///     offset matches, and extend its signature with its delta to seed the page.
    ///     Returns signature 0 (no history) when nothing matches.
    /// </summary>
    private int GhrBootstrap(int offset) {
        int linesPerPage = _offsetMask + 1;
        int best = -1;
        for (var i = 0; i < SppPrefetcher.GhrEntries; i++) {
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

    private struct PfEntry {
        public bool Valid;
        public byte Tag;
        public bool Counted;
    }

    private struct GhrEntry {
        public bool Valid;
        public int Sig;
        public int Confidence;
        public int LastOffset;
        public int Delta;
        public long Age;
    }
}