#region

using System.Numerics;
using System.Text;
using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>One resident (or empty) line, for introspection/tooling — see <see cref="CeaserCache.GetSnapshot" />.</summary>
public sealed record CeaserCacheLine(int Partition, int Set, int Way, bool Valid, ulong Address, int LruAge, bool Dirty);

/// <summary>
///     A set-associative cache with a keyed, periodically re-randomized address→set mapping —
///     CEASER (Qureshi, MICRO 2018) with P = 1, or CEASER-S (Qureshi, ISCA 2019) with P &gt; 1 —
///     defending against cache-based side-channel attacks (Prime+Probe and relatives) that rely on
///     a conventional cache's static bit-slice indexing to build eviction sets.
///     <para>
///         <strong>Why a separate class, not a <see cref="SetAssociativeCache" /> option:</strong>
///         <see cref="SetAssociativeCache" />'s tag storage assumes the classic
///         <c>tag = address &gt;&gt; (offsetBits + indexBits)</c> split, and reconstructs a resident
///         line's full address by concatenating that stored tag with its physical set index at
///         8+ call sites (writeback, inclusion invalidation, eviction callbacks, checkpointing). A
///         keyed/randomized index breaks that reconstruction — the set a line lives in no longer
///         determines the low index bits of its address. Retrofitting every one of those call sites
///         would mean re-deriving the address from elsewhere for a class not designed around that.
///         <see cref="BdiCache" /> already establishes the precedent of a separate, focused class
///         for a cache variant with a fundamentally different addressing model rather than folding
///         it into the shared class every other feature depends on.
///     </para>
///     <para>
///         <strong>No invertible cipher needed.</strong> The papers' hardware design stores the
///         <em>encrypted</em> line address (ELA) as the tag — to keep the tag array the same width
///         as an ordinary cache — and must decrypt ELA→PLA on writeback, which is why they need an
///         invertible block cipher (a 4-stage Feistel network, their "LLBC"). That requirement is an
///         artifact of minimizing real tag-array bits; it does not apply to a functional simulator.
///         This implementation stores the <em>plaintext</em> line address directly as the tag
///         instead — observable-equivalent (identical hit/miss, eviction address, timing, and remap
///         behavior; nothing about tag storage format is user-visible) and it eliminates the need
///         for invertibility entirely, since nothing ever needs to be decrypted: writeback, the
///         remap sweep, and eviction all read the address straight off the tag. The index function
///         is accordingly just a keyed avalanche hash (<see cref="SetIndex" />, murmur3's
///         <c>fmix64</c> finalizer applied to <c>address XOR key</c> — the key is folded in before
///         avalanching, not appended after, so a key change genuinely redistributes the mapping),
///         with no Feistel/S-box/P-box machinery and no per-line EpochID bit (the paper's 1-bit
///         disambiguator between two same-set ELA tags that could coincidentally collide — moot
///         here, since tag comparison against a full plaintext address is never ambiguous). This
///         models CEASER's mapping-randomization and periodic-remap behavior — the part that
///         affects miss rate, latency, and data placement, everything this simulator can actually
///         measure — but makes no claim of cryptographic hardness against a real attacker.
///     </para>
///     <para>
///         <strong>Remap state machine</strong> (§IV of the CEASER paper): each partition keeps a
///         persistent sweeping set pointer <c>SPtr</c> and an access counter <c>ACtr</c>. Every
///         access increments <c>ACtr</c>; once it reaches <c>Aplr × waysPerPartition</c>, the set at
///         <c>SPtr</c> is remapped — each resident line there is re-indexed under the partition's
///         <c>NextKey</c> and relocated if its target set changed — then <c>SPtr</c> advances (mod
///         sets) and <c>ACtr</c> resets. When <c>SPtr</c> wraps to 0 a full epoch has swept every
///         set: <c>CurrKey</c> becomes <c>NextKey</c> and a fresh <c>NextKey</c> is drawn. A lookup
///         for address <c>A</c> checks <c>A</c>'s <c>CurrKey</c>-indexed set if that set is still
///         &gt;= <c>SPtr</c> (not yet swept this epoch), otherwise checks its <c>NextKey</c>-indexed
///         set (already swept and, if resident, already relocated there). Key generation and
///         partition selection use an explicit-state xorshift64 PRNG rather than <see cref="Random" />
///         specifically so that state (a single <c>ulong</c>) round-trips through
///         <see cref="WriteState" />/<see cref="ReadState" /> — a checkpoint restored mid-epoch must
///         regenerate the same future keys the original run would have, not restart the RNG from the
///         construction seed (which would silently diverge every mapping decision made after the
///         first post-restore epoch wrap).
///     </para>
///     <para>
///         <strong>CEASER-S partitioning</strong> (the ISA-S paper: "CEASER-S1 is the same as the
///         original CEASER design"): with <paramref name="partitions" /> P &gt; 1, the ways split
///         into P contiguous ranges, each with its own independent key/SPtr/ACtr state over the
///         same set count. A lookup checks every partition; a hit in any one is a cache hit. A miss
///         installs into a uniformly-randomly chosen partition (the paper: "CEASER-S randomly picks
///         the half in which to install the line"), evicting via that partition's own replacement
///         policy.
///     </para>
///     <para>
///         Scope matches <see cref="BdiCache" />'s own fidelity level: no sectoring, MSHR modeling,
///         victim buffer, bus banking, or inclusion cascade — the paper's own evaluation is a single
///         LLC level's hit/miss and side-channel-defense behavior, not the full feature matrix.
///         <see cref="InvalidateLine" />/<see cref="CleanLine" />/<see cref="FlushLine" /> are left
///         at their <see cref="IMemory" /> no-op defaults, same as <see cref="BdiCache" /> — a
///         deliberate scope match, not an oversight.
///     </para>
/// </summary>
public sealed class CeaserCache : IMemory {
    private readonly int[] _aCtr;
    private readonly IMemory _backing;
    private readonly int _blockBytes;
    private readonly byte[][][][] _blocks; // [partition][set][way][blockBytes]
    private readonly ulong[] _currKey;
    private readonly bool[][][]? _dirty; // [partition][set][way], non-null only in WriteBack mode
    private readonly int _encryptLatency;
    private readonly int _offsetMask;
    private readonly ulong[] _nextKey;
    private readonly int _partitions;
    private readonly IReplacementPolicy[] _policies;
    private readonly int[] _sPtr;
    private readonly int _sets;
    private readonly ulong?[][][] _tags; // [partition][set][way]: null = invalid
    private readonly int _waysPerPartition;
    private readonly WritePolicyKind _writePolicy;
    private readonly int _aplr;
    private long _pendingStalls;

    // xorshift64 state — used instead of System.Random so it can be checkpointed (a single ulong,
    // WriteState/ReadState below) rather than losing its position and silently diverging future
    // partition-selection/key-generation decisions across a save/restore round-trip.
    private ulong _rngState;

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Total cache size in bytes. Must be a power of 2.</param>
    /// <param name="ways">Total associativity, summed across all partitions. Must be a power of 2 and a multiple of <paramref name="partitions" />.</param>
    /// <param name="blockBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    /// <param name="partitions">CEASER-S partition count P (default 1 = plain CEASER; the paper's CEASER-S1 == CEASER).</param>
    /// <param name="aplr">Accesses-Per-Line-Remap: a set is remapped every <c>aplr × waysPerPartition</c> accesses (default 100, matching the paper).</param>
    /// <param name="seed">Seed for the per-partition key generator, for reproducible runs.</param>
    /// <param name="encryptLatency">Extra cycles per access modeling the index function's own compute latency (default 2, matching the paper's sensitivity study).</param>
    /// <param name="writePolicy">Write-hit policy — write-back always allocates on a write miss; write-through never does.</param>
    /// <param name="policyFactory">Optional per-partition replacement-policy factory, invoked with (sets, waysPerPartition). Defaults to LRU.</param>
    public CeaserCache(
        IMemory backing,
        int capacityBytes,
        int ways,
        int blockBytes,
        int missLatency,
        int partitions = 1,
        int aplr = 100,
        int seed = 12345,
        int encryptLatency = 2,
        WritePolicyKind writePolicy = WritePolicyKind.WriteThrough,
        Func<int, int, IReplacementPolicy?>? policyFactory = null
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aplr);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        ArgumentOutOfRangeException.ThrowIfNegative(encryptLatency);
        if (!BitOperations.IsPow2(capacityBytes) || !BitOperations.IsPow2(ways) || !BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");
        if (ways % partitions != 0)
            throw new ArgumentException("ways must be a multiple of partitions.");

        _backing = backing;
        _blockBytes = blockBytes;
        MissLatency = missLatency;
        _partitions = partitions;
        _aplr = aplr;
        _encryptLatency = encryptLatency;
        _writePolicy = writePolicy;
        _waysPerPartition = ways / partitions;

        _sets = capacityBytes / (ways * blockBytes);
        _offsetMask = blockBytes - 1;

        // xorshift64 requires a nonzero seed; mix the int seed through the same avalanche finalizer
        // SetIndex uses so small/adjacent seeds (0, 1, 2, ...) still start from well-distributed state.
        _rngState = MixSeed((ulong)seed);
        _currKey = new ulong[partitions];
        _nextKey = new ulong[partitions];
        _sPtr = new int[partitions];
        _aCtr = new int[partitions];
        _policies = new IReplacementPolicy[partitions];
        _tags = new ulong?[partitions][][];
        _blocks = new byte[partitions][][][];
        _dirty = writePolicy == WritePolicyKind.WriteBack ? new bool[partitions][][] : null;

        for (var p = 0; p < partitions; p++) {
            _currKey[p] = NextRandomKey();
            _nextKey[p] = NextRandomKey();
            _policies[p] = policyFactory?.Invoke(_sets, _waysPerPartition) ?? new LruPolicy(_sets, _waysPerPartition);
            _tags[p] = new ulong?[_sets][];
            _blocks[p] = new byte[_sets][][];
            _dirty?[p] = new bool[_sets][];
            for (var s = 0; s < _sets; s++) {
                _tags[p][s] = new ulong?[_waysPerPartition];
                _blocks[p][s] = new byte[_waysPerPartition][];
                _dirty?[p][s] = new bool[_waysPerPartition];
                for (var w = 0; w < _waysPerPartition; w++) _blocks[p][s][w] = new byte[blockBytes];
            }
        }
    }

    public int MissLatency { get; }

    /// <summary>CEASER-S partition count P (1 = plain CEASER).</summary>
    public int Partitions => _partitions;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }

    /// <summary>Number of completed remap steps (one set relocated per step) across all partitions — for cadence verification.</summary>
    public long RemapSteps { get; private set; }

    /// <summary>Number of lines actually relocated to a different set by a remap step (a subset of what <see cref="RemapSteps" /> touches).</summary>
    public long RemapRelocations { get; private set; }

    public ulong Read(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) return _backing.Read(address, bytes);

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int p, out int set, out int way)) {
            Hits++;
            _policies[p].RecordHit(set, way);
            _pendingStalls += _encryptLatency;
            OnAccess();
            return ReadBytes(_blocks[p][set][way], offset, bytes);
        }

        Misses++;
        _pendingStalls += MissLatency + _encryptLatency;
        (p, set, way) = FillLine(lineAddr);
        OnAccess();
        return ReadBytes(_blocks[p][set][way], offset, bytes);
    }

    /// <summary>
    ///     InvisiSpec (Yan et al., MICRO 2018): non-mutating speculative-buffer peek — a hit reads
    ///     the resident block with no policy/remap-counter update; a miss recurses to the backing
    ///     level rather than installing a line. Mirrors <see cref="BdiCache.PeekRead" />: the remap
    ///     state machine (<c>ACtr</c>/<c>SPtr</c>/keys) is exactly the kind of eviction-accounting
    ///     state a wrong-path peek must not disturb, so <see cref="OnAccess" /> is not called here.
    /// </summary>
    public ulong PeekRead(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) return _backing.PeekRead(address, bytes);

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int p, out int set, out int way)) return ReadBytes(_blocks[p][set][way], offset, bytes);

        _pendingStalls += MissLatency;
        return _backing.PeekRead(address, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        if (_writePolicy == WritePolicyKind.WriteThrough) _backing.Write(address, value, bytes);

        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) {
            ulong end = address + (ulong)bytes;
            if (_writePolicy == WritePolicyKind.WriteBack) {
                for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes)
                    if (TryFind(a, out int fp, out int fs, out int fw))
                        FlushIfDirty(fp, fs, fw);

                _backing.Write(address, value, bytes);
            }

            for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes)
                if (TryFind(a, out int fp, out int fs, out int fw))
                    InvalidateWay(fp, fs, fw);

            return;
        }

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int p, out int set, out int way)) {
            Hits++;
            _policies[p].RecordHit(set, way);
            WriteBytes(_blocks[p][set][way], offset, value, bytes);
            if (_dirty != null) _dirty[p][set][way] = true;
            _pendingStalls += _encryptLatency;
            OnAccess();
            return;
        }

        Misses++;
        if (_writePolicy == WritePolicyKind.WriteBack) {
            _pendingStalls += MissLatency + _encryptLatency;
            (p, set, way) = FillLine(lineAddr);
            WriteBytes(_blocks[p][set][way], offset, value, bytes);
            _dirty![p][set][way] = true;
        }
        else {
            _pendingStalls += MissLatency + _encryptLatency;
        }

        OnAccess();
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        _backing.Load(address, data);
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes)
            if (TryFind(a, out int p, out int set, out int way))
                InvalidateWay(p, set, way);
    }

    /// <summary>Returns and clears the accumulated miss-penalty/encrypt-latency cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
    }

    /// <summary>Per-line introspection across every partition — for tooling (e.g. the Waveform viewer) and tests; mirrors <see cref="SetAssociativeCache.GetSnapshot" />.</summary>
    public CeaserCacheLine[] GetSnapshot() {
        var lines = new List<CeaserCacheLine>(_partitions * _sets * _waysPerPartition);
        for (var p = 0; p < _partitions; p++)
        for (var s = 0; s < _sets; s++)
        for (var w = 0; w < _waysPerPartition; w++) {
            bool valid = _tags[p][s][w].HasValue;
            ulong addr = _tags[p][s][w] ?? 0;
            bool dirty = valid && (_dirty?[p][s][w] ?? false);
            lines.Add(new CeaserCacheLine(p, s, w, valid, addr, _policies[p].GetMetadata(s, w), dirty));
        }

        return lines.ToArray();
    }

    /// <summary>
    ///     The set <paramref name="address" /> currently maps to within one partition — for
    ///     tooling (e.g. visualizing where a line currently resides) and tests. Non-mutating; does
    ///     not affect <see cref="Hits" />/<see cref="Misses" />/remap state.
    /// </summary>
    public int SetOf(int partition, ulong address) => TargetSet(partition, address & ~(ulong)_offsetMask);

    /// <summary>Serializes tag/block/dirty/remap state for a microarchitectural checkpoint, mirroring <see cref="BdiCache.WriteState" />.</summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_partitions);
        w.Write(_sets);
        w.Write(_waysPerPartition);
        w.Write(_blockBytes);
        w.Write(_rngState);

        for (var p = 0; p < _partitions; p++) {
            w.Write(_currKey[p]);
            w.Write(_nextKey[p]);
            w.Write(_sPtr[p]);
            w.Write(_aCtr[p]);

            for (var s = 0; s < _sets; s++)
            for (var wy = 0; wy < _waysPerPartition; wy++) {
                ulong? tag = _tags[p][s][wy];
                w.Write(tag.HasValue);
                if (tag.HasValue) w.Write(tag.Value);
                w.Write(_blocks[p][s][wy]);
                w.Write(_dirty?[p][s][wy] ?? false);
            }

            using var policyMs = new MemoryStream();
            using (var policyW = new BinaryWriter(policyMs, Encoding.UTF8, true)) {
                policyW.Write(_policies[p].GetType().FullName ?? "");
                _policies[p].WriteState(policyW);
            }

            byte[] policyBlob = policyMs.ToArray();
            w.Write(policyBlob.Length);
            if (policyBlob.Length > 0) w.Write(policyBlob);
        }
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Geometry must match exactly.</summary>
    /// <exception cref="CheckpointException">Cache geometry does not match.</exception>
    public void ReadState(BinaryReader r) {
        int partitions = r.ReadInt32();
        int sets = r.ReadInt32();
        int waysPerPartition = r.ReadInt32();
        int blockBytes = r.ReadInt32();
        if (partitions != _partitions || sets != _sets || waysPerPartition != _waysPerPartition || blockBytes != _blockBytes)
            throw new CheckpointException(
                $"CeaserCache.ReadState: geometry mismatch — checkpoint has " +
                $"partitions={partitions} sets={sets} waysPerPartition={waysPerPartition} blockBytes={blockBytes}, " +
                $"but this cache has partitions={_partitions} sets={_sets} waysPerPartition={_waysPerPartition} " +
                $"blockBytes={_blockBytes}."
            );
        _rngState = r.ReadUInt64();

        for (var p = 0; p < partitions; p++) {
            _currKey[p] = r.ReadUInt64();
            _nextKey[p] = r.ReadUInt64();
            _sPtr[p] = r.ReadInt32();
            _aCtr[p] = r.ReadInt32();

            for (var s = 0; s < sets; s++)
            for (var wy = 0; wy < waysPerPartition; wy++) {
                bool hasTag = r.ReadBoolean();
                ulong tag = hasTag ? r.ReadUInt64() : 0;
                _tags[p][s][wy] = hasTag ? tag : null;
                byte[] block = r.ReadBytes(blockBytes);
                Buffer.BlockCopy(block, 0, _blocks[p][s][wy], 0, blockBytes);
                bool dirty = r.ReadBoolean();
                if (_dirty != null) _dirty[p][s][wy] = dirty;
            }

            int policyBlobLen = r.ReadInt32();
            if (policyBlobLen > 0) {
                byte[] policyBlob = r.ReadBytes(policyBlobLen);
                using var policyMs = new MemoryStream(policyBlob);
                using var policyR = new BinaryReader(policyMs);
                string policyType = policyR.ReadString();
                if (policyType == (_policies[p].GetType().FullName ?? "")) _policies[p].ReadState(policyR);
            }
        }
    }

    // ── Keyed indexing ───────────────────────────────────────────────────────

    /// <summary>
    ///     Keyed avalanche hash mapping a line address to a set index — murmur3's <c>fmix64</c>
    ///     finalizer applied to <c>address XOR key</c>. The key is folded in before avalanching
    ///     (not appended after), so a key change genuinely redistributes the mapping. See the class
    ///     docs for why this replaces the papers' invertible Feistel cipher.
    /// </summary>
    private static int SetIndex(ulong lineAddress, ulong key, int numSets) {
        ulong h = lineAddress ^ key;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return (int)(h & (ulong)(numSets - 1));
    }

    private static ulong MixSeed(ulong x) {
        ulong h = x == 0 ? 0x9e3779b97f4a7c15UL : x; // xorshift64 requires nonzero state
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h == 0 ? 1 : h;
    }

    private ulong NextRandomU64() {
        _rngState ^= _rngState << 13;
        _rngState ^= _rngState >> 7;
        _rngState ^= _rngState << 17;
        return _rngState;
    }

    private ulong NextRandomKey() => NextRandomU64();

    /// <summary>
    ///     The set a line address currently resides in (or would be installed into) within one
    ///     partition — <c>CurrKey</c>'s target set if that set hasn't been swept past by
    ///     <c>SPtr</c> yet this epoch, otherwise <c>NextKey</c>'s target set (already swept, so a
    ///     resident line was already relocated there).
    /// </summary>
    private int TargetSet(int p, ulong lineAddr) {
        int setCurr = SetIndex(lineAddr, _currKey[p], _sets);
        return setCurr >= _sPtr[p] ? setCurr : SetIndex(lineAddr, _nextKey[p], _sets);
    }

    private bool TryFind(ulong lineAddr, out int foundP, out int foundSet, out int foundWay) {
        for (var p = 0; p < _partitions; p++) {
            int set = TargetSet(p, lineAddr);
            for (var w = 0; w < _waysPerPartition; w++)
                if (_tags[p][set][w] == lineAddr) {
                    foundP = p;
                    foundSet = set;
                    foundWay = w;
                    return true;
                }
        }

        foundP = foundSet = foundWay = -1;
        return false;
    }

    // ── Fill / eviction / remap ──────────────────────────────────────────────

    private (int p, int set, int way) FillLine(ulong lineAddr) {
        var fresh = new byte[_blockBytes];
        for (var i = 0; i < _blockBytes; i++) fresh[i] = (byte)_backing.Read(lineAddr + (ulong)i, 1);

        int p = _partitions == 1 ? 0 : (int)(NextRandomU64() % (ulong)_partitions);
        int set = TargetSet(p, lineAddr);
        int way = PlaceLine(p, set, lineAddr, fresh, false);
        return (p, set, way);
    }

    /// <summary>Installs <paramref name="data" /> at (p, set), evicting via that partition's replacement policy if the set is full.</summary>
    private int PlaceLine(int p, int set, ulong lineAddr, byte[] data, bool dirty) {
        int way = FindFreeWay(p, set);
        if (way < 0) {
            way = _policies[p].ChooseVictim(set);
            EvictWay(p, set, way);
        }

        Buffer.BlockCopy(data, 0, _blocks[p][set][way], 0, _blockBytes);
        _tags[p][set][way] = lineAddr;
        if (_dirty != null) _dirty[p][set][way] = dirty;
        _policies[p].RecordInstall(set, way);
        return way;
    }

    private int FindFreeWay(int p, int set) {
        for (var w = 0; w < _waysPerPartition; w++)
            if (!_tags[p][set][w].HasValue)
                return w;
        return -1;
    }

    private void EvictWay(int p, int set, int way) {
        if (_tags[p][set][way] is null) return;
        FlushIfDirty(p, set, way);
        _tags[p][set][way] = null;
        if (_dirty != null) _dirty[p][set][way] = false;
        Evictions++;
    }

    private void InvalidateWay(int p, int set, int way) {
        _tags[p][set][way] = null;
        if (_dirty != null) _dirty[p][set][way] = false;
    }

    private void FlushIfDirty(int p, int set, int way) {
        if (_dirty == null || !_dirty[p][set][way] || _tags[p][set][way] is not { } tag) return;
        byte[] block = _blocks[p][set][way];
        for (var i = 0; i < _blockBytes; i++) _backing.Write(tag + (ulong)i, block[i], 1);
        _dirty[p][set][way] = false;
        DirtyEvictions++;
    }

    /// <summary>Advances the remap state machine by one access for every partition — not called from <see cref="PeekRead" />, see its docs.</summary>
    private void OnAccess() {
        for (var p = 0; p < _partitions; p++) {
            if (++_aCtr[p] < _aplr * _waysPerPartition) continue;
            RemapStep(p);
        }
    }

    private void RemapStep(int p) {
        int set = _sPtr[p];
        for (var w = 0; w < _waysPerPartition; w++) {
            if (_tags[p][set][w] is not { } addr) continue;
            int newSet = SetIndex(addr, _nextKey[p], _sets);
            if (newSet == set) continue;

            byte[] data = _blocks[p][set][w];
            bool dirty = _dirty?[p][set][w] ?? false;
            var moved = new byte[_blockBytes];
            Buffer.BlockCopy(data, 0, moved, 0, _blockBytes);
            InvalidateWay(p, set, w);
            PlaceLine(p, newSet, addr, moved, dirty);
            RemapRelocations++;
        }

        RemapSteps++;
        _sPtr[p] = (_sPtr[p] + 1) % _sets;
        _aCtr![p] = 0;
        if (_sPtr[p] == 0) {
            _currKey[p] = _nextKey[p];
            _nextKey[p] = NextRandomKey();
        }
    }

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
