#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Spatio-Temporal Memory Streaming (STeMS) prefetcher (Somogyi et al., ISCA 2009), extending
///     <see cref="SmsPrefetcher" /> (Somogyi et al., ISCA 2006) with temporal miss-sequence
///     recording.
///     <para>
///         SMS alone predicts <em>which</em> blocks a region will touch but not <em>when</em>,
///         losing the ability to prefetch across the boundary between regions. STeMS's
///         innovation (§3.1, Fig. 3/5) is to reconstruct the total predicted miss order by
///         interleaving two recorded sequences: the <strong>temporal</strong> sequence of
///         region-trigger misses (which region is touched next), and, for each trigger, the
///         <strong>spatial</strong> sequence of subsequent misses within that region (in the
///         order they were first observed, not a bit-vector — the key structural change from
///         SMS). Both sequences record each entry's <em>delta</em>: the count of other misses
///         (from any region) interleaved between it and the previous entry of the <em>same</em>
///         sequence. Reconstruction re-derives absolute positions from these deltas via the
///         uniform recurrence <c>pos[entry] = pos[previous same-sequence entry] + delta + 1</c>,
///         merging every sequence's contribution into one ordered prediction — verified here
///         against the paper's own worked example (Fig. 3/5: observed order
///         A, A+4, B, A+2, B+6, A−1, C, D, D+1, D+2 reconstructs to
///         [A+4, B, A+2, B+6, A−1, C, D, D+1] when re-triggered from A).
///     </para>
///     <para>
///         <strong>Region Miss Order Buffer (RMOB):</strong> a circular buffer recording every
///         <em>trigger</em> miss (the first miss to a region during a generation) as
///         <c>(block address, trigger PC, trigger offset, delta)</c>, plus a map from block
///         address to its most recent RMOB slot. On a new trigger miss, STeMS looks up its
///         address in that map — a hit means this exact region has been triggered before, and
///         the RMOB entries recorded after that prior occurrence describe what typically follows
///         it. Sized 128K entries, matching the paper's evaluated configuration.
///     </para>
///     <para>
///         <strong>Active Generation Table (AGT) and Pattern Sequence Table (PST):</strong> the
///         same two-table filter (32-entry FIFO)/accumulation (64-entry FIFO) design and
///         16K-entry, 16-way PST as <see cref="SmsPrefetcher" />'s AGT/PHT, except each entry
///         stores an <em>ordered sequence</em> of <c>(offset, delta)</c> pairs — recorded once
///         per distinct block per generation, "each block can only appear once in a sequence,
///         corresponding to the order in which it was first accessed" (§4.3) — instead of a bit
///         vector. On accumulation-table retirement the sequence trains the PST, keyed exactly
///         like SMS's PHT (trigger PC, trigger offset).
///     </para>
///     <para>
///         <strong>Adaptations to this simulator</strong> (paralleling the documented
///         simplifications in <see cref="SppPrefetcher" />/<see cref="PpfPrefetcher" />): the
///         paper's decoupled streaming layer (stream queues, a streamed-value buffer, "fetch one
///         block, then continue only if consumed" throttling) is not modeled — like every other
///         prefetcher here, reconstruction runs synchronously inside <see cref="OnAccess" /> and
///         returns its full predicted sequence at once, bounded by the caller's <c>targets</c>
///         span (which plays the throttling role the paper gives the stream queues) and by a
///         256-entry reconstruction window (the paper's reconstruction-buffer size, §4.2).
///         Collision handling on that window mirrors the paper's "search at most two elements
///         forward or backward" (99% placement rate, per §4.2). The paper's further accuracy
///         refinements — 2-bit saturating confidence counters on PST entries (vs. plain
///         last-writer-wins here) and appending "spatial misses" (in-generation mispredicted
///         accesses) to the RMOB alongside triggers — are not modeled; only the mechanism
///         demonstrated by the paper's own worked example is implemented. Only misses
///         (<c>wasHit == false</c>) participate in any bookkeeping, matching the paper's
///         "miss order" framing (Fig. 3's caption is literally "Observed Miss Order"); unlike
///         SMS, this prefetcher does not train on hits.
///     </para>
/// </summary>
public sealed class StemsPrefetcher : IPrefetcher {
    // ── Geometry (matches SmsPrefetcher) ──────────────────────────────────────
    private const int RegionBytes = 2048;
    private const int MaxBlocksPerRegion = 64;

    private const int FilterSize = 32;
    private const int AccumSize = 64;

    private const int PstTotalEntries = 16384;
    private const int PstWays = 16;
    private const int PstSets = StemsPrefetcher.PstTotalEntries / StemsPrefetcher.PstWays;

    // ── RMOB (paper: 128K entries) ────────────────────────────────────────────
    private const int RmobSize = 131072;

    // ── Reconstruction window (paper: 256-entry reconstruction buffer) ────────
    private const int ReconWindow = 256;

    private readonly AccumEntry?[] _accum = new AccumEntry?[StemsPrefetcher.AccumSize];
    private readonly int _blockBits;
    private readonly ulong _blockMask;

    private readonly FilterEntry[] _filter = new FilterEntry[StemsPrefetcher.FilterSize];
    private readonly PstEntry?[,] _pst = new PstEntry?[StemsPrefetcher.PstSets, StemsPrefetcher.PstWays];
    private readonly ulong _regionMask;

    private readonly RmobEntry[] _rmob = new RmobEntry[StemsPrefetcher.RmobSize];
    private readonly Dictionary<ulong, int> _rmobIndexByAddr = new();

    private int _age;
    private int _globalMissIdx;
    private int _pstAge;
    private int _rmobCount;
    private int _rmobHead; // next write slot

    public StemsPrefetcher(int blockBytes = 32) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        int blocksPerRegion = StemsPrefetcher.RegionBytes / blockBytes;
        if (blocksPerRegion > StemsPrefetcher.MaxBlocksPerRegion)
            throw new ArgumentException(
                $"blockBytes must be ≥ {StemsPrefetcher.RegionBytes / StemsPrefetcher.MaxBlocksPerRegion}.",
                nameof(blockBytes)
            );
        _blockBits = BitOperations.Log2((uint)blockBytes);
        _regionMask = ~(ulong)(StemsPrefetcher.RegionBytes - 1);
        _blockMask = ~(ulong)(blockBytes - 1);
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        // Only misses participate — see the "Adaptations" doc comment above.
        if (wasHit) return 0;

        int thisIdx = _globalMissIdx++;
        ulong regionBase = address & _regionMask;
        ulong blockAddr = address & _blockMask;
        var blockOffset = (int)((address - regionBase) >> _blockBits);

        // 1. Accumulation table: ongoing multi-access generation.
        int accumIdx = FindAccum(regionBase);
        if (accumIdx >= 0) {
            TrainAccum(_accum[accumIdx]!, blockOffset, thisIdx);
            return 0;
        }

        // 2. Filter table: single-access generation in progress.
        int filterIdx = FindFilter(regionBase);
        if (filterIdx >= 0) {
            ref FilterEntry f = ref _filter[filterIdx];
            if (f.TriggerOffset == blockOffset) {
                f.Age = ++_age;
                return 0;
            }

            // Second distinct block → promote to accumulation table.
            var accum = new AccumEntry {
                RegionBase = regionBase,
                TriggerPc = f.TriggerPc,
                TriggerOffset = f.TriggerOffset,
                LastGlobalIdx = thisIdx,
                Age = ++_age,
            };
            accum.Sequence.Add((blockOffset, thisIdx - f.TriggerGlobalIdx - 1));
            f = default(FilterEntry);
            AllocAccum(accum);
            return 0;
        }

        // 3. Trigger access: this is a new generation. Reconstruct predictions from the RMOB
        // before recording this trigger (so the lookup sees only *prior* occurrences), then
        // append it.
        var count = 0;
        if (_rmobIndexByAddr.TryGetValue(blockAddr, out int priorIdx) && _rmob[priorIdx].BlockAddress == blockAddr)
            count = Reconstruct(priorIdx, pc, blockOffset, regionBase, targets);

        AppendRmob(blockAddr, pc, blockOffset, regionBase, thisIdx);
        AllocFilter(regionBase, pc, blockOffset, thisIdx);
        return count;
    }

    // ── AGT search (identical structure to SmsPrefetcher) ─────────────────────

    private int FindAccum(ulong regionBase) {
        for (var i = 0; i < StemsPrefetcher.AccumSize; i++)
            if (_accum[i] is { } e && e.RegionBase == regionBase)
                return i;
        return -1;
    }

    private int FindFilter(ulong regionBase) {
        for (var i = 0; i < StemsPrefetcher.FilterSize; i++)
            if (_filter[i].Valid && _filter[i].RegionBase == regionBase)
                return i;
        return -1;
    }

    private void AllocFilter(ulong regionBase, ulong triggerPc, int triggerOffset, int triggerGlobalIdx) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < StemsPrefetcher.FilterSize; i++) {
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
            TriggerGlobalIdx = triggerGlobalIdx,
            Age = ++_age,
            Valid = true,
        };
    }

    // ── AGT accumulation: sequence-based, not bit-vector ──────────────────────

    private void TrainAccum(AccumEntry e, int blockOffset, int globalIdx) {
        foreach ((int offset, int _) in e.Sequence)
            if (offset == blockOffset) {
                e.Age = ++_age;
                return; // each block appears at most once in a generation's sequence
            }

        e.Sequence.Add((blockOffset, globalIdx - e.LastGlobalIdx - 1));
        e.LastGlobalIdx = globalIdx;
        e.Age = ++_age;
    }

    private void AllocAccum(AccumEntry entry) {
        int slot = -1;
        var minAge = int.MaxValue;
        for (var i = 0; i < StemsPrefetcher.AccumSize; i++) {
            if (_accum[i] is null) {
                slot = i;
                break;
            }

            if (_accum[i]!.Age < minAge) {
                minAge = _accum[i]!.Age;
                slot = i;
            }
        }

        if (_accum[slot] is { } evicted) PstWrite(evicted.TriggerPc, evicted.TriggerOffset, evicted.Sequence);
        _accum[slot] = entry;
    }

    // ── PST (identical indexing to SmsPrefetcher's PHT) ───────────────────────

    private static (int set, ulong tag) PstIndex(ulong pc, int offset) {
        ulong key = (pc >> 2) ^ ((uint)offset * 2654435761UL);
        return ((int)(key & (StemsPrefetcher.PstSets - 1)), key >> 10);
    }

    private void PstWrite(ulong triggerPc, int triggerOffset, List<(int Offset, int Delta)> sequence) {
        if (sequence.Count == 0) return;
        (int set, ulong tag) = PstIndex(triggerPc, triggerOffset);
        for (var w = 0; w < StemsPrefetcher.PstWays; w++)
            if (_pst[set, w] is { } e && e.Tag == tag) {
                e.Sequence = sequence;
                e.LruAge = ++_pstAge;
                return;
            }

        int victim = FindPstVictim(set);
        _pst[set, victim] = new PstEntry { Tag = tag, Sequence = sequence, LruAge = ++_pstAge, };
    }

    private List<(int Offset, int Delta)>? PstLookup(ulong pc, int offset) {
        (int set, ulong tag) = PstIndex(pc, offset);
        for (var w = 0; w < StemsPrefetcher.PstWays; w++) {
            if (_pst[set, w] is not { } e || e.Tag != tag) continue;
            e.LruAge = ++_pstAge;
            return e.Sequence;
        }

        return null;
    }

    private int FindPstVictim(int set) {
        int victim = 0, minLru = int.MaxValue;
        for (var w = 0; w < StemsPrefetcher.PstWays; w++) {
            if (_pst[set, w] is null) return w;
            if (_pst[set, w]!.LruAge < minLru) {
                minLru = _pst[set, w]!.LruAge;
                victim = w;
            }
        }

        return victim;
    }

    // ── RMOB ───────────────────────────────────────────────────────────────────

    private void AppendRmob(ulong blockAddr, ulong triggerPc, int triggerOffset, ulong regionBase, int globalIdx) {
        int slot = _rmobHead;

        // About to overwrite the oldest live entry (once the buffer is full, _rmobHead always
        // points at one) — drop its address→slot mapping too, so _rmobIndexByAddr stays bounded
        // by RmobSize instead of growing across every distinct trigger address ever seen.
        if (_rmobCount == StemsPrefetcher.RmobSize) {
            ulong evictedAddr = _rmob[slot].BlockAddress;
            if (_rmobIndexByAddr.TryGetValue(evictedAddr, out int idx) && idx == slot)
                _rmobIndexByAddr.Remove(evictedAddr);
        }

        int delta = _rmobCount == 0 ? 0 : globalIdx - _rmob[PrevRmobIndex(slot)].GlobalIdx - 1;
        _rmob[slot] = new RmobEntry {
            BlockAddress = blockAddr,
            TriggerPc = triggerPc,
            TriggerOffset = triggerOffset,
            RegionBase = regionBase,
            Delta = delta,
            GlobalIdx = globalIdx,
        };
        _rmobIndexByAddr[blockAddr] = slot;
        _rmobHead = (slot + 1) % StemsPrefetcher.RmobSize;
        if (_rmobCount < StemsPrefetcher.RmobSize) _rmobCount++;
    }

    private int PrevRmobIndex(int idx) => (idx - 1 + StemsPrefetcher.RmobSize) % StemsPrefetcher.RmobSize;

    private int NextRmobIndex(int idx) => (idx + 1) % StemsPrefetcher.RmobSize;

    // ── Reconstruction (§3.1, Fig. 3/5) ───────────────────────────────────────

    /// <summary>
    ///     Reconstructs the predicted miss sequence following a prior occurrence of the current
    ///     trigger (<paramref name="fromIdx" /> in the RMOB), by walking the temporal (trigger)
    ///     sequence forward and, for each trigger placed (including the current one, anchored at
    ///     position 0), interleaving its own spatial sequence from the PST. Position 0 is the
    ///     current (real) access and is never itself emitted.
    /// </summary>
    private int Reconstruct(
        int fromIdx,
        ulong anchorPc,
        int anchorOffset,
        ulong anchorRegionBase,
        Span<ulong> targets
    ) {
        var placed = new Dictionary<int, ulong>();

        ExpandSpatial(anchorPc, anchorOffset, anchorRegionBase, 0, placed);

        var prevPos = 0;
        int idx = fromIdx;
        var steps = 0;
        while (steps < StemsPrefetcher.ReconWindow) {
            int nextIdx = NextRmobIndex(idx);
            if (nextIdx == _rmobHead) break; // caught up to "now" — no more recorded history
            ref RmobEntry e = ref _rmob[nextIdx];
            int pos = prevPos + e.Delta + 1;
            if (pos >= StemsPrefetcher.ReconWindow) break;
            TryPlace(placed, pos, e.BlockAddress);
            ExpandSpatial(e.TriggerPc, e.TriggerOffset, e.RegionBase, pos, placed);
            prevPos = pos;
            idx = nextIdx;
            steps++;
        }

        var count = 0;
        foreach (int pos in placed.Keys.Where(p => p != 0).Order()) {
            if (count >= targets.Length) break;
            targets[count++] = placed[pos];
        }

        return count;
    }

    private void ExpandSpatial(
        ulong triggerPc,
        int triggerOffset,
        ulong regionBase,
        int anchorPos,
        Dictionary<int, ulong> placed
    ) {
        List<(int Offset, int Delta)>? seq = PstLookup(triggerPc, triggerOffset);
        if (seq is null) return;

        int prevPos = anchorPos;
        ulong blockBytes = 1UL << _blockBits;
        foreach ((int offset, int delta) in seq) {
            int pos = prevPos + delta + 1;
            if (pos >= StemsPrefetcher.ReconWindow) break;
            TryPlace(placed, pos, regionBase + (ulong)offset * blockBytes);
            prevPos = pos;
        }
    }

    /// <summary>
    ///     Places <paramref name="addr" /> at <paramref name="pos" /> in the reconstruction
    ///     window, or the nearest free neighbor within ±2 (paper §4.2: "searches for an adjacent
    ///     free space… at most two elements forward or backward"). Dropped if none is free.
    /// </summary>
    private static void TryPlace(Dictionary<int, ulong> placed, int pos, ulong addr) {
        Span<int> order = [pos, pos + 1, pos - 1, pos + 2, pos - 2,];
        foreach (int cand in order) {
            if (cand < 0 || cand >= StemsPrefetcher.ReconWindow) continue;
            if (placed.ContainsKey(cand)) continue;
            placed[cand] = addr;
            return;
        }
    }

    // ── Entry types ────────────────────────────────────────────────────────────

    private struct FilterEntry {
        public ulong RegionBase;
        public ulong TriggerPc;
        public int TriggerOffset;
        public int TriggerGlobalIdx;
        public int Age;
        public bool Valid;
    }

    private sealed class AccumEntry {
        public int Age;
        public int LastGlobalIdx;
        public ulong RegionBase;
        public int TriggerOffset;
        public ulong TriggerPc;
        public List<(int Offset, int Delta)> Sequence { get; } = [];
    }

    private sealed class PstEntry {
        public int LruAge;
        public List<(int Offset, int Delta)> Sequence = [];
        public ulong Tag;
    }

    private struct RmobEntry {
        public ulong BlockAddress;
        public ulong TriggerPc;
        public int TriggerOffset;
        public ulong RegionBase;
        public int Delta;
        public int GlobalIdx;
    }
}