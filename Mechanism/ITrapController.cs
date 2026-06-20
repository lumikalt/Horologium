namespace Mechanism;

/// <summary>
/// Handles trap entry and return for a hart.
///
/// The trap controller is the only place privileged state transitions live.
/// It saves context, updates CSRs, and redirects the PC — all atomically
/// from the pipeline's perspective, at the commit stage.
/// </summary>
public interface ITrapController {
    /// <summary>
    /// Enters a trap: saves context to CSRs, updates privilege level,
    /// and sets PC to the trap vector.
    /// </summary>
    /// <returns>The PC the pipeline should redirect to.</returns>
    ulong RaiseTrap(TrapInfo trap, IArchState state);

    /// <summary>
    /// Returns from a trap (MRET/SRET): restores privilege level
    /// and sets PC to the saved exception PC.
    /// </summary>
    /// <returns>The PC the pipeline should redirect to.</returns>
    ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state);
}