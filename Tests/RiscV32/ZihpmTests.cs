using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Tests for Zihpm (hardware performance monitor):
///   hpmcounterN / hpmcounterNh  (N=3–31, 0xC03–0xC1F / 0xC83–0xC9F) — user-level read-only shadows, always 0
///   mhpmcounterN / mhpmcounterNh (0xB03–0xB1F / 0xB83–0xB9F) — M-mode counters, always 0 (no event hardware)
///   mhpmeventN  (N=3–31, 0x323–0x33F) — M-mode event selectors, writable, functionally ignored
/// </summary>
public class ZihpmTests {
    private const ulong CodeBase = 0x1000u;
    private const ulong OutBase = 0x0100u;

    // ── Encoding helpers ──────────────────────────────────────────────────────

    private static uint EBreak() => 0x00100073u;

    // CSRRS rd, csr, x0 — read CSR into rd (rs1=x0 → no write side-effect)
    private static uint CsrRead(int rd, uint csr) =>
        (uint)(((csr & 0xFFF) << 20) | (0 << 15) | (2 << 12) | ((rd & 0x1F) << 7) | 0x73u);

    // CSRRW x0, csr, rs1 — write rs1 to CSR (rd=x0 → discard old value)
    private static uint CsrWrite(int rs1, uint csr) =>
        (uint)(((csr & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (1 << 12) | (0 << 7) | 0x73u);

    // SW rs2, imm(rs1)
    private static uint Sw(int rs1, int rs2, int imm) =>
        (((uint)(imm >> 5) & 0x7Fu) << 25) | ((uint)(rs2 & 0x1F) << 20) | ((uint)(rs1 & 0x1F) << 15)
      | (2u << 12) | ((uint)(imm & 0x1F) << 7) | 0x23u;

    // ADDI rd, x0, imm
    private static uint Addi(int rd, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (0 << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Run setup instructions then read a CSR into x1, store to OutBase, return the value.
    private static uint ReadCsr(uint[] setup, uint csr) {
        var mem = new FlatMemory(0x4000);
        uint[] prog = [..setup, CsrRead(1, csr), Sw(0, 1, (int)ZihpmTests.OutBase), EBreak(),];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZihpmTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZihpmTests.CodeBase);
        train.Run(500);
        return (uint)mem.Read(ZihpmTests.OutBase, 4);
    }

    // ── hpmcounterN (user-level shadows, read-only, always 0) ────────────────

    [Fact]
    public void HpmCounter3_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xC03));

    [Fact]
    public void HpmCounter31_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xC1F));

    // ── hpmcounterNh (high-half user-level shadows, read-only, always 0) ─────

    [Fact]
    public void HpmCounter3h_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xC83));

    [Fact]
    public void HpmCounter31h_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xC9F));

    // ── mhpmcounterN (M-mode, always 0) ──────────────────────────────────────

    [Fact]
    public void MHpmCounter3_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xB03));

    [Fact]
    public void MHpmCounter31h_ReadsZero() => Assert.Equal(0u, ReadCsr([], 0xB9F));

    // ── mhpmeventN (M-mode, writable, functionally ignored) ──────────────────

    [Fact]
    public void MHpmEvent3_IsWritableAndReadBack() {
        // addi x1, x0, 0xAB → write 0xAB to mhpmevent3 → read back
        uint val = ReadCsr([Addi(1, 0xAB), CsrWrite(1, 0x323)], 0x323);
        Assert.Equal(0xABu, val);
    }

    [Fact]
    public void MHpmEvent31_IsWritableAndReadBack() {
        uint val = ReadCsr([Addi(1, 0x55), CsrWrite(1, 0x33F)], 0x33F);
        Assert.Equal(0x55u, val);
    }
}
