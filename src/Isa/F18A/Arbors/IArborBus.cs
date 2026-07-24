namespace F18A.Arbors;

/// <summary>
///     Routes port-space reads/writes to the appropriate directional
///     <see cref="RendezvousArbor" /> based on word address.
/// </summary>
public interface IArborBus {
    /// <summary>
    ///     Attempt a port read.
    /// </summary>
    void TryRead(uint wordAddr, out uint value);

    /// <summary>
    ///     Attempt a port write.
    /// </summary>
    void TryWrite(uint wordAddr, uint value);

    /// <summary>
    ///     A port access at this word address will complete without blocking.
    ///     Used by the node to pre-check before decoding and executing.
    /// </summary>
    bool IsReady(uint wordAddr, bool isRead);
}