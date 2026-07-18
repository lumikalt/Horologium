using Orrery.Cache;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     CPI stacks via interval analysis (Eyerman et al., ASPLOS 2006) on OooeTrain.
///     <para>
///         Each test runs a hand-assembled RV32I program engineered to expose one miss-event
///         component and asserts that component dominates the stack, plus the structural
///         invariant that the stack is additive (base ≥ 0, base + components == total CPI).
///         Dominance assertions rather than exact cycle counts keep the tests robust to
///         microarchitectural tuning.
///     </para>
/// </summary>
public class CpiStackTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        int issueWidth = 2,
        int robCapacity = 32,
        MemoryConfig? iMemConfig = null,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuLatency = null
    ) {
        var mem = new FlatMemory(65536);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iMemConfig: iMemConfig,
            dMemConfig: dMemConfig,
            fuLatency: fuLatency
        );
        return (train, mem);
    }

    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static CpiStack RunAndAnalyze(OooeTrain train) {
        train.Run();
        CpiStack? stack = CpiStack.FromSnapshot(train.SnapshotPipeline());
        Assert.NotNull(stack);
        // Structural invariant: the stack is additive and never overflows the total.
        Assert.True(stack.Base >= 0.0);
        Assert.Equal(stack.Total, stack.Base + stack.MissComponents, 6);
        return stack;
    }

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private const uint Ebreak = 0x00100073;

    [Fact]
    public void Base_DominatesOnIndependentAluCode() {
        // 256 independent ALU ops, no caches, no mispredicted branches: nearly every cycle
        // is steady-state streaming, so the stack should be almost entirely base.
        (OooeTrain train, FlatMemory mem) = Make();
        uint[] program = new uint[257];
        for (var i = 0; i < 256; i++) program[i] = Addi(rd: 1 + i % 8, rs1: 0, imm: i % 512);
        program[256] = Ebreak;
        Load(mem, program);

        CpiStack stack = RunAndAnalyze(train);
        Assert.True(stack.Base > 0.5 * stack.Total, $"base {stack.Base:F3} should dominate CPI {stack.Total:F3}");
        Assert.Equal(0.0, stack.L1ICache);
        Assert.Equal(0.0, stack.L2DCache);
        Assert.Equal(0.0, stack.BranchMisprediction);
    }

    [Fact]
    public void BranchMisprediction_DominatesOnMispredictedLoop() {
        // Countdown loop, backward branch taken 199 times against the default
        // always-not-taken predictor: every iteration pays a resolution + refill penalty.
        (OooeTrain train, FlatMemory mem) = Make();
        Load(
            mem,
            Addi(rd: 1, rs1: 0, imm: 200), // addi x1, x0, 200
            Addi(rd: 1, rs1: 1, imm: -1),  // loop: addi x1, x1, -1
            0xFE009EE3,                    // bne x1, x0, -4
            Ebreak
        );

        CpiStack stack = RunAndAnalyze(train);
        Assert.True(
            stack.BranchMisprediction > stack.Base,
            $"bpred {stack.BranchMisprediction:F3} should exceed base {stack.Base:F3}"
        );
        Assert.True(stack.BranchMisprediction > 0.3 * stack.Total);
        Assert.Equal(0.0, stack.L1ICache); // no I-cache configured
    }

    [Fact]
    public void L1ICache_DominatesOnColdStraightLineCode() {
        // 512 straight-line instructions through a 256-byte I-cache with a 20-cycle miss
        // penalty: every 8th fetch misses and freezes the frontend. All fetches are
        // correct-path, so the pending penalties must be posted (sFMT bit) — to the L1I
        // component, since the single-level I-side has no deeper victims.
        (OooeTrain train, FlatMemory mem) = Make(
            iMemConfig: new MemoryConfig(CacheCapacityBytes: 256, CacheBlockBytes: 32, CacheMissLatency: 20)
        );
        uint[] program = new uint[513];
        for (var i = 0; i < 512; i++) program[i] = Addi(rd: 1 + i % 8, rs1: 0, imm: i % 512);
        program[512] = Ebreak;
        Load(mem, program);

        CpiStack stack = RunAndAnalyze(train);
        Assert.True(
            stack.L1ICache > 0.3 * stack.Total,
            $"L1I {stack.L1ICache:F3} should be significant vs CPI {stack.Total:F3}"
        );
        Assert.True(stack.L1ICache > stack.Base);
        Assert.Equal(0.0, stack.L2ICache);
        Assert.Equal(0.0, stack.BranchMisprediction);
    }

    [Fact]
    public void L2DCache_DominatesOnDependentLoadMissChain() {
        // Pointer chase across 64 lines missing both D-cache levels (miss-to-memory):
        // the chain is longer than the 32-entry ROB, so the full ROB blocks on a head
        // load classified as a long L2 miss — the paper's canonical long backend miss.
        (OooeTrain train, FlatMemory mem) = Make(
            dMemConfig: new MemoryConfig(
                CacheCapacityBytes: 512, CacheBlockBytes: 32, CacheMissLatency: 10,
                L2CapacityBytes: 1024, L2BlockBytes: 32, L2MissLatency: 50
            )
        );

        for (var i = 0; i < 64; i++) {
            uint address = 0x1000u + 64u * (uint)i;
            uint next = address + 64;
            mem.Load(address, [(byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)(next >> 24),]);
        }

        uint[] program = new uint[67];
        program[0] = 0x00001097; // auipc x1, 0x1 → x1 = 0x1000
        program[1] = Addi(rd: 1, rs1: 1, imm: 0);
        for (var i = 0; i < 64; i++) program[2 + i] = 0x0000A083; // lw x1, 0(x1)
        program[66] = Ebreak;
        Load(mem, program);

        CpiStack stack = RunAndAnalyze(train);
        Assert.True(
            stack.L2DCache > 0.3 * stack.Total,
            $"L2D {stack.L2DCache:F3} should be significant vs CPI {stack.Total:F3}"
        );
        Assert.True(stack.L2DCache > stack.L1DCache);
        Assert.True(stack.L2DCache > stack.ResourceStall);
    }

    [Fact]
    public void ResourceStall_DominatesOnDependentDivChain() {
        // 40 serialized 20-cycle divides, more than the 32-entry ROB holds: the full ROB
        // blocks on an incomplete non-load head — the paper's long-latency unit stall
        // (which is also where pure dependence serialization lands).
        (OooeTrain train, FlatMemory mem) = Make(fuLatency: new FuLatencyConfig(DivLatency: 20));
        uint[] program = new uint[43];
        program[0] = Addi(rd: 1, rs1: 0, imm: 1000);
        program[1] = Addi(rd: 2, rs1: 0, imm: 3);
        for (var i = 0; i < 40; i++) program[2 + i] = 0x0220C0B3; // div x1, x1, x2
        program[42] = Ebreak;
        Load(mem, program);

        CpiStack stack = RunAndAnalyze(train);
        Assert.True(
            stack.ResourceStall > 0.3 * stack.Total,
            $"resource {stack.ResourceStall:F3} should be significant vs CPI {stack.Total:F3}"
        );
        Assert.True(stack.ResourceStall > stack.Base);
        Assert.Equal(0.0, stack.L1DCache);
        Assert.Equal(0.0, stack.L2DCache);
    }
}
