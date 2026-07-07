using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

public sealed record CacheLine(int Set, int Way, bool Valid, ulong Tag, int LruAge, byte[] Block, bool Dirty = false);

/// <summary>
/// N-way set-associative cache implementing IMemory.
/// Write policy and write-miss policy are configurable; default is write-through + no-write-allocate.
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
    private readonly WritePolicyKind _writePolicy;
    private readonly WriteMissPolicyKind _writeMissPolicy;

    private readonly ulong?[][] _tags;   // [set][way]: null = invalid
    private readonly byte[][][] _blocks; // [set][way][offset]
    private readonly bool[][]? _dirty;   // non-null only in WriteBack mode

    // Write-back buffer: holds dirty-victim lines waiting to drain to backing.
    // null when wbCapacity == 0 (disabled); always null in write-through mode.
    private struct WbEntry {
        public ulong LineBase;
        public byte[]? Data;
    }

    private readonly WbEntry[]? _wbBuffer;
    private readonly int _wbCapacity;
    private int _wbCount;

    private long _pendingStalls;
    private ulong _lastRequestPc;
    private readonly bool _usePcSignature;
    private readonly bool _requirePcOnHit; // Hawkeye needs OPTgen fed on every hit

    // Realistic prefetch latency: lines installed by Prefetch() that have not yet
    // "arrived". A demand hit on one of these pays the remaining countdown instead
    // of zero. Empty (and never touched) when PrefetchLatency = 0.
    private readonly List<(ulong LineBase, int Remaining)> _inFlightPrefetches = [];

    public int TagLatency { get; }
    public int DataLatency { get; }
    public int HitLatency => Math.Max(TagLatency, DataLatency);
    public int MissLatency { get; }
    public int PrefetchLatency { get; }
    public WritePolicyKind WritePolicy => _writePolicy;
    public WriteMissPolicyKind WriteMissPolicy => _writeMissPolicy;
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }
    public long WbDrains { get; private set; }
    public int WbCapacity => _wbCapacity;
    public int WbOccupancy => _wbCount;
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
    /// <param name="tagLatency">Cycles to look up the tag array (informational — used by pipelines
    /// to compute hit latency; does not affect <see cref="_pendingStalls"/>).</param>
    /// <param name="dataLatency">Cycles to read the data array (informational — hit latency is
    /// <c>max(tagLatency, dataLatency)</c>, matching gem5's parallel-access mode).</param>
    /// <param name="writePolicy">Write-hit policy: <see cref="WritePolicyKind.WriteThrough"/> stores
    /// immediately propagate to backing; <see cref="WritePolicyKind.WriteBack"/> keeps stores in the
    /// cache and flushes dirty lines to backing only on eviction.</param>
    /// <param name="writeMissPolicy">Write-miss policy: <see cref="WriteMissPolicyKind.NoWriteAllocate"/>
    /// writes directly to backing without installing a line; <see cref="WriteMissPolicyKind.WriteAllocate"/>
    /// installs the line (paying <paramref name="missLatency"/>) then writes into it.</param>
    /// <param name="wbCapacity">Write-back buffer capacity in lines (0 = disabled). Only active in
    /// write-back mode. Dirty evicted lines go into the buffer and drain asynchronously (one line per
    /// <see cref="TickWb"/> call); a stall is charged only when the buffer is full.</param>
    public SetAssociativeCache(
        IMemory backing,
        int capacityBytes,
        int ways,
        int blockSizeBytes,
        int missLatency,
        int prefetchLatency = 0,
        ReplacementPolicyKind replacementPolicy = ReplacementPolicyKind.Lru,
        int tagLatency = 0,
        int dataLatency = 0,
        WritePolicyKind writePolicy = WritePolicyKind.WriteThrough,
        WriteMissPolicyKind writeMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
        int wbCapacity = 0
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(prefetchLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(tagLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(dataLatency);
        if (!BitOperations.IsPow2(capacityBytes) ||
            !BitOperations.IsPow2(ways) ||
            !BitOperations.IsPow2(blockSizeBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");

        _backing = backing;
        _ways = ways;
        _blockSize = blockSizeBytes;
        int sets = capacityBytes / (ways * blockSizeBytes);
        TagLatency = tagLatency;
        DataLatency = dataLatency;
        MissLatency = missLatency;
        PrefetchLatency = prefetchLatency;
        _writePolicy = writePolicy;
        _writeMissPolicy = writeMissPolicy;
        _wbCapacity = writePolicy == WritePolicyKind.WriteBack ? Math.Max(0, wbCapacity) : 0;

        _offsetBits = BitOperations.Log2((uint)blockSizeBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _dirty = writePolicy == WritePolicyKind.WriteBack ? new bool[sets][] : null;
        _wbBuffer = _wbCapacity > 0 ? new WbEntry[_wbCapacity] : null;

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_ways];
            _blocks[s] = new byte[_ways][];
            if (_dirty != null) _dirty[s] = new bool[_ways];
            for (var w = 0; w < _ways; w++) _blocks[s][w] = new byte[_blockSize];
        }

        _policy = replacementPolicy switch {
            ReplacementPolicyKind.Srrip   => new SrripPolicy(sets, ways),
            ReplacementPolicyKind.Brrip   => new BrripPolicy(sets, ways),
            ReplacementPolicyKind.Drrip   => new DrripPolicy(sets, ways),
            ReplacementPolicyKind.Ship    => new ShipPolicy(sets, ways),
            ReplacementPolicyKind.ShipPc  => new ShipPolicy(sets, ways),
            ReplacementPolicyKind.Random  => new RandomPolicy(sets, ways),
            ReplacementPolicyKind.Fifo    => new FifoPolicy(sets, ways),
            ReplacementPolicyKind.Plru    => new PlruPolicy(sets, ways),
            ReplacementPolicyKind.Mru     => new MruPolicy(sets, ways),
            ReplacementPolicyKind.Clock   => new ClockPolicy(sets, ways),
            ReplacementPolicyKind.Hawkeye => new HawkeyePolicy(sets, ways),
            _                             => new LruPolicy(sets, ways),
        };
        _usePcSignature = replacementPolicy == ReplacementPolicyKind.ShipPc;
        _requirePcOnHit = replacementPolicy == ReplacementPolicyKind.Hawkeye;
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

    // Flushes a dirty line to backing storage or into the write-back buffer.
    // chargeStall: add MissLatency to _pendingStalls (demand paths; prefetch passes false).
    // deferToBuffer: enqueue into the WB buffer instead of writing backing synchronously.
    //   Only FillBlock passes true; cross-boundary, NWA-miss, and Load pass false.
    private void FlushDirtyLine(int set, int way, ulong tag, bool chargeStall, bool deferToBuffer = false) {
        if (_dirty == null || !_dirty[set][way]) return;
        ulong lineBase = (tag << (_offsetBits + _indexBits)) | ((ulong)set << _offsetBits);

        if (deferToBuffer && _wbBuffer != null) {
            // Buffer full: synchronous drain of oldest entry, charge stall for the wait.
            if (_wbCount >= _wbCapacity) {
                DrainWbOldestSync();
                if (chargeStall) _pendingStalls += MissLatency;
            }

            var data = new byte[_blockSize];
            Buffer.BlockCopy(_blocks[set][way], 0, data, 0, _blockSize);
            _wbBuffer[FindFreeWbSlot()] = new WbEntry { LineBase = lineBase, Data = data, };
            _wbCount++;
        }
        else {
            for (var i = 0; i < _blockSize; i++) _backing.Write(lineBase + (ulong)i, _blocks[set][way][i], 1);
            if (chargeStall) _pendingStalls += MissLatency;
        }

        _dirty[set][way] = false;
        DirtyEvictions++;
    }

    private void FillBlock(int set, int way, ulong address, bool chargeWritebackStall = true) {
        if (_tags[set][way] is { } existingTag) FlushDirtyLine(set, way, existingTag, chargeWritebackStall, true);

        ulong lineBase = address & ~(ulong)_offsetMask;
        bool fromWb = _wbBuffer != null && TryForwardFromWbBuffer(lineBase, _blocks[set][way]);
        if (!fromWb)
            for (var i = 0; i < _blockSize; i++)
                _blocks[set][way][i] = (byte)_backing.Read(lineBase + (ulong)i, 1);

        Decompose(address, out _, out ulong tag);
        if (_tags[set][way] is { } oldTag) {
            Evictions++;
            DropInFlightPrefetch((oldTag << (_offsetBits + _indexBits)) | ((ulong)set << _offsetBits));
        }

        _tags[set][way] = tag;
        // A line forwarded from the WB buffer was dirty and hasn't reached backing yet.
        if (_dirty != null) _dirty[set][way] = fromWb;
        _policy.SetPendingAddress(tag, _lastRequestPc);
        _policy.RecordInstall(set, way);
    }

    // ── Write-back buffer helpers ────────────────────────────────────────────

    // Forwards the line at lineBase from the WB buffer into dest, consuming the slot.
    private bool TryForwardFromWbBuffer(ulong lineBase, byte[] dest) {
        for (var i = 0; i < _wbCapacity; i++)
            if (_wbBuffer![i].Data != null && _wbBuffer[i].LineBase == lineBase) {
                Buffer.BlockCopy(_wbBuffer[i].Data!, 0, dest, 0, _blockSize);
                _wbBuffer[i] = default(WbEntry);
                _wbCount--;
                WbDrains++;
                return true;
            }

        return false;
    }

    // Drains the WB buffer entry for lineBase synchronously to backing (without stall charge).
    // Used before NWA backing writes and cross-boundary stores to prevent later drain from
    // overwriting the newly written data.
    private void DrainWbEntryForAddress(ulong lineBase) {
        for (var i = 0; i < _wbCapacity; i++) {
            if (_wbBuffer![i].Data == null || _wbBuffer[i].LineBase != lineBase) continue;
            for (var j = 0; j < _blockSize; j++) _backing.Write(lineBase + (ulong)j, _wbBuffer[i].Data![j], 1);
            _wbBuffer[i] = default(WbEntry);
            _wbCount--;
            WbDrains++;
            return;
        }
    }

    // Discards a WB buffer entry without writing to backing (used by Load, which overwrites backing).
    private void DiscardWbEntryForAddress(ulong lineBase) {
        for (var i = 0; i < _wbCapacity; i++) {
            if (_wbBuffer![i].Data == null || _wbBuffer[i].LineBase != lineBase) continue;
            _wbBuffer[i] = default(WbEntry);
            _wbCount--;
            return;
        }
    }

    // Drains the oldest occupied WB buffer slot to backing without charging a stall.
    private void DrainWbOldestSync() {
        for (var i = 0; i < _wbCapacity; i++) {
            if (_wbBuffer![i].Data == null) continue;
            ulong lb = _wbBuffer[i].LineBase;
            for (var j = 0; j < _blockSize; j++) _backing.Write(lb + (ulong)j, _wbBuffer[i].Data![j], 1);
            _wbBuffer[i] = default(WbEntry);
            _wbCount--;
            WbDrains++;
            return;
        }
    }

    private int FindFreeWbSlot() {
        for (var i = 0; i < _wbCapacity; i++)
            if (_wbBuffer![i].Data == null)
                return i;
        throw new InvalidOperationException("WB buffer unexpectedly full.");
    }

    /// <summary>
    /// Drains one write-back buffer entry to backing per call. Must be called once per
    /// simulated cycle; a no-op when the buffer is empty or disabled.
    /// </summary>
    public void TickWb() {
        if (_wbBuffer == null || _wbCount == 0) return;
        DrainWbOldestSync();
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
            if (_requirePcOnHit) _policy.RecordHitPc(set, way, tag, _lastRequestPc);
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
        if (_writePolicy == WritePolicyKind.WriteThrough) _backing.Write(address, value, bytes);

        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) {
            // Cross-boundary write bypasses the cache entirely.
            // For write-back: flush dirty overlapping lines AND any WB buffer entries first
            // (both synchronously, without defer — a deferred drain after the backing write
            // would overwrite the store's bytes), then write to backing, then invalidate.
            ulong end = address + (ulong)bytes;
            if (_writePolicy == WritePolicyKind.WriteBack) {
                for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockSize) {
                    if (_wbBuffer != null) DrainWbEntryForAddress(a);
                    Decompose(a, out int s, out ulong t);
                    for (var w = 0; w < _ways; w++)
                        if (_tags[s][w] == t)
                            FlushDirtyLine(s, w, t, true); // deferToBuffer=false
                }

                _backing.Write(address, value, bytes);
            }

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
            if (_requirePcOnHit) _policy.RecordHitPc(set, way, tag, _lastRequestPc);
            _policy.RecordHit(set, way);
            ChargeInFlightPrefetch(address);
            WriteBytes(_blocks[set][way], offset, value, bytes);
            if (_dirty != null) _dirty[set][way] = true; // write-back: mark dirty on hit
        }
        else {
            LastAccessWasHit = false;
            Misses++;
            if (_writeMissPolicy == WriteMissPolicyKind.WriteAllocate) {
                _pendingStalls += MissLatency;
                int evict = _policy.ChooseVictim(set);
                _policy.SetPendingSignature(_usePcSignature ? _lastRequestPc : address >> _offsetBits);
                FillBlock(set, evict, address); // flushes any dirty victim
                WriteBytes(_blocks[set][evict], offset, value, bytes);
                if (_dirty != null) _dirty[set][evict] = true;
            }
            else {
                // No-write-allocate: write to backing (if not already done) and skip line install.
                // If the WB buffer holds dirty data for this line, drain it first — otherwise the
                // deferred drain would later overwrite the bytes we're about to write to backing.
                if (_writePolicy == WritePolicyKind.WriteBack) {
                    if (_wbBuffer != null) DrainWbEntryForAddress(address & ~(ulong)_offsetMask);
                    _backing.Write(address, value, bytes);
                }

                _pendingStalls += MissLatency;
            }
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        _backing.Load(address, data);
        // Invalidate cache lines and WB buffer entries that overlap the loaded region.
        // Dirty data is discarded: Load overwrites backing, making any pending dirty data stale.
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockSize) {
            if (_wbBuffer != null) DiscardWbEntryForAddress(a);
            Decompose(a, out int set, out ulong tag);
            for (var w = 0; w < _ways; w++)
                if (_tags[set][w] == tag) {
                    if (_dirty != null) _dirty[set][w] = false;
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
            FillBlock(set, evict, address, false);
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
            bool dirty = _dirty != null && _dirty[s][w];
            lines[idx++] = new CacheLine(s, w, valid, tag, _policy.GetMetadata(s, w), blockCopy, dirty);
        }

        return lines;
    }
}