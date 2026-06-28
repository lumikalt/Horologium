using Mechanism;

namespace RiscV32.Registers;

/// <summary>
/// Machine-mode CSR file for RV32I.
/// Implements the minimum set required for trap handling.
/// CSR addresses from the RISC-V Privileged Specification.
/// </summary>
public sealed class CsrFile : ISystemRegisters {
    // ── CSR addresses ─────────────────────────────────────────────────────────
    // F extension (stub — always reads 0, writes accepted but ignored by hardware model)
    public const uint Fflags = 0x001;
    public const uint Frm = 0x002;
    public const uint Fcsr = 0x003;

    // V extension
    public const uint Vstart = 0x008;
    public const uint Vxsat = 0x009;
    public const uint Vxrm = 0x00A;
    public const uint Vcsr = 0x00F;
    public const uint Vl = 0xC20;    // read-only via public Write (bits[11:10]=3); executor uses DirectWrite
    public const uint Vtype = 0xC21; // same
    public const uint Vlenb = 0xC22; // same, constant VectorRegisterFile.VLenB

    // Machine Information
    public const uint Mvendorid = 0xF11;
    public const uint Marchid = 0xF12;
    public const uint Mimpid = 0xF13;
    public const uint Mhartid = 0xF14;

    // Supervisor Protection and Translation
    public const uint Satp = 0x180;

    // Supervisor Trap Setup
    public const uint Sstatus = 0x100;
    public const uint Sie = 0x104;
    public const uint Stvec = 0x105;

    // Supervisor Trap Handling
    public const uint Sscratch = 0x140;
    public const uint Sepc = 0x141;
    public const uint Scause = 0x142;
    public const uint Stval = 0x143;
    public const uint Sip = 0x144;

    // Machine Trap Setup
    public const uint Mstatus = 0x300;
    public const uint Misa = 0x301;
    public const uint Medeleg = 0x302;
    public const uint Mideleg = 0x303;
    public const uint Mie = 0x304;
    public const uint Mtvec = 0x305;
    public const uint Mcounteren = 0x306;

    // Machine Trap Handling
    public const uint Mscratch = 0x340;
    public const uint Mepc = 0x341;
    public const uint Mcause = 0x342;
    public const uint Mtval = 0x343;
    public const uint Mip = 0x344;

    // Machine Counters (Zicntr)
    public const uint Mcycle = 0xB00;
    public const uint Mcycleh = 0xB80;
    public const uint Minstret = 0xB02;
    public const uint Minstreth = 0xB82;

    // User-level counter shadows (Zicntr, read-only — bits[11:10]=3)
    public const uint Cycle = 0xC00;
    public const uint Time = 0xC01;
    public const uint Instret = 0xC02;
    public const uint Cycleh = 0xC80;
    public const uint Timeh = 0xC81;
    public const uint Instreth = 0xC82;

    // ── mstatus / sstatus bit positions ──────────────────────────────────────
    public const uint MstatusUie = 1u << 0;
    public const uint MstatusSie = 1u << 1;
    public const uint MstatusMie = 1u << 3;
    public const uint MstatusUpie = 1u << 4;
    public const uint MstatusSpie = 1u << 5;
    public const uint MstatusMpie = 1u << 7;
    public const uint MstatusSpp = 1u << 8;
    public const uint MstatusMpp = 3u << 11; // 2-bit field at bits 12:11

    // sstatus-only aliases (same bit positions as in mstatus)
    public const uint SstatusUie = CsrFile.MstatusUie;
    public const uint SstatusSie = CsrFile.MstatusSie;
    public const uint SstatusUpie = CsrFile.MstatusUpie;
    public const uint SstatusSpie = CsrFile.MstatusSpie;
    public const uint SstatusSpp = CsrFile.MstatusSpp;

    // CSR addresses are 12-bit, so a flat array (indexed by address) replaces a
    // Dictionary: DirectRead is called twice every cycle by PeekInterrupt, and
    // dictionary hashing showed up as a top hot spot. _present preserves the
    // distinction between an existing zero CSR and an illegal/absent one.
    private const int CsrSpace = 4096;
    private readonly uint[] _csrs = new uint[CsrFile.CsrSpace];
    private readonly bool[] _present = new bool[CsrFile.CsrSpace];

    private void Seed(uint address, uint value) {
        _csrs[address] = value;
        _present[address] = true;
    }

    public CsrFile() {
        // Initialise to reset values
        Seed(CsrFile.Fflags, 0);
        Seed(CsrFile.Frm, 0);
        Seed(CsrFile.Fcsr, 0);
        // Supervisor Protection and Translation
        Seed(CsrFile.Satp, 0);
        // Supervisor Trap Setup
        Seed(CsrFile.Sstatus, 0);
        Seed(CsrFile.Sie, 0);
        Seed(CsrFile.Stvec, 0);
        Seed(CsrFile.Sscratch, 0);
        Seed(CsrFile.Sepc, 0);
        Seed(CsrFile.Scause, 0);
        Seed(CsrFile.Stval, 0);
        Seed(CsrFile.Sip, 0);

        Seed(CsrFile.Mstatus, 0);
        Seed(CsrFile.Misa, 0x40000100); // RV32I: MXL=01, I extension bit set
        Seed(CsrFile.Medeleg, 0);
        Seed(CsrFile.Mideleg, 0);
        Seed(CsrFile.Mie, 0);
        Seed(CsrFile.Mtvec, 0);
        Seed(CsrFile.Mcounteren, 0);
        Seed(CsrFile.Mscratch, 0);
        Seed(CsrFile.Mepc, 0);
        Seed(CsrFile.Mcause, 0);
        Seed(CsrFile.Mtval, 0);
        Seed(CsrFile.Mip, 0);
        Seed(CsrFile.Mcycle, 0);
        Seed(CsrFile.Mcycleh, 0);
        Seed(CsrFile.Minstret, 0);
        Seed(CsrFile.Minstreth, 0);

        // Read-only machine information
        Seed(CsrFile.Mvendorid, 0);
        Seed(CsrFile.Marchid, 0);
        Seed(CsrFile.Mimpid, 0);
        Seed(CsrFile.Mhartid, 0);

        // V extension
        Seed(CsrFile.Vstart, 0);
        Seed(CsrFile.Vxsat, 0);
        Seed(CsrFile.Vxrm, 0);
        Seed(CsrFile.Vcsr, 0);
        Seed(CsrFile.Vl, 0);
        Seed(CsrFile.Vtype, 0);
        Seed(CsrFile.Vlenb, VectorRegisterFile.VLenB);
    }

    public bool Exists(uint address) => address < CsrFile.CsrSpace && _present[address];

    public ulong Read(uint address, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        // Zicntr: user-level read-only counter shadows (bits[11:10]=3 → read-only enforcement
        // is already handled by CheckNotReadOnly on writes). time/timeh have no external CLINT.
        if (address is CsrFile.Time or CsrFile.Timeh) return 0;
        uint effective = address switch {
            CsrFile.Cycle    => CsrFile.Mcycle,
            CsrFile.Cycleh   => CsrFile.Mcycleh,
            CsrFile.Instret  => CsrFile.Minstret,
            CsrFile.Instreth => CsrFile.Minstreth,
            _                => address,
        };
        return effective < CsrFile.CsrSpace && _present[effective]
            ? _csrs[effective]
            : throw new SystemRegisterAccessException($"CSR 0x{effective:X3} does not exist.");
    }

    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        CheckNotReadOnly(address);
        if (address >= CsrFile.CsrSpace || !_present[address])
            throw new SystemRegisterAccessException($"CSR 0x{address:X3} does not exist.");
        _csrs[address] = (uint)value;
    }

    /// <summary>Direct read bypassing privilege checks — used internally by the trap controller.</summary>
    internal uint DirectRead(uint address) =>
        address < CsrFile.CsrSpace && _present[address] ? _csrs[address] : 0;

    /// <summary>Direct write bypassing privilege checks — used internally by the trap controller.</summary>
    internal void DirectWrite(uint address, uint value) => Seed(address, value);

    public void Reset() {
        for (var i = 0; i < CsrFile.CsrSpace; i++)
            if (_present[i])
                _csrs[i] = 0;
        Seed(CsrFile.Misa, 0x40000100);
        Seed(CsrFile.Vlenb, VectorRegisterFile.VLenB);
    }

    // ── Privilege enforcement ─────────────────────────────────────────────────

    private static void CheckPrivilege(uint address, PrivilegeLevel current) {
        // Bits 9:8 of the CSR address encode the minimum privilege level
        var required = (PrivilegeLevel)((address >> 8) & 0x3);
        if (current < required)
            throw new SystemRegisterAccessException(
                $"CSR 0x{address:X3} requires privilege {required}, " +
                $"but current privilege is {current}."
            );
    }

    private static void CheckNotReadOnly(uint address) {
        // Bits 11:10 == 11 means read-only
        if (((address >> 10) & 0x3) == 0x3)
            throw new SystemRegisterAccessException(
                $"CSR 0x{address:X3} is read-only."
            );
    }
}