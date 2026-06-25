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
    // Priority order per RISC-V spec §3.1.9: MEI > MSI > MTI > SEI > SSI > STI
    private static readonly int[] _interruptPriority = { 11, 3, 7, 9, 1, 5 };

    public ulong RaiseTrap(TrapInfo trap, IArchState state) {
        var rv = (RvArchState)state;
        CsrFile csrs = rv.CsrFile;

        // For exceptions, delegate via medeleg; for interrupts (bit 31 set), use mideleg.
        bool isInterrupt = ((uint)trap.Cause & 0x80000000u) != 0;
        uint causeNum    = (uint)trap.Cause & 0x7FFFFFFFu;
        uint delegReg    = isInterrupt ? csrs.DirectRead(CsrFile.Mideleg) : csrs.DirectRead(CsrFile.Medeleg);
        bool delegated   = state.PrivilegeLevel < RvPrivilege.Machine
                        && ((delegReg >> (int)causeNum) & 1) != 0;

        if (delegated) {
            csrs.DirectWrite(CsrFile.Sepc, (uint)trap.Pc);
            csrs.DirectWrite(CsrFile.Scause, (uint)trap.Cause);
            csrs.DirectWrite(CsrFile.Stval, (uint)trap.TrapValue);

            uint sstatus = csrs.DirectRead(CsrFile.Sstatus);
            uint sie = (sstatus >> 1) & 1;
            var priv = (uint)state.PrivilegeLevel;

            sstatus &= ~CsrFile.SstatusSpie; // clear SPIE
            sstatus |= sie << 5;             // SPIE = old SIE
            sstatus &= ~CsrFile.SstatusSie;  // clear SIE
            sstatus &= ~CsrFile.SstatusSpp;  // clear SPP
            sstatus |= (priv & 0x1) << 8;    // SPP = old privilege (1 bit)

            csrs.DirectWrite(CsrFile.Sstatus, sstatus);
            state.PrivilegeLevel = RvPrivilege.Supervisor;

            return csrs.DirectRead(CsrFile.Stvec) & ~0x3u;
        }

        // M-mode trap entry
        csrs.DirectWrite(CsrFile.Mepc, (uint)trap.Pc);
        csrs.DirectWrite(CsrFile.Mcause, (uint)trap.Cause);
        csrs.DirectWrite(CsrFile.Mtval, (uint)trap.TrapValue);

        uint mstatus = csrs.DirectRead(CsrFile.Mstatus);
        uint mie = (mstatus >> 3) & 1;
        var mpriv = (uint)state.PrivilegeLevel;

        mstatus &= ~CsrFile.MstatusMpie;
        mstatus |= mie << 7;
        mstatus &= ~CsrFile.MstatusMie;
        mstatus &= ~CsrFile.MstatusMpp;
        mstatus |= (mpriv & 0x3) << 11;

        csrs.DirectWrite(CsrFile.Mstatus, mstatus);
        state.PrivilegeLevel = RvPrivilege.Machine;

        return csrs.DirectRead(CsrFile.Mtvec) & ~0x3u;
    }

    public TrapInfo? PeekInterrupt(IArchState state) {
        var rv = (RvArchState)state;
        CsrFile csrs = rv.CsrFile;

        uint pending = csrs.DirectRead(CsrFile.Mip) & csrs.DirectRead(CsrFile.Mie);
        if (pending == 0) return null;

        uint mideleg = csrs.DirectRead(CsrFile.Mideleg);
        uint mstatus = csrs.DirectRead(CsrFile.Mstatus);
        uint sstatus = csrs.DirectRead(CsrFile.Sstatus);

        // M-mode globally enabled: below M-mode, or in M-mode with MIE=1.
        bool mEnabled = state.PrivilegeLevel < RvPrivilege.Machine
                     || (mstatus & CsrFile.MstatusMie) != 0;
        // S-mode globally enabled: in U-mode, or in S-mode with SIE=1.
        bool sEnabled = state.PrivilegeLevel < RvPrivilege.Supervisor
                     || (state.PrivilegeLevel == RvPrivilege.Supervisor
                         && (sstatus & CsrFile.SstatusSie) != 0);

        foreach (int bit in _interruptPriority) {
            if (((pending >> bit) & 1) == 0) continue;
            bool delegated = ((mideleg >> bit) & 1) != 0;
            if (!delegated && mEnabled) return new TrapInfo(RvTrapCause.InterruptCause(bit), 0, state.Pc);
            if (delegated && sEnabled)  return new TrapInfo(RvTrapCause.InterruptCause(bit), 0, state.Pc);
        }
        return null;
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