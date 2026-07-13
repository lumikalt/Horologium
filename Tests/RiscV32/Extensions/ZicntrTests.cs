using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for Zicntr (hardware performance counters):
///     cycle/cycleh   (0xC00/0xC80) — shadow of mcycle/mcycleh
///     time/timeh     (0xC01/0xC81) — no external CLINT; reads as 0
///     instret/instreth (0xC02/0xC82) — shadow of minstret/minstreth
///     mcycle/mcycleh   (0xB00/0xB80) — machine-level cycle counter
///     minstret/minstreth (0xB02/0xB82) — machine-level retired-instruction counter
/// </summary>
public class ZicntrTests {
    private const ulong CodeBase = 0x1000u;
    private const ulong OutBase = 0x0100u;

    // ── Encoding helpers ──────────────────────────────────────────────────────

    private static uint EBreak() => 0x00100073u;
    private static uint Nop() => 0x00000013u; // ADDI x0, x0, 0

    // CSRRS rd, csr, x0 — read CSR into rd (rs1=x0 means no write side effects)
    private static uint CsrRead(int rd, uint csr) =>
        ((csr & 0xFFF) << 20) | (0 << 15) | (2 << 12) | (uint)((rd & 0x1F) << 7) | 0x73u;

    // SW rs2, imm(rs1)
    private static uint Sw(int rs1, int rs2, int imm) =>
        (((uint)(imm >> 5) & 0x7Fu) << 25) | ((uint)(rs2 & 0x1F) << 20) | ((uint)(rs1 & 0x1F) << 15)
      | (2u << 12) | ((uint)(imm & 0x1F) << 7) | 0x23u;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static uint ReadCsr(uint[] instructions, uint csr) {
        var mem = new FlatMemory(0x4000);
        // Program: read CSR into x1, store x1 to OutBase
        uint[] prog = [..instructions, CsrRead(1, csr), Sw(0, 1, (int)ZicntrTests.OutBase), EBreak(),];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        return (uint)mem.Read(ZicntrTests.OutBase, 4);
    }

    // ── time / timeh — always 0 ───────────────────────────────────────────────

    [Fact]
    public void Time_IsZero() => Assert.Equal(0u, ReadCsr([], 0xC01));

    [Fact]
    public void Timeh_IsZero() => Assert.Equal(0u, ReadCsr([], 0xC81));

    // ── cycle counter ─────────────────────────────────────────────────────────

    [Fact]
    public void Cycle_IsNonZeroAfterExecution() {
        // After at least one instruction retires, the cycle should be > 0.
        // (The CSRRS itself reads the count from the cycle that just completed.)
        uint v = ReadCsr([Nop(),], 0xC00);
        Assert.True(v > 0, $"cycle should be > 0, got {v}");
    }

    [Fact]
    public void Mcycle_MatchesCycle() {
        // mcycle (machine-mode) and cycle (user shadow) return the same value.
        var mem = new FlatMemory(0x4000);
        uint[] prog = [
            CsrRead(1, 0xC00), // x1 = cycle
            CsrRead(2, 0xB00), // x2 = mcycle  (one more cycle later)
            Sw(0, 1, (int)ZicntrTests.OutBase),
            Sw(0, 2, (int)(ZicntrTests.OutBase + 4)),
            EBreak(),
        ];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        var cyc = (uint)mem.Read(ZicntrTests.OutBase, 4);
        var mcyc = (uint)mem.Read(ZicntrTests.OutBase + 4, 4);
        // mcycle is read one cycle after cycle; it must be ≥ cycle.
        Assert.True(mcyc >= cyc, $"mcycle ({mcyc}) should be ≥ cycle ({cyc})");
    }

    [Fact]
    public void Cycle_IncreasesMonotonically() {
        var mem = new FlatMemory(0x4000);
        uint[] prog = [
            CsrRead(1, 0xC00), // x1 = cycle at T
            Nop(),             // burn a cycle
            CsrRead(2, 0xC00), // x2 = cycle at T+2 (or later)
            Sw(0, 1, (int)ZicntrTests.OutBase),
            Sw(0, 2, (int)(ZicntrTests.OutBase + 4)),
            EBreak(),
        ];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        var first = (uint)mem.Read(ZicntrTests.OutBase, 4);
        var second = (uint)mem.Read(ZicntrTests.OutBase + 4, 4);
        Assert.True(second > first, $"second cycle read ({second}) must exceed first ({first})");
    }

    // ── instret counter ───────────────────────────────────────────────────────

    [Fact]
    public void Instret_IsNonZeroAfterExecution() {
        // After at least one instruction retires, instret should be > 0.
        uint v = ReadCsr([Nop(),], 0xC02);
        Assert.True(v > 0, $"instret should be > 0, got {v}");
    }

    [Fact]
    public void Minstret_MatchesInstret() {
        // minstret and instret should be close (one instruction apart at most).
        var mem = new FlatMemory(0x4000);
        uint[] prog = [
            CsrRead(1, 0xC02), // x1 = instret
            CsrRead(2, 0xB02), // x2 = minstret (one retire later)
            Sw(0, 1, (int)ZicntrTests.OutBase),
            Sw(0, 2, (int)(ZicntrTests.OutBase + 4)),
            EBreak(),
        ];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        var ir = (uint)mem.Read(ZicntrTests.OutBase, 4);
        var mir = (uint)mem.Read(ZicntrTests.OutBase + 4, 4);
        Assert.True(mir >= ir, $"minstret ({mir}) should be ≥ instret ({ir})");
    }

    [Fact]
    public void Instret_IncreasesMonotonically() {
        var mem = new FlatMemory(0x4000);
        uint[] prog = [
            CsrRead(1, 0xC02), // x1 = instret at T
            Nop(),
            CsrRead(2, 0xC02), // x2 = instret at T+2
            Sw(0, 1, (int)ZicntrTests.OutBase),
            Sw(0, 2, (int)(ZicntrTests.OutBase + 4)),
            EBreak(),
        ];
        for (var i = 0; i < prog.Length; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        var first = (uint)mem.Read(ZicntrTests.OutBase, 4);
        var second = (uint)mem.Read(ZicntrTests.OutBase + 4, 4);
        Assert.True(second > first, $"second instret read ({second}) must exceed first ({first})");
    }

    [Fact]
    public void Instret_CountsNInstructions() {
        // Run exactly N NOPs before reading instret; the delta should be N.
        const int nNops = 10;
        var mem = new FlatMemory(0x4000);
        var prog = new List<uint> { CsrRead(1, 0xC02), }; // x1 = instret before
        for (var i = 0; i < nNops; i++) prog.Add(Nop());
        prog.Add(CsrRead(2, 0xC02)); // x2 = instret after
        prog.Add(Sw(0, 1, (int)ZicntrTests.OutBase));
        prog.Add(Sw(0, 2, (int)(ZicntrTests.OutBase + 4)));
        prog.Add(EBreak());
        for (var i = 0; i < prog.Count; i++)
            mem.Load(ZicntrTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(prog[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZicntrTests.CodeBase);
        train.Run(500);
        var before = (uint)mem.Read(ZicntrTests.OutBase, 4);
        var after = (uint)mem.Read(ZicntrTests.OutBase + 4, 4);
        // nNops NOPs retire between the two reads; the second CSRRS also retires before
        // its own store. Delta = nNops + 1 (the second CSRRS itself).
        uint delta = after - before;
        Assert.Equal((uint)(nNops + 1), delta);
    }

    // ── cycleh / instreth high halves ─────────────────────────────────────────

    [Fact]
    public void Cycleh_IsZeroAtStart() {
        // Very early in execution, the high half should still be 0.
        uint v = ReadCsr([], 0xC80);
        Assert.Equal(0u, v);
    }

    [Fact]
    public void Instreth_IsZeroAtStart() {
        uint v = ReadCsr([], 0xC82);
        Assert.Equal(0u, v);
    }
}