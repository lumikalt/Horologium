#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Multi-Lookahead Offset Prefetcher (MLOP) (Shakerinava, Bakhshalipour, Lotfi-Kamran,
///     Sarbazi-Azad — DPC-3, 2019). An offset prefetcher that generalizes
///     <see cref="BopPrefetcher" />: instead of committing to one "best" offset, it scores
///     candidate offsets independently at 16 prediction <em>lookahead levels</em> and keeps
///     one winning offset per level, trading BOP's single-offset timeliness bias for both
///     higher miss coverage and timeliness.
///     <para>
///         <strong>Access Map Table (AMT):</strong> a 256-entry direct-mapped table, keyed by
///         a 64-line-aligned region (so its spatial bit-vector fits a <see cref="ulong" />,
///         mirroring <see cref="SmsPrefetcher" />'s region pattern). Each entry holds the
///         cumulative access bit-vector for its region plus the last 15 accessed positions,
///         in order. <em>Interpretation note</em>: the paper describes the AMT keyed by "the
///         neighborhood of the address"; this treats the neighborhood as a region-aligned,
///         not address-centered, window — the simpler reading and consistent with the
///         codebase's other region-based prefetchers.
///     </para>
///     <para>
///         <strong>Scoring:</strong> on each eligible access at region-relative position P,
///         for lookahead level L (1..16) the entry's bit-vector has its most recent L−1
///         recorded positions cleared, then every candidate offset d is tested: if bit
///         (P − d) is set in that trimmed vector, d's score at level L is incremented. This
///         is exactly the paper's construction — level 1 excludes nothing (any prior access
///         qualifies), level 16 excludes the 15 most recent (the qualifying access must be
///         further back), so scores at low levels favor timeliness-agnostic coverage and
///         scores at high levels favor only offsets with real lead time. Candidate offsets
///         are 1..63 (positive-only, mirroring this codebase's <see cref="BopPrefetcher" />
///         simplification — the paper's SP/BOP ancestors also test negative offsets, but nothing
///         beyond 63 could ever score here since a 64-line region caps the useful range).
///     </para>
///     <para>
///         <strong>Evaluation period:</strong> every 500 eligible accesses (the paper's
///         empirically-chosen value), the highest-scoring offset per lookahead level becomes
///         that level's selected offset for the next period, and scores reset. A level with
///         no scoring evidence keeps offset 0 (issues nothing) until it wins one.
///     </para>
///     <para>
///         <strong>Issuance:</strong> on each eligible access, prefetch requests are emitted
///         for each level's selected offset, level 1 first (soonest-needed) through level 16
///         last (most lead time), deduplicated and clamped to a page boundary — the
///         page-boundary clamp is not stated in the paper for MLOP specifically, but is
///         imported as a physical-addressing convention from <see cref="BopPrefetcher" />.
///     </para>
///     <para>
///         Eligibility and the "prefetched hit" bit reuse <see cref="BopPrefetcher" />'s
///         approach: <see cref="IPrefetcher" /> carries no native prefetch bit, so this
///         tracks its own (256-entry direct-mapped) to recognize a first demand touch of a
///         line it prefetched, keeping the trigger stream alive once a pattern is covered.
///     </para>
/// </summary>
public sealed class MlopPrefetcher : IPrefetcher {
    private const int MaxLookahead = 16;
    private const int HistoryDepth = MlopPrefetcher.MaxLookahead - 1; // 15
    private const int RegionShift = 6; // 64 lines/region — bit-vector fits a ulong
    private const int BlocksPerRegion = 1 << MlopPrefetcher.RegionShift;

    private const int TableEntries = 256; // AMT and prefetched-bit table sizing
    private const int IndexMask = MlopPrefetcher.TableEntries - 1;
    private const int TagMask = 0xFFF; // 12-bit tags, mirrors BopPrefetcher

    private readonly int[] _bestOffset = new int[MlopPrefetcher.MaxLookahead]; // 0 = none learned yet
    private readonly ulong[] _bits = new ulong[MlopPrefetcher.TableEntries];
    private readonly int _blockShift;
    private readonly int _evalPeriod;
    private readonly int[,] _history = new int[MlopPrefetcher.TableEntries, MlopPrefetcher.HistoryDepth];
    private readonly int[] _histLen = new int[MlopPrefetcher.TableEntries];
    private readonly int _linesPerPageShift;
    private readonly int[] _offsets; // 1..63
    private readonly int[] _pfBit = new int[MlopPrefetcher.TableEntries];
    private readonly int[,] _scores; // [level, offsetIndex]
    private readonly long[] _tag = new long[MlopPrefetcher.TableEntries]; // -1 = invalid

    private int _evalCounter;

    public MlopPrefetcher(int blockBytes = 32, int pageBytes = 4096, int evalPeriod = 500) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes) || pageBytes <= blockBytes)
            throw new ArgumentException("pageBytes must be a power of 2 greater than blockBytes.", nameof(pageBytes));
        if (evalPeriod < 1) throw new ArgumentOutOfRangeException(nameof(evalPeriod), "evalPeriod must be ≥ 1.");

        _blockShift = BitOperations.Log2((uint)blockBytes);
        _linesPerPageShift = BitOperations.Log2((uint)pageBytes) - _blockShift;
        _evalPeriod = evalPeriod;

        _offsets = new int[MlopPrefetcher.BlocksPerRegion - 1];
        for (var i = 0; i < _offsets.Length; i++) _offsets[i] = i + 1;
        _scores = new int[MlopPrefetcher.MaxLookahead, _offsets.Length];

        Array.Fill(_tag, -1L);
        Array.Fill(_pfBit, -1);
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong line = address >> _blockShift;

        bool prefetchedHit = wasHit && PfBitTestAndClear(line);
        if (!wasHit) PfBitTestAndClear(line); // missed → any stale prefetch bit is dead
        bool eligible = !wasHit || prefetchedHit;
        if (!eligible) return 0;

        ulong region = line >> MlopPrefetcher.RegionShift;
        var pos = (int)(line & (MlopPrefetcher.BlocksPerRegion - 1));

        int idx = MlopPrefetcher.HashIndex(region);
        long tag = MlopPrefetcher.HashTag(region);
        if (_tag[idx] != tag) {
            _tag[idx] = tag;
            _bits[idx] = 0;
            _histLen[idx] = 0;
        }

        Train(idx, pos);

        _bits[idx] |= 1UL << pos;
        PushHistory(idx, pos);

        _evalCounter++;
        if (_evalCounter >= _evalPeriod) EndEvaluationPeriod();

        return IssuePrefetches(line, targets);
    }

    /// <summary>Best offset currently selected for 1-indexed lookahead level; 0 = none learned yet.</summary>
    public int BestOffsetForLookahead(int level) {
        if (level < 1 || level > MlopPrefetcher.MaxLookahead)
            throw new ArgumentOutOfRangeException(nameof(level));
        return _bestOffset[level - 1];
    }

    // ── Scoring ────────────────────────────────────────────────────────────────

    private void Train(int idx, int pos) {
        ulong eff = _bits[idx];
        for (var lvl = 0; lvl < MlopPrefetcher.MaxLookahead; lvl++) {
            if (lvl > 0) {
                int histIdx = lvl - 1; // exclude one more recent access per level step
                if (histIdx < _histLen[idx]) eff &= ~(1UL << _history[idx, histIdx]);
            }

            for (var oi = 0; oi < _offsets.Length; oi++) {
                int src = pos - _offsets[oi];
                if (src < 0) continue;
                if ((eff & (1UL << src)) != 0) _scores[lvl, oi]++;
            }
        }
    }

    private void PushHistory(int idx, int pos) {
        int len = _histLen[idx];
        int shiftFrom = Math.Min(len, MlopPrefetcher.HistoryDepth - 1);
        for (int k = shiftFrom; k > 0; k--) _history[idx, k] = _history[idx, k - 1];
        _history[idx, 0] = pos;
        _histLen[idx] = Math.Min(len + 1, MlopPrefetcher.HistoryDepth);
    }

    private void EndEvaluationPeriod() {
        for (var lvl = 0; lvl < MlopPrefetcher.MaxLookahead; lvl++) {
            var bestScore = 0;
            var bestOffset = 0;
            for (var oi = 0; oi < _offsets.Length; oi++)
                if (_scores[lvl, oi] > bestScore) {
                    bestScore = _scores[lvl, oi];
                    bestOffset = _offsets[oi];
                }

            _bestOffset[lvl] = bestOffset;
        }

        Array.Clear(_scores);
        _evalCounter = 0;
    }

    // ── Issuance ───────────────────────────────────────────────────────────────

    private int IssuePrefetches(ulong line, Span<ulong> targets) {
        var count = 0;
        for (var lvl = 0; lvl < MlopPrefetcher.MaxLookahead && count < targets.Length; lvl++) {
            int d = _bestOffset[lvl];
            if (d == 0) continue;

            ulong targetLine = line + (ulong)d;
            if (!SamePage(line, targetLine)) continue;

            ulong targetAddr = targetLine << _blockShift;
            var dup = false;
            for (var k = 0; k < count; k++)
                if (targets[k] == targetAddr) {
                    dup = true;
                    break;
                }

            if (dup) continue;

            targets[count] = targetAddr;
            PfBitSet(targetLine);
            count++;
        }

        return count;
    }

    // ── Prefetched-bit table (paper's hash: index = low 8 bits XOR next 8) ──────

    private static int HashIndex(ulong key) => (int)((key ^ (key >> 8)) & MlopPrefetcher.IndexMask);

    private static int HashTag(ulong key) => (int)((key >> 8) & MlopPrefetcher.TagMask);

    private void PfBitSet(ulong line) => _pfBit[MlopPrefetcher.HashIndex(line)] = (int)MlopPrefetcher.HashTag(line);

    private bool PfBitTestAndClear(ulong line) {
        int idx = MlopPrefetcher.HashIndex(line);
        if (_pfBit[idx] != (int)MlopPrefetcher.HashTag(line)) return false;
        _pfBit[idx] = -1;
        return true;
    }

    private bool SamePage(ulong lineA, ulong lineB) => lineA >> _linesPerPageShift == lineB >> _linesPerPageShift;
}
