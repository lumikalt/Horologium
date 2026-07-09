using System.Numerics;

namespace Orrery.Cache;

/// <summary>
/// Spatial Memory Streaming (SMS) prefetcher (Somogyi et al., ISCA 2006).
/// <para>
/// SMS learns spatial access patterns over fixed 2 KB address regions and predicts
/// which cache blocks will be accessed, indexed by the PC and block offset of the
/// <em>trigger</em> (first) access to each region.
/// </para>
/// <para>
/// <strong>Active Generation Table (AGT):</strong> two fully-associative FIFO tables.
/// The <em>filter table</em> (32 entries) holds single-access generations; on the
/// second distinct block access the entry is promoted to the <em>accumulation table</em>
/// (64 entries), which accumulates a bit-vector spatial pattern.  On AGT capacity
/// pressure the oldest accumulation entry is retired into the PHT; evicted filter
/// entries (single-access generations) are silently discarded.
/// </para>
/// <para>
/// <strong>Pattern History Table (PHT):</strong> 16 K entries, 16-way set-associative,
/// LRU replacement, indexed by a Knuth hash of the trigger PC and block offset.
/// </para>
/// <para>
/// Because <see cref="IPrefetcher"/> receives no eviction callbacks, generation
/// termination relies solely on AGT capacity pressure, which the paper explicitly
/// supports: "if either table is full when a new entry must be allocated, a victim
/// entry is selected and the corresponding generation is terminated."
/// </para>
/// <para>
/// With a 2 KB region and 32 B blocks there are exactly 64 blocks per region,
/// fitting the spatial pattern in a <see cref="ulong"/> bitmask.
/// <c>blockBytes</c> must therefore be ≥ 32.
/// </para>
/// </summary>
public sealed class SmsPrefetcher : IPrefetcher {
    // ── Geometry ──────────────────────────────────────────────────────────────
    private const int RegionBytes = 2048;
    private const int MaxBlocksPerRegion = 64; // pattern must fit in ulong

    private readonly int _blockBits;
    private readonly int _blocksPerRegion;
    private readonly ulong _regionMask; // ~(RegionBytes - 1)

    // ── Filter Table: 32-entry FA FIFO ────────────────────────────────────────
    private const int FilterSize = 32;

    private struct FilterEntry {
        public ulong RegionBase;
        public ulong TriggerPc;
        public int TriggerOffset;
        public int Age;
        public bool Valid;
    }

    private readonly FilterEntry[] _filter = new FilterEntry[SmsPrefetcher.FilterSize];

    // ── Accumulation Table: 64-entry FA FIFO ─────────────────────────────────
    private const int AccumSize = 64;

    private struct AccumEntry {
        public ulong RegionBase;
        public ulong TriggerPc;
        public int TriggerOffset;
        public ulong Pattern;
        public int Age;
        public bool Valid;
    }

    private readonly AccumEntry[] _accum = new AccumEntry[SmsPrefetcher.AccumSize];

    // ── Pattern History Table: 16 K entries, 16-way SA, LRU ──────────────────
    private const int PhtTotalEntries = 16384;
    private const int PhtWays = 16;
    private const int PhtSets = SmsPrefetcher.PhtTotalEntries / SmsPrefetcher.PhtWays; // 1024

    private struct PhtEntry {
        public ulong Tag;
        public ulong Pattern;
        public int LruAge;
        public bool Valid;
    }

    private readonly PhtEntry[,] _pht = new PhtEntry[SmsPrefetcher.PhtSets, SmsPrefetcher.PhtWays];

    // ── Monotone counters ─────────────────────────────────────────────────────
    private int _age;
    private int _phtAge;

    public SmsPrefetcher(int blockBytes = 32) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        int blocksPerRegion = SmsPrefetcher.RegionBytes / blockBytes;
        if (blocksPerRegion > SmsPrefetcher.MaxBlocksPerRegion)
            throw new ArgumentException(
                $"blockBytes must be ≥ {SmsPrefetcher.RegionBytes / SmsPrefetcher.MaxBlocksPerRegion} so the spatial pattern fits in a 64-bit mask.",
                nameof(blockBytes)
            );
        _blockBits = BitOperations.Log2((uint)blockBytes);
        _blocksPerRegion = blocksPerRegion;
        _regionMask = ~(ulong)(SmsPrefetcher.RegionBytes - 1);
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

        // 3. Trigger access: predict from PHT, then allocate filter entry
        int count = PhtPredict(pc, blockOffset, regionBase, targets);
        AllocFilter(regionBase, pc, blockOffset);
        return count;
    }

    // ── AGT search ────────────────────────────────────────────────────────────

    private int FindAccum(ulong regionBase) {
        for (var i = 0; i < SmsPrefetcher.AccumSize; i++)
            if (_accum[i].Valid && _accum[i].RegionBase == regionBase)
                return i;
        return -1;
    }

    private int FindFilter(ulong regionBase) {
        for (var i = 0; i < SmsPrefetcher.FilterSize; i++)
            if (_filter[i].Valid && _filter[i].RegionBase == regionBase)
                return i;
        return -1;
    }

    // ── Filter allocation (FIFO; evicted single-access entries are discarded) ──

    private void AllocFilter(ulong regionBase, ulong triggerPc, int triggerOffset) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < SmsPrefetcher.FilterSize; i++) {
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

    // ── Accumulation allocation (FIFO; evicted entries are trained into PHT) ──

    private void AllocAccum(ulong regionBase, ulong triggerPc, int triggerOffset, ulong initPattern) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < SmsPrefetcher.AccumSize; i++) {
            if (!_accum[i].Valid) {
                slot = i;
                break;
            }

            if (_accum[i].Age < minAge) {
                minAge = _accum[i].Age;
                slot = i;
            }
        }

        if (_accum[slot].Valid) PhtWrite(_accum[slot].TriggerPc, _accum[slot].TriggerOffset, _accum[slot].Pattern);
        _accum[slot] = new AccumEntry {
            RegionBase = regionBase,
            TriggerPc = triggerPc,
            TriggerOffset = triggerOffset,
            Pattern = initPattern,
            Age = ++_age,
            Valid = true,
        };
    }

    // ── PHT ───────────────────────────────────────────────────────────────────

    private static (int set, ulong tag) PhtIndex(ulong pc, int offset) {
        ulong key = (pc >> 2) ^ ((uint)offset * 2654435761UL);
        return ((int)(key & (SmsPrefetcher.PhtSets - 1)), key >> 10);
    }

    private void PhtWrite(ulong triggerPc, int triggerOffset, ulong pattern) {
        if (pattern == 0) return;
        (int set, ulong tag) = PhtIndex(triggerPc, triggerOffset);
        for (var w = 0; w < SmsPrefetcher.PhtWays; w++)
            if (_pht[set, w].Valid && _pht[set, w].Tag == tag) {
                _pht[set, w].Pattern = pattern;
                _pht[set, w].LruAge = ++_phtAge;
                return;
            }

        int victim = FindPhtVictim(set);
        _pht[set, victim] = new PhtEntry { Tag = tag, Pattern = pattern, LruAge = ++_phtAge, Valid = true, };
    }

    private int PhtPredict(ulong pc, int triggerOffset, ulong regionBase, Span<ulong> targets) {
        (int set, ulong tag) = PhtIndex(pc, triggerOffset);
        for (var w = 0; w < SmsPrefetcher.PhtWays; w++) {
            if (!_pht[set, w].Valid || _pht[set, w].Tag != tag) continue;
            _pht[set, w].LruAge = ++_phtAge;
            ulong pattern = _pht[set, w].Pattern & ~(1UL << triggerOffset);
            ulong blockBytes = 1UL << _blockBits;
            var count = 0;
            for (var b = 0; b < _blocksPerRegion && count < targets.Length; b++)
                if (((pattern >> b) & 1) != 0)
                    targets[count++] = regionBase + (ulong)b * blockBytes;
            return count;
        }

        return 0;
    }

    private int FindPhtVictim(int set) {
        int victim = 0, minLru = int.MaxValue;
        for (var w = 0; w < SmsPrefetcher.PhtWays; w++) {
            if (!_pht[set, w].Valid) return w;
            if (_pht[set, w].LruAge < minLru) {
                minLru = _pht[set, w].LruAge;
                victim = w;
            }
        }

        return victim;
    }
}