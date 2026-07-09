namespace F18A.Arbors;

/// <summary>
/// A synchronous rendezvous channel between two adjacent F18A nodes.
/// Neither side consumes the value until BOTH a writer and a reader are
/// present in the same simulated tick — preventing double-delivery on retry.
/// </summary>
public sealed class RendezvousArbor {
    private uint? _pending; // value written by sender, awaiting a reader
    private bool _reading;  // a reader declared intent this tick

    /// <summary>
    /// Attempt a write. Returns true when a reader is simultaneously waiting
    /// (transfer completes this tick). Returns false when no reader is present
    /// yet; the value is buffered and the sender should retry next tick.
    /// </summary>
    public bool TryWrite(uint value) {
        _pending = value;
        if (_reading) {
            _reading = false;
            _pending = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempt a read. Returns true when a writer's value is available
    /// (transfer completes this tick). Returns false when no value is pending
    /// yet; the receiver should retry next tick.
    /// </summary>
    public bool TryRead(out uint value) {
        _reading = true;
        if (_pending.HasValue) {
            value = _pending.Value;
            _pending = null;
            _reading = false;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Returns true when an access will complete without blocking:
    /// read-side: a pending write exists; write-side: a reader is waiting.
    /// </summary>
    public bool HasPending(bool forRead) => forRead ? _pending.HasValue : _reading;

    /// <summary>
    /// Clears stale state at the start of each tick (before nodes run).
    /// Any un-matched pending/reading state from the previous tick is discarded.
    /// </summary>
    public void BeginTick() {
        _pending = null;
        _reading = false;
    }
}