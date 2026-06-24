using Mechanism;
using RiscV.Registers;
using RiscV.State;

namespace RiscV.Trap;

/// <summary>
/// Machine-mode trap controller for RV32I.
/// Implements the trap entry and return sequences from the RISC-V
/// Privileged Specification, Section 3.1.
/// </summary>
public sealed class RvTrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) {
        var rv = (RvArchState)state;
        CsrFile csrs = rv.CsrFile;

        // Save current PC to mepc
        csrs.DirectWrite(CsrFile.Mepc, (uint)trap.Pc);

        // Write mcause — bit 31 = 0 for exceptions (not interrupts)
        csrs.DirectWrite(CsrFile.Mcause, (uint)trap.Cause);

        // Write mtval
        csrs.DirectWrite(CsrFile.Mtval, (uint)trap.TrapValue);

        // Update mstatus: save MIE to MPIE, clear MIE, save current priv to MPP
        uint mstatus = csrs.DirectRead(CsrFile.Mstatus);
        uint mie = (mstatus >> 3) & 1; // current MIE bit
        var priv = (uint)state.PrivilegeLevel;

        mstatus &= ~CsrFile.MstatusMpie; // clear MPIE
        mstatus |= mie << 7;             // MPIE = old MIE
        mstatus &= ~CsrFile.MstatusMie;  // clear MIE
        mstatus &= ~CsrFile.MstatusMpp;  // clear MPP
        mstatus |= (priv & 0x3) << 11;   // MPP = old privilege

        csrs.DirectWrite(CsrFile.Mstatus, mstatus);

        // Transition to Machine mode
        state.PrivilegeLevel = RvPrivilege.Machine;

        // Compute trap vector
        uint mtvec = csrs.DirectRead(CsrFile.Mtvec);
        uint @base = mtvec & ~0x3u;

        // Direct mode: all traps go to BASE
        // Vectored mode: exceptions go to BASE, interrupts to BASE + 4*cause
        // (we only handle exceptions here — no interrupts yet)
        return @base;
    }

    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) {
        var rv = (RvArchState)state;
        CsrFile csrs = rv.CsrFile;

        if (returningFrom == RvPrivilege.Supervisor) {
            // SRET: restore from sstatus.SPP/SPIE, return to sepc
            uint sstatus = csrs.DirectRead(CsrFile.Sstatus);
            uint spp = (sstatus >> 8) & 0x1;
            uint spie = (sstatus >> 5) & 0x1;

            sstatus &= ~CsrFile.SstatusSie; // clear SIE
            sstatus |= spie << 1;           // SIE = SPIE
            sstatus |= CsrFile.SstatusSpie; // SPIE = 1
            sstatus &= ~CsrFile.SstatusSpp; // SPP = 0 (U-mode)

            csrs.DirectWrite(CsrFile.Sstatus, sstatus);
            state.PrivilegeLevel = (PrivilegeLevel)spp;
            return csrs.DirectRead(CsrFile.Sepc);
        }

        // MRET: restore from mstatus.MPP/MPIE, return to mepc
        uint mstatus = csrs.DirectRead(CsrFile.Mstatus);
        uint mpp = (mstatus >> 11) & 0x3;
        uint mpie = (mstatus >> 7) & 0x1;

        mstatus &= ~CsrFile.MstatusMie; // clear MIE
        mstatus |= mpie << 3;           // MIE = MPIE
        mstatus |= CsrFile.MstatusMpie; // MPIE = 1
        mstatus &= ~CsrFile.MstatusMpp; // MPP = U (0)

        csrs.DirectWrite(CsrFile.Mstatus, mstatus);
        state.PrivilegeLevel = (PrivilegeLevel)mpp;
        return csrs.DirectRead(CsrFile.Mepc);
    }
}