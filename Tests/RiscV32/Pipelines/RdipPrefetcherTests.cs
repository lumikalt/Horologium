#region

using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Tests for RAS-Directed Instruction Prefetching (RDIP, Kolli et al., MICRO 2013).
///     Each pipeline type checks:
///     (a) identical committed arch state — correctness;
///     (b) ICache.Prefetches &gt; 0 — RDIP actually issues prefetches.
/// </summary>
public class RdipPrefetcherTests {
    // Program layout (64-byte cache blocks, 4-way LRU, 256-byte = 4-block capacity):
    //
    //   Caller (0x000–0x03F, block B0):
    //     0x000: addi x5, x0, 3        # loop count
    //     0x004: jal  x1, 4092         # call to 0x1000; rd=x1 → IsCall
    //     0x008: addi x5, x5, -1
    //     0x00c: bne  x5, x0, -8       # back to 0x004
    //     0x010: ebreak
    //     0x014–0x03C: nop × 11 (padding)
    //
    //   Callee (0x1000–0x113F, blocks B_c1–B_c5):
    //     B_c1 0x1000–0x103F: nop × 16
    //     B_c2 0x1040–0x107F: nop × 16
    //     B_c3 0x1080–0x10BF: nop × 16
    //     B_c4 0x10C0–0x10FF: nop × 16
    //     B_c5 0x1100–0x113F: nop × 15, then jalr x0, x1, 0  → IsReturn at 0x113C
    //
    // Callee spans 5 blocks. With 4-way LRU cache, loading B_c5 evicts B_c1.
    // Wrong-path from 0x008 (BNE predicted not-taken → sequential → 0x010 ebreak)
    // never reaches 0x1000, so callee blocks are not speculatively pre-loaded.
    // On the 2nd call, B_c1 is absent when RDIP fires → Prefetches > 0.
    private const uint Nop = 0x00000013;

    // addi x5, x0, 3
    private const uint AddiX5Imm3 = 0x00300293;

    // jal x1, 4092  (offset=4092 → target 0x1000 from PC=0x004); rd=1 → IsCall
    private const uint JalX1Imm4092 = 0x7FD000EF;

    // addi x5, x5, -1
    private const uint AddiX5Dec = 0xFFF28293;

    // bne x5, x0, -8
    private const uint BneX5Neg8 = 0xFE029CE3;

    private const uint Ebreak = 0x00100073;

    // jalr x0, x1, 0  (rd=0, rs1=1) → IsReturn
    private const uint JalrRet = 0x00008067;

    private static readonly uint[] CallerWords;
    private static readonly uint[] CalleeWords;

    static RdipPrefetcherTests() {
        var caller = new List<uint> {
            RdipPrefetcherTests.AddiX5Imm3,   // 0x000
            RdipPrefetcherTests.JalX1Imm4092, // 0x004 (call 0x1000)
            RdipPrefetcherTests.AddiX5Dec,    // 0x008
            RdipPrefetcherTests.BneX5Neg8,    // 0x00c
            RdipPrefetcherTests.Ebreak,       // 0x010
        };
        for (var i = 0; i < 11; i++) caller.Add(RdipPrefetcherTests.Nop); // 0x014–0x03C
        RdipPrefetcherTests.CallerWords = caller.ToArray();

        // 79 nops (0x1000–0x1138) then jalr at 0x113C
        var callee = new List<uint>();
        for (var i = 0; i < 79; i++) callee.Add(RdipPrefetcherTests.Nop);
        callee.Add(RdipPrefetcherTests.JalrRet);
        RdipPrefetcherTests.CalleeWords = callee.ToArray();
    }

    private static void Load(FlatMemory mem, ulong address, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(address, bytes);
    }

    // 256 bytes, 4-way, 64-byte blocks = 4 blocks capacity, 10-cycle miss latency.
    // The callee spans 5 blocks (B_c1 - B_c5); its 5th block evicts B_c1 on each call.
    private static MemoryConfig ICache() => new(256, 4, 64);

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Fact]
    public void Rdip_FiveStage_ArchStateIdenticalToWithout() {
        var memOff = new FlatMemory(8192);
        var memOn = new FlatMemory(8192);
        Load(memOff, 0, RdipPrefetcherTests.CallerWords);
        Load(memOn, 0, RdipPrefetcherTests.CallerWords);
        Load(memOff, 0x1000, RdipPrefetcherTests.CalleeWords);
        Load(memOn, 0x1000, RdipPrefetcherTests.CalleeWords);

        var off = new FiveStageTrain(new Rv32Mechanism(), memOff, iMemConfig: ICache());
        var on = new FiveStageTrain(new Rv32Mechanism(), memOn, iMemConfig: ICache(), rdip: true);

        off.Run();
        on.Run();

        for (var r = 0; r < 32; r++)
            Assert.Equal(
                off.ArchState.IntegerRegisters.Read(r),
                on.ArchState.IntegerRegisters.Read(r)
            );
    }

    [Fact]
    public void Rdip_FiveStage_IssuesPrefetchesIntoICache() {
        var mem = new FlatMemory(8192);
        Load(mem, 0, RdipPrefetcherTests.CallerWords);
        Load(mem, 0x1000, RdipPrefetcherTests.CalleeWords);

        var train = new FiveStageTrain(new Rv32Mechanism(), mem, iMemConfig: ICache(), rdip: true);
        train.Run();

        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Prefetches > 0, "RDIP should issue at least one I-cache prefetch");
    }

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rdip_OoO_ArchStateIdenticalToWithout() {
        var memOff = new FlatMemory(8192);
        var memOn = new FlatMemory(8192);
        Load(memOff, 0, RdipPrefetcherTests.CallerWords);
        Load(memOn, 0, RdipPrefetcherTests.CallerWords);
        Load(memOff, 0x1000, RdipPrefetcherTests.CalleeWords);
        Load(memOn, 0x1000, RdipPrefetcherTests.CalleeWords);

        var off = new OooeTrain(new Rv32Mechanism(), memOff, iMemConfig: ICache());
        var on = new OooeTrain(new Rv32Mechanism(), memOn, iMemConfig: ICache(), rdip: true);

        off.Run();
        on.Run();

        for (var r = 0; r < 32; r++)
            Assert.Equal(
                off.ArchState.IntegerRegisters.Read(r),
                on.ArchState.IntegerRegisters.Read(r)
            );
    }

    [Fact]
    public void Rdip_OoO_IssuesPrefetchesIntoICache() {
        var mem = new FlatMemory(8192);
        Load(mem, 0, RdipPrefetcherTests.CallerWords);
        Load(mem, 0x1000, RdipPrefetcherTests.CalleeWords);

        var train = new OooeTrain(new Rv32Mechanism(), mem, iMemConfig: ICache(), rdip: true);
        train.Run();

        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Prefetches > 0, "RDIP should issue at least one I-cache prefetch");
    }
}