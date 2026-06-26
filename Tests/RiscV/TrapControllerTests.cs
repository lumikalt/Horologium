using Mechanism;
using RiscV;
using RiscV.Registers;
using RiscV.State;
using RiscV.Trap;

namespace Tests.RiscV;

public class TrapControllerTests {
    private readonly RvTrapController _tc = new();

    private static RvArchState MakeState(PrivilegeLevel priv, ulong pc = 0x1000) {
        var s = new RvArchState { Pc = pc, };
        s.PrivilegeLevel = priv;
        return s;
    }

    // Read/write CSRs through the public interface using Machine privilege, which
    // allows access to all registers regardless of the hart's current privilege level.
    private static ulong Csr(RvArchState s, uint addr) =>
        s.SystemRegisters.Read(addr, RvPrivilege.Machine);

    private static void SetCsr(RvArchState s, uint addr, uint val) =>
        s.SystemRegisters.Write(addr, val, RvPrivilege.Machine);

    // ── No delegation ──────────────────────────────────────────────────────────

    [Fact]
    public void RaiseTrap_Ecall_UMode_NoDelegation_GoesToMMode() {
        RvArchState s = MakeState(RvPrivilege.User, 0x2000);
        // medeleg defaults to 0 — no delegation
        _tc.RaiseTrap(new TrapInfo(RvTrapCause.EnvironmentCallFromU, 0, 0x2000), s);

        Assert.Equal(RvPrivilege.Machine, s.PrivilegeLevel);
        Assert.Equal(0x2000uL, Csr(s, CsrFile.Mepc));
        Assert.Equal((uint)RvTrapCause.EnvironmentCallFromU, Csr(s, CsrFile.Mcause));
        Assert.Equal(0uL, Csr(s, CsrFile.Sepc)); // S-mode CSRs untouched
    }

    // ── Delegation to S-mode ───────────────────────────────────────────────────

    [Fact]
    public void RaiseTrap_Ecall_UMode_Delegated_GoesToSMode() {
        RvArchState s = MakeState(RvPrivilege.User, 0x3000);
        SetCsr(s, CsrFile.Medeleg, 1u << RvTrapCause.EnvironmentCallFromU);
        SetCsr(s, CsrFile.Stvec, 0x8000);

        ulong vec = _tc.RaiseTrap(new TrapInfo(RvTrapCause.EnvironmentCallFromU, 0, 0x3000), s);

        Assert.Equal(RvPrivilege.Supervisor, s.PrivilegeLevel);
        Assert.Equal(0x8000uL, vec);
        Assert.Equal(0x3000uL, Csr(s, CsrFile.Sepc));
        Assert.Equal((uint)RvTrapCause.EnvironmentCallFromU, Csr(s, CsrFile.Scause));

        ulong sstatus = Csr(s, CsrFile.Sstatus);
        Assert.Equal(0uL, (sstatus >> 8) & 0x1); // SPP = User (0)
        Assert.Equal(0uL, (sstatus >> 5) & 0x1); // SPIE = old SIE (was 0)
        Assert.Equal(0uL, (sstatus >> 1) & 0x1); // SIE = 0

        // M-mode CSRs must not be written
        Assert.Equal(0uL, Csr(s, CsrFile.Mepc));
        Assert.Equal(0uL, Csr(s, CsrFile.Mcause));
    }

    [Fact]
    public void RaiseTrap_Delegated_OldSIE_SavedToSPIE() {
        RvArchState s = MakeState(RvPrivilege.User, 0x1000);
        SetCsr(s, CsrFile.Sstatus, CsrFile.SstatusSie); // SIE = 1 before trap
        SetCsr(s, CsrFile.Medeleg, 1u << RvTrapCause.EnvironmentCallFromU);

        _tc.RaiseTrap(new TrapInfo(RvTrapCause.EnvironmentCallFromU, 0, 0x1000), s);

        ulong sstatus = Csr(s, CsrFile.Sstatus);
        Assert.Equal(1uL, (sstatus >> 5) & 0x1); // SPIE = old SIE (was 1)
        Assert.Equal(0uL, (sstatus >> 1) & 0x1); // SIE = 0
    }

    [Fact]
    public void RaiseTrap_Delegated_FromSupervisor_SPP_SetToSupervisor() {
        RvArchState s = MakeState(RvPrivilege.Supervisor, 0x5000);
        SetCsr(s, CsrFile.Medeleg, 1u << RvTrapCause.EnvironmentCallFromS);

        _tc.RaiseTrap(new TrapInfo(RvTrapCause.EnvironmentCallFromS, 0, 0x5000), s);

        ulong sstatus = Csr(s, CsrFile.Sstatus);
        Assert.Equal(1uL, (sstatus >> 8) & 0x1); // SPP = Supervisor (1)
    }

    // ── Machine-mode traps are never delegated ─────────────────────────────────

    [Fact]
    public void RaiseTrap_MMode_NeverDelegated_EvenIfMedelegSet() {
        RvArchState s = MakeState(RvPrivilege.Machine, 0x1000);
        SetCsr(s, CsrFile.Medeleg, 0xFFFFFFFF); // every bit set

        _tc.RaiseTrap(new TrapInfo(RvTrapCause.EnvironmentCallFromM, 0, 0x1000), s);

        Assert.Equal(RvPrivilege.Machine, s.PrivilegeLevel);
        Assert.Equal(0x1000uL, Csr(s, CsrFile.Mepc));
        Assert.Equal(0uL, Csr(s, CsrFile.Sepc));
    }

    // ── Interrupt dispatch: PeekInterrupt ──────────────────────────────────────

    [Fact]
    public void PeekInterrupt_NoPendingInterrupts_ReturnsNull() {
        RvArchState s = MakeState(RvPrivilege.Machine, 0x2000);
        SetCsr(s, CsrFile.Mstatus, CsrFile.MstatusMie); // MIE=1
        // mip = 0, mie = 0 → nothing pending
        Assert.Null(_tc.PeekInterrupt(s));
    }

    [Fact]
    public void PeekInterrupt_MachineTimerPending_MIE_Set_ReturnsMtiCause() {
        RvArchState s = MakeState(RvPrivilege.Machine, 0x3000);
        SetCsr(s, CsrFile.Mstatus, CsrFile.MstatusMie); // MIE=1
        SetCsr(s, CsrFile.Mip, 1u << 7);                // MTI pending
        SetCsr(s, CsrFile.Mie, 1u << 7);                // MTI enabled

        TrapInfo? trap = _tc.PeekInterrupt(s);
        Assert.NotNull(trap);
        Assert.Equal(RvTrapCause.MachineTimerInterrupt, trap.Cause);
    }

    [Fact]
    public void PeekInterrupt_MIE_Clear_InMMode_ReturnsNull() {
        // MIE=0 in M-mode → no interrupt delivery
        RvArchState s = MakeState(RvPrivilege.Machine, 0x4000);
        SetCsr(s, CsrFile.Mip, 1u << 11); // MEI pending
        SetCsr(s, CsrFile.Mie, 1u << 11); // MEI enabled
        // mstatus.MIE defaults to 0

        Assert.Null(_tc.PeekInterrupt(s));
    }

    [Fact]
    public void PeekInterrupt_UMode_FiresEvenIfMIE_Clear() {
        // Below M-mode → M-mode interrupts fire regardless of MIE bit
        RvArchState s = MakeState(RvPrivilege.User, 0x5000);
        SetCsr(s, CsrFile.Mip, 1u << 11); // MEI pending
        SetCsr(s, CsrFile.Mie, 1u << 11); // MEI enabled
        // mstatus.MIE = 0, but current privilege < M-mode

        TrapInfo? trap = _tc.PeekInterrupt(s);
        Assert.NotNull(trap);
        Assert.Equal(RvTrapCause.MachineExternalInterrupt, trap.Cause);
    }

    [Fact]
    public void PeekInterrupt_MEI_HigherPriorityThan_MTI() {
        RvArchState s = MakeState(RvPrivilege.Machine, 0x6000);
        SetCsr(s, CsrFile.Mstatus, CsrFile.MstatusMie);
        SetCsr(s, CsrFile.Mip, (1u << 11) | (1u << 7)); // MEI + MTI both pending
        SetCsr(s, CsrFile.Mie, (1u << 11) | (1u << 7)); // both enabled

        TrapInfo? trap = _tc.PeekInterrupt(s);
        Assert.NotNull(trap);
        Assert.Equal(RvTrapCause.MachineExternalInterrupt, trap.Cause); // MEI wins
    }

    [Fact]
    public void PeekInterrupt_DelegatedToSMode_SIESet_ReturnsInterrupt() {
        // MTI delegated to S-mode; S-mode with SIE=1 should receive it
        RvArchState s = MakeState(RvPrivilege.Supervisor, 0x7000);
        SetCsr(s, CsrFile.Mip, 1u << 7);                // MTI pending
        SetCsr(s, CsrFile.Mie, 1u << 7);                // MTI enabled
        SetCsr(s, CsrFile.Mideleg, 1u << 7);            // delegated to S-mode
        SetCsr(s, CsrFile.Sstatus, CsrFile.SstatusSie); // SIE=1

        TrapInfo? trap = _tc.PeekInterrupt(s);
        Assert.NotNull(trap);
        Assert.Equal(RvTrapCause.MachineTimerInterrupt, trap.Cause);
    }

    [Fact]
    public void RaiseTrap_Interrupt_UsesMidelegNotMedeleg() {
        // An interrupt cause should be routed via mideleg, not medeleg.
        // mideleg bit 7 set → MTI goes to S-mode.
        // medeleg bit 7 not set (shouldn't matter for interrupts).
        RvArchState s = MakeState(RvPrivilege.User, 0x8000);
        SetCsr(s, CsrFile.Mideleg, 1u << 7); // delegate MTI to S-mode
        SetCsr(s, CsrFile.Stvec, 0xC000);

        ulong vec = _tc.RaiseTrap(new TrapInfo(RvTrapCause.MachineTimerInterrupt, 0, 0x8000), s);

        Assert.Equal(RvPrivilege.Supervisor, s.PrivilegeLevel);
        Assert.Equal(0xC000uL, vec);
        Assert.Equal(0x8000uL, Csr(s, CsrFile.Sepc));
        Assert.Equal(unchecked((uint)RvTrapCause.MachineTimerInterrupt), Csr(s, CsrFile.Scause));
        // mcause/mepc must not be touched
        Assert.Equal(0uL, Csr(s, CsrFile.Mcause));
    }
}