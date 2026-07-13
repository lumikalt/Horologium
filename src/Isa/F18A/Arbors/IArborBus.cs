namespace F18A.Arbors;

/// <summary>
///     Routes port-space reads/writes to the appropriate directional
///     <see cref="RendezvousArbor" /> based on word address.
/// </summary>
public interface IArborBus {
    /// <summary>
    ///     Attempt a port read. Returns true if data was available; false if blocked.
    ///     When false, <paramref name="value" /> is 0 and the caller must retry.
    /// </summary>
    bool TryRead(uint wordAddr, out uint value);

    /// <summary>
    ///     Attempt a port write. Returns true if a receiver was waiting; false if buffered for next tick.
    /// </summary>
    bool TryWrite(uint wordAddr, uint value);

    /// <summary>
    ///     Returns true when a port access at this word address will complete without blocking.
    ///     Used by the node to pre-check before decoding and executing.
    /// </summary>
    bool IsReady(uint wordAddr, bool isRead);
}