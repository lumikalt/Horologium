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
///     Pipeline-level invariant tests for value prediction (Lipasti &amp; Shen, MICRO 1996, LVPT;
///     Perais &amp; Seznec, HPCA 2014, VTAGE + FPC) as wired into <see cref="OooeTrain" /> via the
///     <c>valuePredictor</c> constructor parameter.
///     <para>
///         A value-predicted instruction still executes for real through the ordinary pipeline in
///         the background; the early prediction only supplies a speculative early result. Every
///         test here runs the same program twice (feature off vs on) and asserts identical final
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
        for (var r = 0; r < 32; r++)
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor());
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor());
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
    ///     executes for real) but this is exactly the scenario where a silent history-corruption
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "branch_misses") > 0, "the alternating branch never mispredicted");
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
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
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor(), enableSmbBypass: true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no SMB bypass was ever attempted");
        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
    }

    /// <summary>
    ///     The pipeline wiring is predictor-agnostic: <see cref="LvpPredictor" /> alone (no
    ///     history hooks at all) must integrate exactly like <see cref="VtagePredictor" />.
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
        (OooeTrain on, FlatMemory memOn) = Make(new LvpPredictor());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction was ever supplied");
        Assert.True(Counter(onResult, "vp_correct") > 0, "no value prediction ever verified correct");
    }

    /// <summary>
    ///     Performance sanity check on the constant copy chain: value prediction should
    ///     meaningfully shorten this dependence-bound loop, the textbook win case for both papers
    ///     (Lipasti &amp; Shen §1: "collapse true dependences"; Perais &amp; Seznec §3.2: tight
    ///     loops). Uses <see cref="LTagePredictor" /> instead of the default
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

        (OooeTrain off, FlatMemory memOff) = Make(null, new LTagePredictor());
        (OooeTrain on, FlatMemory memOn) = Make(new VtagePredictor(), new LTagePredictor());
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        long offCycles = Counter(offResult, "cycles");
        long onCycles = Counter(onResult, "cycles");
        Assert.True(onCycles < offCycles, $"value prediction did not speed up the loop: {onCycles} >= {offCycles}");
    }
}
