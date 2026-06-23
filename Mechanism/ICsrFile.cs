namespace Mechanism;

/// <summary>
/// A Control and Status Register file.
/// Access is mediated by privilege level — the implementation enforces this.
/// </summary>
public interface ICsrFile {
    /// <summary>
    /// Reads the CSR at <paramref name="address"/>.
    /// Throws <see cref="CsrAccessException"/> if the current privilege
    /// level does not permit read access.
    /// </summary>
    ulong Read(uint address, PrivilegeLevel currentPrivilege);

    /// <summary>
    /// Writes <paramref name="value"/> to the CSR at <paramref name="address"/>.
    /// Throws <see cref="CsrAccessException"/> if the CSR is read-only or
    /// the current privilege level does not permit write access.
    /// </summary>
    void Write(uint address, ulong value, PrivilegeLevel currentPrivilege);

    /// <summary>
    /// Returns true if a CSR exists at <paramref name="address"/>.
    /// Does not check privilege.
    /// </summary>
    bool Exists(uint address);
}

/// <summary>
/// Thrown when a CSR access violates privilege or read-only constraints.
/// The pipeline converts this into a trap via ITrapController.
/// </summary>
public sealed class CsrAccessException(string message) : Exception(message);