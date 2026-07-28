#region

using System.Numerics;
using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>
///     A set-associative cache that uses <see cref="BdiCompressor" /> (Pekhimenko et al., PACT
///     2012) to let more lines be resident in the same physical data storage than an equivalent
///     uncompressed cache could hold.
///     <para>
///         <strong>Why a separate class, not a <see cref="SetAssociativeCache" /> option:</strong>
///         BΔI's variable per-line footprint breaks the single-victim-per-miss invariant that
///         every one of <see cref="SetAssociativeCache" />'s orthogonal features (the inclusion
///         cascade, Jouppi victim buffer, write-back buffer, sectoring, Exclusive hand-off, MSHR
///         bookkeeping) is written around — freeing space for one incoming or growing line can
///         require evicting several lines at once. Retrofitting that into <c>FillBlock</c> would
///         mean auditing all six co-features for a multi-evict path they don't expect.
///         <see cref="MoesifCache" /> already establishes the precedent of a separate, focused
///         class for a fundamentally different residency/coherence model rather than folding it
///         into the shared class every other feature depends on. This design deliberately omits
///         sectoring, MSHR modeling, the Jouppi victim buffer, bus banking/ports, and inclusion —
///         matching the fidelity of the paper's own evaluation (a single cache level's hit/miss
///         and effective-capacity behavior), not the full feature matrix.
///     </para>
///     <para>
///         <strong>Bookkeeping-only compression:</strong> this is a byte-accurate functional
///         simulator, not a real chip — there is no benefit to literally shrinking a .NET
///         <c>byte[]</c> allocation to match a compressed size. Each resident line keeps its full
///         uncompressed bytes (so reads/writes are trivial and always correct) alongside a
///         separately tracked <em>simulated footprint</em>, in <paramref name="segmentBytes" />-
///         sized segments, computed by <see cref="BdiCompressor.Compress" /> on the real fill/
///         write bytes. Capacity and eviction decisions are driven by that simulated footprint,
///         which is exactly the effect the paper measures (more resident lines, lower MPKI) —
///         decompression latency itself is not separately modeled (the paper's own result: 1-to-
///         5-cycle decompression latency changes performance by under 1%, Section 7).
///     </para>
///     <para>
///         <strong>Two independent capacity constraints</strong>, matching the paper's design
///         (Section 5.1): (1) at most <c>2 × physicalWays</c> tags per set (<em>doubled tags</em>
///         — a real cap on resident line <em>count</em>, independent of size); (2) the sum of
///         resident lines' segment footprints per set must not exceed
///         <c>physicalWays × blockBytes / segmentBytes</c> segments (the real, unchanged
///         <em>physical data budget</em>). A miss or a write that grows a resident line's
///         footprint evicts LRU (or the supplied policy's) victims one at a time until both
///         constraints hold again — the paper's "evict multiple LRU lines to create enough
///         space."
///     </para>
/// </summary>
public sealed class BdiCache : IMemory {
    private readonly IMemory _backing;
    private readonly int _blockBytes;
    private readonly byte[][][] _blocks; // [set][tagWay][blockBytes]: full uncompressed bytes
    private readonly int _budgetSegments; // per set: physicalWays * segmentsPerLine
    private readonly bool[][]? _dirty; // non-null only in WriteBack mode
    private readonly int _indexBits;
    private readonly int _indexMask;
    private readonly int _offsetBits;
    private readonly int _offsetMask;
    private readonly IReplacementPolicy _policy;
    private readonly int _segmentBytes;
    private readonly int _segmentsPerLine; // blockBytes / segmentBytes: a NoCompr line's footprint
    private readonly int[][] _segments; // [set][tagWay]: current footprint in segments, 0 = invalid
    private readonly ulong?[][] _tags; // [set][tagWay]: null = invalid
    private readonly int _tagWays; // 2 * physicalWays
    private readonly WritePolicyKind _writePolicy;
    private long _pendingStalls;

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Physical data storage size in bytes (unchanged by compression). Must be a power of 2.</param>
    /// <param name="physicalWays">Associativity of the physical data storage. Must be a power of 2.</param>
    /// <param name="blockBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    /// <param name="segmentBytes">
    ///     Physical data storage allocation granularity (default 8, matching the paper). Must be a
    ///     power of 2 dividing <paramref name="blockBytes" /> evenly.
    /// </param>
    /// <param name="writePolicy">Write-hit policy — write-back always allocates on a write miss (there is nowhere else for dirty data to live); write-through never does.</param>
    /// <param name="policy">Replacement policy over the doubled <c>2 × physicalWays</c> tag slots per set. Defaults to LRU.</param>
    public BdiCache(
        IMemory backing,
        int capacityBytes,
        int physicalWays,
        int blockBytes,
        int missLatency,
        int segmentBytes = 8,
        WritePolicyKind writePolicy = WritePolicyKind.WriteThrough,
        IReplacementPolicy? policy = null
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalWays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        if (!BitOperations.IsPow2(capacityBytes) || !BitOperations.IsPow2(physicalWays) ||
            !BitOperations.IsPow2(blockBytes) || !BitOperations.IsPow2(segmentBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");
        if (segmentBytes > blockBytes || blockBytes % segmentBytes != 0)
            throw new ArgumentException("segmentBytes must evenly divide blockBytes.");
        if (blockBytes % 8 != 0)
            throw new ArgumentException("blockBytes must be a multiple of 8 (BdiCompressor requirement).");

        _backing = backing;
        _blockBytes = blockBytes;
        MissLatency = missLatency;
        _segmentBytes = segmentBytes;
        _segmentsPerLine = blockBytes / segmentBytes;
        _writePolicy = writePolicy;

        int sets = capacityBytes / (physicalWays * blockBytes);
        _tagWays = physicalWays * 2;
        _budgetSegments = physicalWays * _segmentsPerLine;

        _offsetBits = BitOperations.Log2((uint)blockBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _segments = new int[sets][];
        _dirty = writePolicy == WritePolicyKind.WriteBack ? new bool[sets][] : null;
        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_tagWays];
            _blocks[s] = new byte[_tagWays][];
            _segments[s] = new int[_tagWays];
            _dirty?[s] = new bool[_tagWays];
            for (var w = 0; w < _tagWays; w++) _blocks[s][w] = new byte[blockBytes];
        }

        _policy = policy ?? new LruPolicy(sets, _tagWays);
    }

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }

    /// <summary>Currently-resident line count across every set — bounded by <c>sets × 2 × physicalWays</c>.</summary>
    public int ResidentLineCount {
        get {
            var n = 0;
            foreach (ulong?[] set in _tags)
            foreach (ulong? t in set)
                if (t.HasValue)
                    n++;
            return n;
        }
    }

    /// <summary>
    ///     Sum of uncompressed line sizes currently resident, divided by the physical bytes they
    ///     actually occupy (segment-rounded) — the paper's "effective compression ratio" /
    ///     effective cache size increase. 1.0 with nothing resident.
    /// </summary>
    public double EffectiveCompressionRatio {
        get {
            long uncompressed = 0, physical = 0;
            for (var s = 0; s < _tags.Length; s++)
            for (var w = 0; w < _tagWays; w++)
                if (_tags[s][w].HasValue) {
                    uncompressed += _blockBytes;
                    physical += _segments[s][w] * _segmentBytes;
                }

            return physical == 0 ? 1.0 : (double)uncompressed / physical;
        }
    }

    public ulong Read(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) return _backing.Read(address, bytes);

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            Hits++;
            _policy.RecordHit(set, way);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        Misses++;
        _pendingStalls += MissLatency;
        way = FillLine(set, address);
        return ReadBytes(_blocks[set][way], offset, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        if (_writePolicy == WritePolicyKind.WriteThrough) _backing.Write(address, value, bytes);

        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) {
            // Cross-boundary write bypasses the cache: flush overlapping dirty lines first (so a
            // deferred flush can't later clobber the bytes we're about to write), then invalidate.
            ulong end = address + (ulong)bytes;
            if (_writePolicy == WritePolicyKind.WriteBack) {
                for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes) {
                    Decompose(a, out int s, out ulong t);
                    int w = FindWay(s, t);
                    if (w >= 0) FlushIfDirty(s, w, t);
                }

                _backing.Write(address, value, bytes);
            }

            for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes) {
                Decompose(a, out int s, out ulong t);
                int w = FindWay(s, t);
                if (w >= 0) InvalidateWay(s, w);
            }

            return;
        }

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            Hits++;
            _policy.RecordHit(set, way);
            WriteBytes(_blocks[set][way], offset, value, bytes);
            if (_dirty != null) _dirty[set][way] = true;
            RecomputeFootprint(set, way);
            return;
        }

        Misses++;
        if (_writePolicy == WritePolicyKind.WriteBack) {
            _pendingStalls += MissLatency;
            way = FillLine(set, address);
            WriteBytes(_blocks[set][way], offset, value, bytes);
            _dirty![set][way] = true;
            RecomputeFootprint(set, way);
        }
        else {
            _pendingStalls += MissLatency;
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        _backing.Load(address, data);
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes) {
            Decompose(a, out int set, out ulong tag);
            int way = FindWay(set, tag);
            if (way >= 0) InvalidateWay(set, way);
        }
    }

    /// <summary>Returns and clears the accumulated miss-penalty cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
    }

    // ── Address decomposition ────────────────────────────────────────────────

    private void Decompose(ulong address, out int set, out ulong tag) {
        set = (int)((address >> _offsetBits) & (ulong)_indexMask);
        tag = address >> (_offsetBits + _indexBits);
    }

    private int FindWay(int set, ulong tag) {
        for (var w = 0; w < _tagWays; w++)
            if (_tags[set][w] == tag)
                return w;
        return -1;
    }

    // ── Fill / eviction ───────────────────────────────────────────────────────

    private int FillLine(int set, ulong address) {
        ulong lineBase = address & ~(ulong)_offsetMask;
        var fresh = new byte[_blockBytes];
        for (var i = 0; i < _blockBytes; i++) fresh[i] = (byte)_backing.Read(lineBase + (ulong)i, 1);
        int segments = SegmentsFor(BdiCompressor.Compress(fresh).Data.Length);

        MakeRoom(set, segments);
        int way = FindFreeWay(set);

        Decompose(address, out _, out ulong tag);
        Buffer.BlockCopy(fresh, 0, _blocks[set][way], 0, _blockBytes);
        _tags[set][way] = tag;
        _segments[set][way] = segments;
        if (_dirty != null) _dirty[set][way] = false;
        _policy.RecordInstall(set, way);
        return way;
    }

    private void RecomputeFootprint(int set, int way) {
        int newSegments = SegmentsFor(BdiCompressor.Compress(_blocks[set][way]).Data.Length);
        int oldSegments = _segments[set][way];
        if (newSegments > oldSegments) {
            while (UsedSegments(set) - oldSegments + newSegments > _budgetSegments) {
                int victim = ChooseEvictionVictim(set, way);
                // Only this line itself remains — its own max footprint always fits alone
                // (budgetSegments >= segmentsPerLine for any physicalWays >= 1), so this cannot
                // loop forever; it can only be reached if nothing else is left to evict.
                if (victim < 0 || victim == way) break;
                EvictWay(set, victim);
            }
        }

        _segments[set][way] = newSegments;
    }

    private void MakeRoom(int set, int neededSegments) {
        while (true) {
            int freeWay = -1;
            for (var w = 0; w < _tagWays; w++)
                if (!_tags[set][w].HasValue) {
                    freeWay = w;
                    break;
                }

            if (freeWay >= 0 && UsedSegments(set) + neededSegments <= _budgetSegments) return;

            int victim = ChooseEvictionVictim(set, -1);
            if (victim < 0) return; // safety: nothing left to evict
            EvictWay(set, victim);
        }
    }

    /// <summary>
    ///     Picks the next currently-<em>valid</em> way to evict, excluding <paramref name="protect" />
    ///     (pass -1 for none). A single miss or footprint growth can require evicting several
    ///     lines in a row (segment-budget pressure, not just tag-slot pressure) — a plain repeated
    ///     <see cref="IReplacementPolicy.ChooseVictim" /> call is not enough on its own for that,
    ///     because a policy like LRU has no way to know a way it just returned was already
    ///     invalidated this same call and would return it again forever. <see cref="IReplacementPolicy.ChooseVictim" />
    ///     is still called once per iteration so stateful policies (RRIP's per-access RRPV aging)
    ///     see every eviction "tick"; its return value is used only when it happens to already be
    ///     a currently-valid, non-protected way, otherwise <see cref="IReplacementPolicy.GetMetadata" />
    ///     ranks the valid, non-protected ways directly (highest = oldest, matching every existing
    ///     <see cref="IReplacementPolicy" /> implementation's convention).
    /// </summary>
    private int ChooseEvictionVictim(int set, int protect) {
        int candidate = _policy.ChooseVictim(set);
        if (candidate != protect && _tags[set][candidate].HasValue) return candidate;

        var best = -1;
        var bestAge = -1;
        for (var w = 0; w < _tagWays; w++) {
            if (w == protect || !_tags[set][w].HasValue) continue;
            int age = _policy.GetMetadata(set, w);
            if (age > bestAge) {
                bestAge = age;
                best = w;
            }
        }

        return best;
    }

    private int UsedSegments(int set) {
        var total = 0;
        for (var w = 0; w < _tagWays; w++)
            if (_tags[set][w].HasValue)
                total += _segments[set][w];
        return total;
    }

    private int FindFreeWay(int set) {
        for (var w = 0; w < _tagWays; w++)
            if (!_tags[set][w].HasValue)
                return w;
        throw new InvalidOperationException("BdiCache: no free tag slot after MakeRoom — invariant violated.");
    }

    private void EvictWay(int set, int way) {
        if (_tags[set][way] is not { } tag) return;
        FlushIfDirty(set, way, tag);
        _tags[set][way] = null;
        _segments[set][way] = 0;
        Evictions++;
    }

    private void InvalidateWay(int set, int way) {
        _tags[set][way] = null;
        _segments[set][way] = 0;
        if (_dirty != null) _dirty[set][way] = false;
    }

    private void FlushIfDirty(int set, int way, ulong tag) {
        if (_dirty == null || !_dirty[set][way]) return;
        ulong lineBase = (tag << (_offsetBits + _indexBits)) | ((ulong)set << _offsetBits);
        byte[] block = _blocks[set][way];
        for (var i = 0; i < _blockBytes; i++) _backing.Write(lineBase + (ulong)i, block[i], 1);
        _dirty[set][way] = false;
        DirtyEvictions++;
    }

    private int SegmentsFor(int compressedBytes) =>
        Math.Min(_segmentsPerLine, (compressedBytes + _segmentBytes - 1) / _segmentBytes);

    // ── Byte packing (little-endian, matches SetAssociativeCache) ────────────

    private static ulong ReadBytes(byte[] block, int offset, int bytes) {
        ulong result = 0;
        for (var i = 0; i < bytes; i++) result |= (ulong)block[offset + i] << (i * 8);
        return result;
    }

    private static void WriteBytes(byte[] block, int offset, ulong value, int bytes) {
        for (var i = 0; i < bytes; i++) block[offset + i] = (byte)(value >> (i * 8));
    }
}
