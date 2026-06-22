using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

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

    private readonly ulong?[][] _tags;   // [set][way]: null = invalid
    private readonly byte[][][] _blocks; // [set][way][offset]
    private readonly int[][] _lruAge;    // [set][way]: 0 = MRU, higher = older

    private long _pendingStalls;

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Total cache size in bytes. Must be a power of 2.</param>
    /// <param name="ways">Associativity. Must be a power of 2.</param>
    /// <param name="blockSizeBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    public SetAssociativeCache(
        IMemory backing,
        int capacityBytes,
        int ways,
        int blockSizeBytes,
        int missLatency
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        if (!BitOperations.IsPow2(capacityBytes) ||
            !BitOperations.IsPow2(ways) ||
            !BitOperations.IsPow2(blockSizeBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");

        _backing = backing;
        _ways = ways;
        _blockSize = blockSizeBytes;
        int sets = capacityBytes / (ways * blockSizeBytes);
        MissLatency = missLatency;

        _offsetBits = BitOperations.Log2((uint)blockSizeBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _lruAge = new int[sets][];

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_ways];
            _blocks[s] = new byte[_ways][];
            _lruAge[s] = new int[_ways];
            for (var w = 0; w < _ways; w++) {
                _blocks[s][w] = new byte[_blockSize];
                _lruAge[s][w] = w; // way 0 starts as MRU
            }
        }
    }

    /// <summary>Returns and clears the accumulated miss-penalty cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
    }

    // ── Address decomposition ────────────────────────────────────────────────

    private void Decompose(ulong address, out int set, out ulong tag, out int offset) {
        offset = (int)(address & (ulong)_offsetMask);
        set = (int)((address >> _offsetBits) & (ulong)_indexMask);
        tag = address >> (_offsetBits + _indexBits);
    }

    private int FindWay(int set, ulong tag) {
        for (var w = 0; w < _ways; w++)
            if (_tags[set][w] == tag)
                return w;
        return -1;
    }

    private int LruWay(int set) {
        var oldest = 0;
        for (var w = 1; w < _ways; w++)
            if (_lruAge[set][w] > _lruAge[set][oldest])
                oldest = w;
        return oldest;
    }

    private void TouchLru(int set, int way) {
        int age = _lruAge[set][way];
        for (var w = 0; w < _ways; w++)
            if (_lruAge[set][w] < age)
                _lruAge[set][w]++;
        _lruAge[set][way] = 0;
    }

    // ── Block fill / byte access ─────────────────────────────────────────────

    private void FillBlock(int set, int way, ulong address) {
        ulong lineBase = address & ~(ulong)_offsetMask;
        for (var i = 0; i < _blockSize; i++) _blocks[set][way][i] = (byte)_backing.Read(lineBase + (ulong)i, 1);
        Decompose(address, out _, out ulong tag, out _);
        if (_tags[set][way].HasValue) Evictions++;
        _tags[set][way] = tag;
        TouchLru(set, way);
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

    public ulong Read(ulong address, int bytes) {
        // Access crossing a block boundary bypasses the cache.
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) return _backing.Read(address, bytes);

        Decompose(address, out int set, out ulong tag, out _);
        int way = FindWay(set, tag);
        if (way >= 0) {
            Hits++;
            TouchLru(set, way);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        Misses++;
        _pendingStalls += MissLatency;
        int evict = LruWay(set);
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
                Decompose(a, out int s, out ulong t, out _);
                for (var w = 0; w < _ways; w++)
                    if (_tags[s][w] == t) _tags[s][w] = null;
            }
            return;
        }

        Decompose(address, out int set, out ulong tag, out _);
        int way = FindWay(set, tag);
        if (way >= 0) {
            Hits++;
            TouchLru(set, way);
            WriteBytes(_blocks[set][way], offset, value, bytes);
        }
        else {
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
            Decompose(a, out int set, out ulong tag, out _);
            for (var w = 0; w < _ways; w++)
                if (_tags[set][w] == tag)
                    _tags[set][w] = null;
        }
    }
}