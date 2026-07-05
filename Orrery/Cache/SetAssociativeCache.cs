using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

public sealed record CacheLine(int Set, int Way, bool Valid, ulong Tag, int LruAge, byte[] Block);

/// <summary>
/// N-way set-associative cache implementing IMemory.
/// Policy: write-through, no-write-allocate, LRU replacement.
/// Cache miss does not block — it records a penalty in PendingStalls
/// that the caller drains to inject idle cycles into the pipeline.
/// </summary>
public sealed class SetAssociativeCache : IMemory {
    private readonly int _ways;
    private readonly int _blockSize;
    private readonly int _offsetMask;
    private readonly int _indexMask;
    private readonly int _offsetBits;
    private readonly int _indexBits;
    private readonly IMemory _backing;
    private readonly IReplacementPolicy _policy;

    private readonly ulong?[][] _tags;   // [set][way]: null = invalid
    private readonly byte[][][] _blocks; // [set][way][offset]

    private long _pendingStalls;
    private ulong _lastRequestPc;
    private readonly bool _usePcSignature;

    // Realistic prefetch latency: lines installed by Prefetch() that have not yet
    // "arrived". A demand hit on one of these pays the remaining countdown instead
    // of zero. Empty (and never touched) when PrefetchLatency = 0.
    private readonly List<(ulong LineBase, int Remaining)> _inFlightPrefetches = [];

    public int MissLatency { get; }
    public int PrefetchLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long Prefetches { get; private set; }
    public long LatePrefetchHits { get; private set; }
    public ulong? LastAccessAddress { get; private set; }
    public bool LastAccessWasHit { get; private set; }

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Total cache size in bytes. Must be a power of 2.</param>
    /// <param name="ways">Associativity. Must be a power of 2.</param>
    /// <param name="blockSizeBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    /// <param name="prefetchLatency">Cycles until a prefetched line is usable
    /// (0 = instant/free, the idealized model). While in flight, a demand hit on the
    /// line pays the remaining countdown; the caller must call <see cref="TickPrefetch"/>
    /// once per cycle to advance the countdowns.</param>
    /// <param name="replacementPolicy">Cache replacement policy. Defaults to LRU.</param>
    public SetAssociativeCache(
        IMemory backing,
        int capacityBytes,
        int ways,
        int blockSizeBytes,
        int missLatency,
        int prefetchLatency = 0,
        ReplacementPolicyKind replacementPolicy = ReplacementPolicyKind.Lru
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(prefetchLatency);
        if (!BitOperations.IsPow2(capacityBytes) ||
            !BitOperations.IsPow2(ways) ||
            !BitOperations.IsPow2(blockSizeBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");

        _backing = backing;
        _ways = ways;
        _blockSize = blockSizeBytes;
        int sets = capacityBytes / (ways * blockSizeBytes);
        MissLatency = missLatency;
        PrefetchLatency = prefetchLatency;

        _offsetBits = BitOperations.Log2((uint)blockSizeBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_ways];
            _blocks[s] = new byte[_ways][];
            for (var w = 0; w < _ways; w++)
                _blocks[s][w] = new byte[_blockSize];
        }

        _policy = replacementPolicy switch {
            ReplacementPolicyKind.Srrip  => new SrripPolicy(sets, ways),
            ReplacementPolicyKind.Brrip  => new BrripPolicy(sets, ways),
            ReplacementPolicyKind.Drrip  => new DrripPolicy(sets, ways),
            ReplacementPolicyKind.Ship   => new ShipPolicy(sets, ways),
            ReplacementPolicyKind.ShipPc => new ShipPolicy(sets, ways),
            ReplacementPolicyKind.Random => new RandomPolicy(sets, ways),
            ReplacementPolicyKind.Fifo   => new FifoPolicy(sets, ways),
            ReplacementPolicyKind.Plru   => new PlruPolicy(sets, ways),
            ReplacementPolicyKind.Mru    => new MruPolicy(sets, ways),
            ReplacementPolicyKind.Clock  => new ClockPolicy(sets, ways),
            _                            => new LruPolicy(sets, ways),
        };
        _usePcSignature = replacementPolicy == ReplacementPolicyKind.ShipPc;
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
        for (var w = 0; w < _ways; w++)
            if (_tags[set][w] == tag)
                return w;
        return -1;
    }

    // ── Block fill / byte access ─────────────────────────────────────────────

    private void FillBlock(int set, int way, ulong address) {
        ulong lineBase = address & ~(ulong)_offsetMask;
        for (var i = 0; i < _blockSize; i++) _blocks[set][way][i] = (byte)_backing.Read(lineBase + (ulong)i, 1);
        Decompose(address, out _, out ulong tag);
        if (_tags[set][way] is { } oldTag) {
            Evictions++;
            DropInFlightPrefetch((oldTag << (_offsetBits + _indexBits)) | ((ulong)set << _offsetBits));
        }

        _tags[set][way] = tag;
        _policy.RecordInstall(set, way);
    }

    private static ulong ReadBytes(byte[] block, int offset, int bytes) {
        ulong result = 0;
        for (var i = 0; i < bytes; i++) result |= (ulong)block[offset + i] << (i * 8);
        return result;
    }

    private static void WriteBytes(byte[] block, int offset, ulong value, int bytes) {
        for (var i = 0; i < bytes; i++) block[offset + i] = (byte)(value >> (i * 8));
    }

    // ── IMemory ──────────────────────────────────────────────────────────────

    public void SetRequestPc(ulong pc) {
        _lastRequestPc = pc;
        _backing.SetRequestPc(pc);
    }

    public ulong Read(ulong address, int bytes) {
        // Access crossing a block boundary bypasses the cache.
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) return _backing.Read(address, bytes);

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        LastAccessAddress = address;
        if (way >= 0) {
            LastAccessWasHit = true;
            Hits++;
            _policy.RecordHit(set, way);
            ChargeInFlightPrefetch(address);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        LastAccessWasHit = false;
        Misses++;
        _pendingStalls += MissLatency;
        int evict = _policy.ChooseVictim(set);
        _policy.SetPendingSignature(_usePcSignature ? _lastRequestPc : address >> _offsetBits);
        FillBlock(set, evict, address);
        return ReadBytes(_blocks[set][evict], offset, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        _backing.Write(address, value, bytes); // write-through

        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) {
            // Cross-boundary write: invalidate every line the write touches so
            // future reads don't return stale data.
            ulong end = address + (ulong)bytes;
            for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockSize) {
                Decompose(a, out int s, out ulong t);
                for (var w = 0; w < _ways; w++)
                    if (_tags[s][w] == t) {
                        _tags[s][w] = null;
                        DropInFlightPrefetch(a);
                    }
            }

            return;
        }

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        LastAccessAddress = address;
        if (way >= 0) {
            LastAccessWasHit = true;
            Hits++;
            _policy.RecordHit(set, way);
            ChargeInFlightPrefetch(address);
            WriteBytes(_blocks[set][way], offset, value, bytes);
        }
        else {
            LastAccessWasHit = false;
            Misses++;
            _pendingStalls += MissLatency;
            // No-write-allocate: don't install the line.
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        _backing.Load(address, data);
        // Invalidate cache lines that overlap the loaded region.
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockSize) {
            Decompose(a, out int set, out ulong tag);
            for (var w = 0; w < _ways; w++)
                if (_tags[set][w] == tag) {
                    _tags[set][w] = null;
                    DropInFlightPrefetch(a);
                }
        }
    }

    // ── Prefetch ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Installs the cache line covering <paramref name="address"/> without charging any stall
    /// penalty at install time. No-ops if the line is already present. Used by prefetchers to
    /// warm the cache ahead of demand accesses; callers are responsible for ensuring the
    /// address is not in an uncacheable MMIO region. With <see cref="PrefetchLatency"/> &gt; 0
    /// the line is marked in flight for that many cycles; a demand hit arriving earlier pays
    /// the remaining countdown (see <see cref="TickPrefetch"/>).
    /// </summary>
    public void Prefetch(ulong address) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + 1 > _blockSize) return;
        Decompose(address, out int set, out ulong tag);
        if (FindWay(set, tag) >= 0) return; // already present
        int evict = _policy.ChooseVictim(set);
        _policy.SetPendingSignature(address >> _offsetBits);
        try {
            FillBlock(set, evict, address);
            Prefetches++;
            if (PrefetchLatency > 0) _inFlightPrefetches.Add((address & ~(ulong)_offsetMask, PrefetchLatency));
        }
        catch {
            // Prefetch address is outside the backing memory's valid range; drop silently.
        }
    }

    /// <summary>Number of prefetched lines still in flight (counts against MSHR capacity).</summary>
    public int InFlightPrefetchCount => _inFlightPrefetches.Count;

    /// <summary>
    /// Advances all in-flight prefetch countdowns by one cycle. Must be called once per
    /// simulated cycle when <see cref="PrefetchLatency"/> &gt; 0; a no-op otherwise.
    /// </summary>
    public void TickPrefetch() {
        for (int i = _inFlightPrefetches.Count - 1; i >= 0; i--) {
            (ulong lineBase, int remaining) = _inFlightPrefetches[i];
            if (remaining <= 1)
                _inFlightPrefetches.RemoveAt(i);
            else
                _inFlightPrefetches[i] = (lineBase, remaining - 1);
        }
    }

    /// <summary>
    /// Demand access hit a line whose prefetch is still in flight: pay the remaining
    /// countdown (the fill has not arrived yet) and retire the in-flight entry.
    /// </summary>
    private void ChargeInFlightPrefetch(ulong address) {
        if (_inFlightPrefetches.Count == 0) return;
        ulong lineBase = address & ~(ulong)_offsetMask;
        for (var i = 0; i < _inFlightPrefetches.Count; i++) {
            if (_inFlightPrefetches[i].LineBase != lineBase) continue;
            _pendingStalls += _inFlightPrefetches[i].Remaining;
            LatePrefetchHits++;
            _inFlightPrefetches.RemoveAt(i);
            return;
        }
    }

    /// <summary>Forgets the in-flight prefetch for an evicted or invalidated line, keeping
    /// the invariant that every in-flight entry refers to a resident line.</summary>
    private void DropInFlightPrefetch(ulong lineBase) {
        if (_inFlightPrefetches.Count == 0) return;
        for (var i = 0; i < _inFlightPrefetches.Count; i++) {
            if (_inFlightPrefetches[i].LineBase != lineBase) continue;
            _inFlightPrefetches.RemoveAt(i);
            return;
        }
    }

    // ── Inspection ───────────────────────────────────────────────────────────

    public int Sets => _tags.Length;
    public int Ways => _ways;
    public int BlockBytes => _blockSize;
    public int OffsetBits => _offsetBits;
    public int IndexBits => _indexBits;

    public CacheLine[] GetSnapshot() {
        int sets = _tags.Length;
        var lines = new CacheLine[sets * _ways];
        var idx = 0;
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < _ways; w++) {
            bool valid = _tags[s][w].HasValue;
            ulong tag = _tags[s][w] ?? 0;
            var blockCopy = new byte[_blockSize];
            Buffer.BlockCopy(_blocks[s][w], 0, blockCopy, 0, _blockSize);
            lines[idx++] = new CacheLine(s, w, valid, tag, _policy.GetMetadata(s, w), blockCopy);
        }

        return lines;
    }
}