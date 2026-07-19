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
        int memSize = 4096,
        bool enableEoleEarlyExec = false
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
            enableEoleLateExec: enableEoleLateExec,
            enableEoleEarlyExec: enableEoleEarlyExec
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

        (OooeTrain off, FlatMemory memOff) = Make(new VtagePredictor());
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

        (OooeTrain off, FlatMemory memOff) = Make(new VtagePredictor());
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

        var wideFu = new FuLatencyConfig();
        var narrowFu = new FuLatencyConfig(1);

        (OooeTrain reference, FlatMemory memRef) =
            Make(null, false, new LTagePredictor(), 4, fuLatency: FuLatencyConfig.Default);
        (OooeTrain baseline, FlatMemory memBaseline) =
            Make(new VtagePredictor(), false, new LTagePredictor(), 4, fuLatency: wideFu);
        (OooeTrain narrowEole, FlatMemory memNarrow) =
            Make(new VtagePredictor(), true, new LTagePredictor(), 4, fuLatency: narrowFu);

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

    /// <summary>
    ///     The same independent-ops program as <see cref="LateExec_NarrowIssueApproachesWideIssueBaseline" />,
    ///     but exercising Early Execution alone, with <b>no value predictor at all</b>
    ///     (<c>valuePredictor: null</c>) — all four <c>add</c>s read only the loop-invariant x2, so
    ///     they're eligible for Early Execution from the very first iteration, no VTAGE/FPC warmup
    ///     needed. This is the core evidence that Early Execution's benefit is provenance-agnostic:
    ///     it doesn't require prediction, just already-ready operands.
    /// </summary>
    [Fact]
    public void EarlyExec_ArchStateIdenticalToWithout() {
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

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(null, enableEoleEarlyExec: true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "eole_early_exec"));
        Assert.True(Counter(onResult, "eole_early_exec") > 0, "no instruction was computed via Early Execution");
    }

    /// <summary>
    ///     Regression test for the paper-sourced depth bound in <c>StepRename</c>'s
    ///     <c>_eeWrittenThisTick</c> exclusion (Perais &amp; Seznec §3.2: the paper tried letting
    ///     Early Execution results chain within one rename cycle — "more than a single [ALU]
    ///     stage" — found it "highly inefficient," and settled on a 1-deep design where only the
    ///     *previous* cycle's Early Execution results may feed a new one). The VP copy-chain
    ///     program's two <c>add</c>s form a genuine RAW dependency each iteration
    ///     (<c>x3&lt;-x2; x2&lt;-x3</c>) and are typically renamed in the same tick, so per the
    ///     paper's model neither may use the other's same-tick Early Execution result — the loop
    ///     should barely speed up at all. Uses <see cref="LTagePredictor" /> (not the default
    ///     <c>AlwaysNotTakenPredictor</c>) so per-iteration branch-misprediction squash noise
    ///     doesn't dominate the much smaller chain-latency effect this test isolates — the same
    ///     reasoning as <c>ValuePredictionTests.ConstantCopyChain_CompletesInFewerCyclesThanWithout</c>.
    ///     <para>
    ///         Measured directly (a throwaway probe, matching this exact program): 2012 cycles
    ///         without Early Execution vs. 2008 with it, over 500 iterations — a ~0.2% difference,
    ///         not a collapse. A regression that let Early Execution results chain within a tick
    ///         (defeating <c>_eeWrittenThisTick</c>) would let the whole chain resolve in a handful
    ///         of cycles instead of ~4/iteration, which the 90%-of-baseline floor below would catch.
    ///     </para>
    /// </summary>
    [Fact]
    public void EarlyExec_CopyChainDoesNotCollapseImplausibly() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, false, new LTagePredictor());
        (OooeTrain on, FlatMemory memOn) = Make(null, false, new LTagePredictor(), enableEoleEarlyExec: true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        long offCycles = Counter(offResult, "cycles");
        long onCycles = Counter(onResult, "cycles");
        Assert.True(Counter(onResult, "eole_early_exec") > 0, "no instruction was computed via Early Execution");
        Assert.True(
            onCycles >= offCycles * 9 / 10,
            $"Early Execution collapsed a genuine RAW dependency chain implausibly: {onCycles} cycles vs. " +
            $"{offCycles} without — expected near-parity (~0.2% in a reference measurement), not a same-tick cascade"
        );
    }

    /// <summary>
    ///     Mirrors <c>ValuePredictionTests.BranchMispredictHeavyLoop_...</c>: the alternating
    ///     branch triggers <c>StepPartialSquash</c> roughly every other iteration, exercising the
    ///     rename-queue/RAT rollback path alongside active Early Execution. Early Execution doesn't
    ///     change how physical registers are allocated (only what value lands in the PRF slot
    ///     early), so the existing rollback logic should cover it with no new code — this is the
    ///     empirical check for that claim, not just the structural argument.
    /// </summary>
    [Fact]
    public void EarlyExec_BranchMispredictHeavyLoop_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x25800093, // addi x1, x0, 600
            0x00700113, // addi x2, x0, 7
            0x00000513, // addi x10, x0, 0
            0x00000213, // addi x4, x0, 0
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0x00154513, // xori x10, x10, 1
            0xFFF08093, // addi x1, x1, -1
            0x00050463, // beq x10, x0, skip
            0x00120213, // addi x4, x4, 1
            0xFE0094E3, // skip: bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(null, enableEoleEarlyExec: true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "branch_misses") > 0, "the alternating branch never mispredicted");
    }

    /// <summary>
    ///     All three EOLE-family features on at once — <c>VtagePredictor</c>, Late Execution, and
    ///     Early Execution — on both the copy-chain (RAW-dependent) and independent-ops
    ///     (width-bound) programs. Both programs feed a constant value, so no value-misprediction
    ///     ever fires here; this test only confirms Early and Late Execution's mutual exclusivity
    ///     (an Early-Executed instruction never also carries <c>WasValuePredicted</c>, so it can
    ///     never also be Late-Execution-eligible) holds under combination. The transitive-squash
    ///     case — an Early-Executed instruction consuming an operand that a value prediction later
    ///     gets *wrong* — is exercised separately by
    ///     <see cref="EarlyExec_TransitiveSquashOnValueMispredict_ArchStateIdenticalToWithout" />.
    /// </summary>
    [Fact]
    public void EarlyExec_CombinedWithLateExecutionAndValuePrediction() {
        uint[] chainProgram = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];
        uint[] indepProgram = [
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

        foreach (uint[] program in new[] { chainProgram, indepProgram, }) {
            (OooeTrain off, FlatMemory memOff) = Make(null);
            (OooeTrain on, FlatMemory memOn) = Make(
                new VtagePredictor(), true, enableEoleEarlyExec: true
            );
            Load(memOff, program);
            Load(memOn, program);

            off.Run();
            on.Run();

            AssertIdenticalArchState(off, on);
        }
    }

    /// <summary>
    ///     The single subtlest correctness property of Early Execution, exercised directly rather
    ///     than only argued: an Early-Executed instruction never needs its own commit-time
    ///     verification because the only way one of its operands can be wrong is if it came from a
    ///     value prediction, and in-order commit guarantees that prediction's own
    ///     <c>SetFlush</c> (should it mispredict) flushes the Early-Executed consumer before the
    ///     consumer ever reaches the ROB head. The mid-loop value-shift program forces exactly
    ///     that: the fed-in constant changes from 99 to 55 five iterations before the end, so once
    ///     VTAGE/FPC has converged on 99, the first post-shift value prediction is wrong — and
    ///     whichever <c>add</c> already consumed it via Early Execution that same/next tick must be
    ///     transitively squashed along with it. All three EOLE-family features are on at once so
    ///     the interaction is real, not simulated by only enabling one.
    /// </summary>
    [Fact]
    public void EarlyExec_TransitiveSquashOnValueMispredict_ArchStateIdenticalToWithout() {
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

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor(), true, enableEoleEarlyExec: true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "eole_early_exec"));
        Assert.True(Counter(onResult, "eole_early_exec") > 0, "no instruction was computed via Early Execution");
        Assert.True(Counter(onResult, "vp_mispredicts") > 0, "the mid-loop value shift never mispredicted");
    }

    /// <summary>
    ///     Same shape as <see cref="LateExec_NarrowIssueApproachesWideIssueBaseline" />, but for
    ///     Early Execution alone (no value predictor). Measured directly (a throwaway probe,
    ///     matching this exact program): a wide (<c>IntAluCount=2</c>), no-EOLE baseline runs in
    ///     1231 cycles; a narrow (<c>IntAluCount=1</c>) machine with Early Execution runs in 617 —
    ///     <em>better</em> than the wide baseline, not just "close to" it, since none of these four
    ///     independent adds need any cross-instruction forwarding at all (they all read only the
    ///     loop-invariant x2), so every one of them is Early-Execution-eligible and none ever
    ///     touches an ALU issue port.
    /// </summary>
    [Fact]
    public void EarlyExec_NarrowIssueApproachesWideIssueBaseline() {
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

        var wideFu = new FuLatencyConfig();
        var narrowFu = new FuLatencyConfig(1);

        (OooeTrain reference, FlatMemory memRef) =
            Make(null, false, new LTagePredictor(), 4, fuLatency: FuLatencyConfig.Default);
        (OooeTrain baseline, FlatMemory memBaseline) =
            Make(null, false, new LTagePredictor(), 4, fuLatency: wideFu);
        (OooeTrain narrowEe, FlatMemory memNarrow) =
            Make(null, false, new LTagePredictor(), 4, fuLatency: narrowFu, enableEoleEarlyExec: true);

        Load(memRef, program);
        Load(memBaseline, program);
        Load(memNarrow, program);

        RevolutionResult baselineResult = baseline.Run();
        RevolutionResult narrowResult = narrowEe.Run();
        reference.Run();

        AssertIdenticalArchState(reference, baseline);
        AssertIdenticalArchState(reference, narrowEe);

        long baselineCycles = Counter(baselineResult, "cycles");
        long narrowCycles = Counter(narrowResult, "cycles");
        Assert.True(Counter(narrowResult, "eole_early_exec") > 0, "no instruction was computed via Early Execution");
        Assert.True(
            narrowCycles <= baselineCycles,
            $"narrow+Early-Execution ({narrowCycles} cycles, IntAluCount=1) did not beat the wide no-EOLE " +
            $"baseline ({baselineCycles} cycles, IntAluCount=2)"
        );
    }
}