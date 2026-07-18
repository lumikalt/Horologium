using System.Numerics;

namespace Orrery.Cache;

/// <summary>
///     Best-Offset prefetcher (Michaud, HPCA 2016) — the DPC-2 winner. A degree-one offset
///     prefetcher: on each eligible access to line X it prefetches X + D, where the offset D
///     is re-selected periodically by a scoring tournament that accounts for prefetch
///     <em>timeliness</em>, not just accuracy.
///     <para>
///         A Recent Requests (RR) table (256-entry direct-mapped, 12-bit tags) records the
///         base address of prefetch requests that have <em>completed</em>: when the prefetch
///         of line Y finishes, Y − D is inserted. Learning tests one candidate offset d per
///         eligible access, round-robin over a fixed 52-entry list (all offsets 1–256 whose
///         prime factors are ≤ 5): if X − d hits in the RR table, a prefetch with offset d
///         would have been issued early enough to complete before this access, so d's score
///         is incremented. A learning phase ends when a score reaches SCOREMAX (31) or after
///         ROUNDMAX (100) rounds; the highest-scoring offset becomes the new D.
///     </para>
///     <para>
///         Throttling: when the winning score is ≤ BADSCORE (1), offset prefetching is not
///         paying off and prefetch is turned off. Learning never stops — while off, demand
///         fills are inserted into the RR table directly (D = 0) so a usable offset can be
///         rediscovered and prefetch re-enabled.
///     </para>
///     <para>
///         Adaptation to this simulator: the paper triggers on L2 misses and prefetched hits
///         (a prefetch bit per L2 line). <see cref="IPrefetcher" /> carries no prefetch bit,
///         so the prefetcher tracks its own — a 256-entry direct-mapped table of issued, not
///         yet demanded, prefetch lines. Prefetch completion time is approximated by a fixed
///         <c>latency</c> in ticks (one tick per <see cref="OnAccess" /> call, as in
///         <see cref="BertiPrefetcher" />), defaulting to 10. Prefetches never cross a
///         <c>pageBytes</c> boundary, per the paper.
///     </para>
/// </summary>
public sealed class BopPrefetcher : IPrefetcher {
    /// <summary>
    ///     All offsets in 1..256 whose prime factorization contains only 2, 3, and 5.
    ///     At construction the list is pruned to offsets smaller than the page size in
    ///     lines — the paper notes there is no point considering larger ones, as the
    ///     prefetcher never crosses a page boundary.
    /// </summary>
    private static readonly int[] AllOffsets = [
        1, 2, 3, 4, 5, 6, 8, 9, 10, 12, 15, 16, 18, 20, 24, 25, 27, 30, 32, 36, 40, 45,
        48, 50, 54, 60, 64, 72, 75, 80, 81, 90, 96, 100, 108, 120, 125, 128, 135, 144,
        150, 160, 162, 180, 192, 200, 216, 225, 240, 243, 250, 256,
    ];

    private const int RrEntries = 256;
    private const int RrIndexMask = BopPrefetcher.RrEntries - 1;
    private const int RrTagMask = 0xFFF; // 12-bit tags
    private const int ScoreMax = 31;
    private const int RoundMax = 100;
    private const int BadScore = 1;
    private const int FillQueueCap = 64; // pending completions; oldest dropped when full

    private readonly int _latency;
    private readonly int _lineShift;
    private readonly int _linesPerPageShift;
    private readonly int[] _offsets; // AllOffsets pruned to < lines-per-page
    private readonly int[] _scores;

    // RR table and the internal prefetch-bit table: direct-mapped, tag or -1 when invalid.
    private readonly int[] _rr = new int[BopPrefetcher.RrEntries];
    private readonly int[] _pfBit = new int[BopPrefetcher.RrEntries];

    // Pending completions: prefetched (or, while off, demand-fetched) lines that will be
    // inserted into the RR table once _tick reaches Ready.
    private readonly Queue<(ulong Line, int Ready, bool DemandFill)> _pending = new();

    private int _testIndex; // next offset-list slot to test
    private int _round;
    private int _bestScore;
    private int _bestOffset = 1;

    private int _d = 1; // current prefetch offset
    private bool _prefetchOn = true;
    private int _tick;

    public BopPrefetcher(int blockBytes = 32, int latency = 10, int pageBytes = 4096) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes) || pageBytes <= blockBytes)
            throw new ArgumentException("pageBytes must be a power of 2 greater than blockBytes.", nameof(pageBytes));
        if (latency < 1) throw new ArgumentOutOfRangeException(nameof(latency), "latency must be ≥ 1.");
        _lineShift = BitOperations.Log2((uint)blockBytes);
        _linesPerPageShift = BitOperations.Log2((uint)pageBytes) - _lineShift;
        _latency = latency;
        int linesPerPage = 1 << _linesPerPageShift;
        _offsets = Array.FindAll(BopPrefetcher.AllOffsets, d => d < linesPerPage);
        _scores = new int[_offsets.Length];
        Array.Fill(_rr, -1);
        Array.Fill(_pfBit, -1);
    }

    /// <summary>Current prefetch offset D, in lines (exposed for tests/diagnostics).</summary>
    public int CurrentOffset => _d;

    /// <summary>False while throttled off by a failed learning phase.</summary>
    public bool PrefetchEnabled => _prefetchOn;

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        DrainCompletions();

        ulong line = address >> _lineShift;

        // Eligible accesses are demand misses and prefetched hits (first demand touch of a
        // line this prefetcher fetched) — the same trigger the paper uses at the L2.
        bool prefetchedHit = wasHit && PfBitTestAndClear(line);
        if (!wasHit) PfBitTestAndClear(line); // missed → any stale prefetch bit is dead
        bool eligible = !wasHit || prefetchedHit;
        if (!eligible) {
            _tick++;
            return 0;
        }

        LearnStep(line);

        var count = 0;
        if (_prefetchOn) {
            ulong target = line + (ulong)_d;
            if (SamePage(line, target)) {
                if (!targets.IsEmpty) {
                    targets[0] = target << _lineShift;
                    count = 1;
                }

                PfBitSet(target);
                EnqueueCompletion(target, false);
            }
        }
        else if (!wasHit) {
            // Prefetch off: record demand fills in the RR table (D = 0) so learning can
            // score offsets against the raw access stream and turn prefetch back on.
            EnqueueCompletion(line, true);
        }

        _tick++;
        return count;
    }

    // ── Best-offset learning ──────────────────────────────────────────────────

    private void LearnStep(ulong line) {
        int d = _offsets[_testIndex];
        if ((long)line - d >= 0 && RrHit(line - (ulong)d)) {
            int s = ++_scores[_testIndex];
            if (s > _bestScore) {
                _bestScore = s;
                _bestOffset = d;
            }
        }

        _testIndex++;
        if (_testIndex == _offsets.Length) {
            _testIndex = 0;
            _round++;
        }

        if (_bestScore >= BopPrefetcher.ScoreMax || _round >= BopPrefetcher.RoundMax)
            EndLearningPhase();
    }

    private void EndLearningPhase() {
        _d = _bestOffset;
        _prefetchOn = _bestScore > BopPrefetcher.BadScore;
        Array.Clear(_scores);
        _testIndex = 0;
        _round = 0;
        _bestScore = 0;
        _bestOffset = _offsets[0];
    }

    // ── Completion queue → RR table ───────────────────────────────────────────

    private void EnqueueCompletion(ulong line, bool demandFill) {
        if (_pending.Count >= BopPrefetcher.FillQueueCap) _pending.Dequeue();
        _pending.Enqueue((line, _tick + _latency, demandFill));
    }

    private void DrainCompletions() {
        while (_pending.Count > 0 && _pending.Peek().Ready <= _tick) {
            (ulong y, _, bool demandFill) = _pending.Dequeue();
            if (demandFill) {
                RrInsert(y);
            }
            else {
                // Reconstruct the base address from the *current* offset, as the hardware
                // does; if D changed mid-flight or the base falls off the page, skip.
                ulong baseLine = y - (ulong)_d;
                if ((long)y - _d >= 0 && SamePage(baseLine, y)) RrInsert(baseLine);
            }
        }
    }

    // ── RR / prefetch-bit tables (paper's hash: index = low 8 line bits XOR next 8) ──

    private static int HashIndex(ulong line) =>
        (int)((line ^ (line >> 8)) & BopPrefetcher.RrIndexMask);

    private static int Tag(ulong line) => (int)((line >> 8) & BopPrefetcher.RrTagMask);

    private void RrInsert(ulong line) => _rr[BopPrefetcher.HashIndex(line)] = BopPrefetcher.Tag(line);

    private bool RrHit(ulong line) => _rr[BopPrefetcher.HashIndex(line)] == BopPrefetcher.Tag(line);

    private void PfBitSet(ulong line) => _pfBit[BopPrefetcher.HashIndex(line)] = BopPrefetcher.Tag(line);

    private bool PfBitTestAndClear(ulong line) {
        int idx = BopPrefetcher.HashIndex(line);
        if (_pfBit[idx] != BopPrefetcher.Tag(line)) return false;
        _pfBit[idx] = -1;
        return true;
    }

    private bool SamePage(ulong lineA, ulong lineB) =>
        lineA >> _linesPerPageShift == lineB >> _linesPerPageShift;
}
