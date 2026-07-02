using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// N-way set-associative write-back cache implementing the MOESIF coherence protocol
/// with cache-to-cache supply: a read miss snooping a peer that holds the line in
/// M, O, E, or F receives the block directly from that peer instead of backing memory.
/// A dirty supplier keeps writeback responsibility in the Owned state rather than
/// writing back to backing; a clean supply passes the Forward role to the requester,
/// so exactly one sharer of a clean line keeps answering later misses while memory
/// stays silent.
/// Must be registered with a shared <see cref="MoesifBus"/> that connects it to other caches
/// sharing the same physical address space.
/// Policy: write-back, write-allocate, LRU replacement.
/// </summary>
public sealed class MoesifCache : IMemory {
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
    private readonly MoesifState[][] _state;

    private long _pendingStalls;

    public int MissLatency { get; }

    /// <summary>Stall cycles charged when a read miss is filled cache-to-cache by a peer
    /// instead of from backing memory. Defaults to <see cref="MissLatency"/>.</summary>
    public int PeerSupplyLatency { get; }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long Writebacks { get; private set; }

    /// <summary>Read and write misses that were filled cache-to-cache by a peer
    /// (MOESIF supply on reads, read-for-ownership forwarding on writes).</summary>
    public long PeerSupplies { get; private set; }

    public int Sets => _tags.Length;
    public int Ways => _ways;
    public int BlockBytes => _blockSize;

    public MoesifCache(
        IBus bus,
        int capacityBytes,
        int ways,
        int blockSizeBytes,
        int missLatency = 0,
        int peerSupplyLatency = -1
    ) {
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
        PeerSupplyLatency = peerSupplyLatency < 0 ? missLatency : peerSupplyLatency;

        _offsetBits = BitOperations.Log2((uint)blockSizeBytes);
        _indexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _lruAge = new int[sets][];
        _state = new MoesifState[sets][];

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[_ways];
            _blocks[s] = new byte[_ways][];
            _lruAge[s] = new int[_ways];
            _state[s] = new MoesifState[_ways]; // all Invalid
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

    /// <summary>M and O both hold data newer than backing memory.</summary>
    private static bool IsDirty(MoesifState s) => s is MoesifState.Modified or MoesifState.Owned;

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
            if (IsDirty(_state[set][way])) WriteBackBlock(set, way);
            _bus.Evicted(this, lineBase);
            Evictions++;
        }

        _tags[set][way] = null;
        _state[set][way] = MoesifState.Invalid;
        return way;
    }

    private void LocalInvalidate(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (IsDirty(_state[set][way])) WriteBackBlock(set, way);
        _state[set][way] = MoesifState.Invalid;
        _tags[set][way] = null;
        _bus.Evicted(this, lineBase);
    }

    // ── IMemory cache-maintenance (cbo.inval / cbo.clean / cbo.flush) ───────

    public void InvalidateLine(ulong address) => LocalInvalidate(LineBase(address));

    public void CleanLine(ulong address) {
        ulong lineBase = LineBase(address);
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0 || !IsDirty(_state[set][way])) return;
        WriteBackBlock(set, way);
        // M: clean and still held exclusively. O: peers hold S copies, ownership
        // returns to memory — the line is now an ordinary Shared copy.
        _state[set][way] = _state[set][way] == MoesifState.Modified
            ? MoesifState.Exclusive
            : MoesifState.Shared;
    }

    public void FlushLine(ulong address) => LocalInvalidate(LineBase(address));

    // ── Snooping (invoked by MoesifBus on behalf of remote caches) ────────────

    /// <summary>
    /// Another cache is doing a read. M→O (supply, no writeback), O→O (supply),
    /// E→S and F→S (supply clean; the requester takes over the Forward role),
    /// S→S (no supply). A supplier copies its block into <paramref name="dest"/>
    /// so the requester fills cache-to-cache instead of from backing memory.
    /// </summary>
    internal SnoopResult SnoopRead(ulong lineBase, Span<byte> dest) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return SnoopResult.Miss;
        switch (_state[set][way]) {
            case MoesifState.Modified:
            case MoesifState.Owned:
                _blocks[set][way].CopyTo(dest);
                _state[set][way] = MoesifState.Owned;
                return SnoopResult.SuppliedOwned;
            case MoesifState.Exclusive:
            case MoesifState.Forward:
                _blocks[set][way].CopyTo(dest);
                _state[set][way] = MoesifState.Shared;
                return SnoopResult.Supplied;
            default: return SnoopResult.Shared;
        }
    }

    /// <summary>Another cache wants exclusive access. M/O→writeback+I, E/S/F→I.</summary>
    internal void SnoopInvalidate(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (IsDirty(_state[set][way])) WriteBackBlock(set, way);
        _state[set][way] = MoesifState.Invalid;
        _tags[set][way] = null;
    }

    /// <summary>
    /// Another cache wants exclusive access and will install the line as Modified
    /// (read-for-ownership). M/O/E/F holders forward the block into <paramref name="dest"/>
    /// and invalidate <em>without</em> writing back — the requester's M copy becomes
    /// authoritative. S holders just invalidate (memory or the responder supplies).
    /// Returns true if <paramref name="dest"/> was filled.
    /// </summary>
    internal bool SnoopInvalidateForward(ulong lineBase, Span<byte> dest) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return false;
        bool supply = _state[set][way] != MoesifState.Shared;
        if (supply) _blocks[set][way].CopyTo(dest);
        _state[set][way] = MoesifState.Invalid;
        _tags[set][way] = null;
        return supply;
    }

    /// <summary>
    /// Writes a dirty (M/O) line to backing without changing state. Used by
    /// <see cref="IBus.BusSyncToBacking"/> so that block-boundary-crossing accesses
    /// reading backing directly observe current data.
    /// </summary>
    internal void SnoopWriteback(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0 || !IsDirty(_state[set][way])) return;
        WriteBackBlock(set, way);
    }

    // ── DeferredBus phase-2 hooks ────────────────────────────────────────────

    /// <summary>
    /// Writes all dirty (M/O) lines directly to backing memory without evicting them or
    /// updating coherence state.  Called by <c>DeferredBus.Drain()</c> after draining
    /// the op queue so that backing is authoritative at the start of the next tick's
    /// phase-1, allowing phase-1 fills to read correct data even when this cache holds
    /// dirty lines.
    /// </summary>
    internal void FlushToBacking() {
        for (var s = 0; s < _tags.Length; s++)
        for (var w = 0; w < _ways; w++) {
            if (!IsDirty(_state[s][w]) || !_tags[s][w].HasValue) continue;
            ulong lineBase = ReconstructLineBase(s, _tags[s][w]!.Value);
            _bus.Backing.Load(lineBase, _blocks[s][w]);
        }
    }

    /// <summary>
    /// Corrects the phase-1 Exclusive install after the phase-2 <see cref="IBus.BusRead"/>
    /// replay reveals that peers held the line: E (or S, when an earlier replayed op
    /// already snooped this line down) becomes F for clean responses and S for a dirty
    /// supply — the same state sequential execution would have installed. No-op for M/O
    /// (a same-tick intra-hart write must not be downgraded) and for invalidated lines.
    /// Called only by <c>DeferredBus.Drain()</c>.
    /// </summary>
    internal void UpdateCoherenceState(ulong lineBase, BusReadResponse response) {
        if (response == BusReadResponse.NoSharers) return;
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_state[set][way] is not (MoesifState.Exclusive or MoesifState.Shared or MoesifState.Forward)) return;
        _state[set][way] = response == BusReadResponse.SuppliedDirty
            ? MoesifState.Shared
            : MoesifState.Forward;
    }

    /// <summary>
    /// Re-reads the cache line from backing after a cross-hart writeback has updated
    /// backing memory during phase 2.  Skips dirty (M/O) lines to avoid clobbering
    /// intra-hart writes from the same phase-1 tick.
    /// Called only by <c>DeferredBus.Drain()</c>.
    /// </summary>
    internal void RefillFromBacking(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (IsDirty(_state[set][way])) return;
        FillFromBacking(set, way, lineBase);
    }

    /// <summary>
    /// Overwrites the cache line with a block supplied cache-to-cache during phase 2
    /// (the peer held it M/O and did not write back to backing).  Skips dirty (M/O)
    /// lines to avoid clobbering intra-hart writes from the same phase-1 tick.
    /// Called only by <c>DeferredBus.Drain()</c>.
    /// </summary>
    internal void RefillFromSupply(ulong lineBase, ReadOnlySpan<byte> block) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (IsDirty(_state[set][way])) return;
        block.CopyTo(_blocks[set][way]);
    }

    // ── IMemory ──────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockSize) {
            // Cross-boundary: force dirty holders (including this cache) to write back,
            // then read backing directly. States and the directory are left untouched.
            ulong end = address + (ulong)bytes;
            for (ulong a = LineBase(address); a < end; a += (ulong)_blockSize) _bus.BusSyncToBacking(a);
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
        ulong lineBase = LineBase(address);
        int victimWay = EvictWay(set);
        BusReadResponse response = _bus.BusRead(this, lineBase, _blocks[set][victimWay]);
        if (response is BusReadResponse.SuppliedClean or BusReadResponse.SuppliedDirty) {
            PeerSupplies++;
            _pendingStalls += PeerSupplyLatency;
        }
        else {
            FillFromBacking(set, victimWay, lineBase);
            _pendingStalls += MissLatency;
        }

        _tags[set][victimWay] = tag;
        _state[set][victimWay] = response switch {
            BusReadResponse.NoSharers => MoesifState.Exclusive,
            // Dirty line: the O owner keeps forwarding duty; we are a plain sharer.
            BusReadResponse.SuppliedDirty => MoesifState.Shared,
            // Clean line: the requester becomes the designated forwarder — either the
            // F role migrated from the E/F supplier, or memory supplied because no
            // forwarder existed and the newest sharer takes the role.
            _ => MoesifState.Forward,
        };
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
                case MoesifState.Shared:
                case MoesifState.Owned:
                case MoesifState.Forward:
                    _bus.BusReadInvalidate(this, lineBase); // S/O/F→M: snoop all peers + cancel reservations
                    break;
                case MoesifState.Exclusive:
                    _bus.BusSilentUpgrade(lineBase); // E→M: no snoop needed, but cancel reservations
                    break;
            }

            _state[set][way] = MoesifState.Modified;
            TouchLru(set, way);
            WriteBytes(_blocks[set][way], offset, value, bytes);
        }
        else {
            // Write-allocate: RFO — an M/O/E holder forwards the block with the
            // invalidation; otherwise fetch from backing. Install as M, write into it.
            Misses++;
            int victimWay = EvictWay(set);
            if (_bus.BusReadForOwnership(this, lineBase, _blocks[set][victimWay])) {
                PeerSupplies++;
                _pendingStalls += PeerSupplyLatency;
            }
            else {
                FillFromBacking(set, victimWay, lineBase);
                _pendingStalls += MissLatency;
            }

            _tags[set][victimWay] = tag;
            _state[set][victimWay] = MoesifState.Modified;
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

    /// <summary>Returns the MOESIF state of the cache line covering <paramref name="address"/>.</summary>
    public MoesifState StateOf(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        return way >= 0 ? _state[set][way] : MoesifState.Invalid;
    }

    /// <summary>
    /// Writes all dirty (M/O) lines back to backing without evicting them.
    /// Use for testing or teardown to make backing memory consistent with the cache.
    /// </summary>
    public void Flush() {
        int sets = _tags.Length;
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < _ways; w++)
            if (IsDirty(_state[s][w]))
                WriteBackBlock(s, w);
    }
}