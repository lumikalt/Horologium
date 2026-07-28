#region

using Mechanism.ValuePred;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Verification, not new gating, for value-prediction training/squash gating (Yu et al., MICRO
///     2019, §6.4.2/§6.5's "round out the paper's complete DelayExecute+STT variant" follow-up) TODO
///     item, mirroring <see cref="SttStoreForwardTests" />'s own "safe by construction" closure of the
///     store-to-load-forwarding channel.
///     <list type="bullet">
///         <item>
///             <c>_valuePredictor.Update</c> (both the EOLE Late-Execution mismatch path and the
///             ordinary <c>IsVpEligible</c> path) and the value-misprediction squash (<c>SetFlush</c>)
///             all fire exclusively inside <c>StepCommit</c>'s ROB-head retire loop — confirmed by
///             direct trace, same as <c>_predictor.Update</c> and the memory-order-violation squash.
///             There was never a flag-dependent immediate-vs-deferred choice to protect here (see the
///             <c>Debug.Assert</c>s added alongside this file, mirroring the memory-order-violation
///             one).
///         </item>
///         <item>
///             A candidate composition channel was considered and ruled out by tracing, not
///             assumption: value prediction is eligible for <c>ToothClass.Load</c> (see the
///             <c>vpEligible</c> switch in <c>StepRename</c>), and a confidently-predicted value is
///             written to the PRF immediately at Rename — well before the load's own Issue. This is
///             NOT a hole in STT-ExpOnly for the same reason SMB bypass isn't (see
///             <see cref="SttStoreForwardTests" />): ExpOnly permits tainted data <em>propagation</em>,
///             it only gates <em>transmitters</em>. The load's own real memory access still has to
///             happen for real and be verified — and critically, EOLE Late Execution (the ONE
///             mechanism that skips Issue/Execute entirely) is restricted to
///             <c>instr.Class == ToothClass.IntegerAlu</c> (confirmed directly in <c>StepRename</c>'s
///             <c>leEligible</c> computation) — a value-predicted <em>Load</em> can never take that
///             path, so it always falls through to the ordinary <c>IsVpEligible</c> commit-time
///             verification, meaning its real Execute (and therefore <c>TryIssueSlot</c>'s
///             unconditional STT-ExpOnly gate) is never skipped.
///         </item>
///     </list>
///     <para>
///         <b>What <see cref="ExpOnlyAndValuePredictionCoexist_ArchStateAndPredictionCountUnchanged" />
///         proves, precisely</b> (same honest framing as <c>SttStoreForwardTests</c>'s own composition
///         test, after running the same check here): the same-instance claim above — "a load that IS
///         value-predicted is ALSO still gated" — rests on the code inspection (<c>leEligible</c>'s
///         class restriction), not on this dynamic test. Traced through: RootB is kept short (to
///         survive the loop's own per-iteration mispredict-flush, matching <c>SttStoreForwardTests</c>'s
///         own reasoning), so it resolves within the first handful of iterations — only THOSE early
///         iterations' target loads are gated. <c>LvpVp</c>'s confidence counter needs on the order of
///         a hundred correct observations (probabilistic forward transitions at 1/16 and 1/32 per
///         step) to saturate, so predictions only start landing much later in the 500-iteration loop.
///         The measured stalls and the measured predictions therefore land on different dynamic
///         instances — confirmed by deliberately injecting the exact regression this test would need
///         to catch (exempting <c>WasValuePredicted</c> loads from the gate) and observing the test
///         still passes. So this test demonstrates coexistence (both mechanisms fire in the same run,
///         neither's outcome — prediction count, final architectural state — perturbed by the other),
///         not the dynamic same-instance guarantee.
///     </para>
/// </summary>
public class SttValuePredictionTests {
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Mul(int rd, int rs1, int rs2) =>
        (uint)((0b0000001 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private const uint Ebreak = 0x00100073;

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static OooTrain Make(FlatMemory mem, bool enableSttExpOnly) =>
        new(
            new Rv32Mechanism(), mem,
            robCapacity: 64,
            iqCapacity: 64,
            valuePredictor: new LvpVp(),
            enableSttExpOnly: enableSttExpOnly
        );

    private static void LoadWords(FlatMemory mem, ulong address, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(address, bytes);
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     RootB (a 6x chained-mul dependency, ~18 cycles, short enough to retire before the loop's
    ///     own per-iteration mispredict-flush — see <c>SttStoreForwardTests</c>'s own doc comment for
    ///     why a long RootB here would instead flush away the still-speculative anchor) and the anchor
    ///     load (<c>lw x2,0(x1)</c>, untainted address, so x2 is tainted purely by the anchor's own
    ///     InstrId) both sit once, before the loop. The loop body is just the target load
    ///     (<c>lw x4,0(x2)</c>), SAME PC and SAME address (hence same loaded value, mem[500] is never
    ///     written) every iteration — the classic LVP win case, but 500 iterations deep (matching
    ///     <c>ValuePredictionTests.cs</c>'s own convention): <c>LvpVp</c>'s <c>ForwardProbabilisticCounter</c>
    ///     needs many correct observations, most gated behind low (1/16, 1/32) per-step probabilities,
    ///     to saturate before a prediction is ever trusted. RootB is short enough to resolve (and
    ///     retire) within the first handful of iterations, so only the earliest iterations' target
    ///     loads are actually gated by ExpOnly — but that's all this test needs: proof the gate fires
    ///     at least once in a run where value prediction also, eventually, fires.
    /// </summary>
    private static uint[] TaintedRepeatedLoadLoop() {
        List<uint> program = [
            Addi(7, 0, 1), // seed for RootB's mul chain
        ];
        for (var i = 0; i < 6; i++) program.Add(Mul(6, 7, 7)); // RootB: ~6*3 = 18-cycle window
        program.Add(Bne(6, 0, 4)); // RootB: always taken, targets its own fall-through -- timing-only
        program.Add(Addi(1, 0, 300)); // x1 = 300: fixed anchor-load address
        program.Add(Lw(2, 1, 0)); // anchor load: x2 = mem[300] = 500 -- untainted address (x1),
        // taints x2 with the anchor's own InstrId, unsafe while RootB above is unresolved
        program.Add(Addi(3, 0, 500)); // loop counter -- LvpVp's ForwardProbabilisticCounter needs many
        // correct observations to saturate (see ValuePredictionTests.cs's own 500-iteration convention)
        int loopStart = program.Count * 4;
        program.Add(Lw(4, 2, 0)); // TARGET load: SourceYrot = x2's Yrot (anchor's own InstrId) --
        // unsafe until RootB resolves; SAME PC + SAME address every iteration -- LVP's win case
        program.Add(Addi(3, 3, -1));
        int loopCtrlPc = program.Count * 4;
        program.Add(Bne(3, 0, loopStart - loopCtrlPc)); // back to the load at loop start
        program.Add(Ebreak);
        return program.ToArray();
    }

    /// <summary>
    ///     Proves coexistence: with STT-ExpOnly and value prediction both enabled, (a) the target
    ///     load's own issue is still held by ExpOnly's gate at least once (an early iteration, before
    ///     RootB resolves), (b) value prediction still fires (and exactly as often as with ExpOnly
    ///     disabled), and (c) final architectural state is identical either way. Confirmed to fail
    ///     (zero stalls) if the STT-ExpOnly gate is temporarily disabled.
    /// </summary>
    [Fact]
    public void ExpOnlyAndValuePredictionCoexist_ArchStateAndPredictionCountUnchanged() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, TaintedRepeatedLoadLoop());
        LoadWords(memOn, 0, TaintedRepeatedLoadLoop());
        LoadWords(memOff, 300, 500);
        LoadWords(memOn, 300, 500);
        LoadWords(memOff, 500, 0xABCD);
        LoadWords(memOn, 500, 0xABCD);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.True(
            Counter(onResult, "stt_load_issue_stalls") > 0,
            "STT-ExpOnly's gate was never exercised anywhere in this run"
        );

        Assert.True(Counter(onResult, "vp_predictions") > 0, "no value prediction occurred in this composition");
        Assert.Equal(Counter(offResult, "vp_predictions"), Counter(onResult, "vp_predictions"));
        Assert.Equal(0L, Counter(onResult, "vp_mispredicts"));
    }
}
