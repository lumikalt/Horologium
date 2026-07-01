using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// A bus façade used during the parallel phase-1 of two-phase concurrent multi-hart
/// simulation.  Bus operations are queued rather than executed immediately; <see cref="Drain"/>
/// replays them in issue order against the wrapped <see cref="MesiBus"/> during the
/// serial phase-2 (hart-0 → hart-N), then <see cref="Clear"/> resets the queue for the
/// next tick.
/// <para>
/// <see cref="BusRead"/> returns <c>false</c> so the requesting <see cref="MesiCache"/>
/// installs the line as Exclusive.  <see cref="Drain"/> then calls
/// <see cref="MesiCache.UpdateCoherenceState"/> (E→S correction) and
/// <see cref="MesiCache.RefillFromBacking"/> (re-read after a cross-hart writeback) to
/// match the state that sequential execution would have produced.
/// </para>
/// <para>
/// Bit-identical to sequential <see cref="MultiHartPipeline.Run"/> for well-synchronized
/// programs (no same-tick cross-hart write-then-read on the same cache line).
/// </para>
/// </summary>
public sealed class DeferredBus : IBus {
    private readonly MesiBus _real;
    private readonly List<BusOp> _queue = new();
    private readonly List<MesiCache> _caches = new();

    public IMemory Backing => _real.Backing;

    public DeferredBus(MesiBus real) {
        ArgumentNullException.ThrowIfNull(real);
        _real = real;
    }

    public void Register(MesiCache cache) {
        _real.Register(cache);
        _caches.Add(cache);
    }

    /// <summary>Queues a read-miss snoop. Returns <c>false</c>; phase 2 corrects E→S if needed.</summary>
    public bool BusRead(MesiCache requester, ulong lineBase) {
        _queue.Add(new BusOp(BusOpKind.Read, requester, lineBase));
        return false;
    }

    public void BusReadInvalidate(MesiCache requester, ulong lineBase) =>
        _queue.Add(new BusOp(BusOpKind.ReadInvalidate, requester, lineBase));

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
                    bool shared = _real.BusRead(op.Requester!, op.LineBase);
                    op.Requester!.UpdateCoherenceState(op.LineBase, shared);
                    op.Requester!.RefillFromBacking(op.LineBase);
                    break;
                case BusOpKind.ReadInvalidate: _real.BusReadInvalidate(op.Requester!, op.LineBase); break;
                case BusOpKind.SilentUpgrade:  _real.BusSilentUpgrade(op.LineBase); break;
                case BusOpKind.Load:           _real.BusLoad(op.LineBase); break;
                case BusOpKind.Writeback:      _real.Writeback(op.LineBase, op.Block!); break;
            }

        // Write all M-state lines directly to backing so the next tick's phase-1
        // fills see current data without waiting for a snoop-triggered writeback.
        foreach (MesiCache cache in _caches) cache.FlushToBacking();
    }

    /// <summary>Clears the pending op queue. Call after <see cref="Drain"/> each tick.</summary>
    public void Clear() => _queue.Clear();

    // ── Internal op record ────────────────────────────────────────────────────

    private enum BusOpKind {
        Read,
        ReadInvalidate,
        SilentUpgrade,
        Load,
        Writeback,
    }

    private readonly struct BusOp(
        BusOpKind kind,
        MesiCache? requester,
        ulong lineBase,
        byte[]? block = null
    ) {
        public readonly BusOpKind Kind = kind;
        public readonly MesiCache? Requester = requester;
        public readonly ulong LineBase = lineBase;
        public readonly byte[]? Block = block;
    }
}