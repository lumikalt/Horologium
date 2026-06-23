namespace Mechanism;

/// <summary>
/// The complete architectural state of a running hart (hardware thread).
///
/// This is the live mutable state the executor reads and writes.
/// The ISA plugin owns the concrete implementation — the pipeline
/// only ever sees this interface.
/// </summary>
public interface IArchState {
    /// <summary>The program counter.</summary>
    ulong Pc { get; set; }

    /// <summary>The privilege level the hart is currently executing at.</summary>
    PrivilegeLevel PrivilegeLevel { get; set; }

    /// <summary>The integer register file.</summary>
    IRegisterFile IntegerRegisters { get; }

    /// <summary>
    /// The CSR file. Null if the ISA does not define CSRs
    /// (e.g. a minimal custom ISA with no privileged spec).
    /// </summary>
    ICsrFile Csrs { get; }

    /// <summary>
    /// Creates a deep copy of this state.
    /// Used by the pipeline to checkpoint state for precise exceptions.
    /// </summary>
    IArchState Snapshot();
}

/// <summary>
/// The privilege level of the executing hart.
/// Numeric values match the RISC-V privileged specification.
/// </summary>
public enum PrivilegeLevel {
    User = 0, Supervisor = 1, Machine = 3,
}