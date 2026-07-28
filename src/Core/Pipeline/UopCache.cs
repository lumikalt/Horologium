#region

using Mechanism;
using Orrery.Cache;

#endregion

namespace Pipeline;

/// <summary>
///     Micro-Operation Cache (Solomon, Mendelson, Orenstien, Almog &amp; Ronen, ISLPED 2001):
///     an n-way set-associative cache of already-decoded <see cref="ITooth" /> basic blocks,
///     tagged by the starting PC of the block. A hit lets the front end skip re-decoding a
///     block it has fetched before — the "loop buffer" behavior the paper's stream-mode
///     describes.
///     <para>
///         Basic blocks are entered only via their first instruction (paper §2.4): a lookup at
///         any PC other than a stored line's start is a miss, even if that PC falls inside an
///         already-cached line. A line ends at a branch (<see cref="ToothClass.Branch" /> or
///         <see cref="ToothClass.ConditionalBranch" />) or at <c>lineCapacity</c> uops, whichever
///         comes first — this codebase has no micro-sequencer/complex-instruction case, so only
///         two of the paper's three block-ending rules apply.
///     </para>
///     <para>
///         <b>This is a power paper, not a performance one</b> — its own experiments measure
///         instruction/line hit-rate and build/switch counts, never cycles or IPC, and its design
///         keeps the IC lookup running in parallel on a UC hit ("zero switch penalty") precisely
///         so timing does not change. In this simulator, decode already costs zero modeled cycles,
///         and RISC-V's fixed-length ISA never hits the variable-length decode-bandwidth wall the
///         paper solves for x86 — so a faithful integration is expected to show ~0 cycle-count
///         benefit. <b>Known modeling divergence</b>: unlike the paper, a hit here skips the
///         I-cache access entirely (no backing-memory read, no miss-latency charge) rather than
///         running the IC lookup in parallel — simpler to implement, but it means a workload with
///         heavy I$ pressure could show an artificial cycle-count difference with the UC enabled.
///         This is a modeling simplification to flag, not a result to report.
///     </para>
///     <para>
///         The trailing branch of a line (if any) is <em>not</em> given a cached predicted
///         target — the caller must still run a live predictor call for it, exactly as on a
///         normal decode, since the paper's UC and IC share one branch-prediction unit predicted
///         fresh on every visit. Only the decoded uops and the block's shape are cached.
///     </para>
///     <para>
///         Deferred (paper §2.7's own "design space" framing, not built here): access counters
///         (§4.3 — filter builds by a per-PC hit-count threshold before caching; a power-only
///         optimization with even less relevance without a power model).
///     </para>
/// </summary>
public sealed class UopCache {
    private readonly int _blockShift;
    private readonly UopLine[,] _lines;
    private readonly IReplacementPolicy _policy;
    private readonly int _sets;
    private readonly int _ways;

    public UopCache(int sets, int ways, int lineCapacity, int blockShift = 4, IReplacementPolicy? policy = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCapacity);
        _sets = sets;
        _ways = ways;
        _blockShift = blockShift;
        LineCapacity = lineCapacity;
        _policy = policy ?? new LruPolicy(sets, ways);
        _lines = new UopLine[sets, ways];
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < ways; w++)
            _lines[s, w] = new UopLine(lineCapacity);
    }

    public int LineCapacity { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Builds { get; private set; }

    /// <summary>
    ///     Looks up the block starting at <paramref name="pc" />. Only ever hits at a line's
    ///     recorded start PC — an address that merely falls inside an already-cached line's byte
    ///     range is a miss, per the paper's single-entry-point basic-block model.
    /// </summary>
    public bool TryLookup(ulong pc, out IReadOnlyList<ITooth> uops, out bool endsInBranch) {
        int set = SetOf(pc);
        for (var way = 0; way < _ways; way++) {
            UopLine line = _lines[set, way];
            if (!line.Valid || line.Tag != pc) continue;
            Hits++;
            _policy.RecordHit(set, way);
            uops = line.Slice();
            endsInBranch = line.EndsInBranch;
            return true;
        }

        Misses++;
        uops = [];
        endsInBranch = false;
        return false;
    }

    /// <summary>
    ///     Installs (or overwrites, if already resident) the basic block starting at
    ///     <paramref name="startPc" />. <paramref name="uops" /> must be non-empty and no longer
    ///     than <see cref="LineCapacity" />.
    /// </summary>
    public void Insert(ulong startPc, IReadOnlyList<ITooth> uops, bool endsInBranch) {
        ArgumentOutOfRangeException.ThrowIfZero(uops.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(uops.Count, LineCapacity);

        int set = SetOf(startPc);
        var way = -1;
        for (var w = 0; w < _ways; w++)
            if (_lines[set, w] is { Valid: true, } l && l.Tag == startPc) {
                way = w;
                break;
            }

        if (way < 0) way = _policy.ChooseVictim(set);

        UopLine line = _lines[set, way];
        line.Tag = startPc;
        line.Valid = true;
        line.EndsInBranch = endsInBranch;
        line.Fill(uops);
        _policy.RecordInstall(set, way);
        Builds++;
    }

    private int SetOf(ulong pc) => (int)((pc >> _blockShift) % (ulong)_sets);

    private sealed class UopLine(int capacity) {
        private readonly ITooth[] _uops = new ITooth[capacity];
        private int _count;
        public bool EndsInBranch;
        public ulong Tag;
        public bool Valid;

        public void Fill(IReadOnlyList<ITooth> uops) {
            _count = uops.Count;
            for (var i = 0; i < _count; i++) _uops[i] = uops[i];
        }

        public IReadOnlyList<ITooth> Slice() => new ArraySegment<ITooth>(_uops, 0, _count);
    }
}
