using Pipeline;
using RiscV;
using RiscV.Decode;
using RiscV.Memory;

namespace Tests.RiscV;

/// <summary>
/// Tests for Zbc (carry-less multiplication): clmul, clmulh, clmulr.
///
/// clmul(a,b)  = lower 32 bits of the GF(2)[x] product of a and b.
/// clmulh(a,b) = upper 32 bits (bits [63:32]).
/// clmulr(a,b) = bits [62:31] of the full 64-bit product.
/// </summary>
public class ZbcTests {

    // ── Encode helpers ────────────────────────────────────────────────────────

    private static uint RType(int funct7, int rs2, int rs1, int funct3, int rd) =>
        (uint)(((funct7 & 0x7F) << 25) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint EBreak() => 0x00100073u;

    private static uint Clmul (int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 1, rd);
    private static uint Clmulh(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 3, rd);
    private static uint Clmulr(int rd, int rs1, int rs2) => RType(0x05, rs2, rs1, 2, rd);

    // ── Reference implementation ──────────────────────────────────────────────

    private static ulong RefClmul64(uint a, uint b) {
        ulong r = 0;
        for (int i = 0; i < 32; i++)
            if (((b >> i) & 1u) != 0) r ^= (ulong)a << i;
        return r;
    }

    private static uint RefClmul (uint a, uint b) => (uint) RefClmul64(a, b);
    private static uint RefClmulh(uint a, uint b) => (uint)(RefClmul64(a, b) >> 32);
    private static uint RefClmulr(uint a, uint b) => (uint)(RefClmul64(a, b) >> 31);

    // ── Test runner ──────────────────────────────────────────────────────────

    private static uint RunInstr(uint x1Val, uint x2Val, uint instr) {
        const ulong codeBase = 0x1000u;
        const ulong outAddr  = 0x0100u;

        var mem = new FlatMemory(0x4000);
        mem.Load(0x200u, BitConverter.GetBytes(x1Val));
        mem.Load(0x204u, BitConverter.GetBytes(x2Val));

        uint lw1 = (uint)((0x200 << 20) | (0 << 15) | (2 << 12) | (1 << 7) | 0x03u);
        uint lw2 = (uint)((0x204 << 20) | (0 << 15) | (2 << 12) | (2 << 7) | 0x03u);
        uint sw3 = (uint)((((0x100 >> 5) & 0x7F) << 25) | (3 << 20) | (0 << 15)
                        | (2 << 12) | ((0x100 & 0x1F) << 7) | 0x23u);

        uint[] words = [lw1, lw2, instr, sw3, EBreak()];
        for (int i = 0; i < words.Length; i++)
            mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new SingleCycleTrain(new RvMechanism(), mem, entryPoint: codeBase);
        train.Run(maxTicks: 200);
        return (uint)mem.Read(outAddr, 4);
    }

    // ── Fixed-vector smoke tests for clmul ────────────────────────────────────
    // Hand-verified: clmul(a, 0)=0; clmul(a, 1)=a; clmul(2,2)=4 (bit1×bit1→bit2).
    // clmul(0b1010, 0b1100): bits 2 and 3 of 0b1100 are set →
    //   (0b1010 << 2) XOR (0b1010 << 3) = 0b101000 XOR 0b1010000 = 0b1111000 = 120.

    [Theory]
    [InlineData(0xFFFFFFFFu, 0u,  0u)]
    [InlineData(0xFFFFFFFFu, 1u,  0xFFFFFFFFu)]
    [InlineData(2u,          2u,  4u)]
    [InlineData(0b1010u,     0b1100u, 120u)]
    public void Clmul_FixedVectors(uint a, uint b, uint expected) =>
        Assert.Equal(expected, RunInstr(a, b, Clmul(3, 1, 2)));

    // ── Property tests across diverse inputs ──────────────────────────────────

    public static TheoryData<uint, uint> TestPairs => new() {
        { 0u,           0u },
        { 0xFFFFFFFFu,  0xFFFFFFFFu },
        { 0xAAAAAAAAu,  0x55555555u },
        { 0x12345678u,  0x9ABCDEF0u },
        { 0x80000000u,  0x80000000u },
        { 1u,           0xFFFFFFFFu },
        { 0xFFFFFFFFu,  1u },
        { 0xDEADBEEFu,  0xCAFEBABEu },
        { 0x0000FFFFu,  0xFFFF0000u },
    };

    [Theory]
    [MemberData(nameof(TestPairs))]
    public void Clmul_MatchesReference(uint a, uint b) =>
        Assert.Equal(RefClmul(a, b), RunInstr(a, b, Clmul(3, 1, 2)));

    [Theory]
    [MemberData(nameof(TestPairs))]
    public void Clmulh_MatchesReference(uint a, uint b) =>
        Assert.Equal(RefClmulh(a, b), RunInstr(a, b, Clmulh(3, 1, 2)));

    [Theory]
    [MemberData(nameof(TestPairs))]
    public void Clmulr_MatchesReference(uint a, uint b) =>
        Assert.Equal(RefClmulr(a, b), RunInstr(a, b, Clmulr(3, 1, 2)));

    // clmul is commutative in GF(2)[x].
    [Theory]
    [MemberData(nameof(TestPairs))]
    public void Clmul_IsCommutative(uint a, uint b) =>
        Assert.Equal(RunInstr(a, b, Clmul(3, 1, 2)), RunInstr(b, a, Clmul(3, 1, 2)));

    // All three operations are slices of the same 64-bit product.
    [Theory]
    [MemberData(nameof(TestPairs))]
    public void AllThree_ConsistentWithProduct(uint a, uint b) {
        ulong product = RefClmul64(a, b);
        Assert.Equal((uint)product,          RunInstr(a, b, Clmul (3, 1, 2)));
        Assert.Equal((uint)(product >> 32),  RunInstr(a, b, Clmulh(3, 1, 2)));
        Assert.Equal((uint)(product >> 31),  RunInstr(a, b, Clmulr(3, 1, 2)));
    }
}
