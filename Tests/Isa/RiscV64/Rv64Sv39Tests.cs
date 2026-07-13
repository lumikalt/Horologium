using Mechanism;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.Memory;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
///     Tests for Sv39 address translation (loads, stores, and instruction fetch)
///     on RV64. satp lives in <see cref="Rv64ArchState.Rv64Csrs" /> rather than the
///     shared 32-bit CsrFile, so it is set here by actually executing a CSRRW
///     instruction rather than by direct CSR-file writes — this also exercises
///     the satp CSR-interception path in Rv64Executor.
/// </summary>
public class Rv64Sv39Tests {
    // csrrw x0, satp, x1
    private const uint CsrrwSatpX1 = (CsrFile.Satp << 20) | (1u << 15) | (0b001u << 12) | 0x73;
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();

    private Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state, IMemory mem, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, mem);
    }

    // Sets satp by executing csrrw x0, satp, x1 while in Machine mode (the default),
    // then leaves the caller free to lower PrivilegeLevel for the actual test.
    private void SetSatp(Rv64ArchState state, IMemory mem, ulong satp) {
        state.IntegerRegisters.Write(1, satp);
        ExecuteResult r = Exec(Rv64Sv39Tests.CsrrwSatpX1, state, mem);
        Assert.Null(r.Trap);
    }

    // Builds a 64KB flat memory with a three-level Sv39 page table:
    //   Root PT (PPN=1) at PA 0x1000 — entry vpn2=0 points to L1 PT (PPN=2) at PA 0x2000
    //   L1 PT (PPN=2) at PA 0x2000 — entry vpn1=0 points to L0 PT (PPN=3) at PA 0x3000
    //   L0 PT (PPN=3) at PA 0x3000 — leaf entries vpn0=0..7, caller fills these in
    // satp = MODE=8 (Sv39), PPN=1 (root PT at PA 0x1000).
    private static (FlatMemory mem, ulong satp) BuildSv39Memory() {
        var mem = new FlatMemory(0x10000);
        mem.Write(0x1000UL, (2UL << 10) | 1UL, 8); // root PT entry 0 → L1 PT at PA 0x2000
        mem.Write(0x2000UL, (3UL << 10) | 1UL, 8); // L1 PT entry 0 → L0 PT at PA 0x3000
        return (mem, (8UL << 60) | 1UL);
    }

    // Leaf PTE for a read-write user page at the given PPN, with A and D bits set.
    private static ulong UserRwPte(ulong ppn) => (ppn << 10) | 0b1101_0111UL; // D|A|U|W|R|V

    [Fact]
    public void Execute_Load_BareMode_NoTranslation() {
        var mem = new FlatMemory(4096);
        mem.Write(100UL, 0xDEADBEEFu, 4);
        Rv64ArchState s = MakeState((2, 100UL));
        s.PrivilegeLevel = RvPrivilege.User;
        // satp defaults to 0 (Bare) in a freshly-constructed Rv64ArchState
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.Null(r.Trap);
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Load_Sv39_ValidUserMapping_TranslatesAddress() {
        // VA 0x0000_0000_0000_3000 → VPN[2]=0, VPN[1]=0, VPN[0]=3, offset=0 → PA 0x3000
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        mem.Write(0x3018UL, UserRwPte(4), 8); // L0 PT entry 3 → PA 0x4000
        mem.Write(0x4000UL, 0xBEEFCAFEu, 4);
        Rv64ArchState s = MakeState((2, 0x3000UL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.Null(r.Trap);
        Assert.Equal(0xBEEFCAFEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Load_Sv39_InvalidPte_RaisesLoadPageFault() {
        // L0 PTE for VPN[0]=4 left as zero (V=0) — page not present
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        Rv64ArchState s = MakeState((2, 0x4000UL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
        Assert.Equal(0x4000UL, r.Trap.TrapValue);
    }

    [Fact]
    public void Execute_Store_Sv39_WriteProtected_RaisesStorePageFault() {
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        ulong roPage = (5UL << 10) | 0b0101_0011UL; // A|U|R|V, no W, no D
        mem.Write(0x3028UL, roPage, 8);             // L0 PT entry 5 → PA 0x5000
        Rv64ArchState s = MakeState((2, 0x5000UL), (3, 0xABCDUL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x00312023, s, mem); // sw x3, 0(x2)
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.StorePageFault, r.Trap.Cause);
        Assert.Equal(0x5000UL, r.Trap.TrapValue);
    }

    [Fact]
    public void Execute_Load_Sv39_AccessBitClear_RaisesLoadPageFault() {
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        ulong noABit = (6UL << 10) | 0b0001_0111UL; // U|W|R|V, no A, no D
        mem.Write(0x3030UL, noABit, 8);             // L0 PT entry 6
        Rv64ArchState s = MakeState((2, 0x6000UL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv39_KernelPage_UserAccess_RaisesLoadPageFault() {
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        ulong kernelPage = (7UL << 10) | 0b1100_0011UL; // D|A|R|V, no U, no W
        mem.Write(0x3038UL, kernelPage, 8);             // L0 PT entry 7
        Rv64ArchState s = MakeState((2, 0x7000UL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv39_UserPage_SMode_SumDisabled_RaisesLoadPageFault() {
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        mem.Write(0x3000UL, UserRwPte(4), 8); // L0 entry 0 → PA 0x4000
        mem.Write(0x4000UL, 0xCAFEBABEu, 4);
        Rv64ArchState s = MakeState((2, 0x0000UL));
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.Supervisor;  // SUM bit left clear
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv39_UserPage_SMode_SumEnabled_Succeeds() {
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        mem.Write(0x3008UL, UserRwPte(4), 8); // L0 entry 1 → PA 0x4000
        mem.Write(0x4000UL, 0xDEADC0DEu, 4);
        Rv64ArchState s = MakeState((2, 0x1000UL));
        SetSatp(s, mem, satp);
        s.SystemRegisters.Write(CsrFile.Sstatus, CsrFile.SstatusSum, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.Supervisor;
        ExecuteResult r = Exec(0x00016183, s, mem); // lwu x3, 0(x2)
        Assert.Null(r.Trap);
        Assert.Equal(0xDEADC0DEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Fetch_Sv39_UserPage_SMode_SumEnabled_StillRaisesInstructionPageFault() {
        // SUM never grants S-mode instruction-fetch access to U-pages (priv spec §4.3.1).
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        ulong execUserPte = (5UL << 10) | 0b0101_1111UL; // A|U|X|R|V
        mem.Write(0x3010UL, execUserPte, 8);             // L0 entry 2 → VA 0x2000 → PA 0x5000
        Rv64ArchState s = MakeState();
        SetSatp(s, mem, satp);
        s.SystemRegisters.Write(CsrFile.Sstatus, CsrFile.SstatusSum, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.Supervisor;
        var translator = new Rv64FetchTranslator(s, mem);
        (_, int fault) = translator.Translate(0x2000UL);
        Assert.Equal(RvTrapCause.InstructionPageFault, fault);
    }

    [Fact]
    public void Fetch_Sv39_NonCanonicalAddress_RaisesInstructionPageFault() {
        // VA bit 38 set but bits 63:39 clear — fails the 39-bit sign-extension check.
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        Rv64ArchState s = MakeState();
        SetSatp(s, mem, satp);
        s.PrivilegeLevel = RvPrivilege.User;
        var translator = new Rv64FetchTranslator(s, mem);
        (_, int fault) = translator.Translate(1UL << 38);
        Assert.Equal(RvTrapCause.InstructionPageFault, fault);
    }

    [Fact]
    public void Execute_SatpWrite_FromUser_RaisesIllegalInstruction() {
        Rv64ArchState s = MakeState((1, (8UL << 60) | 1UL));
        s.PrivilegeLevel = RvPrivilege.User;
        var mem = new FlatMemory(0x10000);
        ExecuteResult r = Exec(Rv64Sv39Tests.CsrrwSatpX1, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap.Cause);
    }

    [Fact]
    public void Execute_SatpCsrrs_RS1Zero_ReadsWithoutWriting() {
        // csrrs x5, satp, x0 — rs1=x0 means "read only", per spec §2.8.
        (FlatMemory mem, ulong satp) = BuildSv39Memory();
        Rv64ArchState s = MakeState();
        SetSatp(s, mem, satp);
        uint csrrsSatpRead = (CsrFile.Satp << 20) | (0u << 15) | (0b010u << 12) | (5u << 7) | 0x73;
        ExecuteResult r = Exec(csrrsSatpRead, s, mem);
        Assert.Null(r.Trap);
        Assert.Equal(satp, r.RegisterResult.Value);
    }
}