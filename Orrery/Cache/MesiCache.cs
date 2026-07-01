using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// N-way set-associative write-back cache implementing the MESI coherence protocol.
/// Must be registered with a shared <see cref="MesiBus"/> that connects it to other caches
/// sharing the same physical address space.
/// Policy: write-back, write-allocate, LRU replacement.
/// </summary>
public sealed class MesiCache : IMemory {
    private readonly IBus _bus;
    private readonly int _ways;
    private readonly int _blockSize;
    private readonly int _offsetMask;
    private readonly int _indexMask;
    private readonly int _offsetBits;
    private readonly int _indexBits;

    private readonly ulong?[][] _tags;
    private readonly byte[][][] _blocks;
    private readonly int[][] _lruAge;
    private readonly MesiState[][] _state;

    private long _pendingStalls;

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long Writebacks { get; private set; }

    public int Sets => _tags.Length;
    public int Ways => _ways;
    public int BlockBytes => _blockSize;

    public MesiCache(IBus bus, int capacityBytes, int ways, int blockSizeBytes, int missLatency = 0) {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        if (!BitOperations.IsPow2(capacityBytes) || !BitOperations.IsPow2(ways)
                                                 || !BitOperations.IsPow2(blockSizeBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");

        _bus = bus;
        _ways = ways;
        _blockSize = blockSizeBytes;
        int sets = capacityBytes / (ways * blockSizeBytes);
        if (sets < 1) throw new ArgumentException("Cache configuration produces 0 sets.");
        MissLatency = missLatency;

        _offsetBits = BitOperations.Log2((uint)blockSizeBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _lruAge = new int[sets][];
        _state = new MesiState[sets][];

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_ways];
            _blocks[s] = new byte[_ways][];
            _lruAge[s] = new int[_ways];
            _state[s] = new MesiState[_ways]; // all Invalid
            for (var w = 0; w < _ways; w++) {
                _blocks[s][w] = new byte[_blockSize];
                _lruAge[s][w] = w;
            }
        }

        bus.Register(this);
    }

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

    private ulong LineBase(ulong address) => address & ~(ulong)_offsetMask;

    private ulong ReconstructLineBase(int set, ulong tag) =>
        (tag << (_offsetBits + _indexBits)) | ((ulong)set << _offsetBits);

    // ── Block fill / writeback ───────────────────────────────────────────────

    private void FillFromBacking(int set, int way, ulong lineBase) {
        for (var i = 0; i < _blockSize; i++) _blocks[set][way][i] = (byte)_bus.Backing.Read(lineBase + (ulong)i, 1);
    }

    private void WriteBackBlock(int set, int way) {
        ulong tag = _tags[set][way]!.Value;
        ulong lineBase = ReconstructLineBase(set, tag);
        _bus.Writeback(lineBase, _blocks[set][way]);
        Writebacks++;
    }

    // ── Eviction ─────────────────────────────────────────────────────────────

    private int EvictWay(int set) {
        int way = LruWay(set);
        if (_tags[set][way].HasValue) {
            ulong lineBase = ReconstructLineBase(set, _tags[set][way]!.Value);
            if (_state[set][way] == MesiState.Modified) WriteBackBlock(set, way);
            _bus.Evicted(this, lineBase);
            Evictions++;
        }

        _tags[set][way] = null;
        _state[set][way] = MesiState.Invalid;
        return way;
    }

    private void LocalInvalidate(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_state[set][way] == MesiState.Modified) WriteBackBlock(set, way);
        _state[set][way] = MesiState.Invalid;
        _tags[set][way] = null;
        _bus.Evicted(this, lineBase);
    }

    // ── IMemory cache-maintenance (cbo.inval / cbo.clean / cbo.flush) ───────

    public void InvalidateLine(ulong address) => LocalInvalidate(LineBase(address));

    public void CleanLine(ulong address) {
        ulong lineBase = LineBase(address);
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0 || _state[set][way] != MesiState.Modified) return;
        WriteBackBlock(set, way);
        _state[set][way] = MesiState.Exclusive; // clean and still owned exclusively
    }

    public void FlushLine(ulong address) => LocalInvalidate(LineBase(address));

    // ── Snooping (invoked by MesiBus on behalf of remote caches) ────────────

    /// <summary>Another cache is doing a read. M→writeback+S, E→S. Returns true if we held the line.</summary>
    internal bool SnoopRead(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return false;
        switch (_state[set][way]) {
            case MesiState.Modified:
                WriteBackBlock(set, way);
                _state[set][way] = MesiState.Shared;
                break;
            case MesiState.Exclusive: _state[set][way] = MesiState.Shared; break;
        }

        return true;
    }

    /// <summary>Another cache wants exclusive access. M→writeback+I, E/S→I.</summary>
    internal void SnoopInvalidate(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_state[set][way] == MesiState.Modified) WriteBackBlock(set, way);
        _state[set][way] = MesiState.Invalid;
        _tags[set][way] = null;
    }

    // ── DeferredBus phase-2 hooks ────────────────────────────────────────────

    /// <summary>
    /// Writes all Modified lines directly to backing memory without evicting them or
    /// updating coherence state.  Called by <c>DeferredBus.Drain()</c> after draining
    /// the op queue so that backing is authoritative at the start of the next tick's
    /// phase-1, allowing phase-1 fills to read correct data even when this cache holds
    /// lines in M state.
    /// </summary>
    internal void FlushToBacking() {
        for (var s = 0; s < _tags.Length; s++)
        for (var w = 0; w < _ways; w++) {
            if (_state[s][w] != MesiState.Modified || !_tags[s][w].HasValue) continue;
            ulong lineBase = ReconstructLineBase(s, _tags[s][w]!.Value);
            _bus.Backing.Load(lineBase, _blocks[s][w]);
        }
    }

    /// <summary>
    /// Corrects E→S after a phase-2 <see cref="IBus.BusRead"/> reveals that a peer
    /// held the line. No-op for M (a same-tick intra-hart write must not be downgraded).
    /// Called only by <c>DeferredBus.Drain()</c>.
    /// </summary>
    internal void UpdateCoherenceState(ulong lineBase, bool shared) {
        if (!shared) return;
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_state[set][way] == MesiState.Exclusive) _state[set][way] = MesiState.Shared;
    }

    /// <summary>
    /// Re-reads the cache line from backing after a cross-hart writeback has updated
    /// backing memory during phase 2.  Skips M-state lines to avoid clobbering
    /// intra-hart writes from the same phase-1 tick.
    /// Called only by <c>DeferredBus.Drain()</c>.
    /// </summary>
    internal void RefillFromBacking(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_state[set][way] == MesiState.Modified) return;
        FillFromBacking(set, way, lineBase);
    }

    // ── IMemory ──────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) {
            // Cross-boundary: force M→writeback in any holder, then read backing directly.
            ulong end = address + (ulong)bytes;
            for (ulong a = LineBase(address); a < end; a += (ulong)_blockSize) _bus.BusRead(this, a);
            return _bus.Backing.Read(address, bytes);
        }

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            Hits++;
            TouchLru(set, way);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        Misses++;
        _pendingStalls += MissLatency;
        ulong lineBase = LineBase(address);
        bool shared = _bus.BusRead(this, lineBase);
        int victimWay = EvictWay(set);
        FillFromBacking(set, victimWay, lineBase);
        _tags[set][victimWay] = tag;
        _state[set][victimWay] = shared ? MesiState.Shared : MesiState.Exclusive;
        TouchLru(set, victimWay);
        return ReadBytes(_blocks[set][victimWay], offset, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) {
            // Cross-boundary: invalidate all caches for each touched line, then write to backing.
            ulong end = address + (ulong)bytes;
            for (ulong a = LineBase(address); a < end; a += (ulong)_blockSize) {
                _bus.BusReadInvalidate(this, a);
                LocalInvalidate(a);
            }

            _bus.Backing.Write(address, value, bytes);
            return;
        }

        ulong lineBase = LineBase(address);
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);

        if (way >= 0) {
            Hits++;
            switch (_state[set][way]) {
                case MesiState.Shared:
                    _bus.BusReadInvalidate(this, lineBase); // S→M: snoop all peers + cancel reservations
                    break;
                case MesiState.Exclusive:
                    _bus.BusSilentUpgrade(lineBase); // E→M: no snoop needed, but cancel reservations
                    break;
            }

            _state[set][way] = MesiState.Modified;
            TouchLru(set, way);
            WriteBytes(_blocks[set][way], offset, value, bytes);
        }
        else {
            // Write-allocate: fetch line, install as M, write into it.
            Misses++;
            _pendingStalls += MissLatency;
            _bus.BusReadInvalidate(this, lineBase);
            int victimWay = EvictWay(set);
            FillFromBacking(set, victimWay, lineBase);
            _tags[set][victimWay] = tag;
            _state[set][victimWay] = MesiState.Modified;
            TouchLru(set, victimWay);
            WriteBytes(_blocks[set][victimWay], offset, value, bytes);
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        // Bulk write directly to backing; invalidate all cached copies in every cache.
        _bus.Backing.Load(address, data);
        ulong end = address + (ulong)data.Length;
        for (ulong a = LineBase(address); a < end; a += (ulong)_blockSize) _bus.BusLoad(a);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private static ulong ReadBytes(byte[] block, int offset, int bytes) {
        ulong result = 0;
        for (var i = 0; i < bytes; i++) result |= (ulong)block[offset + i] << (i * 8);
        return result;
    }

    private static void WriteBytes(byte[] block, int offset, ulong value, int bytes) {
        for (var i = 0; i < bytes; i++) block[offset + i] = (byte)(value >> (i * 8));
    }

    // ── Inspection ───────────────────────────────────────────────────────────

    /// <summary>Returns the MESI state of the cache line covering <paramref name="address"/>.</summary>
    public MesiState StateOf(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        return way >= 0 ? _state[set][way] : MesiState.Invalid;
    }

    /// <summary>
    /// Writes all Modified lines back to backing without evicting them.
    /// Use for testing or teardown to make backing memory consistent with the cache.
    /// </summary>
    public void Flush() {
        int sets = _tags.Length;
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < _ways; w++)
            if (_state[s][w] == MesiState.Modified)
                WriteBackBlock(s, w);
    }
}