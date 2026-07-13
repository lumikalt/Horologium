namespace RiscV64.Registers;

/// <summary>
///     RV64-specific CSR storage for registers whose width matters and that the inherited RV32
///     CsrFile can't hold correctly (it stores every CSR as a 32-bit uint). Currently only satp:
///     Sv39's MODE field lives in bits 63:60, which the RV32 CsrFile would silently truncate away.
///     All other CSRs continue to be served by the inherited RV32 CsrFile — RV64 doesn't yet need
///     their full 64-bit width for anything implemented so far.
/// </summary>
internal sealed class Rv64CsrFile {
    public ulong Satp { get; set; }
}