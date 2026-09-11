#region

using System.Numerics;
using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>One resident (or empty) line, for introspection/tooling — see <see cref="ScatterCache.GetSnapshot" />.</summary>
public sealed record ScatterCacheLine(int Way, int Row, bool Valid, ulong Address, bool Dirty);

/// <summary>
///     A cache where each way is indexed <em>independently</em> by a keyed, security-domain-aware
///     function — ScatterCache (Werner et al., USENIX Security 2019). Unlike a conventional
///     set-associative cache (or CEASER/CEASER-S, which still index every way of a given set
///     identically), no fixed "set" of <c>nways</c> lines exists for a given address: each way
///     computes its own row via the Index Derivation Function (IDF), so the <c>nways</c> lines that
///     happen to hold an address's data are usually scattered across <c>nways</c> different rows.
///     This makes finding addresses that collide across <em>every</em> way — the eviction sets
///     Prime+Probe needs — combinatorially harder than defeating a single shared index (CEASER's
///     threat model), on top of the address→index unpredictability CEASER already provides.
///     <para>
///         <strong>Why a separate class, not <see cref="CeaserCache" /> generalized:</strong> CEASER's
///         shape (one shared set index, all ways of that set considered together) cannot represent
///         "each way has its own independently-computed row" — there is no single set for
///         <see cref="IReplacementPolicy.ChooseVictim" /> to operate over, so this needed its own
///         per-way parallel arrays rather than reusing <see cref="CeaserCache" />'s per-set ones.
///     </para>
///     <para>
///         <strong>No invertible cipher needed, same reasoning as CEASER.</strong> The paper's
///         hardware stores index bits alongside the tag (&lt;5% overhead) so a non-invertible IDF's
///         write-back path can still locate the line. A functional simulator has no such constraint:
///         storing the <em>plaintext</em> line address as the tag (as <see cref="CeaserCache" />
///         already does) sidesteps invertibility entirely. This also means the paper's SCv1 (hashing)
///         variant is sufficient — SCv2's tag-dependent permutation exists purely to avoid
///         <em>performance</em>-degrading birthday-bound index collisions in real hardware, not a
///         correctness concern here. <see cref="Idf" /> is the same murmur3 <c>fmix64</c> avalanche
///         mix <see cref="CeaserCache" /> uses, with the way index and the Security-Domain ID (SDID)
///         folded in before avalanching alongside the key, so each way and each domain get an
///         independent mapping for the same address.
///     </para>
///     <para>
///         <strong>Random replacement, not pluggable.</strong> The paper mandates simple random
///         replacement among the <c>nways</c> candidate slots specifically to avoid introducing a
///         systematic bias and to simplify security analysis — this is hardcoded here (via the same
///         checkpoint-serializable xorshift64 state <see cref="CeaserCache" /> uses instead of
///         <see cref="Random" />), not a shortcut: an <see cref="IReplacementPolicy" /> couldn't
///         express this shape anyway (see above).
///     </para>
///     <para>
///         <strong>Rekeying is a full flush, not gradual remap.</strong> Unlike CEASER's persistent
///         sweeping remap, the paper is explicit that a key change always accompanies a full cache
///         flush (write-back: flush every dirty line first; then invalidate everything) — no
///         incremental relocation machinery. <see cref="Rekey" /> does this on demand;
///         <c>rekeyInterval</c> (0 = disabled, matching the paper's own "it is unclear if
///         adding the additional hardware complexity [of dynamic remapping] is worthwhile...
///         performing an occasional cache flush... can be the better choice") triggers it
///         automatically every N accesses.
///     </para>
///     <para>
///         <strong>SDID correctness note</strong> (paper §3.4): a resident line's location depends on
///         which SDID it was written under. <see cref="Load" /> (bulk backing writes — an initial
///         program image, or a coherence writeback) only invalidates a line if it still resolves
///         under the <em>current</em> <see cref="SetRequestSdid" /> value at call time. This mirrors
///         the paper's own explicit constraint — shared/writable memory must always be accessed with
///         the same SDID it was written under, and software (the OS) is responsible for ensuring no
///         dirty lines remain resident when reassigning a page's domain — not a bug to work around.
///     </para>
///     <para>
///         Scope matches <see cref="CeaserCache" />/<see cref="BdiCache" />'s fidelity level: no
///         sectoring, MSHR modeling, victim buffer, bus banking, or inclusion cascade.
///         <see cref="IMemory.InvalidateLine" />/<see cref="IMemory.CleanLine" />/
///         <see cref="IMemory.FlushLine" /> are left at
///         their <see cref="IMemory" /> no-op defaults, same deliberate scope match.
///     </para>
/// </summary>
public sealed class ScatterCache : IMemory {
    private readonly IMemory _backing;
    private readonly int _blockBytes;
    private readonly byte[][][] _blocks; // [way][row][blockBytes]
    private readonly bool[][]? _dirty;   // [way][row], non-null only in WriteBack mode
    private readonly int _offsetMask;
    private readonly int _rekeyInterval;
    private readonly int _rowsPerWay;
    private readonly ulong?[][] _tags; // [way][row]: null = invalid
    private readonly int _ways;
    private readonly WritePolicyKind _writePolicy;
    private long _accessesSinceRekey;
    private ulong _key;
    private long _pendingStalls;
    private ulong _rngState; // xorshift64 — see class docs for why not System.Random
    private int _sdid;

    /// <param name="backing">Backing memory.</param>
    /// <param name="capacityBytes">Total cache size in bytes. Must be a power of 2.</param>
    /// <param name="ways">Associativity (nways). Must be a power of 2.</param>
    /// <param name="blockBytes">Cache line size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    /// <param name="rekeyInterval">
    ///     Accesses between automatic <see cref="Rekey" /> calls (0 = manual/external only, matching
    ///     the paper's own default preference).
    /// </param>
    /// <param name="seed">Seed for the key/replacement PRNG, for reproducible runs.</param>
    /// <param name="writePolicy">Write-hit policy — write-back always allocates on a write miss; write-through never does.</param>
    public ScatterCache(
        IMemory backing,
        int capacityBytes,
        int ways,
        int blockBytes,
        int missLatency,
        int rekeyInterval = 0,
        int seed = 12345,
        WritePolicyKind writePolicy = WritePolicyKind.WriteThrough
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rekeyInterval);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        if (!BitOperations.IsPow2(capacityBytes) || !BitOperations.IsPow2(ways) || !BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("Cache dimensions must be powers of 2.");

        _backing = backing;
        _blockBytes = blockBytes;
        MissLatency = missLatency;
        _ways = ways;
        _rekeyInterval = rekeyInterval;
        _writePolicy = writePolicy;

        _rowsPerWay = capacityBytes / (ways * blockBytes);
        _offsetMask = blockBytes - 1;

        _rngState = MixSeed((ulong)seed);
        _key = NextRandomU64();

        _tags = new ulong?[ways][];
        _blocks = new byte[ways][][];
        _dirty = writePolicy == WritePolicyKind.WriteBack ? new bool[ways][] : null;
        for (var w = 0; w < ways; w++) {
            _tags[w] = new ulong?[_rowsPerWay];
            _blocks[w] = new byte[_rowsPerWay][];
            _dirty?[w] = new bool[_rowsPerWay];
            for (var r = 0; r < _rowsPerWay; r++) _blocks[w][r] = new byte[blockBytes];
        }
    }

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long DirtyEvictions { get; private set; }

    /// <summary>Number of completed rekey events (manual or automatic) — for cadence verification.</summary>
    public long RekeyCount { get; private set; }

    public ulong Read(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) return _backing.Read(address, bytes);

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int way, out int row)) {
            Hits++;
            OnAccess();
            return ReadBytes(_blocks[way][row], offset, bytes);
        }

        Misses++;
        _pendingStalls += MissLatency;
        (way, row) = FillLine(lineAddr);
        OnAccess();
        return ReadBytes(_blocks[way][row], offset, bytes);
    }

    /// <summary>
    ///     InvisiSpec (Yan et al., MICRO 2018): non-mutating speculative-buffer peek — a hit reads
    ///     the resident block with no rekey-counter update; a miss recurses to the backing level
    ///     rather than installing a line. Mirrors <see cref="CeaserCache.PeekRead" />: the rekey
    ///     timer is exactly the kind of eviction-accounting state a wrong-path peek must not
    ///     disturb, so <see cref="OnAccess" /> is not called here.
    /// </summary>
    public ulong PeekRead(ulong address, int bytes) {
        var offset = (int)(address & (ulong)_offsetMask);
        if (offset + bytes > _blockBytes) return _backing.PeekRead(address, bytes);

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int way, out int row)) return ReadBytes(_blocks[way][row], offset, bytes);

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
                    if (TryFind(a, out int fw, out int fr))
                        FlushIfDirty(fw, fr);

                _backing.Write(address, value, bytes);
            }

            for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes)
                if (TryFind(a, out int fw, out int fr))
                    InvalidateWay(fw, fr);

            return;
        }

        ulong lineAddr = address & ~(ulong)_offsetMask;
        if (TryFind(lineAddr, out int way, out int row)) {
            Hits++;
            WriteBytes(_blocks[way][row], offset, value, bytes);
            if (_dirty != null) _dirty[way][row] = true;
            OnAccess();
            return;
        }

        Misses++;
        if (_writePolicy == WritePolicyKind.WriteBack) {
            _pendingStalls += MissLatency;
            (way, row) = FillLine(lineAddr);
            WriteBytes(_blocks[way][row], offset, value, bytes);
            _dirty![way][row] = true;
        }
        else { _pendingStalls += MissLatency; }

        OnAccess();
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        _backing.Load(address, data);
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & ~(ulong)_offsetMask; a < end; a += (ulong)_blockBytes)
            if (TryFind(a, out int way, out int row))
                InvalidateWay(way, row);
    }

    /// <summary>
    ///     Notifies this level of the Security-Domain ID for the next access — mirrors
    ///     <see cref="IMemory.SetRequestPc" />'s "supply extra addressing context before the
    ///     corresponding Read/Write" pattern. Default 0 (single domain), matching the paper's own
    ///     "still provides protection without software support" fallback.
    /// </summary>
    public void SetRequestSdid(int sdid) => _sdid = sdid;

    /// <summary>
    ///     Rekeys the cache: flushes every dirty line to backing (write-back mode only), invalidates
    ///     every resident line, and draws a fresh key. Matches the paper's key-management section —
    ///     a key change is always a full cache flush, never a partial/incremental remap.
    /// </summary>
    public void Rekey() {
        if (_dirty != null)
            for (var w = 0; w < _ways; w++)
            for (var r = 0; r < _rowsPerWay; r++)
                FlushIfDirty(w, r);

        for (var w = 0; w < _ways; w++)
        for (var r = 0; r < _rowsPerWay; r++)
            InvalidateWay(w, r);

        _key = NextRandomU64();
        RekeyCount++;
        _accessesSinceRekey = 0;
    }

    /// <summary>
    ///     Writes every dirty line to backing without invalidating anything — mirrors
    ///     <see cref="SetAssociativeCache.FlushAllToBacking" />. For inspecting or comparing final
    ///     backing-memory state after a run ends, distinct from <see cref="Rekey" /> (which also
    ///     invalidates and draws a fresh key).
    /// </summary>
    public void FlushAllToBacking() {
        if (_dirty == null) return;
        for (var w = 0; w < _ways; w++)
        for (var r = 0; r < _rowsPerWay; r++)
            FlushIfDirty(w, r);
    }

    /// <summary>Returns and clears the accumulated miss-penalty cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
    }

    /// <summary>
    ///     Per-line introspection across every way — for tooling and tests; mirrors
    ///     <see cref="SetAssociativeCache.GetSnapshot" />.
    /// </summary>
    public ScatterCacheLine[] GetSnapshot() {
        var lines = new List<ScatterCacheLine>(_ways * _rowsPerWay);
        for (var w = 0; w < _ways; w++)
        for (var r = 0; r < _rowsPerWay; r++) {
            bool valid = _tags[w][r].HasValue;
            ulong addr = _tags[w][r] ?? 0;
            bool dirty = valid && (_dirty?[w][r] ?? false);
            lines.Add(new ScatterCacheLine(w, r, valid, addr, dirty));
        }

        return lines.ToArray();
    }

    /// <summary>
    ///     The row <paramref name="address" /> currently maps to in one way, under the current SDID
    ///     and key — for tooling and tests. Non-mutating; does not affect
    ///     <see cref="Hits" />/<see cref="Misses" />/rekey state.
    /// </summary>
    public int IdfRow(int way, ulong address) => Idf(address & ~(ulong)_offsetMask, _sdid, _key, way);

    /// <summary>
    ///     Serializes tag/block/dirty/key/rekey state for a microarchitectural checkpoint, mirroring
    ///     <see cref="CeaserCache.WriteState" />.
    /// </summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_ways);
        w.Write(_rowsPerWay);
        w.Write(_blockBytes);
        w.Write(_rngState);
        w.Write(_key);
        w.Write(_sdid);
        w.Write(_accessesSinceRekey);

        for (var way = 0; way < _ways; way++)
        for (var row = 0; row < _rowsPerWay; row++) {
            ulong? tag = _tags[way][row];
            w.Write(tag.HasValue);
            if (tag.HasValue) w.Write(tag.Value);
            w.Write(_blocks[way][row]);
            w.Write(_dirty?[way][row] ?? false);
        }
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Geometry must match exactly.</summary>
    /// <exception cref="CheckpointException">Cache geometry does not match.</exception>
    public void ReadState(BinaryReader r) {
        int ways = r.ReadInt32();
        int rowsPerWay = r.ReadInt32();
        int blockBytes = r.ReadInt32();
        if (ways != _ways || rowsPerWay != _rowsPerWay || blockBytes != _blockBytes)
            throw new CheckpointException(
                $"ScatterCache.ReadState: geometry mismatch — checkpoint has " +
                $"ways={ways} rowsPerWay={rowsPerWay} blockBytes={blockBytes}, " +
                $"but this cache has ways={_ways} rowsPerWay={_rowsPerWay} blockBytes={_blockBytes}."
            );
        _rngState = r.ReadUInt64();
        _key = r.ReadUInt64();
        _sdid = r.ReadInt32();
        _accessesSinceRekey = r.ReadInt64();

        for (var way = 0; way < ways; way++)
        for (var row = 0; row < rowsPerWay; row++) {
            bool hasTag = r.ReadBoolean();
            ulong tag = hasTag ? r.ReadUInt64() : 0;
            _tags[way][row] = hasTag ? tag : null;
            byte[] block = r.ReadBytes(blockBytes);
            Buffer.BlockCopy(block, 0, _blocks[way][row], 0, blockBytes);
            bool dirty = r.ReadBoolean();
            if (_dirty != null) _dirty[way][row] = dirty;
        }
    }

    // ── Index Derivation Function (SCv1 hashing variant) ──────────────────────

    /// <summary>
    ///     Keyed avalanche hash mapping a line address, SDID, key, and way index to a row — murmur3's
    ///     <c>fmix64</c> finalizer, same as <see cref="CeaserCache.SetIndex" />, extended with the way
    ///     index and SDID folded in before avalanching so each way and each security domain get an
    ///     independent mapping for the same address. See the class docs for why this SCv1-style
    ///     hashing variant is sufficient (no SCv2 permutation, no invertibility requirement).
    /// </summary>
    private int Idf(ulong lineAddress, int sdid, ulong key, int way) {
        ulong h = lineAddress ^ key ^ ((ulong)(uint)sdid << 32) ^ ((ulong)(way + 1) * 0x9e3779b97f4a7c15UL);
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return (int)(h & (ulong)(_rowsPerWay - 1));
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

    private bool TryFind(ulong lineAddr, out int foundWay, out int foundRow) {
        for (var w = 0; w < _ways; w++) {
            int row = Idf(lineAddr, _sdid, _key, w);
            if (_tags[w][row] == lineAddr) {
                foundWay = w;
                foundRow = row;
                return true;
            }
        }

        foundWay = foundRow = -1;
        return false;
    }

    // ── Fill / eviction / rekey ────────────────────────────────────────────────

    private (int way, int row) FillLine(ulong lineAddr) {
        var fresh = new byte[_blockBytes];
        for (var i = 0; i < _blockBytes; i++) fresh[i] = (byte)_backing.Read(lineAddr + (ulong)i, 1);

        // Prefer an empty candidate slot among the nways ways (mirroring CeaserCache/SetAssociativeCache's
        // find-free-before-evict pattern) so a warming cache fills before it starts self-evicting. Random
        // replacement only kicks in once every one of the nways candidates is occupied — that's the paper's
        // "random replacement among the candidates" scope, not "random even when free slots exist".
        int way = -1, row = -1;
        for (var w = 0; w < _ways; w++) {
            int r = Idf(lineAddr, _sdid, _key, w);
            if (_tags[w][r].HasValue) continue;
            way = w;
            row = r;
            break;
        }

        if (way < 0) {
            way = (int)(NextRandomU64() % (ulong)_ways); // random replacement among the nways candidates
            row = Idf(lineAddr, _sdid, _key, way);
            EvictWay(way, row);
        }

        Buffer.BlockCopy(fresh, 0, _blocks[way][row], 0, _blockBytes);
        _tags[way][row] = lineAddr;
        if (_dirty != null) _dirty[way][row] = false;
        return (way, row);
    }

    private void EvictWay(int way, int row) {
        FlushIfDirty(way, row);
        _tags[way][row] = null;
        if (_dirty != null) _dirty[way][row] = false;
        Evictions++;
    }

    private void InvalidateWay(int way, int row) {
        _tags[way][row] = null;
        if (_dirty != null) _dirty[way][row] = false;
    }

    private void FlushIfDirty(int way, int row) {
        if (_dirty == null || !_dirty[way][row] || _tags[way][row] is not { } tag) return;
        byte[] block = _blocks[way][row];
        for (var i = 0; i < _blockBytes; i++) _backing.Write(tag + (ulong)i, block[i], 1);
        _dirty[way][row] = false;
        DirtyEvictions++;
    }

    /// <summary>Advances the auto-rekey counter by one access — not called from <see cref="PeekRead" />, see its docs.</summary>
    private void OnAccess() {
        if (_rekeyInterval <= 0) return;
        if (++_accessesSinceRekey >= _rekeyInterval) Rekey();
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