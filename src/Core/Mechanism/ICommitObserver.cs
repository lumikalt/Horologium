namespace Mechanism;

/// <summary>
///     Receives a notification for each instruction that successfully commits.
///     Not called for EBREAK (halt), traps, or trap-returns.
///     All architectural states in
///     <paramref>
///         <name>IArchState</name>
///     </paramref>
///     reflect the committed result
///     when this is called.
/// </summary>
public interface ICommitObserver {
    /// <summary>Called for each instruction that successfully commits.</summary>
    void OnCommit(ulong pc, uint rawEncoding, IArchState state);
}