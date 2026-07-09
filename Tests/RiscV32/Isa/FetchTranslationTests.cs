using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

namespace Tests.RiscV32.Isa;

/// <summary>
/// End-to-end tests for instruction fetch address translation (Sv32).
/// Verifies that InstructionPageFault (cause 12) is raised through all four
/// pipeline types when a fetch hits a page with X=0 or an invalid PTE.
/// </summary>
public class FetchTranslationTests {
    // ── Sv32 memory builder ────────────────────────────────────────────────────

    // Memory layout:
    //   0x0000..0x0FFF  trap handler area (physically addressed; M-mode on trap entry)
    //   0x1000          root page table (PPN=1)
    //   0x2000          level-1 page table (PPN=2)
    //   0x3000          code/data page (PPN=3) — used for executable tests
    //   0x4000          non-executable data page (PPN=4) — for fault tests
    //
    // Virtual address layout (with VPN[1]=0, VPN[0]=varies):
    //   VA 0x0000_0000  → mapped by L1 PT entry 0
    //   VA 0x0000_1000  → mapped by L1 PT entry 1  (etc.)
    //
    // satp = 0x8000_0001: MODE=1, PPN=1 (root PT at PA 0x1000)

    private const uint Satp = 0x80000001u;  // MODE=1, root at PA 0x1000
    private const ulong HandlerPa = 0x0000; // physical: M-mode handler
    private const ulong RootPtPa = 0x1000;
    private const ulong L1PtPa = 0x2000;
    private const ulong CodePa = 0x3000; // executable code page (PPN=3)
    private const ulong DataPa = 0x4000; // non-executable data page (PPN=4)

    private const uint Ebreak = 0x00100073u;
    private const uint Nop = 0x00000013u;   // addi x0, x0, 0
    private const uint Addi1 = 0x00100093u; // addi x1, x0, 1

    // VA 0x0000_N000 maps through L1 PT entry N.
    // PTE: X|R|U|A|V for executable user page.
    private static uint ExecUserPte(uint ppn) => (ppn << 10) | 0b0101_1111u; // A|U|X|R|V (no W,D)

    // PTE: R|U|A|V but no X — read-only user page (fetch should fault).
    private static uint RoUserPte(uint ppn) => (ppn << 10) | 0b0101_0011u; // A|U|R|V (no X,W)

    private static FlatMemory BuildMemory(Action<FlatMemory> configureL1) {
        var mem = new FlatMemory(0x5000);
        // Root PT entry 0: pointer PTE → L1 PT at PA 0x2000, V=1
        mem.Write(FetchTranslationTests.RootPtPa, (2u << 10) | 1u, 4);
        configureL1(mem);
        // Physical handler at 0x0000: just an EBREAK (halts the pipeline).
        mem.Load(FetchTranslationTests.HandlerPa, BitConverter.GetBytes(FetchTranslationTests.Ebreak));
        return mem;
    }

    private static void WriteCsr(IArchState s, uint addr, uint val) =>
        s.SystemRegisters.Write(addr, val, RvPrivilege.Machine);

    private static ulong ReadCsr(IArchState s, uint addr) =>
        s.SystemRegisters.Read(addr, RvPrivilege.Machine);

    // Sets up Sv32 mode and U-privilege on the given arch state.
    private static void EnableSv32(IArchState s) {
        WriteCsr(s, CsrFile.Satp, FetchTranslationTests.Satp);
        // mtvec points to the physical handler page (PA 0x0000).
        // On trap entry the CPU is in M-mode, so no address translation → physical read.
        WriteCsr(s, CsrFile.Mtvec, 0x0000);
        s.PrivilegeLevel = RvPrivilege.User;
    }

    // ── FiveStageTrain ─────────────────────────────────────────────────────────

    [Fact]
    public void FiveStage_Sv32_NonExecutablePage_RaisesInstructionPageFault() {
        // L1 PT entry 0: VA 0x0000 → PA 0x4000 (R=1, X=0 — fetch should fault).
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, RoUserPte(4), 4));
        // Place EBREAK at PA 0x4000 — it should never be reached.
        mem.Load(FetchTranslationTests.DataPa, BitConverter.GetBytes(FetchTranslationTests.Nop));

        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        // mcause must be InstructionPageFault (12); mepc = faulting VA.
        Assert.Equal(12uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(0x0000uL, ReadCsr(train.ArchState, CsrFile.Mepc));
        // mtval = faulting virtual address.
        Assert.Equal(0x0000uL, ReadCsr(train.ArchState, CsrFile.Mtval));
    }

    [Fact]
    public void FiveStage_Sv32_InvalidPte_RaisesInstructionPageFault() {
        // L1 PT entry 0 left as zero (V=0) — page not present.
        FlatMemory mem = BuildMemory(_ => { });

        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        Assert.Equal(12uL, ReadCsr(train.ArchState, CsrFile.Mcause));
    }

    [Fact]
    public void FiveStage_Sv32_ExecutablePage_RunsNormally() {
        // L1 PT entry 0: VA 0x0000 → PA 0x3000 (X=1 — execute permitted).
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, ExecUserPte(3), 4));
        // Code at PA 0x3000: addi x1,x0,1 then ebreak.
        mem.Load(
            FetchTranslationTests.CodePa, [
                .. BitConverter.GetBytes(FetchTranslationTests.Addi1),
                .. BitConverter.GetBytes(FetchTranslationTests.Ebreak),
            ]
        );

        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(30);

        // No trap should fire; x1 should hold 1.
        Assert.Equal(0uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(1uL, train.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void FiveStage_BareMode_NoFetchTranslation() {
        // satp = 0 (MODE=0): fetch uses PA directly regardless of privilege.
        var mem = new FlatMemory(0x1000);
        mem.Load(
            0,
            [
                .. BitConverter.GetBytes(FetchTranslationTests.Addi1),
                .. BitConverter.GetBytes(FetchTranslationTests.Ebreak),
            ]
        );

        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        // Leave satp = 0 (default); switch to User mode.
        train.ArchState.PrivilegeLevel = RvPrivilege.User;

        train.Run(20);

        Assert.Equal(0uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(1uL, train.ArchState.IntegerRegisters.Read(1));
    }

    // ── SingleCycleTrain ───────────────────────────────────────────────────────

    [Fact]
    public void SingleCycle_Sv32_NonExecutablePage_RaisesInstructionPageFault() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, RoUserPte(4), 4));
        mem.Load(FetchTranslationTests.DataPa, BitConverter.GetBytes(FetchTranslationTests.Nop));

        var train = new SingleCycleTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        Assert.Equal(12uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(0x0000uL, ReadCsr(train.ArchState, CsrFile.Mepc));
    }

    [Fact]
    public void SingleCycle_Sv32_ExecutablePage_RunsNormally() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, ExecUserPte(3), 4));
        mem.Load(
            FetchTranslationTests.CodePa,
            [
                .. BitConverter.GetBytes(FetchTranslationTests.Addi1),
                .. BitConverter.GetBytes(FetchTranslationTests.Ebreak),
            ]
        );

        var train = new SingleCycleTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        Assert.Equal(0uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(1uL, train.ArchState.IntegerRegisters.Read(1));
    }

    // ── OooeTrain ──────────────────────────────────────────────────────────────

    [Fact]
    public void OooE_Sv32_NonExecutablePage_RaisesInstructionPageFault() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, RoUserPte(4), 4));
        mem.Load(FetchTranslationTests.DataPa, BitConverter.GetBytes(FetchTranslationTests.Nop));

        var train = new OooeTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(50);

        Assert.Equal(12uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(0x0000uL, ReadCsr(train.ArchState, CsrFile.Mepc));
    }

    [Fact]
    public void OooE_Sv32_ExecutablePage_RunsNormally() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, ExecUserPte(3), 4));
        mem.Load(
            FetchTranslationTests.CodePa,
            [
                .. BitConverter.GetBytes(FetchTranslationTests.Addi1),
                .. BitConverter.GetBytes(FetchTranslationTests.Ebreak),
            ]
        );

        var train = new OooeTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(50);

        Assert.Equal(0uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(1uL, train.ArchState.IntegerRegisters.Read(1));
    }

    // ── SuperscalarTrain ───────────────────────────────────────────────────────

    [Fact]
    public void Superscalar_Sv32_NonExecutablePage_RaisesInstructionPageFault() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, RoUserPte(4), 4));
        mem.Load(FetchTranslationTests.DataPa, BitConverter.GetBytes(FetchTranslationTests.Nop));

        var train = new SuperscalarTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        Assert.Equal(12uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(0x0000uL, ReadCsr(train.ArchState, CsrFile.Mepc));
    }

    [Fact]
    public void Superscalar_Sv32_ExecutablePage_RunsNormally() {
        FlatMemory mem = BuildMemory(m => m.Write(FetchTranslationTests.L1PtPa, ExecUserPte(3), 4));
        mem.Load(
            FetchTranslationTests.CodePa,
            [
                .. BitConverter.GetBytes(FetchTranslationTests.Addi1),
                .. BitConverter.GetBytes(FetchTranslationTests.Ebreak),
            ]
        );

        var train = new SuperscalarTrain(new Rv32Mechanism(), mem);
        EnableSv32(train.ArchState);

        train.Run(20);

        Assert.Equal(0uL, ReadCsr(train.ArchState, CsrFile.Mcause));
        Assert.Equal(1uL, train.ArchState.IntegerRegisters.Read(1));
    }
}