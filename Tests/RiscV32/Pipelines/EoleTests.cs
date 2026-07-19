using Mechanism;
using Mechanism.BranchPredictModels;
using Mechanism.ValuePredictModels;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for EOLE Late Execution (Perais &amp; Seznec, ISCA 2014) as
///     wired into <see cref="OooeTrain" /> via the <c>enableEoleLateExec</c> constructor flag.
///     <para>
///         A Late-Execution-eligible instruction never enters the IQ/Issue/Execute path at all:
///         its predicted value is already live in the PRF from Rename, and it is verified in-order
///         near Commit instead. Every correctness test here runs the same program twice (feature
///         off vs on) and asserts identical final architectural register state, regardless of
///         whether the underlying value prediction was correct, wrong, or never attempted.
///     </para>
/// </summary>
public class EoleTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        IValuePredictor? valuePredictor,
        bool enableEoleLateExec = false,
        IBranchPredictor? predictor = null,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 16,
        FuLatencyConfig? fuLatency = null,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            predictor: predictor,
            fuLatency: fuLatency,
            valuePredictor: valuePredictor,
            enableEoleLateExec: enableEoleLateExec
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

    private static void AssertIdenticalArchState(OooeTrain a, OooeTrain b) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(a.ArchState.IntegerRegisters.Read(r), b.ArchState.IntegerRegisters.Read(r));
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     The same register-copy chain used by <c>ValuePredictionTests</c>
    ///     (<c>ConstantCopyChain_...</c>): a dependence chain that always converges to 99, so once
    ///     VTAGE's FPC saturates, every <c>add</c> in the chain is Late-Execution-eligible.
    /// </summary>
    [Fact]
    public void LateExec_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(new VtagePredictor(), false);
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor(), true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "eole_late_exec"));
        Assert.True(Counter(onResult, "eole_late_exec") > 0, "no instruction was retired via Late Execution");
    }

    /// <summary>
    ///     The same mid-loop value-shift program used by <c>ValuePredictionTests</c>
    ///     (<c>MidLoopValueShift_...</c>): the fed-in constant changes from 99 to 55 five
    ///     iterations before the loop ends, forcing a misprediction after VTAGE has converged.
    ///     With Late Execution on, that misprediction is caught by the new commit-time verify
    ///     branch (the instruction never reached <c>StepComplete</c>), not the pre-existing one —
    ///     this is the regression test for that specific squash path.
    /// </summary>
    [Fact]
    public void LateExec_MispredictSquashes_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x25800093, // addi x1, x0, 600
            0x06300113, // addi x2, x0, 99
            0x00500493, // addi x9, x0, 5
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0x00908463, // beq x1, x9, do_redirect
            0x0080006F, // jal x0, cont
            0x03700113, // do_redirect: addi x2, x0, 55
            0xFE0094E3, // cont: bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(new VtagePredictor(), false);
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor(), true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "eole_late_exec"));
        Assert.True(Counter(onResult, "eole_late_exec") > 0, "no instruction was retired via Late Execution");
        Assert.True(Counter(onResult, "vp_mispredicts") > 0, "the mid-loop value shift never mispredicted");
    }

    /// <summary>
    ///     The headline EOLE claim (Perais &amp; Seznec §6.5, Fig. 13): a narrow-issue machine with
    ///     Late Execution enabled should approach the cycle count of a wider-issue machine without
    ///     it, because confidently-predicted independent ALU ops vacate the ALU issue ports
    ///     entirely instead of competing for them.
    ///     <para>
    ///         Four independent, VP-predictable <c>add</c>s per iteration (each reads only the
    ///         loop-invariant x2, so none of the four ever waits on another) are exactly the
    ///         width-bound workload where <see cref="FuLatencyConfig.IntAluCount" /> gates cycle
    ///         count: a throwaway probe confirmed this same program shape runs in 115/86/71 cycles
    ///         at IntAluCount 1/2/4 with EOLE off, i.e. port count — not any dependence chain — is
    ///         the bottleneck. That is deliberately the opposite workload from
    ///         <c>ValuePredictionTests.ConstantCopyChain_CompletesInFewerCyclesThanWithout</c>,
    ///         which is latency- not width-bound and would show no EOLE effect at all.
    ///     </para>
    ///     Assembled from:
    ///     <c>
    ///         addi x1,x0,600; addi x2,x0,99; loop: add x10,x2,x0; add x11,x2,x0; add x12,x2,x0;
    ///         add x13,x2,x0; addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void LateExec_NarrowIssueApproachesWideIssueBaseline() {
        uint[] program = [
            0x25800093, // addi x1, x0, 600
            0x06300113, // addi x2, x0, 99
            0x00010533, // loop: add x10, x2, x0
            0x000105B3, // add x11, x2, x0
            0x00010633, // add x12, x2, x0
            0x000106B3, // add x13, x2, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        var wideFu = new FuLatencyConfig(IntAluCount: 2);
        var narrowFu = new FuLatencyConfig(IntAluCount: 1);

        (OooeTrain reference, FlatMemory memRef) =
            Make(null, false, new LTagePredictor(), issueWidth: 4, fuLatency: FuLatencyConfig.Default);
        (OooeTrain baseline, FlatMemory memBaseline) =
            Make(new VtagePredictor(), false, new LTagePredictor(), issueWidth: 4, fuLatency: wideFu);
        (OooeTrain narrowEole, FlatMemory memNarrow) =
            Make(new VtagePredictor(), true, new LTagePredictor(), issueWidth: 4, fuLatency: narrowFu);

        Load(memRef, program);
        Load(memBaseline, program);
        Load(memNarrow, program);

        RevolutionResult baselineResult = baseline.Run();
        RevolutionResult narrowResult = narrowEole.Run();
        reference.Run();

        AssertIdenticalArchState(reference, baseline);
        AssertIdenticalArchState(reference, narrowEole);

        long baselineCycles = Counter(baselineResult, "cycles");
        long narrowCycles = Counter(narrowResult, "cycles");
        Assert.True(Counter(narrowResult, "eole_late_exec") > 0, "no instruction was retired via Late Execution");
        Assert.True(
            narrowCycles <= (long)(baselineCycles * 1.2),
            $"narrow+EOLE ({narrowCycles} cycles, IntAluCount=1) did not approach the wide no-EOLE " +
            $"baseline ({baselineCycles} cycles, IntAluCount=2)"
        );
    }
}
