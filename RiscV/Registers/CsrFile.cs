using Mechanism;

namespace RiscV.Registers;

/// <summary>
/// Machine-mode CSR file for RV32I.
/// Implements the minimum set required for trap handling.
/// CSR addresses from the RISC-V Privileged Specification.
/// </summary>
public sealed class CsrFile : ICsrFile {
    // ── CSR addresses ─────────────────────────────────────────────────────────
    // Machine Information
    public const uint Mvendorid = 0xF11;
    public const uint Marchid = 0xF12;
    public const uint Mimpid = 0xF13;
    public const uint Mhartid = 0xF14;

    // Machine Trap Setup
    public const uint Mstatus = 0x300;
    public const uint Misa = 0x301;
    public const uint Mie = 0x304;
    public const uint Mtvec = 0x305;

    // Machine Trap Handling
    public const uint Mscratch = 0x340;
    public const uint Mepc = 0x341;
    public const uint Mcause = 0x342;
    public const uint Mtval = 0x343;
    public const uint Mip = 0x344;

    // Machine Counters
    public const uint Mcycle = 0xB00;
    public const uint Minstret = 0xB02;

    // ── mstatus bit positions ─────────────────────────────────────────────────
    public const uint MstatusUIE = 1u << 0;
    public const uint MstatusSIE = 1u << 1;
    public const uint MstatusMIE = 1u << 3;
    public const uint MstatusUPIE = 1u << 4;
    public const uint MstatusSPIE = 1u << 5;
    public const uint MstatusMPIE = 1u << 7;
    public const uint MstatusSPP = 1u << 8;
    public const uint MstatusMPP = 3u << 11; // 2-bit field at bits 12:11

    private readonly Dictionary<uint, uint> _csrs = new();

    public CsrFile() {
        // Initialise to reset values
        _csrs[CsrFile.Mstatus] = 0;
        _csrs[CsrFile.Misa] = 0x40000100; // RV32I: MXL=01, I extension bit set
        _csrs[CsrFile.Mie] = 0;
        _csrs[CsrFile.Mtvec] = 0;
        _csrs[CsrFile.Mscratch] = 0;
        _csrs[CsrFile.Mepc] = 0;
        _csrs[CsrFile.Mcause] = 0;
        _csrs[CsrFile.Mtval] = 0;
        _csrs[CsrFile.Mip] = 0;
        _csrs[CsrFile.Mcycle] = 0;
        _csrs[CsrFile.Minstret] = 0;

        // Read-only machine information
        _csrs[CsrFile.Mvendorid] = 0;
        _csrs[CsrFile.Marchid] = 0;
        _csrs[CsrFile.Mimpid] = 0;
        _csrs[CsrFile.Mhartid] = 0;
    }

    public bool Exists(uint address) => _csrs.ContainsKey(address);

    public ulong Read(uint address, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        return _csrs.TryGetValue(address, out uint v)
            ? v
            : throw new CsrAccessException($"CSR 0x{address:X3} does not exist.");
    }

    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        CheckNotReadOnly(address);
        if (!_csrs.ContainsKey(address)) throw new CsrAccessException($"CSR 0x{address:X3} does not exist.");
        _csrs[address] = (uint)value;
    }

    /// <summary>Direct read bypassing privilege checks — used internally by the trap controller.</summary>
    internal uint DirectRead(uint address) =>
        _csrs.TryGetValue(address, out uint v) ? v : 0;

    /// <summary>Direct write bypassing privilege checks — used internally by the trap controller.</summary>
    internal void DirectWrite(uint address, uint value) =>
        _csrs[address] = value;

    public void Reset() {
        foreach (uint key in _csrs.Keys.ToList()) _csrs[key] = 0;
        _csrs[CsrFile.Misa] = 0x40000100;
    }

    // ── Privilege enforcement ─────────────────────────────────────────────────

    private static void CheckPrivilege(uint address, PrivilegeLevel current) {
        // Bits 9:8 of the CSR address encode the minimum privilege level
        var required = (PrivilegeLevel)((address >> 8) & 0x3);
        if (current < required)
            throw new CsrAccessException(
                $"CSR 0x{address:X3} requires privilege {required}, " +
                $"but current privilege is {current}."
            );
    }

    private static void CheckNotReadOnly(uint address) {
        // Bits 11:10 == 11 means read-only
        if (((address >> 10) & 0x3) == 0x3)
            throw new CsrAccessException(
                $"CSR 0x{address:X3} is read-only."
            );
    }
}