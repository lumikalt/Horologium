#region

using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for Zawrs, Zicbom, Zicboz, and Zimop NOP/minor-effect extensions.
///     <para>
///         Zawrs: wrs.nto / wrs.sto — NOP in single-core simulation.
///         Zicbom: cbo.inval / cbo.clean / cbo.flush — NOP on an uncached memory chain (nothing to
///         maintain); against a configured write-back D-cache they drive real dirty-line state
///         (clean = writeback and keep, flush = writeback and invalidate, inval = discard without
///         writeback) — see the cache-backed tests below.
///         Zicboz: cbo.zero — zeros a 64-byte cache-line-aligned block.
///         Zimop: mop.r.N / mop.rr.N — always write 0 to rd.
///     </para>
/// </summary>
public class ZawrsZicbomZicbozZimopTests {
    private const ulong CodeBase = 0x1000u;

    // ── Encoding helpers ──────────────────────────────────────────────────────

    private static uint EBreak() => 0x00100073u;

    // I-type: imm[11:0] | rs1[4:0] | funct3[2:0] | rd[4:0] | opcode[6:0]
    private static uint IType(int imm12, int rs1, int funct3, int rd, int opcode) =>
        (uint)(((imm12 & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7)
             | (opcode & 0x7F));

    // SW: opcode=0x23; imm split into [11:5] in bits[31:25] and [4:0] in bits[11:7]
    private static uint Sw(int rs1, int rs2, int imm12) =>
        (uint)((((imm12 >> 5) & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (2 << 12) | ((imm12 & 0x1F) << 7) | 0x23u);

    // ADDI: opcode=0x13, funct3=0 — used to load a small constant into a register
    private static uint Addi(int rd, int rs1, int imm12) => IType(imm12, rs1, 0, rd, 0x13);

    // Zawrs: SYSTEM space (opcode=0x73), funct3=0
    private static uint WrsNto() => 0x00D00073u;
    private static uint WrsSto() => 0x01D00073u;

    // Zicbom/Zicboz: opcode=0x0F, funct3=2; bits[24:20] select op; rs1 = base address register
    private static uint CboInval(int rs1) =>
        (uint)((0x00 << 20) | ((rs1 & 0x1F) << 15) | (2 << 12) | 0x0Fu);

    private static uint CboClean(int rs1) =>
        (uint)((0x01 << 20) | ((rs1 & 0x1F) << 15) | (2 << 12) | 0x0Fu);

    private static uint CboFlush(int rs1) =>
        (uint)((0x02 << 20) | ((rs1 & 0x1F) << 15) | (2 << 12) | 0x0Fu);

    private static uint CboZero(int rs1) =>
        (uint)((0x04 << 20) | ((rs1 & 0x1F) << 15) | (2 << 12) | 0x0Fu);

    // Zimop: opcode=0x73, funct3=4; CSR address pattern selects mop.r vs mop.rr.
    // mop.r.0: csr=0x81C → (0x81C << 20) | (0 << 15) | (4 << 12) | (rd << 7) | 0x73
    // mop.rr.0: csr=0x823 → (0x823 << 20) | (rs1 << 15) | (4 << 12) | (rd << 7) | 0x73
    private static uint MopR(int rd) =>
        (uint)((0x81C << 20) | (0 << 15) | (4 << 12) | ((rd & 0x1F) << 7) | 0x73u);

    private static uint MopRr(int rd, int rs1) =>
        (uint)((0x823 << 20) | ((rs1 & 0x1F) << 15) | (4 << 12) | ((rd & 0x1F) << 7) | 0x73u);

    // ── Test runners ──────────────────────────────────────────────────────────

    /// Run a sequence of instructions and return whether execution completed without exception.
    private static bool RunProgram(uint[] instructions) {
        var mem = new FlatMemory(0x4000);
        for (var i = 0; i < instructions.Length; i++)
            mem.Load(ZawrsZicbomZicbozZimopTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(instructions[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZawrsZicbomZicbozZimopTests.CodeBase);
        train.Run(200);
        return true;
    }

    /// Run a program that stores x3 to address 0x100 (via Sw(0, 3, 0x100)) and returns that value.
    private static uint RunForResult(uint[] instructions) {
        const ulong outAddr = 0x100;
        var mem = new FlatMemory(0x4000);
        for (var i = 0; i < instructions.Length; i++)
            mem.Load(ZawrsZicbomZicbozZimopTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(instructions[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZawrsZicbomZicbozZimopTests.CodeBase);
        train.Run(200);
        return (uint)mem.Read(outAddr, 4);
    }

    /// Run a program and return the memory contents at [addr, addr+bytes).
    private static byte[] RunAndReadMemory(
        uint[] instructions,
        ulong addr,
        int bytes,
        Action<FlatMemory>? setup = null
    ) {
        var mem = new FlatMemory(0x4000);
        setup?.Invoke(mem);
        for (var i = 0; i < instructions.Length; i++)
            mem.Load(ZawrsZicbomZicbozZimopTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(instructions[i]));
        var train = new SingleCycleTrain(new Rv32Mechanism(), mem, ZawrsZicbomZicbozZimopTests.CodeBase);
        train.Run(200);
        var result = new byte[bytes];
        for (var i = 0; i < bytes; i++) result[i] = (byte)mem.Read(addr + (ulong)i, 1);
        return result;
    }

    // ── Zawrs tests ───────────────────────────────────────────────────────────

    [Fact]
    public void WrsNto_IsNop() { Assert.True(RunProgram([WrsNto(), EBreak(),])); }

    [Fact]
    public void WrsSto_IsNop() { Assert.True(RunProgram([WrsSto(), EBreak(),])); }

    [Fact]
    public void WrsNto_DoesNotModifyRegister() {
        // Set x3=42, run wrs.nto, store x3, expect it's unchanged
        uint[] prog = [
            Addi(3, 0, 42),  // x3 = 42
            WrsNto(),        // NOP
            Sw(0, 3, 0x100), // mem[0x100] = x3
            EBreak(),
        ];
        Assert.Equal(42u, RunForResult(prog));
    }

    // ── Zicbom tests ─────────────────────────────────────────────────────────

    [Fact]
    public void CboInval_IsNop() { Assert.True(RunProgram([CboInval(0), EBreak(),])); }

    [Fact]
    public void CboClean_IsNop() { Assert.True(RunProgram([CboClean(0), EBreak(),])); }

    [Fact]
    public void CboFlush_IsNop() { Assert.True(RunProgram([CboFlush(0), EBreak(),])); }

    [Fact]
    public void CboInval_DoesNotModifyMemory() {
        // Fill some memory, run cbo.inval, verify memory unchanged
        const ulong sentinel = 0x300;
        byte[] before = [0xDE, 0xAD, 0xBE, 0xEF,];
        uint[] prog = [
            Addi(1, 0, 0x40), // x1 = 0x40 (some base address)
            CboInval(1),      // cbo.inval(x1) — NOP
            EBreak(),
        ];
        byte[] after = RunAndReadMemory(prog, sentinel, 4, m => { m.Load(sentinel, before); });
        Assert.Equal(before, after);
    }

    // ── Zicbom cache-backed tests ────────────────────────────────────────────

    private static MemoryConfig WriteBackDCacheConfig() => new(
        64, 4, 16, 5,
        CacheWritePolicy: WritePolicyKind.WriteBack, CacheWriteMissPolicy: WriteMissPolicyKind.WriteAllocate
    );

    private static SingleCycleTrain RunWithDCache(uint[] instructions, out FlatMemory mem) {
        mem = new FlatMemory(0x4000);
        for (var i = 0; i < instructions.Length; i++)
            mem.Load(ZawrsZicbomZicbozZimopTests.CodeBase + (ulong)(i * 4), BitConverter.GetBytes(instructions[i]));
        var train = new SingleCycleTrain(
            new Rv32Mechanism(), mem, ZawrsZicbomZicbozZimopTests.CodeBase, dMemConfig: WriteBackDCacheConfig()
        );
        train.Run(200);
        return train;
    }

    [Fact]
    public void CboClean_DirtyLine_WritesBackAndStaysResident() {
        uint[] prog = [
            Addi(1, 0, 0x40), // x1 = 0x40 (cache-line-aligned)
            Addi(3, 0, 0x2A), // x3 = 42
            Sw(1, 3, 0),      // mem[0x40] = 42 (write-allocate miss: dirties the line in cache)
            CboClean(1),      // cbo.clean(x1): writeback, stays resident
            EBreak(),
        ];
        SingleCycleTrain train = RunWithDCache(prog, out FlatMemory mem);

        Assert.True(train.DCache!.IsSectorResident(0x40)); // still resident after clean
        Assert.Equal(42UL, mem.Read(0x40, 4));             // dirty data written back to backing
    }

    [Fact]
    public void CboFlush_DirtyLine_WritesBackAndInvalidates() {
        uint[] prog = [
            Addi(1, 0, 0x40),
            Addi(3, 0, 0x2A),
            Sw(1, 3, 0),
            CboFlush(1), // cbo.flush(x1): writeback, then invalidate
            EBreak(),
        ];
        SingleCycleTrain train = RunWithDCache(prog, out FlatMemory mem);

        Assert.False(train.DCache!.IsSectorResident(0x40)); // invalidated
        Assert.Equal(42UL, mem.Read(0x40, 4));              // dirty data written back before invalidate
    }

    [Fact]
    public void CboInval_DirtyLine_DiscardsWithoutWritingBack() {
        uint[] prog = [
            Addi(1, 0, 0x40),
            Addi(3, 0, 0x2A),
            Sw(1, 3, 0),
            CboInval(1), // cbo.inval(x1): discard dirty data, invalidate
            EBreak(),
        ];
        SingleCycleTrain train = RunWithDCache(prog, out FlatMemory mem);

        Assert.False(train.DCache!.IsSectorResident(0x40)); // invalidated
        Assert.Equal(0UL, mem.Read(0x40, 4));               // dirty data discarded, never reached backing
    }

    // ── Zicboz tests ─────────────────────────────────────────────────────────

    [Fact]
    public void CboZero_ZeroesAligned64Bytes() {
        // Pre-fill the 64-byte region at 0x200 with 0xFF bytes, then cbo.zero it.
        const ulong lineAddr = 0x200u;
        uint[] prog = [
            Addi(1, 0, (int)lineAddr), // x1 = 0x200 (64-byte aligned)
            CboZero(1),                // zero the cache line
            EBreak(),
        ];
        byte[] after = RunAndReadMemory(
            prog, lineAddr, 64, m => {
                for (ulong i = 0; i < 64; i += 4) m.Load(lineAddr + i, [0xFF, 0xFF, 0xFF, 0xFF,]);
            }
        );
        Assert.All(after, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CboZero_AlignsDown() {
        // Call cbo.zero with an unaligned address inside the line; the whole line must be zeroed.
        const ulong lineAddr = 0x200u;
        uint[] prog = [
            Addi(1, 0, (int)(lineAddr + 7)), // x1 = 0x207 (inside the line at 0x200)
            CboZero(1),
            EBreak(),
        ];
        byte[] after = RunAndReadMemory(
            prog, lineAddr, 64, m => {
                for (ulong i = 0; i < 64; i += 4) m.Load(lineAddr + i, [0xFF, 0xFF, 0xFF, 0xFF,]);
            }
        );
        Assert.All(after, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CboZero_DoesNotZeroAdjacentLine() {
        // Verify cbo.zero at 0x200 does not touch memory at 0x240 (next line).
        const ulong lineAddr = 0x200u;
        const ulong nextLine = 0x240u;
        const byte sentinel = 0xAB;
        uint[] prog = [
            Addi(1, 0, (int)lineAddr),
            CboZero(1),
            EBreak(),
        ];
        byte[] after = RunAndReadMemory(
            prog, nextLine, 4, m => { m.Load(nextLine, [sentinel, sentinel, sentinel, sentinel,]); }
        );
        Assert.All(after, b => Assert.Equal(sentinel, b));
    }

    // ── Zimop tests ───────────────────────────────────────────────────────────

    [Fact]
    public void MopR_WritesZeroToRd() {
        // x3 = 0xDEAD_BEEF, then mop.r.0 x3, then store x3 — expect 0
        uint[] prog = [
            // Load 0xDEADBEEF into x3 via LUI + ADDI
            0xDEADC000u | (3 << 7) | 0x37u, // LUI x3, 0xDEADB
            IType(-0x411, 3, 0, 3, 0x13),   // ADDI x3, x3, -0x411 (makes DEADBEEF)
            MopR(3),                        // mop.r.0 x3 → x3 = 0
            Sw(0, 3, 0x100),
            EBreak(),
        ];
        Assert.Equal(0u, RunForResult(prog));
    }

    [Fact]
    public void MopRr_WritesZeroToRd() {
        uint[] prog = [
            Addi(1, 0, 99), // x1 = 99 (source register — ignored by mop.rr)
            Addi(3, 0, 42), // x3 = 42
            MopRr(3, 1),    // mop.rr.0 x3, x1 → x3 = 0
            Sw(0, 3, 0x100),
            EBreak(),
        ];
        Assert.Equal(0u, RunForResult(prog));
    }

    [Fact]
    public void MopR_DifferentNValues_AllReturnZero() {
        // mop.r.1 (N=1, csr=0x81D), mop.r.7 (N=7, csr=0x81F), mop.r.16 (N=16, csr=0x91C)
        foreach (int csr in new[] { 0x81D, 0x81F, 0x91C, }) {
            uint[] prog = [
                Addi(3, 0, 99),
                MopRn(3, csr),
                Sw(0, 3, 0x100),
                EBreak(),
            ];
            Assert.Equal(0u, RunForResult(prog));
        }

        return;

        // mop.r.1: csr=0x81D, mop.r.7: csr=0x81F (N=0b00111 → all bottom 3 bits set)
        // mop.r.31: csr = 0x81C | (1<<10) | (1<<7) | (1<<6) | (1<<1) | 1 = 0xBC7
        // These are some N values; all should produce 0 in rd.
        uint MopRn(int rd, int csr) =>
            (uint)(((csr & 0xFFF) << 20) | (0 << 15) | (4 << 12) | ((rd & 0x1F) << 7) | 0x73u);
    }
}