#region

using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for Vector Runahead (Naithani, Ainsworth, Jones &amp;
///     Eeckhout, ISCA 2021) — the vectorized extension of scalar runahead execution (see
///     <see cref="RunaheadTests" />) implemented in <see cref="OooeTrain" /> via
///     <c>enableVectorRunahead</c>/<c>runaheadVectorWidth</c>.
///     <para>
///         As with scalar runahead, the shadow lane never commits anything to real architectural
///         state or the real VRF — vectorization is modeled entirely as N-wide replication of the
///         existing scalar shadow body against scratch, physical-register-indexed lane state.
///         Every test here therefore runs the same program twice (feature off vs on) and asserts
///         identical final architectural register state.
///     </para>
/// </summary>
public class VectorRunaheadTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        bool enableRunahead,
        bool enableVectorRunahead = false,
        int runaheadVectorWidth = 8,
        int runaheadBudget = 200,
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
            // Default not-taken predictor mispredicts the backward loop branch, so wrong-path
            // fetch runs ahead of the ROB-full stall and a runahead episode inherits a
            // wrong-path _fetchPc pointed past the loop body entirely.
            predictor: new AlwaysTakenPredictor(),
            dMemConfig: new MemoryConfig(1024),
            enableRunahead: enableRunahead,
            runaheadBudget: runaheadBudget,
            enableVectorRunahead: enableVectorRunahead,
            runaheadVectorWidth: runaheadVectorWidth
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

    /// <summary>
    ///     A 20-iteration stride-4 array walk over a 1024-byte/4-way/32-byte-line L1 (8 words per
    ///     line): the first line (words 0-7) takes one compulsory miss (iteration 1) and then
    ///     hits; the second line (words 8-15) takes a second compulsory miss (iteration 9). The
    ///     first miss triggers a runahead episode before the load's own PC has any stride-table
    ///     history (confidence 0) — no vectorization is possible yet. By the time the second miss
    ///     triggers a second episode, iterations 2-8 have trained that PC's stride-table entry to
    ///     saturated confidence (five consecutive same-stride hits) — the shadow lane continuing
    ///     the loop past the second stall must recognize the chain and vectorize. Each loop
    ///     iteration also chases a dependent indirect load through a second array (
    ///     <c>add x5,x4,x6; lw x7,0(x5)</c>), exercising propagation through dependent arithmetic
    ///     and an indirect load, not just the chain-origin load itself. Assembled from:
    ///     <c>
    ///         addi x1,x0,800; addi x2,x0,20; addi x6,x0,2000; loop: lw
    ///         x4,0(x1); add x5,x4,x6; lw x7,0(x5); addi x1,x1,4; addi x2,x2,-1; bnez x2,loop;
    ///         ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void StridedDependentChain_VectorizesAfterTraining_ArchStateIdenticalToWithout() {
        uint[] program = [
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

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true, true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "runahead_episodes") > 0, "no runahead episode was ever entered");
        Assert.True(
            Counter(onResult, "runahead_vector_chains") > 0,
            "the trained stride-table entry should have vectorized at least one dependent-load chain"
        );

        // The chain-origin load, its dependent add, and its dependent indirect load are all
        // vectorizable — a single vectorized loop pass should touch well more lane-work than one
        // load's worth of N-wide replication alone.
        Assert.True(
            Counter(onResult, "runahead_vector_lane_accesses") >= 2 * 8L,
            "expected propagation through the dependent add/indirect-load pair, not just the chain origin"
        );
    }

    /// <summary>
    ///     Same ROB-filling shape as
    ///     <see cref="RunaheadTests.FullRobStallBehindMissingLoad_ArchStateIdenticalToWithout_AndEpisodeOccurs" />
    ///     — a single cold-miss load with no repeated PC history — but with
    ///     <c>enableVectorRunahead: true</c>. The stride table is cold for every PC the shadow
    ///     lane ever reaches, so no vectorization should fire; this guards against a false-positive
    ///     vectorization decision on an untrained/default-initialized table entry.
    /// </summary>
    [Fact]
    public void UntrainedStrideTable_NeverVectorizes() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00100193, // addi x3, x0, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00600413, // addi x8, x0, 6
            0x00700493, // addi x9, x0, 7
            0x00800513, // addi x10, x0, 8
            0x00900593, // addi x11, x0, 9
            0x00A00613, // addi x12, x0, 10
            0x00B00693, // addi x13, x0, 11
            0x00C00713, // addi x14, x0, 12
            0x00110793, // addi x15, x2, 1
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true, true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.True(Counter(onResult, "runahead_episodes") > 0, "no runahead episode was ever entered");
        Assert.Equal(0L, Counter(onResult, "runahead_vector_chains"));
        Assert.Equal(0L, Counter(onResult, "runahead_vector_lane_accesses"));
    }

    /// <summary>
    ///     The termination-condition change (Naithani et al., ISCA 2021, innovation #1): once a
    ///     dependent-load chain is actively vectorizing, the shadow lane must keep running past
    ///     the point a scalar-only shadow lane would have exited (the real blocking load's
    ///     resolution), so a vector-enabled run of the same strided-chain program accumulates
    ///     strictly more total shadow-lane work (<c>runahead_instructions</c>, charged in
    ///     scalar-equivalent units — N per vectorized step) than a scalar-only run of the same
    ///     program and budget.
    /// </summary>
    [Fact]
    public void ChainActiveTermination_AccumulatesMoreShadowWorkThanScalarOnly() {
        uint[] program = [
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

        (OooeTrain scalar, FlatMemory memScalar) = Make(true);
        (OooeTrain vector, FlatMemory memVector) = Make(true, true);
        Load(memScalar, program);
        Load(memVector, program);

        RevolutionResult scalarResult = scalar.Run();
        RevolutionResult vectorResult = vector.Run();

        AssertIdenticalArchState(scalar, vector);
        Assert.True(
            Counter(vectorResult, "runahead_instructions") > Counter(scalarResult, "runahead_instructions"),
            "vectorized chain-active runahead should out-work scalar-only runahead on the same program"
        );
    }
}