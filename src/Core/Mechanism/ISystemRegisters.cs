namespace Mechanism;

/// <summary>
///     A system register file, providing ISA-defined named registers accessed by address.
///     Access is mediated by privilege level — the implementation enforces this.
/// </summary>
public interface ISystemRegisters {
    /// <summary>
    ///     Reads the register at <paramref name="address" />.
    ///     Throws <see cref="SystemRegisterAccessException" /> if the current privilege
    ///     level does not permit read access.
    /// </summary>
    ulong Read(uint address, PrivilegeLevel currentPrivilege);

    /// <summary>
    ///     Writes <paramref name="value" /> to the register at <paramref name="address" />.
    ///     Throws <see cref="SystemRegisterAccessException" /> if the register is read-only or
    ///     the current privilege level does not permit write access.
    /// </summary>
    void Write(uint address, ulong value, PrivilegeLevel currentPrivilege);

    /// <summary>
    ///     Returns true if a register exists at <paramref name="address" />.
    ///     Does not check privilege.
    /// </summary>
    bool Exists(uint address);
}

/// <summary>
///     Thrown when a system register access violates privilege or read-only constraints.
/// </summary>
public sealed class SystemRegisterAccessException(string message) : Exception(message);