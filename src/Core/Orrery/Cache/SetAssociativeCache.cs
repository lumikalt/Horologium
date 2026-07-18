using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

public sealed record CacheLine(int Set, int Way, bool Valid, ulong Tag, int LruAge, byte[] Block, bool Dirty = false);

/// <summary>
///     N-way set-associative cache implementing IMemory.
///     Write policy and write-miss policy are configurable; default is write-through and no-write-allocate.
///     Cache miss does not block — it records a penalty in PendingStalls
///     that the caller drains to inject idle cycles into the pipeline.
/// </summary>
public sealed class SetAssociativeCache : IMemory {
    private readonly IMemory _backing;
    private readonly int[]? _bankReadUsage;  // non-null only when ReadPorts > 0
    private readonly int[]? _bankWriteUsage; // non-null only when WritePorts > 0
    private readonly byte[][][] _blocks;     // [set][way][offset]
    private readonly bool[][]? _dirty;       // non-null only in WriteBack mode
    private readonly int _indexMask;

    // Realistic prefetch latency: lines installed by Prefetch() that have not yet
    // "arrived". A demand hit on one of these pays the remaining countdown instead
    // of zero. Empty (and never touched) when PrefetchLatency = 0.
    private readonly List<(ulong LineBase, int Remaining)> _inFlightPrefetches = [];
    private readonly MshrEntry[]? _mshrs;
    private readonly int _offsetMask;
    private readonly IReplacementPolicy _policy;
    private readonly bool _requirePcOnHit; // Hawkeye needs OPTgen fed on every hit

    private readonly bool[][][]?
        _sectorDirty; // [set][way][sector]: non-null only when SectorBytes > 0 in WriteBack mode

    private readonly int _sectorsPerLine;      // 1 when sectoring is disabled
    private readonly bool[][][]? _sectorValid; // [set][way][sector]: non-null only when SectorBytes > 0

    private readonly ulong?[][] _tags; // [set][way]: null = invalid
    private readonly bool _usePcSignature;

    // Jouppi victim cache (ISCA 1990): a small fully associative FIFO buffer beside the main
    // array that captures lines evicted due to conflict misses instead of flushing/discarding
    // them immediately. Distinct from InsertVictim/VictimInserts (the Exclusive-inclusion-policy
    // hand-off, an unrelated pre-existing mechanism) and IReplacementPolicy.ChooseVictim (generic
    // "pick a way to evict", also unrelated). Null when disabled (VictimCacheEntries == 0).
    private readonly VictimBufferEntry[]? _victimBuffer;

    private readonly WbEntry[]? _wbBuffer;

    // The cache directly inside this one (closer to the CPU), if any has been attached via
    // AttachInner. Used to enforce InclusionPolicy toward that level.
    private SetAssociativeCache? _innerCache;
    private ulong _lastRequestPc;

    private long _pendingStalls;
    private int _victimHead; // physical index of the oldest (next FIFO-overflow) entry

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
    ///     legacy behavior). Each demand miss allocates a slot for <see cref="MissLatency" /> cycles; a
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
    ///     <see cref="AttachInner" />. No effect until an inner cache is attached. See
    ///     <see cref="InclusionPolicyKind" />.
    /// </param>
    /// <param name="criticalWordLatency">
    ///     Critical-word-first / early restart (0 = disabled, legacy behavior: a miss charges the
    ///     full <paramref name="missLatency" /> to the requester). When positive, a fresh demand
    ///     miss charges only <paramref name="criticalWordLatency" /> to the requesting access — the
    ///     demanded word is assumed to arrive first off the bus — while the MSHR entry keeps
    ///     counting down the full <paramref name="missLatency" /> in the background, so a
    ///     <em>different</em> access that hits the same in-flight line before the line fully
    ///     arrives still pays the remaining full-line latency via the existing hit-under-miss path.
    ///     Requires <paramref name="mshrCount" /> &gt; 0 (early restart needs the MSHR table to
    ///     distinguish the requester from later accesses) and cannot exceed
    ///     <paramref name="missLatency" />.
    /// </param>
    /// <param name="bankCount">
    ///     Number of independent banks the cache is split into (default 1 = unbanked). The line
    ///     address selects the bank; <paramref name="readPorts" />/<paramref name="writePorts" />
    ///     apply per bank, so widening <paramref name="bankCount" /> spreads concurrent accesses
    ///     across more independent port budgets.
    /// </param>
    /// <param name="readPorts">
    ///     Read accesses one bank can service per cycle (0 = unlimited, legacy behavior). Access to a bank already at
    ///     capacity this cycle pays a 1-cycle structural-hazard stall.
    ///     Call <see cref="TickPorts" /> once per simulated cycle to reset per-bank usage.
    /// </param>
    /// <param name="writePorts">
    ///     Write accesses one bank can service per cycle (0 = unlimited). See
    ///     <paramref name="readPorts" />.
    /// </param>
    /// <param name="sectorBytes">
    ///     Sector size in bytes (0 = disabled, legacy whole-line valid/dirty granularity). Must be a
    ///     power of 2 dividing <paramref name="blockSizeBytes" /> evenly. When positive, a line's
    ///     valid and (in <see cref="WritePolicyKind.WriteBack" />) dirty state is tracked per
    ///     sector instead of per whole line: a fresh miss fetches only the sector covering the
    ///     triggering address, leaving the rest of the line's sectors invalid until touched (each
    ///     later access to an untouched sector on an otherwise-resident line pays
    ///     <paramref name="missLatency" /> for that sector alone); evictions write back only dirty
    ///     sectors instead of the whole line. Not currently combinable with <paramref name="wbCapacity" /> &gt; 0.
    /// </param>
    /// <param name="victimCacheEntries">
    ///     Capacity, in lines, of a small fully associative FIFO buffer beside the main array (0 =
    ///     disabled) that captures lines evicted by a conflict miss instead of flushing/discarding
    ///     them immediately (Jouppi, ISCA 1990). A later miss that hits in the buffer performs a
    ///     full swap: the hit line installs into the main array via the normal replacement policy
    ///     (charging <paramref name="victimCacheHitLatency" />, no MSHR allocation), and the line it
    ///     displaces takes the vacated buffer slot — so both levels stay populated with the working
    ///     set rather than stranding a line permanently in the small buffer. A victim-buffer hit
    ///     counts as a demand hit, not a miss. Captures are free (no writeback at capture time); a
    ///     line that eventually leaves the buffer via FIFO overflow while still dirty is billed
    ///     exactly like an ordinary write-back-mode eviction (deferred into the write-back buffer if
    ///     configured, else a synchronous <paramref name="missLatency" /> stall), or handed off to
    ///     the backing cache if it is itself configured <see cref="InclusionPolicyKind.Exclusive" />.
    ///     Not currently combinable with <paramref name="sectorBytes" /> &gt; 0. Distinct from the
    ///     unrelated pre-existing <c>InsertVictim</c>/<c>VictimInserts</c>/<c>ChooseVictim</c>
    ///     symbols (Exclusive-inclusion-policy hand-off and generic replacement-policy eviction
    ///     selection, respectively).
    /// </param>
    /// <param name="victimCacheHitLatency">
    ///     Cycles charged to a demand access that hits in the victim buffer instead of the main
    ///     array. Only meaningful when <paramref name="victimCacheEntries" /> &gt; 0. Cannot exceed
    ///     <paramref name="missLatency" />.
    /// </param>
    /// <param name="customPolicy">
    ///     Caller-built replacement-policy instance (e.g. <see cref="RtlFfiReplacementPolicy" />)
    ///     that overrides <paramref name="replacementPolicy" /> when non-null. The caller owns its
    ///     lifetime; the cache never disposes of it.
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
        InclusionPolicyKind inclusionPolicy = InclusionPolicyKind.Nine,
        int criticalWordLatency = 0,
        int bankCount = 1,
        int readPorts = 0,
        int writePorts = 0,
        int sectorBytes = 0,
        int victimCacheEntries = 0,
        int victimCacheHitLatency = 1,
        IReplacementPolicy? customPolicy = null
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(prefetchLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(tagLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(dataLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(criticalWordLatency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bankCount);
        ArgumentOutOfRangeException.ThrowIfNegative(readPorts);
        ArgumentOutOfRangeException.ThrowIfNegative(writePorts);
        ArgumentOutOfRangeException.ThrowIfNegative(sectorBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(victimCacheEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(victimCacheHitLatency);
        if (!BitOperations.IsPow2(capacityBytes) ||
            !BitOperations.IsPow2(ways) ||
            !BitOperations.IsPow2(blockSizeBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");
        if (criticalWordLatency > 0 && mshrCount <= 0)
            throw new ArgumentException(
                "criticalWordLatency requires mshrCount > 0 (early restart needs the MSHR table to " +
                "track in-flight lines)."
            );
        if (criticalWordLatency > missLatency)
            throw new ArgumentException("criticalWordLatency cannot exceed missLatency.");
        if (sectorBytes > 0) {
            if (!BitOperations.IsPow2(sectorBytes)) throw new ArgumentException("sectorBytes must be a power of 2.");
            if (sectorBytes > blockSizeBytes || blockSizeBytes % sectorBytes != 0)
                throw new ArgumentException("sectorBytes must evenly divide blockSizeBytes.");
            if (wbCapacity > 0)
                throw new ArgumentException("sectorBytes cannot currently be combined with wbCapacity > 0.");
            if (victimCacheEntries > 0)
                throw new ArgumentException("sectorBytes cannot currently be combined with victimCacheEntries > 0.");
        }

        if (victimCacheHitLatency > missLatency)
            throw new ArgumentException("victimCacheHitLatency cannot exceed missLatency.");

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
        CriticalWordLatency = criticalWordLatency;
        BankCount = bankCount;
        ReadPorts = readPorts;
        WritePorts = writePorts;
        _bankReadUsage = readPorts > 0 ? new int[bankCount] : null;
        _bankWriteUsage = writePorts > 0 ? new int[bankCount] : null;
        SectorBytes = sectorBytes;
        _sectorsPerLine = sectorBytes > 0 ? blockSizeBytes / sectorBytes : 1;

        OffsetBits = BitOperations.Log2((uint)blockSizeBytes);
        IndexBits = BitOperations.Log2((uint)sets);
        _offsetMask = blockSizeBytes - 1;
        _indexMask = sets - 1;

        MshrCount = Math.Max(0, mshrCount);
        _mshrs = MshrCount > 0 ? new MshrEntry[MshrCount] : null;

        _tags = new ulong?[sets][];
        _blocks = new byte[sets][][];
        // Sectored write-back caches track dirty per sector instead of per line (see _sectorDirty).
        _dirty = writePolicy == WritePolicyKind.WriteBack && sectorBytes == 0 ? new bool[sets][] : null;
        _sectorValid = sectorBytes > 0 ? new bool[sets][][] : null;
        _sectorDirty = sectorBytes > 0 && writePolicy == WritePolicyKind.WriteBack ? new bool[sets][][] : null;
        _wbBuffer = WbCapacity > 0 ? new WbEntry[WbCapacity] : null;
        VictimCacheEntries = victimCacheEntries;
        VictimCacheHitLatency = victimCacheHitLatency;
        _victimBuffer = VictimCacheEntries > 0 ? new VictimBufferEntry[VictimCacheEntries] : null;

        for (var s = 0; s < sets; s++) {
            _tags[s] = new ulong?[Ways];
            _blocks[s] = new byte[Ways][];
            _dirty?[s] = new bool[Ways];
            _sectorValid?[s] = new bool[Ways][];
            _sectorDirty?[s] = new bool[Ways][];
            for (var w = 0; w < Ways; w++) {
                _blocks[s][w] = new byte[BlockBytes];
                _sectorValid?[s][w] = new bool[_sectorsPerLine];
                _sectorDirty?[s][w] = new bool[_sectorsPerLine];
            }
        }

        // A caller-supplied policy instance (e.g., RtlFfiReplacementPolicy) overrides the kind.
        _policy = customPolicy ?? replacementPolicy switch {
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

    private int TagLatency { get; }
    private int DataLatency { get; }
    public CacheAccessModeKind AccessMode { get; }

    public int HitLatency => AccessMode == CacheAccessModeKind.Sequential
        ? TagLatency + DataLatency
        : Math.Max(TagLatency, DataLatency);

    private int MissLatency { get; }
    public int PrefetchLatency { get; }
    private WritePolicyKind WritePolicy { get; }

    private WriteMissPolicyKind WriteMissPolicy { get; }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }
    public long WbDrains { get; private set; }
    private int WbCapacity { get; }

    public int WbOccupancy { get; private set; }

    public long Prefetches { get; private set; }
    private long PrefetchRedundant { get; set; }
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

    /// <summary>0 = disabled. See the constructor parameter of the same name.</summary>
    public int CriticalWordLatency { get; }

    /// <summary>Number of independent banks (1 = unbanked). See the constructor parameter of the same name.</summary>
    public int BankCount { get; }

    /// <summary>Read accesses one bank can service per cycle (0 = unlimited).</summary>
    public int ReadPorts { get; }

    /// <summary>Write accesses one bank can service per cycle (0 = unlimited).</summary>
    public int WritePorts { get; }

    /// <summary>
    ///     Accesses that paid a 1-cycle structural-hazard stall because their bank was already at port capacity this
    ///     cycle.
    /// </summary>
    public long BankConflicts { get; private set; }

    /// <summary>0 = disabled (whole-line valid/dirty granularity). See the constructor parameter of the same name.</summary>
    public int SectorBytes { get; }

    /// <summary>
    ///     Sector-granularity fetches from backing: the initial sector of a fresh line installation,
    ///     plus every later on-demand fetch of a sector that was still invalid on an otherwise-resident line.
    /// </summary>
    public long SectorFills { get; private set; }

    private InclusionPolicyKind InclusionPolicy { get; }

    /// <summary>Lines dropped here because the attached outer level (Inclusive) evicted them.</summary>
    public long BackInvalidations { get; private set; }

    /// <summary>Lines installed here because the attached inner level (Exclusive) evicted them.</summary>
    public long VictimInserts { get; private set; }

    /// <summary>
    ///     0 = disabled. Capacity of the local Jouppi victim buffer (see the constructor parameter
    ///     of the same name). Unrelated to <see cref="VictimInserts" />/<see cref="InsertVictim" />
    ///     (the Exclusive-inclusion-policy hand-off).
    /// </summary>
    public int VictimCacheEntries { get; }

    /// <summary>Cycles charged to a demand access that hits in the victim buffer instead of the main array.</summary>
    private int VictimCacheHitLatency { get; }

    /// <summary>Current occupancy of the victim buffer.</summary>
    public int VictimCacheOccupancy { get; private set; }

    /// <summary>Demand accesses serviced by a victim-buffer hit-and-swap rather than the main array or backing.</summary>
    public long VictimCacheHits { get; private set; }

    /// <summary>Lines captured into the victim buffer on a main-array conflict eviction.</summary>
    public long VictimCacheCaptures { get; private set; }

    /// <summary>Number of prefetched lines still in flight (counts against MSHR capacity).</summary>
    public int InFlightPrefetchCount => _inFlightPrefetches.Count;

    // ── Inspection ───────────────────────────────────────────────────────────

    private int Ways { get; }

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

        ChargePort(address, false);
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        LastAccessAddress = address;
        if (way >= 0) {
            LastAccessWasHit = true;
            Hits++;
            if (_requirePcOnHit) _policy.RecordHitPc(set, way, tag, _lastRequestPc);
            _policy.RecordHit(set, way);
            EnsureSectorResident(set, way, address);
            ChargeInFlightMshr(address);
            ChargeInFlightPrefetch(address);
            return ReadBytes(_blocks[set][way], offset, bytes);
        }

        if (_victimBuffer != null && TryVictimBufferSwap(set, tag, address, out int vWay)) {
            LastAccessWasHit = true;
            Hits++;
            return ReadBytes(_blocks[set][vWay], offset, bytes);
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
            // (both synchronously, without deferring — a deferred drain after the backing write
            // would overwrite the store's bytes), then write to backing, then invalidate.
            ulong end = address + (ulong)bytes;
            if (WritePolicy == WritePolicyKind.WriteBack) {
                for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)BlockBytes) {
                    if (_wbBuffer != null) DrainWbEntryForAddress(a);
                    Decompose(a, out int s, out ulong t);
                    for (var w = 0; w < Ways; w++)
                        if (_tags[s][w] == t)
                            FlushDirtyLine(s, w, t, true); // deferToBuffer=false
                    if (_victimBuffer != null) {
                        int vSlot = FindVictimBufferSlot(s, t);
                        if (vSlot >= 0 && _victimBuffer[vSlot].Dirty)
                            WritebackOrBuffer(a, _victimBuffer[vSlot].Data!, true, false);
                    }
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

                if (_victimBuffer != null) {
                    int vSlot = FindVictimBufferSlot(s, t);
                    if (vSlot >= 0) RemoveVictimBufferSlot(vSlot);
                }
            }

            return;
        }

        ChargePort(address, true);
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        LastAccessAddress = address;
        if (way >= 0) {
            LastAccessWasHit = true;
            Hits++;
            if (_requirePcOnHit) _policy.RecordHitPc(set, way, tag, _lastRequestPc);
            _policy.RecordHit(set, way);
            EnsureSectorResident(set, way, address);
            ChargeInFlightMshr(address);
            ChargeInFlightPrefetch(address);
            WriteBytes(_blocks[set][way], offset, value, bytes);
            if (_dirty != null) _dirty[set][way] = true; // write-back: mark dirty on hit
            if (_sectorDirty != null) _sectorDirty[set][way][SectorIndex(address)] = true;
        }
        else if (_victimBuffer != null && WriteMissPolicy == WriteMissPolicyKind.WriteAllocate &&
                 TryVictimBufferSwap(set, tag, address, out int vWay)) {
            LastAccessWasHit = true;
            Hits++;
            WriteBytes(_blocks[set][vWay], offset, value, bytes);
            if (_dirty != null) _dirty[set][vWay] = true;
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
                if (_sectorDirty != null) _sectorDirty[set][evict][SectorIndex(address)] = true;
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

            if (_victimBuffer != null) {
                int vSlot = FindVictimBufferSlot(set, tag);
                if (vSlot >= 0)
                    RemoveVictimBufferSlot(vSlot); // dirty data discarded, matches main-array semantics above
            }
        }
    }

    // ── Cache maintenance (Zicbom: cbo.clean / cbo.flush / cbo.inval) ────────

    /// <summary>
    ///     cbo.clean: writes back the line covering <paramref name="address" /> to backing if
    ///     dirty, leaving it resident and valid. No-op if the line is not present or already clean.
    /// </summary>
    public void CleanLine(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            FlushDirtyLine(set, way, tag, false);
            return;
        }

        if (_victimBuffer == null) return;
        int vSlot = FindVictimBufferSlot(set, tag);
        if (vSlot < 0 || !_victimBuffer[vSlot].Dirty) return;
        ulong lineBase = address & ~(ulong)_offsetMask;
        WritebackOrBuffer(lineBase, _victimBuffer[vSlot].Data!, false, false);
        VictimBufferEntry e = _victimBuffer[vSlot];
        e.Dirty = false;
        _victimBuffer[vSlot] = e;
    }

    /// <summary>
    ///     cbo.flush: writes back the line covering <paramref name="address" /> to backing if
    ///     dirty, then invalidates it. No-op if the line is not present.
    /// </summary>
    public void FlushLine(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            FlushDirtyLine(set, way, tag, false);
            ulong lineBase0 = address & ~(ulong)_offsetMask;
            _tags[set][way] = null;
            DropInFlightPrefetch(lineBase0);
            DropInFlightMshr(lineBase0);
            return;
        }

        if (_victimBuffer == null) return;
        int vSlot = FindVictimBufferSlot(set, tag);
        if (vSlot < 0) return;
        if (_victimBuffer[vSlot].Dirty) {
            ulong lineBase = address & ~(ulong)_offsetMask;
            WritebackOrBuffer(lineBase, _victimBuffer[vSlot].Data!, false, false);
        }

        RemoveVictimBufferSlot(vSlot);
    }

    /// <summary>
    ///     cbo.inval: invalidates the line covering <paramref name="address" /> and discards any
    ///     dirty data without writing it back to backing. No-op if the line is not present.
    /// </summary>
    public void InvalidateLine(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way >= 0) {
            ulong lineBase = address & ~(ulong)_offsetMask;
            if (_dirty != null) _dirty[set][way] = false;
            if (_sectorDirty != null) Array.Clear(_sectorDirty[set][way]);
            _tags[set][way] = null;
            DropInFlightPrefetch(lineBase);
            DropInFlightMshr(lineBase);
            return;
        }

        if (_victimBuffer == null) return;
        int vSlot = FindVictimBufferSlot(set, tag);
        if (vSlot >= 0) RemoveVictimBufferSlot(vSlot); // discard, no writeback
    }

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
    //   Only FillBlock passes true; cross-boundary, NWA-miss, and <c>Load</c>s pass false.
    private void FlushDirtyLine(int set, int way, ulong tag, bool chargeStall, bool deferToBuffer = false) {
        // Sectored write-back: only dirty sectors move, never the whole line (wbCapacity is
        // disallowed alongside sectorBytes, so deferToBuffer is always false here in practice).
        if (_sectorDirty != null) {
            FlushDirtySectors(set, way, tag, chargeStall);
            return;
        }

        if (_dirty == null || !_dirty[set][way]) return;
        ulong lineBase = (tag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);
        WritebackOrBuffer(lineBase, _blocks[set][way], chargeStall, deferToBuffer && _wbBuffer != null);
        _dirty[set][way] = false;
        DirtyEvictions++;
    }

    // Writes a line to backing synchronously, or defers it into the WB buffer (draining the
    // oldest entry first, with a stall if the buffer is full). Shared by FlushDirtyLine's
    // main-array path and the victim buffer's FIFO-overflow disposal path.
    private void WritebackOrBuffer(ulong lineBase, byte[] data, bool chargeStall, bool deferToBuffer) {
        if (deferToBuffer) {
            // Buffer full: synchronous drain of oldest entry, charge stall for the wait.
            if (WbOccupancy >= WbCapacity) {
                DrainWbOldestSync();
                if (chargeStall) _pendingStalls += MissLatency;
            }

            var copy = new byte[BlockBytes];
            Buffer.BlockCopy(data, 0, copy, 0, BlockBytes);
            _wbBuffer![FindFreeWbSlot()] = new WbEntry { LineBase = lineBase, Data = copy, };
            WbOccupancy++;
        }
        else {
            for (var i = 0; i < BlockBytes; i++) _backing.Write(lineBase + (ulong)i, data[i], 1);
            if (chargeStall) _pendingStalls += MissLatency;
        }
    }

    // Writes back only the dirty sectors of a sectored line (bandwidth refinement over
    // whole-line writeback), clearing each as it drains.
    private void FlushDirtySectors(int set, int way, ulong tag, bool chargeStall) {
        bool[] dirty = _sectorDirty![set][way];
        ulong lineBase = (tag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);
        var any = false;
        for (var sec = 0; sec < _sectorsPerLine; sec++) {
            if (!dirty[sec]) continue;
            any = true;
            int blockOffset = sec * SectorBytes;
            ulong sectorBase = lineBase + (ulong)blockOffset;
            for (var i = 0; i < SectorBytes; i++)
                _backing.Write(sectorBase + (ulong)i, _blocks[set][way][blockOffset + i], 1);
            dirty[sec] = false;
        }

        if (!any) return;
        if (chargeStall) _pendingStalls += MissLatency;
        DirtyEvictions++;
    }

    private void FillBlock(
        int set,
        int way,
        ulong address,
        bool chargeWritebackStall = true,
        bool fetchWholeLine = false
    ) {
        if (_tags[set][way] is { } existingTag) {
            ulong evictedBase = (existingTag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);

            // Inclusive: our own eviction must also drop the inner level's copy, so it never
            // outlives ours. Do this first so any dirty inner data folds into our (still valid)
            // copy before we flush/hand it off below. No-op unless InclusionPolicy is Inclusive.
            BackInvalidateInner(set, way, evictedBase);

            // Jouppi victim cache: capture the evicted line locally instead of flushing/handing it
            // off immediately — later access that hits in the buffer avoids the round trip.
            if (_victimBuffer != null)
                CaptureIntoVictimBuffer(set, way, existingTag);
            // Exclusive: the level below acts as our victim cache, so the evicted line (clean or
            // dirty) is handed off there instead of just flushed/discarded.
            else if (_backing is SetAssociativeCache { InclusionPolicy: InclusionPolicyKind.Exclusive, } outer)
                outer.InsertVictim(evictedBase, _blocks[set][way], _dirty != null && _dirty[set][way]);
            else
                FlushDirtyLine(set, way, existingTag, chargeWritebackStall, true);
        }

        ulong lineBase = address & ~(ulong)_offsetMask;
        bool fromWb = _wbBuffer != null && TryForwardFromWbBuffer(lineBase, _blocks[set][way]);
        if (!fromWb) {
            if (_sectorValid != null) {
                // Sectored: start every sector invalid/clean, then fetch only what's needed now —
                // an untouched sector arrives lazily via EnsureSectorResident on a later hit.
                // fetchWholeLine (Prefetch) instead warms every sector immediately.
                for (var i = 0; i < _sectorsPerLine; i++) {
                    _sectorValid[set][way][i] = false;
                    if (_sectorDirty != null) _sectorDirty[set][way][i] = false;
                }

                if (fetchWholeLine)
                    for (var i = 0; i < _sectorsPerLine; i++)
                        FetchSector(set, way, lineBase + (ulong)(i * SectorBytes));
                else
                    FetchSector(set, way, address);
            }
            else {
                for (var i = 0; i < BlockBytes; i++) _blocks[set][way][i] = (byte)_backing.Read(lineBase + (ulong)i, 1);
            }
        }

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
            if (iWay < 0) {
                // Not in the inner cache's main array — check its Jouppi victim buffer too, so an
                // Inclusive cascade can't miss a line just because it happens to be sitting there.
                if (inner._victimBuffer == null) continue;
                int vSlot = inner.FindVictimBufferSlot(iSet, iTag);
                if (vSlot < 0) continue;

                VictimBufferEntry entry = inner.RemoveVictimBufferSlot(vSlot);
                if (entry.Dirty) {
                    var offset = (int)(a - rangeBase);
                    Buffer.BlockCopy(entry.Data!, 0, _blocks[set][way], offset, inner.BlockBytes);
                    if (_dirty != null)
                        _dirty[set][way] = true;
                    else
                        for (var i = 0; i < inner.BlockBytes; i++)
                            _backing.Write(a + (ulong)i, entry.Data![i], 1);
                }

                inner.BackInvalidations++;
                // No further cascade here: this line was already evicted from inner's main array
                // once (into its victim buffer), and BackInvalidateInner already ran at that time.
                continue;
            }

            if (inner._dirty != null && inner._dirty[iSet][iWay]) {
                var offset = (int)(a - rangeBase);
                Buffer.BlockCopy(inner._blocks[iSet][iWay], 0, _blocks[set][way], offset, inner.BlockBytes);
                if (_dirty != null)
                    _dirty[set][way] = true;
                else
                    // We're write-through ourselves, so there's no deferred-dirty slot to mark —
                    // the folded data must go straight to backing now, or it's lost.
                    for (var i = 0; i < inner.BlockBytes; i++)
                        _backing.Write(a + (ulong)i, inner._blocks[iSet][iWay][i], 1);
            }

            inner._tags[iSet][iWay] = null;
            inner.DropInFlightPrefetch(a);
            inner.DropInFlightMshr(a);
            inner.BackInvalidations++;
            // Cascade further inward (e.g., L3 evicting invalidates L2, which — if L2 is itself
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
                if (_victimBuffer != null)
                    CaptureIntoVictimBuffer(set, way, existingTag);
                else
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
        if (way >= 0) {
            if (_dirty != null && _dirty[set][way]) FlushDirtyLine(set, way, tag, false);
            _tags[set][way] = null;
            DropInFlightPrefetch(lineBase);
            DropInFlightMshr(lineBase);
            return;
        }

        if (_victimBuffer == null) return;
        int vSlot = FindVictimBufferSlot(set, tag);
        if (vSlot < 0) return;
        VictimBufferEntry entry = RemoveVictimBufferSlot(vSlot);
        if (entry.Dirty)
            for (var i = 0; i < BlockBytes; i++)
                _backing.Write(lineBase + (ulong)i, entry.Data![i], 1);
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
        if (way >= 0) {
            _tags[set][way] = null;
            if (_dirty != null) _dirty[set][way] = false;
            DropInFlightPrefetch(lineBase);
            DropInFlightMshr(lineBase);
            BackInvalidations++;
            PropagateInvalidate(lineBase, BlockBytes);
            return;
        }

        if (_victimBuffer == null) return;
        int vSlot = FindVictimBufferSlot(set, tag);
        if (vSlot < 0) return;
        RemoveVictimBufferSlot(vSlot); // discard, no writeback — matches the main-array path above
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

    // ── Banking / port limits ────────────────────────────────────────────────

    private int Bank(ulong address) => BankCount <= 1 ? 0 : (int)((address >> OffsetBits) % (ulong)BankCount);

    // Charges a 1-cycle structural-hazard stall when the access's bank has already used up its
    // read/write port budget this cycle. No-op for the op type when the corresponding port count
    // is 0 (unlimited, legacy behavior).
    private void ChargePort(ulong address, bool isWrite) {
        int[]? usage = isWrite ? _bankWriteUsage : _bankReadUsage;
        if (usage == null) return;
        int bank = Bank(address);
        int limit = isWrite ? WritePorts : ReadPorts;
        if (usage[bank] >= limit) {
            _pendingStalls++;
            BankConflicts++;
        }

        usage[bank]++;
    }

    /// <summary>
    ///     Resets per-bank read/write port usage. Must be called once per simulated cycle when
    ///     <see cref="ReadPorts" /> or <see cref="WritePorts" /> is nonzero; a no-op otherwise.
    /// </summary>
    public void TickPorts() {
        if (_bankReadUsage != null) Array.Clear(_bankReadUsage);
        if (_bankWriteUsage != null) Array.Clear(_bankWriteUsage);
    }

    // ── Sectored valid/dirty bits ────────────────────────────────────────────

    private int SectorIndex(ulong address) => (int)((address & (ulong)_offsetMask) / (ulong)SectorBytes);

    // Fetches the sector covering `address` from backing into the already-tagged (set, way) line
    // and marks it valid. No stall charge here — callers charge whatever is appropriate for their
    // context (a fresh line installation's first sector rides on the miss stall already charged by the
    // caller; a later on-demand sector fetch is charged by EnsureSectorResident).
    private void FetchSector(int set, int way, ulong address) {
        int sector = SectorIndex(address);
        ulong sectorBase = address & ~(ulong)(SectorBytes - 1);
        int blockOffset = sector * SectorBytes;
        for (var i = 0; i < SectorBytes; i++)
            _blocks[set][way][blockOffset + i] = (byte)_backing.Read(sectorBase + (ulong)i, 1);
        _sectorValid![set][way][sector] = true;
        SectorFills++;
    }

    // Called on every hit: if sectoring is disabled, or the sector is already resident, this is a
    // no-op. Otherwise, the accessed line is resident (tag hit), but this particular sector was never
    // fetched — a sector miss — so fetch it now and charge MissLatency for that fetch alone.
    private void EnsureSectorResident(int set, int way, ulong address) {
        if (_sectorValid == null) return;
        if (_sectorValid[set][way][SectorIndex(address)]) return;
        FetchSector(set, way, address);
        _pendingStalls += MissLatency;
    }

    /// <summary>
    ///     True if the sector covering <paramref name="address" /> is resident (fetched from
    ///     backing) in this cache. Always true when <see cref="SectorBytes" /> is 0 (sectoring
    ///     disabled — a resident tag implies the whole line is resident) or when the line itself
    ///     isn't resident at all is reported as false, matching "not yet available" either way.
    /// </summary>
    public bool IsSectorResident(ulong address) {
        Decompose(address, out int set, out ulong tag);
        int way = FindWay(set, tag);
        if (way < 0) return false;
        return _sectorValid == null || _sectorValid[set][way][SectorIndex(address)];
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
    ///     penalty at installation time. No-ops if the line is already present. Used by prefetchers to
    ///     warm the cache ahead of demand accesses; callers are responsible for ensuring the
    ///     address is not in an uncacheable MMIO region. With <see cref="PrefetchLatency" /> &gt; 0
    ///     the line is marked in flight for that many cycles; a demand hit arriving earlier pays
    ///     the remaining countdown (see <see cref="TickPrefetch" />).
    /// </summary>
    public void Prefetch(ulong address) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + 1 > BlockBytes) return;
        Decompose(address, out int set, out ulong tag);
        if (FindWay(set, tag) >= 0 || (_victimBuffer != null && FindVictimBufferSlot(set, tag) >= 0)) {
            PrefetchRedundant++;
            return;
        } // already present

        int evict = _policy.ChooseVictim(set);
        _policy.SetPendingSignature(address >> OffsetBits);
        try {
            FillBlock(set, evict, address, false, true);
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
    ///     the stall to charge <em>to the requester</em>. When unlimited (<see cref="MshrCount" />
    ///     == 0) returns <see cref="MissLatency" /> unchanged (legacy path). When a slot is
    ///     available, allocates it — tracked for the full <see cref="MissLatency" /> regardless of
    ///     early restart, since the line itself takes that long to fully arrive — and returns
    ///     <see cref="CriticalWordLatency" /> when early restart is enabled (nonzero), else
    ///     <see cref="MissLatency" />. When all slots are full returns
    ///     <c>minRemaining + (early-restart-aware requester latency)</c> and increments
    ///     <see cref="MshrCapacityStalls" />; the new fill is not tracked (the slot will be
    ///     available after the stall expires).
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

        int requesterLatency = CriticalWordLatency > 0 ? CriticalWordLatency : MissLatency;
        if (freeIdx >= 0) {
            _mshrs[freeIdx] = new MshrEntry { LineBase = lineBase, Remaining = MissLatency, };
            return requesterLatency;
        }

        MshrCapacityStalls++;
        return minRemaining + requesterLatency;
    }

    /// <summary>
    ///     If demand access hits a line whose fill is still in-flight (tracked in the MSHR
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

    // ── Jouppi victim cache ──────────────────────────────────────────────────

    /// <summary>
    ///     Looks up (set, tag) in the victim buffer. On hit, performs the full Jouppi swap:
    ///     removes the entry from the FIFO buffer, chooses a main-array way via the normal
    ///     replacement policy, moves whatever currently occupies that way into the vacated buffer
    ///     slot (folding any Inclusive inner-cache copy first, exactly as an ordinary eviction
    ///     would), and installs the hit data at that way — using the same
    ///     SetPendingSignature/ChooseVictim/SetPendingAddress/RecordInstall sequence an ordinary
    ///     fill uses, so replacement-policy bookkeeping (LRU age, SRRIP RRPV, SHiP SHCT) stays
    ///     consistent. No MSHR is allocated: the data is already fully resident on-chip. Charges
    ///     VictimCacheHitLatency to _pendingStalls. Caller is responsible for Hits/LastAccessWasHit.
    /// </summary>
    private bool TryVictimBufferSwap(int set, ulong tag, ulong address, out int way) {
        way = -1;
        int slot = FindVictimBufferSlot(set, tag);
        if (slot < 0) return false;

        VictimBufferEntry hitEntry = RemoveVictimBufferSlot(slot);

        _policy.SetPendingSignature(_usePcSignature ? _lastRequestPc : address >> OffsetBits);
        way = _policy.ChooseVictim(set);

        if (_tags[set][way] is { } existingTag) {
            ulong evictedBase = (existingTag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits);
            BackInvalidateInner(set, way, evictedBase);
            Evictions++;
            DropInFlightPrefetch(evictedBase);
            DropInFlightMshr(evictedBase);

            var displacedData = new byte[BlockBytes];
            Buffer.BlockCopy(_blocks[set][way], 0, displacedData, 0, BlockBytes);
            InsertVictimBufferEntry(set, existingTag, displacedData, _dirty != null && _dirty[set][way]);
        }

        Buffer.BlockCopy(hitEntry.Data!, 0, _blocks[set][way], 0, BlockBytes);
        _tags[set][way] = tag;
        if (_dirty != null) _dirty[set][way] = hitEntry.Dirty;
        _policy.SetPendingAddress(tag, _lastRequestPc);
        _policy.RecordInstall(set, way);

        VictimCacheHits++;
        _pendingStalls += VictimCacheHitLatency;

        // Exclusive: this line just became resident here, so it must not also remain resident in
        // the level below (mirrors FillBlock's own Exclusive-removal call).
        if (_backing is SetAssociativeCache { InclusionPolicy: InclusionPolicyKind.Exclusive, } exclusiveOuter)
            exclusiveOuter.RemoveResident((tag << (OffsetBits + IndexBits)) | ((ulong)set << OffsetBits));

        return true;
    }

    // Captures the line currently at (set, way) — about to be overwritten by a fill, or already
    // being handed elsewhere — into the victim buffer. No writeback here: the dirty bit travels
    // with the data; avoiding this exact round trip at capture time is the whole point.
    private void CaptureIntoVictimBuffer(int set, int way, ulong tag) {
        var data = new byte[BlockBytes];
        Buffer.BlockCopy(_blocks[set][way], 0, data, 0, BlockBytes);
        InsertVictimBufferEntry(set, tag, data, _dirty != null && _dirty[set][way]);
        VictimCacheCaptures++;
    }

    // Inserts a captured entry, evicting the oldest (FIFO) entry first if the buffer is full. Used
    // both by CaptureIntoVictimBuffer (main-array eviction) and TryVictimBufferSwap (the line
    // displaced by a swap-in takes the vacated slot).
    private void InsertVictimBufferEntry(int set, ulong tag, byte[] data, bool dirty) {
        if (VictimCacheOccupancy >= VictimCacheEntries) {
            DisposeVictimBufferEntry(_victimBuffer![_victimHead], true);
            _victimHead = (_victimHead + 1) % VictimCacheEntries;
            VictimCacheOccupancy--;
        }

        int insertIdx = (_victimHead + VictimCacheOccupancy) % VictimCacheEntries;
        _victimBuffer![insertIdx] = new VictimBufferEntry { Set = set, Tag = tag, Data = data, Dirty = dirty, };
        VictimCacheOccupancy++;
    }

    // FIFO overflow / final disposal of a line leaving the victim buffer entirely: mirrors
    // FillBlock's own eviction-completion logic (Exclusive hand-off to a configured backing cache,
    // else flush-if-dirty to backing/WB buffer), so a buffered line is billed identically to an
    // ordinary write-back-mode eviction once it actually leaves the chip.
    private void DisposeVictimBufferEntry(VictimBufferEntry e, bool chargeStall) {
        ulong lineBase = (e.Tag << (OffsetBits + IndexBits)) | ((ulong)e.Set << OffsetBits);
        if (_backing is SetAssociativeCache { InclusionPolicy: InclusionPolicyKind.Exclusive, } outer) {
            outer.InsertVictim(lineBase, e.Data!, e.Dirty);
            return;
        }

        if (!e.Dirty) return;
        WritebackOrBuffer(lineBase, e.Data!, chargeStall, _wbBuffer != null);
        DirtyEvictions++;
    }

    // Scans the occupied logical window [_victimHead, _victimHead + _victimCount) for a (set, tag).
    // O(VictimCacheEntries) — fine, matches the linear-scan style already used for MSHR/WB lookups.
    private int FindVictimBufferSlot(int set, ulong tag) {
        if (_victimBuffer == null) return -1;
        for (var i = 0; i < VictimCacheOccupancy; i++) {
            int idx = (_victimHead + i) % VictimCacheEntries;
            if (_victimBuffer[idx].Set == set && _victimBuffer[idx].Tag == tag) return idx;
        }

        return -1;
    }

    // Removes the entry at physical index idx, shifting later logical entries back one slot to
    // keep the remaining entries' FIFO order intact. Buffer is small, so an O(n) shift is fine.
    private VictimBufferEntry RemoveVictimBufferSlot(int idx) {
        VictimBufferEntry removed = _victimBuffer![idx];
        int logicalPos = (idx - _victimHead + VictimCacheEntries) % VictimCacheEntries;
        for (int i = logicalPos; i < VictimCacheOccupancy - 1; i++) {
            int cur = (_victimHead + i) % VictimCacheEntries;
            int next = (_victimHead + i + 1) % VictimCacheEntries;
            _victimBuffer[cur] = _victimBuffer[next];
        }

        VictimCacheOccupancy--;
        return removed;
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
            bool dirty = valid && ((_dirty != null && _dirty[s][w]) || AnySectorDirty(s, w));
            lines[idx++] = new CacheLine(s, w, valid, tag, _policy.GetMetadata(s, w), blockCopy, dirty);
        }

        return lines;
    }

    private bool AnySectorDirty(int set, int way) {
        if (_sectorDirty == null) return false;
        bool[] dirty = _sectorDirty[set][way];
        for (var i = 0; i < _sectorsPerLine; i++)
            if (dirty[i])
                return true;
        return false;
    }

    // Write-back buffer: holds dirty-victim lines waiting to drain to backing.
    // null when wbCapacity == 0 (disabled); always null in write-through mode.
    private struct WbEntry {
        public ulong LineBase;
        public byte[]? Data;
    }

    // Jouppi victim buffer entry. Only the logical window [_victimHead, _victimHead+_victimCount)
    // of _victimBuffer is ever read — occupancy is tracked by _victimHead/_victimCount rather than
    // a null-Data sentinel scanned over the full array (unlike WbEntry/MshrEntry).
    private struct VictimBufferEntry {
        public int Set;
        public ulong Tag;
        public byte[]? Data;
        public bool Dirty;
    }

    // MSHRs: tracks in-flight demand fills (filled synchronously but the timing window still open).
    // null = unlimited (legacy behavior, MshrCount == 0).
    // A miss allocates a slot; a demand hit on the in-flight line charges the remaining
    // countdown (merge / hit-under-miss). TickMshr() decrements all countdowns each cycle.
    private struct MshrEntry {
        public ulong LineBase;
        public int Remaining;
    }
}