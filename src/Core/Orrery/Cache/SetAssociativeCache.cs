using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

public sealed record CacheLine(int Set, int Way, bool Valid, ulong Tag, int LruAge, byte[] Block, bool Dirty = false);

/// <summary>
///     N-way set-associative cache implementing IMemory.
///     Write policy and write-miss policy are configurable; default is write-through + no-write-allocate.
///     Cache miss does not block — it records a penalty in PendingStalls
///     that the caller drains to inject idle cycles into the pipeline.
/// </summary>
public sealed class SetAssociativeCache : IMemory {
    private readonly IMemory _backing;
    private readonly byte[][][] _blocks; // [set][way][offset]
    private readonly bool[][]? _dirty;   // non-null only in WriteBack mode
    private readonly int _indexMask;

    // Realistic prefetch latency: lines installed by Prefetch() that have not yet
    // "arrived". A demand hit on one of these pays the remaining countdown instead
    // of zero. Empty (and never touched) when PrefetchLatency = 0.
    private readonly List<(ulong LineBase, int Remaining)> _inFlightPrefetches = [];
    private readonly MshrEntry[]? _mshrs;
    private readonly int _offsetMask;
    private readonly IReplacementPolicy _policy;
    private readonly bool _requirePcOnHit; // Hawkeye needs OPTgen fed on every hit

    private readonly ulong?[][] _tags; // [set][way]: null = invalid
    private readonly bool _usePcSignature;

    private readonly WbEntry[]? _wbBuffer;
    private ulong _lastRequestPc;

    // The cache directly inside this one (closer to the CPU), if any has been attached via
    // AttachInner. Used to enforce InclusionPolicy toward that level.
    private SetAssociativeCache? _innerCache;

    private long _pendingStalls;

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Total cache size in bytes. Must be a power of 2.</param>
    /// <param name="ways">Associativity. Must be a power of 2.</param>
    /// <param name="blockSizeBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    /// <param name="prefetchLatency">
    ///     Cycles until a prefetched line is usable
    ///     (0 = instant/free, the idealized model). While in flight, a demand hit on the
    ///     line pays the remaining countdown; the caller must call <see cref="TickPrefetch" />
    ///     once per cycle to advance the countdowns.
    /// </param>
    /// <param name="replacementPolicy">Cache replacement policy. Defaults to LRU.</param>
    /// <param name="tagLatency">
    ///     Cycles to look up the tag array (informational — used by pipelines
    ///     to compute hit latency; does not affect <see cref="_pendingStalls" />).
    /// </param>
    /// <param name="dataLatency">
    ///     Cycles to read the data array (informational — combined with
    ///     <paramref name="accessMode" /> to compute <see cref="HitLatency" />).
    /// </param>
    /// <param name="writePolicy">
    ///     Write-hit policy: <see cref="WritePolicyKind.WriteThrough" /> stores
    ///     immediately propagate to backing; <see cref="WritePolicyKind.WriteBack" /> keeps stores in the
    ///     cache and flushes dirty lines to backing only on eviction.
    /// </param>
    /// <param name="writeMissPolicy">
    ///     Write-miss policy: <see cref="WriteMissPolicyKind.NoWriteAllocate" />
    ///     writes directly to backing without installing a line; <see cref="WriteMissPolicyKind.WriteAllocate" />
    ///     installs the line (paying <paramref name="missLatency" />) then writes into it.
    /// </param>
    /// <param name="wbCapacity">
    ///     Write-back buffer capacity in lines (0 = disabled). Only active in
    ///     write-back mode. Dirty evicted lines go into the buffer and drain asynchronously (one line per
    ///     <see cref="TickWb" /> call); a stall is charged only when the buffer is full.
    /// </param>
    /// <param name="mshrCount">
    ///     MSHR (Miss Status Holding Register) capacity in slots (0 = unlimited,
    ///     legacy behaviour). Each demand miss allocates a slot for <see cref="MissLatency" /> cycles; a
    ///     subsequent demand hit on the same in-flight line charges only the remaining countdown (merge /
    ///     hit-under-miss). When all slots are occupied a new unique-line miss pays the minimum remaining
    ///     countdown plus <see cref="MissLatency" />. Call <see cref="TickMshr" /> once per simulated cycle
    ///     to advance the countdowns.
    /// </param>
    /// <param name="accessMode">
    ///     Tag/data access ordering: <see cref="CacheAccessModeKind.Parallel" /> (default) computes
    ///     <see cref="HitLatency" /> as <c>max(tagLatency, dataLatency)</c>; <see cref="CacheAccessModeKind.Sequential" />
    ///     probes tags first and reads only the matching way, computing <see cref="HitLatency" /> as
    ///     <c>tagLatency + dataLatency</c> — typical of large lower-level caches.
    /// </param>
    /// <param name="inclusionPolicy">
    ///     This level's inclusion policy toward whatever cache is attached as its inner level via
    ///     <see cref="AttachInner" />. No effect until a inner cache is attached. See
    ///     <see cref="InclusionPolicyKind" />.
    /// </param>
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
        int wbCapacity = 0,
        int mshrCount = 0,
        CacheAccessModeKind accessMode = CacheAccessModeKind.Parallel,
        InclusionPolicyKind inclusionPolicy = InclusionPolicyKind.Nine
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
        Ways = ways;
        BlockBytes = blockSizeBytes;
        int sets = capacityBytes / (ways * blockSizeBytes);
        TagLatency = tagLatency;
        DataLatency = dataLatency;
        AccessMode = accessMode;
        MissLatency = missLatency;
        PrefetchLatency = prefetchLatency;
        WritePolicy = writePolicy;
        WriteMissPolicy = writeMissPolicy;
        WbCapacity = writePolicy == WritePolicyKind.WriteBack ? Math.Max(0, wbCapacity) : 0;
        InclusionPolicy = inclusionPolicy;

        OffsetBits = BitOperations.Log2((uint)blockSizeBytes);
        IndexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        MshrCount = Math.Max(0, mshrCount);
        _mshrs = MshrCount > 0 ? new MshrEntry[MshrCount] : null;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        _dirty = writePolicy == WritePolicyKind.WriteBack ? new bool[sets][] : null;
        _wbBuffer = WbCapacity > 0 ? new WbEntry[WbCapacity] : null;

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[Ways];
            _blocks[s] = new byte[Ways][];
            if (_dirty != null) _dirty[s] = new bool[Ways];
            for (var w = 0; w < Ways; w++) _blocks[s][w] = new byte[BlockBytes];
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

    public int TagLatency { get; }
    public int DataLatency { get; }
    public CacheAccessModeKind AccessMode { get; }

    public int HitLatency => AccessMode == CacheAccessModeKind.Sequential
        ? TagLatency + DataLatency
        : Math.Max(TagLatency, DataLatency);

    public int MissLatency { get; }
    public int PrefetchLatency { get; }
    public WritePolicyKind WritePolicy { get; }

    public WriteMissPolicyKind WriteMissPolicy { get; }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }
    public long WbDrains { get; private set; }
    public int WbCapacity { get; }

    public int WbOccupancy { get; private set; }

    public long Prefetches { get; private set; }
    public long PrefetchRedundant { get; private set; }
    public long LatePrefetchHits { get; private set; }
    public ulong? LastAccessAddress { get; private set; }
    public bool LastAccessWasHit { get; private set; }
    public int MshrCount { get; }

    public int MshrOccupancy {
        get {
            if (_mshrs == null) return 0;
            var n = 0;
            for (var i = 0; i < MshrCount; i++)
                if (_mshrs[i].Remaining > 0)
                    n++;
            return n;
        }
    }

    public long MshrMerges { get; private set; }
    public long MshrCapacityStalls { get; private set; }

    public InclusionPolicyKind InclusionPolicy { get; }

    /// <summary>Lines dropped here because the attached outer level (Inclusive) evicted them.</summary>
    public long BackInvalidations { get; private set; }

    /// <summary>Lines installed here because the attached inner level (Exclusive) evicted them.</summary>
    public long VictimInserts { get; private set; }

    /// <summary>
    ///     Registers <paramref name="inner" /> as the cache directly inside this one (closer to
    ///     the CPU) for the purposes of <see cref="InclusionPolicy" />. Call after constructing
    ///     both caches, once the chain is fully wired.
    /// </summary>
    public void AttachInner(SetAssociativeCache inner) {
        if (InclusionPolicy == InclusionPolicyKind.Exclusive && BlockBytes != inner.BlockBytes)
            throw new ArgumentException(
                "Exclusive inclusion policy requires equal block sizes between the two levels " +
                $"(this level: {BlockBytes}, inner level: {inner.BlockBytes})."
            );

        _innerCache = inner;
    }

    /// <summary>Number of prefetched lines still in flight (counts against MSHR capacity).</summary>
    public int InFlightPrefetchCount => _inFlightPrefetches.Count;

    // ── Inspection ───────────────────────────────────────────────────────────

    public int Sets => _tags.Length;
    public int Ways { get; }

    public int BlockBytes { get; }

    public int OffsetBits { get; }

    public int IndexBits { get; }

    // ── IMemory ──────────────────────────────────────────────────────────────

    public void SetRequestPc(ulong pc) {
        _lastRequestPc = pc;
        _backing.SetRequestPc(pc);
    }

    public ulong Read(ulong address, int bytes) {
        // Access crossing a block boundary bypasses the cache.
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > BlockBytes) return _backing.Read(address, bytes);

        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        LastAccessAddress = address;
        if (way >= 0) {
            LastAccessWasHit = true;
            Hits++;
            if (_requirePcOnHit) _policy.RecordHitPc(set, way, tag, _lastRequestPc);
            _policy.RecordHit(set, way);
            ChargeInFlightMshr(address);
            ChargeInFlightPrefetch(address);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        LastAccessWasHit = false;
        Misses++;
        ulong readLineBase = address & ~(ulong)_offsetMask;
        _pendingStalls += ChargeAndAllocateMshr(readLineBase);
        int evict = _policy.ChooseVictim(set);
        _policy.SetPendingSignature(_usePcSignature ? _lastRequestPc : address >> OffsetBits);
        FillBlock(set, evict, address);
        return ReadBytes(_blocks[set][evict], offset, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        if (WritePolicy == WritePolicyKind.WriteThrough) _backing.Write(address, value, bytes);

        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > BlockBytes) {
            // Cross-boundary write bypasses the cache entirely.
            // For write-back: flush dirty overlapping lines AND any WB buffer entries first
            // (both synchronously, without defer — a deferred drain after the backing write
            // would overwrite the store's bytes), then write to backing, then invalidate.
            ulong end = address + (ulong)bytes;
            if (WritePolicy == WritePolicyKind.WriteBack) {
                for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)BlockBytes) {
                    if (_wbBuffer != null) DrainWbEntryForAddress(a);
                    Decompose(a, out int s, out ulong t);
                    for (var w = 0; w < Ways; w++)
                        if (_tags[s][w] == t)
                            FlushDirtyLine(s, w, t, true); // deferToBuffer=false
                }

                _backing.Write(address, value, bytes);
            }

            for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)BlockBytes) {
                Decompose(a, out int s, out ulong t);
                for (var w = 0; w < Ways; w++)
                    if (_tags[s][w] == t) {
                        _tags[s][w] = null;
                        DropInFlightPrefetch(a);
                        DropInFlightMshr(a);
                        PropagateInvalidate(a, BlockBytes);
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
            ChargeInFlightMshr(address);
            ChargeInFlightPrefetch(address);
            WriteBytes(_blocks[set][way], offset, value, bytes);
            if (_dirty != null) _dirty[set][way] = true; // write-back: mark dirty on hit
        }
        else {
            LastAccessWasHit = false;
            Misses++;
            if (WriteMissPolicy == WriteMissPolicyKind.WriteAllocate) {
                ulong writeLineBase = address & ~(ulong)_offsetMask;
                _pendingStalls += ChargeAndAllocateMshr(writeLineBase);
                int evict = _policy.ChooseVictim(set);
                _policy.SetPendingSignature(_usePcSignature ? _lastRequestPc : address >> OffsetBits);
                FillBlock(set, evict, address); // flushes any dirty victim
                WriteBytes(_blocks[set][evict], offset, value, bytes);
                if (_dirty != null) _dirty[set][evict] = true;
            }
            else {
                // No-write-allocate: write to backing (if not already done) and skip line install.
                // If the WB buffer holds dirty data for this line, drain it first — otherwise the
                // deferred drain would later overwrite the bytes we're about to write to backing.
                if (WritePolicy == WritePolicyKind.WriteBack) {
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
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)BlockBytes) {
            if (_wbBuffer != null) DiscardWbEntryForAddress(a);
            Decompose(a, out int set, out ulong tag);
            for (var w = 0; w < Ways; w++)
                if (_tags[set][w] == tag) {
                    if (_dirty != null) _dirty[set][w] = false;
                    _tags[set][w] = null;
                    DropInFlightPrefetch(a);
                    DropInFlightMshr(a);
                    PropagateInvalidate(a, BlockBytes);
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

    private void Decompose(ulong address, out int set, out ulong tag) {
        set = (int)((address >> OffsetBits) & (ulong)_indexMask);
        tag = address >> (OffsetBits + IndexBits);
    }

    private int FindWay(int set, ulong tag) {
        for (var w = 0; w < Ways; w++)
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
        ulong lineBase = (tag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);

        if (deferToBuffer && _wbBuffer != null) {
            // Buffer full: synchronous drain of oldest entry, charge stall for the wait.
            if (WbOccupancy >= WbCapacity) {
                DrainWbOldestSync();
                if (chargeStall) _pendingStalls += MissLatency;
            }

            var data = new byte[BlockBytes];
            Buffer.BlockCopy(_blocks[set][way], 0, data, 0, BlockBytes);
            _wbBuffer[FindFreeWbSlot()] = new WbEntry { LineBase = lineBase, Data = data, };
            WbOccupancy++;
        }
        else {
            for (var i = 0; i < BlockBytes; i++) _backing.Write(lineBase + (ulong)i, _blocks[set][way][i], 1);
            if (chargeStall) _pendingStalls += MissLatency;
        }

        _dirty[set][way] = false;
        DirtyEvictions++;
    }

    private void FillBlock(int set, int way, ulong address, bool chargeWritebackStall = true) {
        if (_tags[set][way] is { } existingTag) {
            ulong evictedBase = (existingTag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);

            // Inclusive: our own eviction must also drop the inner level's copy, so it never
            // outlives ours. Do this first so any dirty inner data folds into our (still valid)
            // copy before we flush/hand it off below. No-op unless InclusionPolicy is Inclusive.
            BackInvalidateInner(set, way, evictedBase);

            // Exclusive: the level below acts as our victim cache, so the evicted line (clean or
            // dirty) is handed off there instead of just flushed/discarded.
            if (_backing is SetAssociativeCache { InclusionPolicy: InclusionPolicyKind.Exclusive, } outer)
                outer.InsertVictim(evictedBase, _blocks[set][way], _dirty != null && _dirty[set][way]);
            else
                FlushDirtyLine(set, way, existingTag, chargeWritebackStall, true);
        }

        ulong lineBase = address & ~(ulong)_offsetMask;
        bool fromWb = _wbBuffer != null && TryForwardFromWbBuffer(lineBase, _blocks[set][way]);
        if (!fromWb)
            for (var i = 0; i < BlockBytes; i++)
                _blocks[set][way][i] = (byte)_backing.Read(lineBase + (ulong)i, 1);

        Decompose(address, out _, out ulong tag);
        if (_tags[set][way] is { } oldTag) {
            ulong evictedBase = (oldTag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);
            Evictions++;
            DropInFlightPrefetch(evictedBase);
            DropInFlightMshr(evictedBase);
        }

        _tags[set][way] = tag;
        // A line forwarded from the WB buffer was dirty and hasn't reached backing yet.
        if (_dirty != null) _dirty[set][way] = fromWb;
        _policy.SetPendingAddress(tag, _lastRequestPc);
        _policy.RecordInstall(set, way);

        // Exclusive: this line just became resident here, so it must not also remain resident in
        // the level below (a line lives in exactly one of the two levels).
        if (_backing is SetAssociativeCache { InclusionPolicy: InclusionPolicyKind.Exclusive, } exclusiveOuter)
            exclusiveOuter.RemoveResident(lineBase);
    }

    /// <summary>
    ///     Called when this cache is evicting its own line, currently at <paramref name="set" />/
    ///     <paramref name="way" /> and covering <c>[rangeBase, rangeBase + BlockBytes)</c>: drops
    ///     every attached-inner-cache line overlapping that range, folding any dirty inner data
    ///     into our (still-resident, about-to-be-handed-off) copy first — the inner copy may be
    ///     more current than ours. The inner cache's block size may be smaller than ours, in which
    ///     case several of its lines fall inside our one block (both are powers of 2, so the range
    ///     always divides evenly). No-op unless <see cref="InclusionPolicy" /> is
    ///     <see cref="InclusionPolicyKind.Inclusive" /> with an inner cache attached.
    /// </summary>
    private void BackInvalidateInner(int set, int way, ulong rangeBase) {
        if (InclusionPolicy != InclusionPolicyKind.Inclusive || _innerCache == null) return;
        SetAssociativeCache inner = _innerCache;
        for (ulong a = rangeBase; a < rangeBase + (ulong)BlockBytes; a += (ulong)inner.BlockBytes) {
            inner.Decompose(a, out int iSet, out ulong iTag);
            int iWay = inner.FindWay(iSet, iTag);
            if (iWay < 0) continue;

            if (inner._dirty != null && inner._dirty[iSet][iWay]) {
                var offset = (int)(a - rangeBase);
                Buffer.BlockCopy(inner._blocks[iSet][iWay], 0, _blocks[set][way], offset, inner.BlockBytes);
                if (_dirty != null)
                    _dirty[set][way] = true;
                else
                    // We're write-through ourselves, so there's no deferred-dirty slot to mark —
                    // the folded data must go straight to backing now or it's lost.
                    for (var i = 0; i < inner.BlockBytes; i++)
                        _backing.Write(a + (ulong)i, inner._blocks[iSet][iWay][i], 1);
            }

            inner._tags[iSet][iWay] = null;
            inner.DropInFlightPrefetch(a);
            inner.DropInFlightMshr(a);
            inner.BackInvalidations++;
            // Cascade further inward (e.g. L3 evicting invalidates L2, which — if L2 is itself
            // Inclusive over L1 — must in turn invalidate L1).
            inner.BackInvalidateInner(iSet, iWay, a);
        }
    }

    /// <summary>
    ///     Called by an inner cache configured <see cref="InclusionPolicyKind.Exclusive" /> (from
    ///     this cache's point of view) when that inner cache evicts a resident line: installs it
    ///     as this cache's own line instead of letting it be discarded, since under an exclusive
    ///     policy a line must not vanish from both levels at once. Requires equal block sizes,
    ///     enforced by <see cref="AttachInner" />.
    /// </summary>
    private void InsertVictim(ulong lineBase, byte[] blockData, bool dirty) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) {
            way = _policy.ChooseVictim(set);
            if (_tags[set][way] is { } existingTag) {
                ulong evictedBase = (existingTag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);
                BackInvalidateInner(set, way, evictedBase);
                FlushDirtyLine(set, way, existingTag, false, true);
                Evictions++;
                DropInFlightPrefetch(evictedBase);
                DropInFlightMshr(evictedBase);
            }
        }

        Buffer.BlockCopy(blockData, 0, _blocks[set][way], 0, BlockBytes);
        _tags[set][way] = tag;
        if (_dirty != null) _dirty[set][way] = dirty;
        _policy.SetPendingAddress(tag, _lastRequestPc);
        _policy.RecordInstall(set, way);
        VictimInserts++;
    }

    /// <summary>
    ///     Called by an inner cache after it fills <paramref name="lineBase" /> from this cache,
    ///     which it has determined (via this cache's <see cref="InclusionPolicy" />) to be
    ///     <see cref="InclusionPolicyKind.Exclusive" />: drops this cache's copy, writing back any
    ///     dirty data first so it is not lost now that this level no longer holds the line.
    /// </summary>
    private void RemoveResident(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        if (_dirty != null && _dirty[set][way]) FlushDirtyLine(set, way, tag, false);
        _tags[set][way] = null;
        DropInFlightPrefetch(lineBase);
        DropInFlightMshr(lineBase);
    }

    /// <summary>
    ///     Drops every attached-inner-cache line overlapping
    ///     <c>[rangeBase, rangeBase + rangeBytes)</c> — our own range that was just invalidated —
    ///     discarding any dirty inner data (the caller just overwrote the authoritative copy
    ///     out-of-band, making stale dirty data meaningless). Used for out-of-band invalidation
    ///     (cross-boundary write, <see cref="Load" />) rather than a normal capacity eviction.
    ///     No-op unless <see cref="InclusionPolicy" /> is Inclusive with an inner cache attached.
    /// </summary>
    private void PropagateInvalidate(ulong rangeBase, int rangeBytes) {
        if (InclusionPolicy != InclusionPolicyKind.Inclusive || _innerCache == null) return;
        SetAssociativeCache inner = _innerCache;
        for (ulong a = rangeBase; a < rangeBase + (ulong)rangeBytes; a += (ulong)inner.BlockBytes)
            inner.DropIfPresent(a);
    }

    private void DropIfPresent(ulong lineBase) {
        Decompose(lineBase, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return;
        _tags[set][way] = null;
        if (_dirty != null) _dirty[set][way] = false;
        DropInFlightPrefetch(lineBase);
        DropInFlightMshr(lineBase);
        BackInvalidations++;
        PropagateInvalidate(lineBase, BlockBytes);
    }

    // ── Write-back buffer helpers ────────────────────────────────────────────

    // Forwards the line at lineBase from the WB buffer into dest, consuming the slot.
    private bool TryForwardFromWbBuffer(ulong lineBase, byte[] dest) {
        for (var i = 0; i < WbCapacity; i++)
            if (_wbBuffer![i].Data != null && _wbBuffer[i].LineBase == lineBase) {
                Buffer.BlockCopy(_wbBuffer[i].Data!, 0, dest, 0, BlockBytes);
                _wbBuffer[i] = default(WbEntry);
                WbOccupancy--;
                WbDrains++;
                return true;
            }

        return false;
    }

    // Drains the WB buffer entry for lineBase synchronously to backing (without stall charge).
    // Used before NWA backing writes and cross-boundary stores to prevent later drain from
    // overwriting the newly written data.
    private void DrainWbEntryForAddress(ulong lineBase) {
        for (var i = 0; i < WbCapacity; i++) {
            if (_wbBuffer![i].Data == null || _wbBuffer[i].LineBase != lineBase) continue;
            for (var j = 0; j < BlockBytes; j++) _backing.Write(lineBase + (ulong)j, _wbBuffer[i].Data![j], 1);
            _wbBuffer[i] = default(WbEntry);
            WbOccupancy--;
            WbDrains++;
            return;
        }
    }

    // Discards a WB buffer entry without writing to backing (used by Load, which overwrites backing).
    private void DiscardWbEntryForAddress(ulong lineBase) {
        for (var i = 0; i < WbCapacity; i++) {
            if (_wbBuffer![i].Data == null || _wbBuffer[i].LineBase != lineBase) continue;
            _wbBuffer[i] = default(WbEntry);
            WbOccupancy--;
            return;
        }
    }

    // Drains the oldest occupied WB buffer slot to backing without charging a stall.
    private void DrainWbOldestSync() {
        for (var i = 0; i < WbCapacity; i++) {
            if (_wbBuffer![i].Data == null) continue;
            ulong lb = _wbBuffer[i].LineBase;
            for (var j = 0; j < BlockBytes; j++) _backing.Write(lb + (ulong)j, _wbBuffer[i].Data![j], 1);
            _wbBuffer[i] = default(WbEntry);
            WbOccupancy--;
            WbDrains++;
            return;
        }
    }

    private int FindFreeWbSlot() {
        for (var i = 0; i < WbCapacity; i++)
            if (_wbBuffer![i].Data == null)
                return i;
        throw new InvalidOperationException("WB buffer unexpectedly full.");
    }

    /// <summary>
    ///     Drains one write-back buffer entry to backing per call. Must be called once per
    ///     simulated cycle; a no-op when the buffer is empty or disabled.
    /// </summary>
    public void TickWb() {
        if (_wbBuffer == null || WbOccupancy == 0) return;
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

    // ── Prefetch ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Installs the cache line covering <paramref name="address" /> without charging any stall
    ///     penalty at install time. No-ops if the line is already present. Used by prefetchers to
    ///     warm the cache ahead of demand accesses; callers are responsible for ensuring the
    ///     address is not in an uncacheable MMIO region. With <see cref="PrefetchLatency" /> &gt; 0
    ///     the line is marked in flight for that many cycles; a demand hit arriving earlier pays
    ///     the remaining countdown (see <see cref="TickPrefetch" />).
    /// </summary>
    public void Prefetch(ulong address) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + 1 > BlockBytes) return;
        Decompose(address, out int set, out ulong tag);
        if (FindWay(set, tag) >= 0) {
            PrefetchRedundant++;
            return;
        } // already present

        int evict = _policy.ChooseVictim(set);
        _policy.SetPendingSignature(address >> OffsetBits);
        try {
            FillBlock(set, evict, address, false);
            Prefetches++;
            if (PrefetchLatency > 0) _inFlightPrefetches.Add((address & ~(ulong)_offsetMask, PrefetchLatency));
        }
        catch {
            // Prefetch address is outside the backing memory's valid range; drop silently.
        }
    }

    /// <summary>
    ///     Advances all in-flight prefetch countdowns by one cycle. Must be called once per
    ///     simulated cycle when <see cref="PrefetchLatency" /> &gt; 0; a no-op otherwise.
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
    ///     Demand access hit a line whose prefetch is still in flight: pay the remaining
    ///     countdown (the fill has not arrived yet) and retire the in-flight entry.
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

    /// <summary>
    ///     Forgets the in-flight prefetch for an evicted or invalidated line, keeping
    ///     the invariant that every in-flight entry refers to a resident line.
    /// </summary>
    private void DropInFlightPrefetch(ulong lineBase) {
        if (_inFlightPrefetches.Count == 0) return;
        for (var i = 0; i < _inFlightPrefetches.Count; i++) {
            if (_inFlightPrefetches[i].LineBase != lineBase) continue;
            _inFlightPrefetches.RemoveAt(i);
            return;
        }
    }

    // ── MSHR ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Allocates an MSHR slot for a demand miss on <paramref name="lineBase" /> and returns
    ///     the stall to charge. When unlimited (<see cref="MshrCount" /> == 0) returns
    ///     <see cref="MissLatency" /> unchanged (legacy path). When a slot is available allocates
    ///     it and returns <see cref="MissLatency" />. When all slots are full returns
    ///     <c>minRemaining + MissLatency</c> and increments <see cref="MshrCapacityStalls" />;
    ///     the new fill is not tracked (the slot will be available after the stall expires).
    /// </summary>
    private int ChargeAndAllocateMshr(ulong lineBase) {
        if (_mshrs == null) return MissLatency;

        int freeIdx = -1;
        var minRemaining = int.MaxValue;
        for (var i = 0; i < MshrCount; i++) {
            if (_mshrs[i].Remaining <= 0) {
                freeIdx = i;
                break;
            }

            if (_mshrs[i].Remaining < minRemaining) minRemaining = _mshrs[i].Remaining;
        }

        if (freeIdx >= 0) {
            _mshrs[freeIdx] = new MshrEntry { LineBase = lineBase, Remaining = MissLatency, };
            return MissLatency;
        }

        MshrCapacityStalls++;
        return minRemaining + MissLatency;
    }

    /// <summary>
    ///     If a demand access hits a line whose fill is still in-flight (tracked in the MSHR
    ///     table), charges the remaining countdown to <see cref="ConsumePendingStalls" /> and
    ///     frees the slot. This is the hit-under-miss / MSHR-merge path.
    /// </summary>
    private void ChargeInFlightMshr(ulong address) {
        if (_mshrs == null) return;
        ulong lineBase = address & ~(ulong)_offsetMask;
        for (var i = 0; i < MshrCount; i++) {
            if (_mshrs[i].Remaining <= 0 || _mshrs[i].LineBase != lineBase) continue;
            _pendingStalls += _mshrs[i].Remaining;
            _mshrs[i] = default(MshrEntry);
            MshrMerges++;
            return;
        }
    }

    /// <summary>Releases the MSHR slot for an evicted or invalidated line.</summary>
    private void DropInFlightMshr(ulong lineBase) {
        if (_mshrs == null) return;
        for (var i = 0; i < MshrCount; i++)
            if (_mshrs[i].Remaining > 0 && _mshrs[i].LineBase == lineBase)
                _mshrs[i] = default(MshrEntry);
    }

    /// <summary>
    ///     Advances all MSHR in-flight countdowns by one cycle. Must be called once per
    ///     simulated cycle when <see cref="MshrCount" /> &gt; 0; a no-op otherwise.
    /// </summary>
    public void TickMshr() {
        if (_mshrs == null) return;
        for (var i = 0; i < MshrCount; i++)
            if (_mshrs[i].Remaining > 0)
                _mshrs[i].Remaining--;
    }

    public CacheLine[] GetSnapshot() {
        int sets = _tags.Length;
        var lines = new CacheLine[sets * Ways];
        var idx = 0;
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < Ways; w++) {
            bool valid = _tags[s][w].HasValue;
            ulong tag = _tags[s][w] ?? 0;
            var blockCopy = new byte[BlockBytes];
            Buffer.BlockCopy(_blocks[s][w], 0, blockCopy, 0, BlockBytes);
            bool dirty = _dirty != null && _dirty[s][w];
            lines[idx++] = new CacheLine(s, w, valid, tag, _policy.GetMetadata(s, w), blockCopy, dirty);
        }

        return lines;
    }

    // Write-back buffer: holds dirty-victim lines waiting to drain to backing.
    // null when wbCapacity == 0 (disabled); always null in write-through mode.
    private struct WbEntry {
        public ulong LineBase;
        public byte[]? Data;
    }

    // MSHRs: tracks in-flight demand fills (filled synchronously but timing window still open).
    // null = unlimited (legacy behaviour, MshrCount == 0).
    // A miss allocates a slot; a demand hit on the in-flight line charges the remaining
    // countdown (merge / hit-under-miss). TickMshr() decrements all countdowns each cycle.
    private struct MshrEntry {
        public ulong LineBase;
        public int Remaining;
    }
}