#region

using Mechanism;
using RiscV32.State;

// ReSharper disable MemberCanBePrivate.Global

#endregion

namespace RiscV32.Registers;

/// <summary>
///     Machine-mode CSR file for RV32I.
///     Implements the minimum set required for trap handling.
///     CSR addresses from the RISC-V Privileged Specification.
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

    // Zkr: entropy source
    public const uint Seed = 0x015;

    // seed[31:30] OPST status field (§4.1). This simulator models seed as a virtual entropy
    // source (§4.2.3): every poll succeeds with fresh pseudorandomness, so only ES16 is ever
    // returned — BIST/WAIT/DEAD are legal per spec ("a virtual source... does not need to
    // implement WAIT or BIST states") but add no observable behavior worth modeling here.
    private const uint SeedOpstEs16 = 0b10u << 30;

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
    public const uint Scounteren = 0x106;

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

    // Machine Environment Configuration (Smstateen / OpenSBI — accept writes, no effect)
    public const uint Menvcfg = 0x30A;
    public const uint Menvcfgh = 0x31A;
    public const uint Senvcfg = 0x10A;

    // RV32 upper mstatus half (mstatush — M-mode bit-endianness control; writable, ignored)
    public const uint Mstatush = 0x310;

    // Physical Memory Protection (Smepmp / OpenSBI — writable, no enforcement in simulation)
    // pmpcfg0-3: 0x3A0–0x3A3  (4 configuration registers, each packing 4 PMP entries for RV32)
    // pmpaddr0-15: 0x3B0–0x3BF
    public const uint Pmpcfg0 = 0x3A0;
    public const uint Pmpcfg1 = 0x3A1;
    public const uint Pmpcfg2 = 0x3A2;
    public const uint Pmpcfg3 = 0x3A3;
    public const uint Pmpaddr0 = 0x3B0;

    // Machine Counters (Zicntr)
    public const uint Mcycle = 0xB00;
    public const uint Mcycleh = 0xB80;
    public const uint Minstret = 0xB02;
    public const uint Minstreth = 0xB82;

    // Machine Hardware Performance Counters (Zihpm, N=3–31)
    // mhpmcounterN:  0xB03–0xB1F  (N = address − 0xB00)
    // mhpmcounterNh: 0xB83–0xB9F  (N = address − 0xB80)
    // mhpmeventN:    0x323–0x33F  (N = address − 0x320)
    // All counters read as 0 — no hardware event counting in simulation.
    // Event selectors are writable from M-mode but have no effect.

    // User-level counter shadows (Zicntr, read-only — bits[11:10]=3)
    public const uint Cycle = 0xC00;
    public const uint Time = 0xC01;
    public const uint Instret = 0xC02;
    public const uint Cycleh = 0xC80;
    public const uint Timeh = 0xC81;

    public const uint Instreth = 0xC82;
    // User-level HPM shadows (Zihpm, read-only — bits[11:10]=3)
    // hpmcounterN:  0xC03–0xC1F  routed → mhpmcounterN  (0xB03–0xB1F)
    // hpmcounterNh: 0xC83–0xC9F  routed → mhpmcounterNh (0xB83–0xB9F)

    // ── mstatus / sstatus bit positions ──────────────────────────────────────
    public const uint MstatusUie = 1u << 0;
    public const uint MstatusSie = 1u << 1;
    public const uint MstatusMie = 1u << 3;
    public const uint MstatusUpie = 1u << 4;
    public const uint MstatusSpie = 1u << 5;
    public const uint MstatusMpie = 1u << 7;
    public const uint MstatusSpp = 1u << 8;
    public const uint MstatusMpp = 3u << 11; // 2-bit field at bits 12:11
    public const uint MstatusSum = 1u << 18; // Supervisor User Memory access

    // sstatus-only aliases (same bit positions as in mstatus)
    public const uint SstatusUie = CsrFile.MstatusUie;
    public const uint SstatusSie = CsrFile.MstatusSie;
    public const uint SstatusUpie = CsrFile.MstatusUpie;
    public const uint SstatusSpie = CsrFile.MstatusSpie;
    public const uint SstatusSpp = CsrFile.MstatusSpp;
    public const uint SstatusSum = CsrFile.MstatusSum;

    // CSR addresses are 12-bit, so a flat array (indexed by address) replaces a
    // Dictionary: DirectRead is called twice every cycle by PeekInterrupt, and
    // dictionary hashing showed up as a top hot spot. _present preserves the
    // distinction between an existing zero CSR and an illegal/absent one.
    private const int CsrSpace = 4096;
    private readonly uint[] _csrs = new uint[CsrFile.CsrSpace];

    // Zkr entropy source: deterministic (seed 0) so simulation runs stay reproducible. Not
    // visible to Spike co-sim — excluded there the same way UVE state is (see SpikeCoSimTests).
    private readonly Random _entropyRng = new(0);
    private readonly bool[] _present = new bool[CsrFile.CsrSpace];

    public CsrFile() {
        // Initialise to reset values
        InitCsr(CsrFile.Fflags, 0);
        InitCsr(CsrFile.Frm, 0);
        InitCsr(CsrFile.Fcsr, 0);
        // Supervisor Protection and Translation
        InitCsr(CsrFile.Satp, 0);
        // Supervisor Trap Setup
        InitCsr(CsrFile.Sstatus, 0);
        InitCsr(CsrFile.Sie, 0);
        InitCsr(CsrFile.Stvec, 0);
        InitCsr(CsrFile.Scounteren, 0);
        InitCsr(CsrFile.Sscratch, 0);
        InitCsr(CsrFile.Sepc, 0);
        InitCsr(CsrFile.Scause, 0);
        InitCsr(CsrFile.Stval, 0);
        InitCsr(CsrFile.Sip, 0);

        InitCsr(CsrFile.Mstatus, 0);
        InitCsr(CsrFile.Misa, 0x40141105); // RV32IMACSU: MXL=01, I/M/A/C/S/U extension bits
        InitCsr(CsrFile.Medeleg, 0);
        InitCsr(CsrFile.Mideleg, 0);
        InitCsr(CsrFile.Mie, 0);
        InitCsr(CsrFile.Mtvec, 0);
        InitCsr(CsrFile.Mcounteren, 0);
        InitCsr(CsrFile.Mscratch, 0);
        InitCsr(CsrFile.Mepc, 0);
        InitCsr(CsrFile.Mcause, 0);
        InitCsr(CsrFile.Mtval, 0);
        InitCsr(CsrFile.Mip, 0);
        InitCsr(CsrFile.Mcycle, 0);
        InitCsr(CsrFile.Mcycleh, 0);
        InitCsr(CsrFile.Minstret, 0);
        InitCsr(CsrFile.Minstreth, 0);

        // Zihpm: machine hardware performance counters (always 0) and event selectors
        for (uint n = 3; n <= 31; n++) {
            InitCsr(0xB00 + n, 0); // mhpmcounterN
            InitCsr(0xB80 + n, 0); // mhpmcounterNh
            InitCsr(0x320 + n, 0); // mhpmeventN
        }

        // Privileged extension stubs (OpenSBI/Linux writes these; no enforcement in simulation)
        InitCsr(CsrFile.Menvcfg, 0);
        InitCsr(CsrFile.Menvcfgh, 0);
        InitCsr(CsrFile.Senvcfg, 0);
        InitCsr(CsrFile.Mstatush, 0);
        for (uint i = 0; i < 4; i++) InitCsr(CsrFile.Pmpcfg0 + i, 0);
        for (uint i = 0; i < 16; i++) InitCsr(CsrFile.Pmpaddr0 + i, 0);

        // Read-only machine information
        InitCsr(CsrFile.Mvendorid, 0);
        InitCsr(CsrFile.Marchid, 0);
        InitCsr(CsrFile.Mimpid, 0);
        InitCsr(CsrFile.Mhartid, 0);

        // Zkr: entropy source (value is regenerated on every Read; this only marks it present)
        InitCsr(CsrFile.Seed, 0);

        // V extension
        InitCsr(CsrFile.Vstart, 0);
        InitCsr(CsrFile.Vxsat, 0);
        InitCsr(CsrFile.Vxrm, 0);
        InitCsr(CsrFile.Vcsr, 0);
        InitCsr(CsrFile.Vl, 0);
        InitCsr(CsrFile.Vtype, 0);
        InitCsr(CsrFile.Vlenb, VectorRegisterFile.VLenB);
    }

    public bool Exists(uint address) => address < CsrFile.CsrSpace && _present[address];

    public ulong Read(uint address, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        // Zicntr: user-level read-only counter shadows (bits[11:10]=3 → read-only enforcement
        // is already handled by CheckNotReadOnly on writes). time/timeh have no external CLINT.
        if (address is CsrFile.Time or CsrFile.Timeh) return 0;
        // Zkr: every poll is treated as a successful ES16 read (§4.2.3, virtual entropy source).
        // Polling has the side effect of "wipe-on-read" by construction: the returned entropy is
        // freshly drawn, never replayed.
        if (address == CsrFile.Seed) return CsrFile.SeedOpstEs16 | (uint)_entropyRng.Next(0, 0x10000);
        uint effective = address switch {
            CsrFile.Cycle    => CsrFile.Mcycle,
            CsrFile.Cycleh   => CsrFile.Mcycleh,
            CsrFile.Instret  => CsrFile.Minstret,
            CsrFile.Instreth => CsrFile.Minstreth,
            // Zihpm: route user-level read-only shadows to their M-mode mirrors
            >= 0xC03u and <= 0xC1Fu => address - 0xC00u + 0xB00u, // hpmcounterN  → mhpmcounterN
            >= 0xC83u and <= 0xC9Fu => address - 0xC80u + 0xB80u, // hpmcounterNh → mhpmcounterNh
            _                       => address,
        };
        if (effective == CsrFile.Fcsr) return (_csrs[CsrFile.Frm] << 5) | _csrs[CsrFile.Fflags];
        return effective < CsrFile.CsrSpace && _present[effective]
            ? _csrs[effective]
            : throw new SystemRegisterAccessException($"CSR 0x{effective:X3} does not exist.");
    }

    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) {
        CheckPrivilege(address, currentPrivilege);
        CheckNotReadOnly(address);
        if (address >= CsrFile.CsrSpace || !_present[address])
            throw new SystemRegisterAccessException($"CSR 0x{address:X3} does not exist.");
        switch (address) {
            case CsrFile.Seed:
                // §4.1: "The write value ... must be ignored by implementations. The purpose of
                // the write is to signal polling and flushing." Polling itself happens in Read.
                break;
            case CsrFile.Fcsr:
                _csrs[CsrFile.Fflags] = (uint)value & 0x1F;
                _csrs[CsrFile.Frm] = ((uint)value >> 5) & 0x7;
                _csrs[CsrFile.Fcsr] = (uint)value & 0xFF;
                break;
            case CsrFile.Frm:
                _csrs[CsrFile.Frm] = (uint)value & 0x7;
                _csrs[CsrFile.Fcsr] = (_csrs[CsrFile.Frm] << 5) | _csrs[CsrFile.Fflags];
                break;
            case CsrFile.Fflags:
                _csrs[CsrFile.Fflags] = (uint)value & 0x1F;
                _csrs[CsrFile.Fcsr] = (_csrs[CsrFile.Frm] << 5) | _csrs[CsrFile.Fflags];
                break;
            default: _csrs[address] = (uint)value; break;
        }
    }

    private void InitCsr(uint address, uint value) {
        _csrs[address] = value;
        _present[address] = true;
    }

    /// <summary>Direct read bypassing privilege checks — used internally by the trap controller.</summary>
    internal uint DirectRead(uint address) =>
        address < CsrFile.CsrSpace && _present[address] ? _csrs[address] : 0;

    /// <summary>Direct write bypassing privilege checks — used internally by the trap controller.</summary>
    internal void DirectWrite(uint address, uint value) => InitCsr(address, value);

    /// <summary>OR new FP exception flags into fflags (and keep fcsr in sync). Used by the FP executor.</summary>
    internal void OrFflags(uint flags) {
        _csrs[CsrFile.Fflags] = (_csrs[CsrFile.Fflags] | flags) & 0x1F;
        _csrs[CsrFile.Fcsr] = (_csrs[CsrFile.Frm] << 5) | _csrs[CsrFile.Fflags];
    }

    public void Reset() {
        for (var i = 0; i < CsrFile.CsrSpace; i++)
            if (_present[i])
                _csrs[i] = 0;
        InitCsr(CsrFile.Misa, 0x40141105); // RV32IMACSU: MXL=01, I/M/A/C/S/U extension bits
        InitCsr(CsrFile.Vlenb, VectorRegisterFile.VLenB);
    }

    // ── Privilege enforcement ─────────────────────────────────────────────────

    private static void CheckPrivilege(uint address, PrivilegeLevel current) {
        // Zkr seed (§4.3): default M-mode-only access, overridable per mode via
        // mseccfg.sseed/.useed. mseccfg isn't modeled here, so this simulator always enforces
        // the un-overridden default (M-mode-only) rather than the address bits' own encoding,
        // which would otherwise place seed (0x015) at User level.
        PrivilegeLevel required = address == CsrFile.Seed
            ? RvPrivilege.Machine
            // Bits 9:8 of the CSR address encode the minimum privilege level
            : (PrivilegeLevel)((address >> 8) & 0x3);
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