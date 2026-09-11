#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Bingo spatial data prefetcher (Bakhshalipour, Shakerinava, Lotfi-Kamran &amp;
///     Sarbazi-Azad, HPCA 2019). Extends <see cref="SmsPrefetcher" />'s Active Generation
///     Table (AGT) — same 32-entry filter / 64-entry accumulation FIFO tables, same 2 KB
///     region, same footprint-bitmask accumulation — with a TAGE-like <em>dual-event</em>
///     history table in place of SMS's single-event Pattern History Table.
///     <para>
///         Each footprint is associated with a <em>long</em> event (trigger PC + the exact
///         trigger address — "the same address was touched") and, implicitly, a <em>short</em>
///         event (trigger PC + trigger block offset — "the same instruction touched some
///         block of some page"). The short event is always derivable from the long one (the
///         offset is a function of the address), so the table is indexed <em>once</em>, using
///         only the short event, and looked up up to twice against the same set: first for a
///         precise match against the full long event (high accuracy, low match probability),
///         and — only on a miss — a fallback match against just the short event, generalizing
///         the footprint across every page ever touched at that PC+offset (lower accuracy,
///         much higher match probability). This is the paper's central contribution (§IV,
///         Fig. 5): one physical table instead of N cascaded TAGE-style tables, with no
///         redundant storage, because a footprint is only ever written once (under its long
///         event) yet remains reachable via the short event too.
///     </para>
///     <para>
///         <strong>Modeling simplification:</strong> the paper's hardware tags each entry with
///         the literal bits of PC+Address and indexes with a hash of just the PC+Offset bits.
///         This class instead stores <c>Pc</c>/<c>RegionBase</c>/<c>Offset</c> as literal
///         fields rather than a bit-packed tag — behaviorally identical (the index is still
///         computed from <c>(Pc, Offset)</c> alone, so a write and a lookup keyed by the same
///         short event always land in the same set, regardless of <c>RegionBase</c>), but
///         avoids a tag-decomposition trick that only matters for real silicon bit width. Same
///         category of simplification as <see cref="SmsPrefetcher" />'s own hashed (not
///         bit-packed) PHT tag.
///     </para>
///     <para>
///         <strong>No <see cref="SetAssociativeCache.OnEviction" /> wiring.</strong> Real
///         per-line eviction feedback exists in this codebase (see <see cref="PpfPrefetcher" />
///         ), but Bingo — like both of its structural ancestors <see cref="SmsPrefetcher" />
///         and <see cref="StemsPrefetcher" /> — relies solely on AGT capacity pressure to
///         terminate a page generation, matching the paper's own supported fallback
///         ("if either table is full when a new entry must be allocated, a victim entry is
///         selected and the corresponding generation is terminated"). Bingo's own contribution
///         is the history-table lookup scheme, not the AGT/termination model it inherits
///         unchanged from SMS, so wiring real eviction feedback here would be an unrelated
///         divergence from that shared model.
///     </para>
///     <para>
///         <strong>Multiple fallback matches:</strong> when a short-event lookup matches more
///         than one entry (several different pages sharing the same trigger PC+offset), the
///         paper resolves conflicting footprints by majority: "a cache block is prefetched if
///         it is present in the footprint of at least 20% of matching entries" (§IV). This
///         class applies that literal threshold.
///     </para>
/// </summary>
public sealed class BingoPrefetcher : IPrefetcher {
    // ── Geometry (identical to SmsPrefetcher) ────────────────────────────────────
    private const int RegionBytes = 2048;
    private const int MaxBlocksPerRegion = 64; // pattern must fit in ulong

    // ── Filter Table: 32-entry FA FIFO ────────────────────────────────────────
    private const int FilterSize = 32;

    // ── Accumulation Table: 64-entry FA FIFO ─────────────────────────────────
    private const int AccumSize = 64;

    // ── History Table: 16 K entries, 16-way SA, LRU ──────────────────────────
    private const int HistTotalEntries = 16384;
    private const int HistWays = 16;
    private const int HistSets = BingoPrefetcher.HistTotalEntries / BingoPrefetcher.HistWays; // 1024

    // Paper §IV: a block is prefetched if present in the footprint of at least 1/5 (20%)
    // of entries matched by the short-event (PC+Offset-only) fallback lookup. Expressed as
    // an exact integer ratio (count*5 >= matchCount) rather than a float comparison, to
    // avoid boundary fragility at the threshold.
    private const int FallbackAggregationThresholdDenominator = 5;

    private readonly AccumEntry[] _accum = new AccumEntry[BingoPrefetcher.AccumSize];

    private readonly int _blockBits;
    private readonly int _blocksPerRegion;

    private readonly FilterEntry[] _filter = new FilterEntry[BingoPrefetcher.FilterSize];

    private readonly HistEntry[,] _hist = new HistEntry[BingoPrefetcher.HistSets, BingoPrefetcher.HistWays];
    private readonly ulong _regionMask; // ~(RegionBytes - 1)

    // ── Monotone counters ─────────────────────────────────────────────────────
    private int _age;
    private int _histAge;

    public BingoPrefetcher(int blockBytes = 32) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        int blocksPerRegion = BingoPrefetcher.RegionBytes / blockBytes;
        if (blocksPerRegion > BingoPrefetcher.MaxBlocksPerRegion)
            throw new ArgumentException(
                $"blockBytes must be ≥ {BingoPrefetcher.RegionBytes / BingoPrefetcher.MaxBlocksPerRegion} so the spatial pattern fits in a 64-bit mask.",
                nameof(blockBytes)
            );
        _blockBits = BitOperations.Log2((uint)blockBytes);
        _blocksPerRegion = blocksPerRegion;
        _regionMask = ~(ulong)(BingoPrefetcher.RegionBytes - 1);
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong regionBase = address & _regionMask;
        var blockOffset = (int)((address - regionBase) >> _blockBits);

        // 1. Accumulation table: ongoing multi-access generation
        int accumIdx = FindAccum(regionBase);
        if (accumIdx >= 0) {
            _accum[accumIdx].Pattern |= 1UL << blockOffset;
            _accum[accumIdx].Age = ++_age;
            return 0;
        }

        // 2. Filter table: single-access generation in progress
        int filterIdx = FindFilter(regionBase);
        if (filterIdx >= 0) {
            if (_filter[filterIdx].TriggerOffset == blockOffset) {
                // Same block as trigger; still single-access generation
                _filter[filterIdx].Age = ++_age;
                return 0;
            }

            // Second distinct block → promote to accumulation table
            ulong tPc = _filter[filterIdx].TriggerPc;
            int tOff = _filter[filterIdx].TriggerOffset;
            _filter[filterIdx] = default(FilterEntry);
            AllocAccum(regionBase, tPc, tOff, (1UL << tOff) | (1UL << blockOffset));
            return 0;
        }

        // 3. Trigger access: predict from the history table, then allocate filter entry
        int count = HistPredict(pc, blockOffset, regionBase, targets);
        AllocFilter(regionBase, pc, blockOffset);
        return count;
    }

    // ── AGT search ────────────────────────────────────────────────────────────

    private int FindAccum(ulong regionBase) {
        for (var i = 0; i < BingoPrefetcher.AccumSize; i++)
            if (_accum[i].Valid && _accum[i].RegionBase == regionBase)
                return i;
        return -1;
    }

    private int FindFilter(ulong regionBase) {
        for (var i = 0; i < BingoPrefetcher.FilterSize; i++)
            if (_filter[i].Valid && _filter[i].RegionBase == regionBase)
                return i;
        return -1;
    }

    // ── Filter allocation (FIFO; evicted single-access entries are discarded) ──

    private void AllocFilter(ulong regionBase, ulong triggerPc, int triggerOffset) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < BingoPrefetcher.FilterSize; i++) {
            if (!_filter[i].Valid) {
                slot = i;
                break;
            }

            if (_filter[i].Age < minAge) {
                minAge = _filter[i].Age;
                slot = i;
            }
        }

        _filter[slot] = new FilterEntry {
            RegionBase = regionBase,
            TriggerPc = triggerPc,
            TriggerOffset = triggerOffset,
            Age = ++_age,
            Valid = true,
        };
    }

    // ── Accumulation allocation (FIFO; evicted entries are retired into the history table) ──

    private void AllocAccum(ulong regionBase, ulong triggerPc, int triggerOffset, ulong initPattern) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < BingoPrefetcher.AccumSize; i++) {
            if (!_accum[i].Valid) {
                slot = i;
                break;
            }

            if (_accum[i].Age < minAge) {
                minAge = _accum[i].Age;
                slot = i;
            }
        }

        if (_accum[slot].Valid)
            HistWrite(
                _accum[slot].TriggerPc, _accum[slot].RegionBase, _accum[slot].TriggerOffset, _accum[slot].Pattern
            );
        _accum[slot] = new AccumEntry {
            RegionBase = regionBase,
            TriggerPc = triggerPc,
            TriggerOffset = triggerOffset,
            Pattern = initPattern,
            Age = ++_age,
            Valid = true,
        };
    }

    // ── History table ─────────────────────────────────────────────────────────

    // Indexed by the short event (Pc, Offset) alone — identical formula to
    // SmsPrefetcher.PhtIndex — so a write and a lookup sharing the same short event
    // always land in the same set, regardless of RegionBase.
    private static int HistSet(ulong pc, int offset) {
        ulong key = (pc >> 2) ^ ((uint)offset * 2654435761UL);
        return (int)(key & (BingoPrefetcher.HistSets - 1));
    }

    private void HistWrite(ulong triggerPc, ulong regionBase, int triggerOffset, ulong pattern) {
        if (pattern == 0) return;
        int set = HistSet(triggerPc, triggerOffset);

        // Exact re-write: the same page was already resident once before.
        for (var w = 0; w < BingoPrefetcher.HistWays; w++)
            if (_hist[set, w].Valid && _hist[set, w].Pc == triggerPc && _hist[set, w].RegionBase == regionBase
             && _hist[set, w].Offset == triggerOffset) {
                _hist[set, w].Pattern = pattern;
                _hist[set, w].LruAge = ++_histAge;
                return;
            }

        int victim = FindHistVictim(set);
        _hist[set, victim] = new HistEntry {
            Pc = triggerPc, RegionBase = regionBase, Offset = triggerOffset, Pattern = pattern, LruAge = ++_histAge,
            Valid = true,
        };
    }

    private int HistPredict(ulong pc, int triggerOffset, ulong regionBase, Span<ulong> targets) {
        int set = HistSet(pc, triggerOffset);

        // Pass 1: precise match against the full long event (Pc, RegionBase, Offset).
        for (var w = 0; w < BingoPrefetcher.HistWays; w++) {
            ref HistEntry e = ref _hist[set, w];
            if (!e.Valid || e.Pc != pc || e.RegionBase != regionBase || e.Offset != triggerOffset) continue;
            e.LruAge = ++_histAge;
            return EmitPattern(e.Pattern & ~(1UL << triggerOffset), regionBase, targets);
        }

        // Pass 2: fallback match against just the short event (Pc, Offset), aggregated
        // across every matching entry via the paper's 20% threshold.
        Span<int> bitCounts = stackalloc int[BingoPrefetcher.MaxBlocksPerRegion];
        bitCounts.Clear();
        var matchCount = 0;
        for (var w = 0; w < BingoPrefetcher.HistWays; w++) {
            ref HistEntry e = ref _hist[set, w];
            if (!e.Valid || e.Pc != pc || e.Offset != triggerOffset) continue;
            matchCount++;
            e.LruAge = ++_histAge;
            for (var b = 0; b < _blocksPerRegion; b++)
                if (((e.Pattern >> b) & 1) != 0)
                    bitCounts[b]++;
        }

        if (matchCount == 0) return 0;

        ulong aggregate = 0;
        for (var b = 0; b < _blocksPerRegion; b++)
            if (bitCounts[b] * BingoPrefetcher.FallbackAggregationThresholdDenominator >= matchCount)
                aggregate |= 1UL << b;

        return EmitPattern(aggregate & ~(1UL << triggerOffset), regionBase, targets);
    }

    private int EmitPattern(ulong pattern, ulong regionBase, Span<ulong> targets) {
        ulong blockBytes = 1UL << _blockBits;
        var count = 0;
        for (var b = 0; b < _blocksPerRegion && count < targets.Length; b++)
            if (((pattern >> b) & 1) != 0)
                targets[count++] = regionBase + (ulong)b * blockBytes;
        return count;
    }

    private int FindHistVictim(int set) {
        int victim = 0, minLru = int.MaxValue;
        for (var w = 0; w < BingoPrefetcher.HistWays; w++) {
            if (!_hist[set, w].Valid) return w;
            if (_hist[set, w].LruAge < minLru) {
                minLru = _hist[set, w].LruAge;
                victim = w;
            }
        }

        return victim;
    }

    private struct FilterEntry {
        public ulong RegionBase;
        public ulong TriggerPc;
        public int TriggerOffset;
        public int Age;
        public bool Valid;
    }

    private struct AccumEntry {
        public ulong RegionBase;
        public ulong TriggerPc;
        public int TriggerOffset;
        public ulong Pattern;
        public int Age;
        public bool Valid;
    }

    private struct HistEntry {
        public ulong Pc;
        public ulong RegionBase;
        public int Offset;
        public ulong Pattern;
        public int LruAge;
        public bool Valid;
    }
}