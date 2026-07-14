using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for Vector Runahead unrolling and register reclamation
///     (Naithani, Ainsworth, Jones &amp; Eeckhout, ISCA 2021, §III-G) — the v2 extension of
///     <see cref="VectorRunaheadTests" /> that issues up to <c>runaheadUnrollLength</c> total
///     N-wide rounds from the same chain origin instead of stopping after one, using immediate
///     free-on-rename register reclamation (see <c>FreeShadowRename</c> in <see cref="OooeTrain" />)
///     as a substitute for the paper's VRAT + register-deallocation queue (not needed here since
///     the shadow lane issues strictly in program order — see the design note on that method).
/// </summary>
public class VectorRunaheadUnrollTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        bool enableRunahead,
        bool enableVectorRunahead = false,
        int runaheadVectorWidth = 8,
        int runaheadUnrollLength = 8,
        int runaheadBudget = 400,
        int extraPhysRegs = 32,
        int issueWidth = 2,
        int robCapacity = 8,
        int iqCapacity = 32,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            extraPhysRegs: extraPhysRegs,
            predictor: new AlwaysTakenPredictor(),
            dMemConfig: new MemoryConfig(1024),
            enableRunahead: enableRunahead,
            runaheadBudget: runaheadBudget,
            enableVectorRunahead: enableVectorRunahead,
            runaheadVectorWidth: runaheadVectorWidth,
            runaheadUnrollLength: runaheadUnrollLength
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

    private static void AssertIdenticalArchState(OooeTrain off, OooeTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    // Same strided-dependent-chain program as VectorRunaheadTests.StridedDependentChain...: a
    // 20-iteration stride-4 walk over a 1024-byte/4-way/32-byte-line L1, with a compulsory miss
    // at iteration 1 (before any stride-table history) and a second at iteration 9 (by which
    // point the PC is trained to saturated confidence) — the second miss's episode is long
    // enough, relative to the 10-cycle default L1 miss latency and issueWidth=2, for the shadow
    // lane to loop back to the chain-origin load several times before the real load resolves,
    // giving unrolling room to actually issue multiple rounds.
    private static uint[] StridedChainProgram() => [
        0x32000093, // addi x1, x0, 800
        0x01400113, // addi x2, x0, 20
        0x7D000313, // addi x6, x0, 2000
        0x0000A203, // lw   x4, 0(x1)
        0x006202B3, // add  x5, x4, x6
        0x0002A383, // lw   x7, 0(x5)
        0x00408093, // addi x1, x1, 4
        0xFFF10113, // addi x2, x2, -1
        0xFE0116E3, // bnez x2, loop
        0x00100073, // ebreak
    ];

    /// <summary>
    ///     A small unroll cap (1 round — no unrolling beyond the first) must produce strictly less
    ///     total vector lane work than a larger cap (8 rounds, the paper's default) on the same
    ///     program and stall shape, proving <c>runaheadUnrollLength</c> actually bounds/extends how
    ///     many rounds a chain issues rather than being inert.
    /// </summary>
    [Fact]
    public void LargerUnrollCap_ProducesMoreVectorLaneWorkThanSmallCap() {
        uint[] program = StridedChainProgram();

        (OooeTrain capped, FlatMemory memCapped) = Make(true, true, 8, 1);
        (OooeTrain uncapped, FlatMemory memUncapped) = Make(true, true, 8, 8);
        Load(memCapped, program);
        Load(memUncapped, program);

        RevolutionResult cappedResult = capped.Run();
        RevolutionResult uncappedResult = uncapped.Run();

        (OooeTrain off, FlatMemory memOff) = Make(false);
        Load(memOff, program);
        off.Run();

        AssertIdenticalArchState(off, capped);
        AssertIdenticalArchState(off, uncapped);

        Assert.True(
            Counter(uncappedResult, "runahead_vector_lane_accesses")
            > Counter(cappedResult, "runahead_vector_lane_accesses"),
            $"expected cap=8 ({Counter(uncappedResult, "runahead_vector_lane_accesses")}) to out-work "
            + $"cap=1 ({Counter(cappedResult, "runahead_vector_lane_accesses")})"
        );
    }

    /// <summary>
    ///     Once a chain's unroll budget is spent, the origin load must not silently restart a fresh
    ///     chain (round 0 again) the next time the shadow lane loops back to it — that would defeat
    ///     the cap entirely (the exact bug the capped-origin guard in <c>TerminateOrUnroll</c>
    ///     fixes). The program has exactly two loads whose PCs can independently saturate the
    ///     stride table and become their own chain origin (the base strided load and its dependent
    ///     indirect load), so with a 2-round cap, total vectorized-round events across the whole run
    ///     are bounded by roughly 2 origins &#215; 2 rounds — not unbounded despite the loop revisiting
    ///     each origin PC many more times than that over its 20 iterations.
    /// </summary>
    [Fact]
    public void RoundCap_StaysBoundedAcrossRepeatedOriginRevisits() {
        uint[] program = StridedChainProgram();
        (OooeTrain on, FlatMemory memOn) = Make(true, true, 8, 2, runaheadBudget: 400);
        Load(memOn, program);

        RevolutionResult onResult = on.Run();

        long chains = Counter(onResult, "runahead_vector_chains");
        Assert.True(chains > 0, "expected at least one vectorized round");
        Assert.True(chains <= 4, $"expected the 2-round cap (x2 possible origins) to bound total chain events, got {chains}");
    }

    /// <summary>
    ///     Immediate free-on-rename reclamation (<c>FreeShadowRename</c>) is what lets deep
    ///     unrolling proceed without exhausting the RAT free list: the loop body touches ~5
    ///     architectural registers, so an un-reclaimed episode issuing many rounds would need a
    ///     fresh physical register per round per touched register (tens of registers for just a
    ///     few rounds), while reclamation keeps the concurrent working set roughly constant — only
    ///     the handful of registers live at any one instant, not one per round ever issued. A small
    ///     ROB (<c>robCapacity: 4</c>, reducing how many physical registers real in-flight execution
    ///     itself needs) plus a modest <c>extraPhysRegs</c> (16 — enough for a handful of rounds'
    ///     concurrent working set, nowhere near enough for <c>runaheadUnrollLength</c> (8) rounds'
    ///     worth of distinct registers without reclamation) must still let multiple rounds complete
    ///     and must still produce architectural state identical to the feature-off run.
    /// </summary>
    [Fact]
    public void RegisterReclamation_AllowsMultiRoundUnrollingUnderTinyPhysRegBudget() {
        uint[] program = StridedChainProgram();

        (OooeTrain off, FlatMemory memOff) = Make(false, robCapacity: 4);
        (OooeTrain on, FlatMemory memOn) = Make(true, true, 8, 8, extraPhysRegs: 16, robCapacity: 4);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.True(
            Counter(onResult, "runahead_vector_chains") >= 4,
            $"expected multiple rounds under a tiny extraPhysRegs budget, got "
            + $"{Counter(onResult, "runahead_vector_chains")} — register reclamation should prevent "
            + "phys-reg exhaustion from cutting the episode short after just one round"
        );
    }
}
