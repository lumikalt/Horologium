#region

using Mechanism.BranchPred;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     µop cache (TODO.md's µops-section item; Solomon, Mendelson, Orenstien, Almog &amp; Ronen,
///     "Micro-Operation Cache," ISLPED 2001) wired into <see cref="SuperscalarTrain" />.
///     <para>
///         This is a power paper, not a performance one — its own experiments never measure
///         cycles or IPC, and RISC-V's fixed-length decoder never hits the variable-length
///         decode-bandwidth wall the paper solves for x86. These tests verify the paper's own
///         metrics (a hot loop's body gets built once, then served from the cache on every later
///         iteration) and correctness (identical final architectural state and identical
///         <c>retired</c> count with the cache on vs off) — deliberately <b>not</b> a cycle-count
///         claim; see <see cref="UopCache" />'s doc comment for why none is expected here.
///     </para>
/// </summary>
public class SuperscalarUopCacheTests {
    private const uint Ebreak = 0x00100073;

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

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    // x1 = iterations; loop body (pc=4: decrement, pc=8: branch back while x1 != 0) is a single
    // 2-uop basic block, revisited `iterations` times — the first pass misses and builds it, every
    // later pass should hit.
    private static uint[] BackwardLoopProgram(int iterations) => [
        Addi(1, 0, iterations),
        Addi(1, 1, -1),                  // pc=4: loop body start
        Bne(1, 0, -4),                   // pc=8: back edge
        SuperscalarUopCacheTests.Ebreak, // pc=12
    ];

    // AlwaysBackwardNotForwards, not the trains' default AlwaysNotTakenPredictor: RV32's direct
    // branches carry a statically known target, so this predicts the loop's back-edge correctly
    // (taken) from the very first iteration — no misprediction/flush noise from a cold BTB
    // muddying the hit/miss trace this test wants to make legible.
    private static SuperscalarTrain Run(uint[] program, bool enableUopCache) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new SuperscalarTrain(
            new Rv32Mechanism(), mem, issueWidth: 2,
            predictor: new AlwaysBackwardNotForwards(),
            uopCacheSets: enableUopCache ? 4 : 0
        );
        train.Run();
        return train;
    }

    [Fact]
    public void HotLoop_ReusesTheCachedLoopBody() {
        SuperscalarTrain train = Run(BackwardLoopProgram(5), true);
        DialBoardSnapshot snap = train.SnapshotPipeline();

        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(1));
        // Exact counts depend on speculative fetch-ahead details (queueCapacity lets fetch run
        // ahead of issue, so the loop body's line may be hit more than once per real iteration
        // before a misprediction is caught) — the illustrative bar is "the cache recognizes more
        // than one block and reuses at least one of them", not a precise trace.
        Assert.True(snap.Counters["uop_cache_builds"] >= 2, "expected at least 2 distinct blocks built");
        Assert.True(snap.Counters["uop_cache_hits"] > 0, "expected the loop body to be served from the cache");
    }

    [Fact]
    public void DisabledByDefault_NoUopCacheCountersRegistered() {
        SuperscalarTrain train = Run(BackwardLoopProgram(5), false);
        DialBoardSnapshot snap = train.SnapshotPipeline();
        Assert.False(snap.Counters.ContainsKey("uop_cache_hits"));
        Assert.False(snap.Counters.ContainsKey("uop_cache_builds"));
    }

    [Fact]
    public void HotLoop_PreservesArchStateAndRetiredCount() {
        SuperscalarTrain withCache = Run(BackwardLoopProgram(5), true);
        SuperscalarTrain withoutCache = Run(BackwardLoopProgram(5), false);

        Assert.Equal(
            withoutCache.ArchState.IntegerRegisters.Read(1), withCache.ArchState.IntegerRegisters.Read(1)
        );
        Assert.Equal(
            withoutCache.SnapshotPipeline().Counters["retired"], withCache.SnapshotPipeline().Counters["retired"]
        );
    }
}