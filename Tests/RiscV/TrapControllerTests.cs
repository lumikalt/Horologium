using Mechanism;
using RiscV;
using RiscV.Registers;
using RiscV.State;
using RiscV.Trap;

namespace Tests.RiscV;

public class TrapControllerTests {
    private readonly RvTrapController _tc = new();

    private static RvArchState MakeState(PrivilegeLevel priv, ulong pc = 0x1000) {
        var s = new RvArchState { Pc = pc };
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
}
