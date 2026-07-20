#region

using Mechanism;
using Mechanism.BranchPred;
using Mechanism.ValuePred;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for value prediction (Lipasti &amp; Shen, MICRO 1996, LVPT;
///     Perais &amp; Seznec, HPCA 2014, VTAGE + FPC) as wired into <see cref="OooeTrain" /> via the
///     <c>valuePredictor</c> constructor parameter.
///     <para>
///         A value-predicted instruction still executes for real through the ordinary pipeline in
///         the background; the early prediction only supplies a speculative early result. Every
///         test here runs the same program twice (feature off vs on) and asserts an identical final
///         architectural register state, regardless of whether the predictions made during the
///         run were correct, wrong, or never attempted.
///     </para>
/// </summary>
public class ValuePredictionTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        IValuePredictor? valuePredictor,
        IBranchPredictor? predictor = null,
        bool enableSmbBypass = false,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 16,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            predictor: predictor,
            enableSmbBypass: enableSmbBypass,
            valuePredictor: valuePredictor
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
        // 0-31 are the integer registers; 32-63 are the FP registers, renamed through the same
        // RAT/PRF at index rd+32 (see Rv32Decoder.Fp.cs) — comparing the full range covers both.
        for (var r = 0; r < 64; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     A register-copy chain (<c>add x3,x2,x0; add x2,x3,x0</c>) that always converges to the
    ///     same value (99) every iteration despite being a genuine RAW dependency through
    ///     renaming — the classic value-prediction win case (Lipasti &amp; Shen §1; Perais &amp;
    ///     Seznec §3.2, "tight loops"). Assembled from:
    ///     <c>
    ///         addi x1,x0,500; addi x2,x0,99; loop: add x3,x2,x0; add x2,x3,x0;
    ///         addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void ConstantCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     Same copy-chain as above, but the fed-in constant shifts from 99 to 55 five iterations
    ///     before the loop ends (via a rare branch positioned after the chain, so it never delays
    ///     the chain's own completion). 600 pre-shift iterations give VTAGE's probabilistic FPC
    ///     (state transitions gated at 1/16-1/32 probability) ample deterministic (seeded) margin
    ///     to converge confidently on 99, so the first post-shift iteration mispredicts —
    ///     exercising verify-at-Complete, squash-at-Commit, and re-execution recovery. Assembled
    ///     from:
    ///     <c>
    ///         addi x1,x0,600; addi x2,x0,99; addi x9,x0,5; loop: add x3,x2,x0; add x2,x3,x0;
    ///         addi x1,x1,-1; beq x1,x9,do_redirect; jal x0,cont; do_redirect: addi x2,x0,55;
    ///         cont: bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void MidLoopValueShift_ArchStateIdenticalToWithout_AndMispredictOccurs() {
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_mispredicts"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_mispredicts") > 0, "the mid-loop value shift never mispredicted");
    }

    /// <summary>
    ///     The VP-eligible copy chain runs alongside a branch that alternates taken/not-taken
    ///     every iteration — unpredictable for the default <c>AlwaysNotTakenPredictor</c>, so
    ///     roughly half the iterations trigger <c>OooeTrain.StepPartialSquash</c> (an
    ///     execute-time branch-misprediction recovery, distinct from value prediction's own
    ///     commit-time squash). This is the regression test for VTAGE's speculative
    ///     history surviving ordinary branch mispredictions: if its checkpoint/restore were
    ///     wrong, architectural state would still be correct (the producing instruction always
    ///     executes for real), but this is exactly the scenario where a silent history-corruption
    ///     bug would otherwise go undetected. The alternating toggle bit keeps the value
    ///     predictor's branch history oscillating rather than settling immediately, so 600
    ///     iterations give ample deterministic (seeded) margin for the FPC to still converge.
    ///     Assembled from:
    ///     <c>
    ///         addi x1,x0,600; addi x2,x0,7; addi x10,x0,0; addi x4,x0,0; loop: add x3,x2,x0;
    ///         add x2,x3,x0; xori x10,x10,1; addi x1,x1,-1; beq x10,x0,skip; addi x4,x4,1;
    ///         skip: bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void BranchMispredictHeavyLoop_ArchStateIdenticalToWithout_NoCrash() {
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp());
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "branch_misses") > 0, "the alternating branch never mispredicted");
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
    }

    /// <summary>
    ///     Regression test for a VTAGE tag-aliasing livelock found during eligibility-widening
    ///     work: <c>TryPredict</c> indexed by the speculative global history (<c>_history.Value</c>,
    ///     read live at Rename) while <c>Update</c> indexed by the committed shadow
    ///     (<c>_history.Committed</c>, read at Commit) — two different snapshots for the same
    ///     dynamic instruction whenever a full flush's refetching let extra speculative folds occur
    ///     between an instruction's own fetch and its (much later, or never-reached) commit. Under
    ///     the default <c>AlwaysNotTakenPredictor</c> (which mispredicts this loop's backward branch
    ///     every single iteration, triggering constant full flushes) the two histories could
    ///     permanently drift apart by exactly the one bit separating two <em>different</em>
    ///     instructions' tags, aliasing <c>addi x1,x1,-1</c>'s prediction onto an unrelated,
    ///     already-confident neighboring instruction's slot. A slot <c>Update</c> (indexing by the
    ///     other history) could never reach to correct, so the machine spun forever mispredicting
    ///     the same wrong value. Fixed by capturing a per-instruction <c>ValueHistoryCheckpoint</c>
    ///     at Fetch (already the mechanism branches use for their own recovery) and threading it
    ///     through Rename/Commit so a single dynamic instruction's own predict and train calls
    ///     always agree on which history to index — eliminating the drift instead of requiring a
    ///     competent branch predictor to avoid ever reaching it. Reproduced with plain ALU
    ///     instructions (no MulDiv/FP involved) at this exact register/PC layout — asserting
    ///     against <c>AlwaysNotTakenPredictor</c> here (unlike every other test in this file, which
    ///     correctly avoids it per the documented measurement pitfall) is deliberate: it's the
    ///     trigger, not noise, for this specific regression. Assembled from:
    ///     <c>
    ///         addi x1,x0,500; addi x2,x0,99; addi x6,x0,1; loop: add x3,x2,x0; add x2,x3,x0;
    ///         addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void VtageTagAliasing_DoesNotLivelockUnderAdversarialBranchPredictor() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x00100313, // addi x6, x0, 1 (dead register, preserves the PC layout that aliases)
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        long offCycles = Counter(offResult, "cycles");
        long onCycles = Counter(onResult, "cycles");
        Assert.True(
            onCycles < offCycles * 2,
            $"VTAGE livelocked instead of converging: {onCycles} cycles vs. {offCycles} without " +
            "(a converging run should be in the same ballpark, not pinned at the tick cap)"
        );

        // The cycle bound alone can't distinguish "the fix converges VTAGE" from "VTAGE never
        // predicts here at all" (no predictions -> no squashes -> onCycles ~= offCycles, which
        // also passes). Assert the predictor is actually active and, post-fix, actually correct.
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     A load can be sped up by two independent predictors at once: <c>SmbPredictor</c>
    ///     (NoSQ) supplies an early value from a live in-flight store at dispatch, while the value
    ///     predictor separately supplies one at rename. Both compare their own bookkeeping against
    ///     the same PRF slot at Complete, and either one's mismatch triggers an identical
    ///     <c>SetFlush(head.Pc)</c> recovery — so regardless of which fires, or whether one
    ///     "steals credit" for the other's correct value by overwriting the PRF first,
    ///     architectural correctness cannot depend on which predictor happened to run last. A
    ///     fixed-distance store/reload loop, like <c>SmbBypassTests</c>'s, but storing (and
    ///     therefore loading) the same constant every iteration so the value — not just the
    ///     distance — is also predictable. Assembled from:
    ///     <c>
    ///         addi x1,x0,100; addi x2,x0,42; addi x3,x0,600; loop: sw x2,0(x1); lw x4,0(x1);
    ///         addi x3,x3,-1; bne x3,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void CombinedWithSmbBypass_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x06400093, // addi x1, x0, 100
            0x02A00113, // addi x2, x0, 42
            0x25800193, // addi x3, x0, 600
            0x0020A023, // loop: sw x2, 0(x1)
            0x0000A203, // lw x4, 0(x1)
            0xFFF18193, // addi x3, x3, -1
            0xFE019AE3, // bne x3, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp(), enableSmbBypass: true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no SMB bypass was ever attempted");
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
    }

    /// <summary>
    ///     The pipeline wiring is predictor-agnostic: <see cref="LvpVp" /> alone (no
    ///     history hooks at all) must integrate exactly like <see cref="VtageVp" />.
    /// </summary>
    [Fact]
    public void LvpPredictorAlone_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new LvpVp());
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     Performance sanity check on the constant copy chain: value prediction should
    ///     meaningfully shorten this dependence-bound loop, the textbook win case for both papers
    ///     (Lipasti &amp; Shen §1: "collapse true dependences"; Perais &amp; Seznec §3.2: tight
    ///     loops). Uses <see cref="LTageBp" /> instead of the default
    ///     <c>AlwaysNotTakenPredictor</c> for branch prediction so the loop-closing branch
    ///     converges to near-zero mispredictions — with the trivial default predictor, this
    ///     backward branch mispredicts (and squashes) on <em>every</em> iteration, and that noise
    ///     completely swamps the much smaller per-iteration dependence-chain effect this test
    ///     means to isolate.
    /// </summary>
    [Fact]
    public void ConstantCopyChain_CompletesInFewerCyclesThanWithout() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTageBp());
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp(), new LTageBp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        long offCycles = Counter(offResult, "cycles");
        long onCycles = Counter(onResult, "cycles");
        Assert.True(onCycles < offCycles, $"value prediction did not speed up the loop: {onCycles} >= {offCycles}");
    }

    // ── Widened eligibility (TODO.md: "beyond scalar ALU/load") ─────────────────
    //
    // Several tests below use LTageBp instead of the default AlwaysNotTakenPredictor.
    // This isn't the usual "branch noise swamps a small measured effect" pitfall documented
    // elsewhere in this file — under investigation, AlwaysNotTakenPredictor (mispredicting the
    // loop's backward branch every iteration) drove some of these programs into a genuine
    // livelock, since VTAGE's TryPredict and Update disagreed on which speculative-history
    // snapshot indexed a given dynamic instruction (fixed below, see
    // VtageTagAliasing_DoesNotLivelockUnderAdversarialBranchPredictor). LTageBp is kept
    // here regardless, since these particular tests are about eligibility widening, not about
    // re-exercising the aliasing fix, and a competent branch predictor is the simplest way to
    // avoid depending on it.

    /// <summary>
    ///     The same copy-chain shape as <see cref="ConstantCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur" />,
    ///     but through <c>ToothClass.FloatingPoint</c> destinations instead of integer ones: <c>f2</c>
    ///     and <c>f3</c> alternate via <c>fsgnj.s</c> (the FP move idiom, matching the integer chain's
    ///     <c>add rd,rs,x0</c>), fed by a one-time <c>fcvt.s.w</c> seed so no float immediate needs
    ///     encoding. FP architectural registers are renamed through the same RAT/PRF as integer ones
    ///     (index <c>rd+32</c>), so this exercises the same rename/verify/train path with a different
    ///     PhysDest range. Uses <see cref="LTageBp" /> — see the section comment above.
    ///     Assembled from:
    ///     <c>
    ///         addi x1,x0,500; addi x2,x0,99; fcvt.s.w f2,x2; loop: fsgnj.s f3,f2,f2; fsgnj.s f2,f3,f3;
    ///         addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void FloatingPointCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0xD0010153, // fcvt.s.w f2, x2
            0x202101D3, // loop: fsgnj.s f3, f2, f2
            0x20318153, // fsgnj.s f2, f3, f3
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTageBp());
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp(), new LTageBp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     FP counterpart to <c>MidLoopValueShift_...</c>: the FP copy chain converges on <c>99.0f</c>
    ///     via <c>f2</c>/<c>f3</c>, then a rare branch re-converts <c>x10</c> (55) into <c>f2</c> five
    ///     iterations before the end, mispredicting the FP destination once VTAGE has converged.
    ///     Regression test for value-mispredict squash-at-commit driven by a noninteger,
    ///     non-ALU-class PhysDest. Uses <see cref="LTageBp" /> — see the section comment
    ///     above. Assembled from:
    ///     <c>
    ///         addi x1,x0,600; addi x2,x0,99; addi x9,x0,5; fcvt.s.w f2,x2;
    ///         loop: fsgnj.s f3,f2,f2; fsgnj.s f2,f3,f3; addi x1,x1,-1; beq x1,x9,do_redirect;
    ///         jal x0,cont; do_redirect: addi x10,x0,55; fcvt.s.w f2,x10; cont: bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void FloatingPointMidLoopValueShift_ArchStateIdenticalToWithout_AndMispredictOccurs() {
        uint[] program = [
            0x25800093, // addi x1, x0, 600
            0x06300113, // addi x2, x0, 99
            0x00500493, // addi x9, x0, 5
            0xD0010153, // fcvt.s.w f2, x2
            0x202101D3, // loop: fsgnj.s f3, f2, f2
            0x20318153, // fsgnj.s f2, f3, f3
            0xFFF08093, // addi x1, x1, -1
            0x00908463, // beq x1, x9, do_redirect
            0x00C0006F, // jal x0, cont
            0x03700513, // do_redirect: addi x10, x0, 55
            0xD0050153, // fcvt.s.w f2, x10
            0xFE0092E3, // cont: bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTageBp());
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp(), new LTageBp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_mispredicts"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_mispredicts") > 0, "the mid-loop FP value shift never mispredicted");
    }

    /// <summary>
    ///     Same copy-chain shape again, through <c>ToothClass.IntegerMulDiv</c>: <c>mul rd,rs,x6</c>
    ///     with <c>x6=1</c> (multiplicative identity) is the MulDiv analogue of the ALU chain's
    ///     <c>add rd,rs,x0</c> — a genuine RAW dependency that always converges on the same value.
    ///     Uses <see cref="LTageBp" /> — see the section comment above (this exact PC
    ///     layout, with the default predictor, was the one that surfaced the VTAGE aliasing
    ///     livelock during investigation). Assembled from:
    ///     <c>
    ///         addi x1,x0,500; addi x2,x0,99; addi x6,x0,1; loop: mul x3,x2,x6; mul x2,x3,x6;
    ///         addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void MulDivCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x00100313, // addi x6, x0, 1
            0x026101B3, // loop: mul x3, x2, x6
            0x02618133, // mul x2, x3, x6
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTageBp());
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp(), new LTageBp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     <c>ToothClass.System</c> (CSR reads): <c>csrrs x3, mscratch, x0</c> reads a scratch CSR
    ///     (rs1=x0, so per spec this never writes the CSR — see <c>Rv32Executor.ExecuteCsr</c>'s
    ///     <c>writeIfSrcZero</c>) primed once to a constant, so every dynamic instance of the read
    ///     returns the same value — a value-prediction win case with no register dependency chain at
    ///     all, unlike every other test here. System instructions are head-serialized (may only issue
    ///     at the ROB head — see <c>OooeTrain.TryIssueSlot</c>), so the benefit is entirely about
    ///     letting <em>younger</em> instructions elsewhere consume the predicted value early; this
    ///     test only checks that the prediction/verify/train path fires correctly for the class, not
    ///     a timing win. Assembled from:
    ///     <c>
    ///         addi x2,x0,99; csrrw x0,mscratch,x2; addi x1,x0,500;
    ///         loop: csrrs x3,mscratch,x0; addi x1,x1,-1; bne x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void CsrReadLoop_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x06300113, // addi x2, x0, 99
            0x34011073, // csrrw x0, mscratch, x2
            0x1F400093, // addi x1, x0, 500
            0x340021F3, // loop: csrrs x3, mscratch, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new VtageVp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    // ── Stride / hybrid predictor (TODO.md: "computational (stride-family) predictor component
    // to hybridize with VTAGE") ──────────────────────────────────────────────────────────────
    //
    // StrideVp (a 2-delta-style confidence FSM, see its own doc comment for provenance)
    // and VTAGE are complementary (Sazeides & Smith's computational vs. context-based taxonomy,
    // per Perais & Seznec HPCA 2014 §2): a monotonically incrementing register never repeats a value,
    // so LVP/VTAGE's confidence never saturates on it, but its stride is trivially constant. The
    // tests below use exactly such a program to demonstrate the new predictor firing on its own,
    // and firing identically when wrapped in HybridVp alongside VTAGE (per §7.1.2's
    // combination rule: single-component pass-through, since VTAGE never confidently disagrees
    // on an ever-changing value).

    /// <summary>
    ///     A monotonically incrementing counter (<c>addi x2,x2,4</c>, looped) is the complement of
    ///     the copy-chain tests above: the same value is never seen twice, so a value-repetition
    ///     predictor (LVP, or VTAGE without <see cref="StrideVp" />) can never saturate
    ///     confidence on it, while the stride between successive occurrences is constant from the
    ///     very first iteration. Assembled from:
    ///     <c>
    ///         addi x1,x0,300; addi x2,x0,0; loop: addi x2,x2,4; addi x1,x1,-1; bne x1,x0,loop;
    ///         ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void StrideOnly_MonotonicCounterLoop_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x12C00093, // addi x1, x0, 300
            0x00000113, // addi x2, x0, 0
            0x00410113, // loop: addi x2, x2, 4
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new StrideVp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     Regression test for <see cref="StrideVp" />'s in-flight depth tracking (see its
    ///     own doc comment for the mechanism and why it deviates from the paper). Same
    ///     monotonic-counter program as above, but with a competent branch predictor
    ///     (<c>LTageBp</c>, per this file's documented convention for VP timing/accuracy
    ///     tests) rather than the default <c>AlwaysNotTakenPredictor</c> — that default mispredicts
    ///     this loop's backward branch every iteration, squashing everything younger before more
    ///     than one iteration is ever simultaneously in flight, which masks the effect entirely.
    ///     Under <c>LTageBp</c>, multiple iterations genuinely overlap in this OoOE pipeline,
    ///     so predicting from the last committed value with a single, unscaled stride step
    ///     often mispredicts (undercounting how many occurrences are actually still unresolved).
    ///     Asserts a ratio with headroom so a regression that reintroduces committed-value-only
    ///     prediction fails loud; the exact figures aren't asserted since they're one hand-built
    ///     loop's measurement, not a general accuracy claim.
    /// </summary>
    [Fact]
    public void StrideInFlightDepth_MonotonicCounterLoop_ArchStateIdenticalToWithout_AndMispredictRateIsLow() {
        uint[] program = [
            0x12C00093, // addi x1, x0, 300
            0x00000113, // addi x2, x0, 0
            0x00410113, // loop: addi x2, x2, 4
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTageBp());
        (OooeTrain on, FlatMemory memOn) = Make(new StrideVp(), new LTageBp());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        long correct = Counter(onResult, "vp_correct");
        long mispredicts = Counter(onResult, "vp_mispredicts");
        Assert.True(
            correct > mispredicts * 3,
            $"stride's in-flight depth tracking regressed: correct={correct}, mispredicts={mispredicts} " +
            "(expected correct to substantially outweigh mispredicts once overlap is allowed to develop)"
        );
    }

    /// <summary>
    ///     Same monotonic-counter program as above, but the predictor under test is
    ///     <c>HybridVp(VtageVp, StrideVp)</c> — the actual "hybridize
    ///     with VTAGE" README item. VTAGE's own component never confidently predicts this program's
    ///     ever-changing value (no repeat to key a tagged component's confidence on), so the
    ///     hybrid's combination rule (single-component pass-through) must let Stride's confident
    ///     prediction through unblocked, exactly as it would stand alone. This is the regression
    ///     test for that pass-through path specifically, as opposed to
    ///     <see cref="Tests.Mechanism.HybridValuePredictionTests" />'s isolated, non-pipeline
    ///     coverage of the same combination logic.
    /// </summary>
    [Fact]
    public void HybridVtageStride_MonotonicCounterLoop_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x12C00093, // addi x1, x0, 300
            0x00000113, // addi x2, x0, 0
            0x00410113, // loop: addi x2, x2, 4
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) = Make(new HybridVp(new VtageVp(), new StrideVp()));
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    // ── Dynamic classification (TODO.md: Rychlik-style hybrid component selection) ──────────────
    //
    // DynamicClassificationVp assigns each PC to at most one component (rather than
    // HybridVp's always-query-both-and-gate-on-agreement), after a short 3-value
    // learning window. Routing correctness (which program shape lands on which component) is
    // covered precisely by Tests.Mechanism.DynamicClassificationValuePredictionTests; these two
    // pipeline tests just confirm the same wiring holds end-to-end through OooeTrain on the two
    // program shapes already used elsewhere in this file for the same purpose.

    /// <summary>
    ///     Same monotonic-counter program used by the Stride/Hybrid tests above — a value that
    ///     never repeats, so its 3-value learning window sees two equal (non-zero) deltas and
    ///     classifies to the computational component.
    /// </summary>
    [Fact]
    public void DynamicClassification_MonotonicCounterLoop_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x12C00093, // addi x1, x0, 300
            0x00000113, // addi x2, x0, 0
            0x00410113, // loop: addi x2, x2, 4
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) =
            Make(new DynamicClassificationVp(new VtageVp(), new StrideVp()));
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     Same constant copy-chain program as <c>ConstantCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur</c>
    ///     above — a value that always converges to the same constant (99) every iteration. Its
    ///     first three committed values are already 99, 99, 99 (zero deltas), which — per the
    ///     "equal deltas, including zero" rule — classifies to the <em>computational</em> component,
    ///     not context; this test exercises the same routing decision as the monotonic-counter test
    ///     above (equal, non-zero deltas), just via the zero-delta case instead. Routing to the
    ///     context component specifically is covered at the unit level, in
    ///     <see cref="Tests.Mechanism.DynamicClassificationValuePredictionTests" />
    ///     .<c>NonConstantDeltaHistory_ClassifiesToContext_AndEventuallyPredicts</c> — no program in
    ///     this file happens to produce the non-constant-delta learning window that path needs.
    /// </summary>
    [Fact]
    public void DynamicClassification_ConstantCopyChain_ArchStateIdenticalToWithout_AndPredictionsOccur() {
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(null);
        (OooeTrain on, FlatMemory memOn) =
            Make(new DynamicClassificationVp(new VtageVp(), new StrideVp()));
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "vp_predictions"));
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }
}