using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// A bus façade used during the parallel phase-1 of two-phase concurrent multi-hart
/// simulation.  Bus operations are queued rather than executed immediately; <see cref="Drain"/>
/// replays them in issue order against the wrapped <see cref="MoesifBus"/> during the
/// serial phase-2 (hart-0 → hart-N), then <see cref="Clear"/> resets the queue for the
/// next tick.
/// <para>
/// <see cref="BusRead"/> returns <see cref="BusReadResponse.NoSharers"/> so the requesting
/// <see cref="MoesifCache"/> installs the line as Exclusive from backing.  <see cref="Drain"/>
/// then calls <see cref="MoesifCache.UpdateCoherenceState"/> (correcting the phase-1 E to
/// F or S per the replayed response) and refreshes the requester's line — from the peer's
/// cache-to-cache supply when a responder answered, otherwise from backing — to match the
/// state that sequential execution would have produced.
/// </para>
/// <para>
/// Bit-identical to sequential <c>MultiHartPipeline.Run</c> for well-synchronized
/// programs (no same-tick cross-hart write-then-read on the same cache line), provided
/// <see cref="MoesifCache.PeerSupplyLatency"/> equals <see cref="MoesifCache.MissLatency"/>
/// (the default) — phase-1 misses always charge the full miss latency because
/// cache-to-cache supply is only discovered during the phase-2 replay.
/// </para>
/// </summary>
public sealed class DeferredBus : IBus {
    private readonly MoesifBus _real;
    private readonly List<BusOp> _queue = [];
    private readonly List<MoesifCache> _caches = [];
    private byte[] _scratch = []; // reusable block buffer for phase-2 cache-to-cache fills

    public IMemory Backing => _real.Backing;

    public DeferredBus(MoesifBus real) {
        ArgumentNullException.ThrowIfNull(real);
        _real = real;
    }

    public void Register(MoesifCache cache) {
        _real.Register(cache);
        _caches.Add(cache);
    }

    /// <summary>Queues a read-miss snoop. Returns <see cref="BusReadResponse.NoSharers"/>
    /// (no cache-to-cache supply in phase 1); phase 2 corrects state and data.</summary>
    public BusReadResponse BusRead(MoesifCache requester, ulong lineBase, Span<byte> dest) {
        _queue.Add(new BusOp(BusOpKind.Read, requester, lineBase));
        return BusReadResponse.NoSharers;
    }

    public void BusSyncToBacking(ulong lineBase) =>
        _queue.Add(new BusOp(BusOpKind.SyncToBacking, null, lineBase));

    public void BusReadInvalidate(MoesifCache requester, ulong lineBase) =>
        _queue.Add(new BusOp(BusOpKind.ReadInvalidate, requester, lineBase));

    /// <summary>Queues an RFO snoop. Returns false (no forwarding in phase 1 — the
    /// requester fills from backing, authoritative at tick start); phase 2 replays the
    /// invalidation and discards the forwarded block, since the requester's phase-1
    /// write already made its Modified copy authoritative.</summary>
    public bool BusReadForOwnership(MoesifCache requester, ulong lineBase, Span<byte> dest) {
        _queue.Add(new BusOp(BusOpKind.ReadForOwnership, requester, lineBase));
        return false;
    }

    public void BusSilentUpgrade(ulong lineBase) =>
        _queue.Add(new BusOp(BusOpKind.SilentUpgrade, null, lineBase));

    public void BusLoad(ulong lineBase) =>
        _queue.Add(new BusOp(BusOpKind.Load, null, lineBase));

    public void Writeback(ulong lineBase, ReadOnlySpan<byte> block) {
        var copy = new byte[block.Length];
        block.CopyTo(copy);
        _queue.Add(new BusOp(BusOpKind.Writeback, null, lineBase, copy));
    }

    /// <summary>
    /// Replays all queued bus operations in issue order against the real bus.
    /// After each <see cref="BusOpKind.Read"/>, corrects the requester's coherence
    /// state and refreshes its cache line from backing.
    /// Call <see cref="Clear"/> afterwards to prepare for the next tick.
    /// </summary>
    public void Drain() {
        foreach (BusOp op in _queue)
            switch (op.Kind) {
                case BusOpKind.Read:
                    int blockBytes = op.Requester!.BlockBytes;
                    if (_scratch.Length < blockBytes) _scratch = new byte[blockBytes];
                    Span<byte> block = _scratch.AsSpan(0, blockBytes);
                    BusReadResponse response = _real.BusRead(op.Requester!, op.LineBase, block);
                    op.Requester!.UpdateCoherenceState(op.LineBase, response);
                    if (response is BusReadResponse.SuppliedClean or BusReadResponse.SuppliedDirty)
                        op.Requester!.RefillFromSupply(op.LineBase, block);
                    else
                        op.Requester!.RefillFromBacking(op.LineBase);
                    break;
                case BusOpKind.ReadInvalidate: _real.BusReadInvalidate(op.Requester!, op.LineBase); break;
                case BusOpKind.ReadForOwnership:
                    int rfoBytes = op.Requester!.BlockBytes;
                    if (_scratch.Length < rfoBytes) _scratch = new byte[rfoBytes];
                    _real.BusReadForOwnership(op.Requester!, op.LineBase, _scratch.AsSpan(0, rfoBytes));
                    break;
                case BusOpKind.SilentUpgrade: _real.BusSilentUpgrade(op.LineBase); break;
                case BusOpKind.Load:          _real.BusLoad(op.LineBase); break;
                case BusOpKind.Writeback:     _real.Writeback(op.LineBase, op.Block!); break;
                case BusOpKind.SyncToBacking: _real.BusSyncToBacking(op.LineBase); break;
            }

        // Write all dirty (M/O) lines directly to backing so the next tick's phase-1
        // fills see current data without waiting for a snoop-triggered writeback.
        foreach (MoesifCache cache in _caches) cache.FlushToBacking();
    }

    /// <summary>Clears the pending op queue. Call after <see cref="Drain"/> each tick.</summary>
    public void Clear() => _queue.Clear();

    // ── Internal op record ────────────────────────────────────────────────────

    private enum BusOpKind {
        Read,
        ReadInvalidate,
        ReadForOwnership,
        SilentUpgrade,
        Load,
        Writeback,
        SyncToBacking,
    }

    private readonly struct BusOp(
        BusOpKind kind,
        MoesifCache? requester,
        ulong lineBase,
        byte[]? block = null
    ) {
        public readonly BusOpKind Kind = kind;
        public readonly MoesifCache? Requester = requester;
        public readonly ulong LineBase = lineBase;
        public readonly byte[]? Block = block;
    }
}