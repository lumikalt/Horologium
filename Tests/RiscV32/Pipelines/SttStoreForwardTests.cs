#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Verification, not new gating, for the store-to-load-forwarding implicit channel (Yu et al.,
///     MICRO 2019, §6.4.2/§6.5) TODO item. Both of the two mechanisms the item names turned out to
///     already be safe by construction on direct tracing:
///     <list type="bullet">
///         <item>
///             <c>CheckLoadViolations</c> (called from <c>StepComplete</c> the instant a store's
///             address resolves) only sets <c>LqEntry.Violated</c> — it never squashes. The squash
///             itself (<c>SetFlush</c>) fires only in <c>StepCommit</c>'s ROB-head retire loop,
///             unconditionally, regardless of any STT flag — a memory-order violation was already
///             commit-time-only by construction, with no flag-dependent immediate-vs-deferred choice
///             to protect (see the <c>Debug.Assert</c> added alongside this file, mirroring the one
///             at the branch-mispredict site).
///         </item>
///         <item>
///             <c>StoreSetPredictor.OnStoreIssued</c> (also called from <c>StepComplete</c> at the
///             same point) only clears an internal LFST bookkeeping slot. The actual issue-time
///             release decision (<c>StoreSetStallLoad</c>) reads <c>SqEntry.AddressKnown</c> directly
///             — never <c>_lfst</c> — and a load's own <c>PredStoreSeqNo</c> is fixed at its own
///             dispatch, independent of any later <c>OnStoreIssued</c> call. Confirmed by direct
///             trace: <c>OnStoreIssued</c> has zero observable effect on pipeline timing, register
///             values, or memory state — it changes only which internal table slot a later lookup
///             would (harmlessly) miss versus short-circuit.
///         </item>
///     </list>
///     A third candidate channel was considered and ruled out (also by tracing, not assumption): SMB
///     (NoSQ)'s early bypass broadcast (<c>CheckBypassLoads</c>) delivers a bypassed load's value to
///     dependents the instant its predicted producer store resolves, with no reference to the
///     bypassed load's own taint. This is NOT a hole in STT-ExpOnly: ExpOnly's threat model
///     deliberately permits tainted <em>data propagation</em> to registers — it only gates
///     <em>transmitters</em> (a load's own address-based cache access). The bypassed load's own
///     shadow/verification execution — its real cache access, the actual transmitter — still passes
///     through <c>TryIssueSlot</c>'s unconditional <c>ToothClass.Load</c> STT gate (confirmed: the
///     switch case has no <c>lq.Bypassed</c> exception), and a consumer's own <c>SourceYrot</c> is
///     computed purely from its producer's Yrot (set once at the producer's own Dispatch), entirely
///     independent of when the producer's value physically arrives.
///     <para>
///         <b>What <see cref="ExpOnlyAndSmbBypassCoexist_ArchStateAndBypassCountUnchanged" /> proves,
///         precisely:</b> the same-instance claim above ("a load that IS bypassed is ALSO still
///         gated") rests on the <em>code inspection</em> — the switch case has no exemption, full
///         stop — not on this dynamic test. Traced through the test's own actual timing: SMB needs 2
///         prior trainings before it predicts, so the bypass only fires on the 3rd loop iteration;
///         RootB (kept short to survive the loop's own per-iteration mispredict-flush — see the
///         program's own doc comment) has long since resolved by then, so the 3rd iteration's reload
///         is not gated. The measured stalls and the measured bypass therefore land on <em>different
///         dynamic instances</em> (iterations 1–2 gated-but-not-bypassed, iteration 3
///         bypassed-but-not-gated) — confirmed by deliberately injecting the exact regression this
///         test would need to catch (exempting <c>lq.Bypassed</c> loads from the gate) and observing
///         the test still passes. So this test demonstrates <em>coexistence</em> (both mechanisms fire
///         in the same run, neither's outcome — bypass count, final architectural state — perturbed by
///         the other) — a real and useful property — but does NOT dynamically prove the same-instance
///         guarantee; that guarantee is established by reading <c>TryIssueSlot</c>'s source, not by
///         this test.
///     </para>
/// </summary>
public class SttStoreForwardTests {
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Sw(int rs2, int rs1, int imm) {
        var u = (uint)imm;
        uint imm11_5 = (u >> 5) & 0x7F;
        uint imm4_0 = u & 0x1F;
        return (imm11_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4_0 << 7) | 0b0100011u;
    }

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
            enableSmbBypass: true,
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
    ///     RootB (a 6x chained-mul dependency, ~18 cycles) and the anchor load (<c>lw x2,0(x1)</c>,
    ///     address = fixed x1, so the anchor's own untainted address means its destination x2 is
    ///     tainted purely by the anchor's own InstrId — the "a load always roots taint at its own
    ///     destination" rule) both sit ONCE, before the loop — matching the already-proven fixed
    ///     address/no-per-iteration-re-taint shape from <c>SmbBypassTests</c>/<c>SttMemDepGatingTests</c>
    ///     (an earlier draft tainted x2 fresh every iteration via a per-iteration anchor load and hit
    ///     exactly the timing gotcha those tests already warn about: the store/reload's own address
    ///     chain gained an extra hop of latency, racing forwarding/training instead of letting it
    ///     settle). The loop body (<c>sw x5,0(x2); lw x4,0(x2)</c>) is otherwise identical to the
    ///     proven pattern: SAME PC every iteration, fixed SeqNo distance, no address computation
    ///     inside the loop at all. RootB deliberately stays SHORT: the loop's own backward
    ///     loop-control branch mispredicts every iteration under the default not-taken predictor
    ///     (expected — proven-working tests like <c>SmbBypassTests</c> rely on the same shape), and a
    ///     full-flush squash discards anything still speculative (not yet retired) younger than the
    ///     mispredict — including RootB and the anchor themselves, if RootB is made long enough that
    ///     they haven't retired by the time the loop's own first mispredict fires (confirmed
    ///     empirically: an earlier 30x-mul RootB reliably suppressed the composition entirely, since
    ///     the anchor's still-speculative x2 write got flushed away before any loop iteration ran).
    ///     A short RootB retires well before the loop even starts, avoiding that race — and still
    ///     leaves the anchor's own SourceYrot unsafe for long enough (out-of-order execution lets the
    ///     loop's early iterations dispatch and attempt issue while RootB is still mid-resolution, even
    ///     though RootB sits earlier in program order) to gate iterations 1–2's target reload (before
    ///     SMB's confidence threshold is met) — which is all
    ///     <see cref="ExpOnlyAndSmbBypassCoexist_ArchStateAndBypassCountUnchanged" /> needs; see that
    ///     test's own doc comment for exactly what is and isn't proven dynamically here.
    /// </summary>
    private static uint[] TaintedReloadWithBypassEligibleLoop() {
        List<uint> program = [
            Addi(7, 0, 1), // seed for RootB's mul chain
        ];
        for (var i = 0; i < 6; i++) program.Add(Mul(6, 7, 7)); // RootB: ~6*3 = 18-cycle window
        program.Add(Bne(6, 0, 4)); // RootB: always taken, targets its own fall-through -- timing-only
        program.Add(Addi(1, 0, 300)); // x1 = 300: fixed anchor-load address
        program.Add(Lw(2, 1, 0)); // anchor load: x2 = mem[300] = 500 -- untainted address (x1),
        // taints x2 with the anchor's own InstrId, unsafe while RootB above is unresolved
        program.Add(Addi(5, 0, 42)); // x5 = 42: fixed store value
        program.Add(Addi(3, 0, 3)); // loop counter = 3
        int loopStart = program.Count * 4;
        program.Add(Sw(5, 2, 0)); // store mem[x2]=mem[500] = 42 -- SAME PC every iteration; stores
        // aren't STT-gated
        program.Add(Lw(4, 2, 0)); // TARGET reload: SourceYrot = x2's Yrot (anchor's own InstrId) --
        // unsafe until RootB resolves; SAME PC every iteration -- trains SmbPredictor
        program.Add(Addi(3, 3, -1));
        int loopCtrlPc = program.Count * 4;
        program.Add(Bne(3, 0, loopStart - loopCtrlPc)); // back to the store at loop start
        program.Add(Ebreak);
        return program.ToArray();
    }

    /// <summary>
    ///     Proves coexistence, not the same-instance guarantee (see the class doc comment): with
    ///     STT-ExpOnly and SMB bypass both enabled, (a) at least one iteration's reload is still held
    ///     by ExpOnly's gate (iterations 1–2, before SMB's confidence threshold is met — confirmed
    ///     nonzero, not asserted to be the bypassed instance), (b) SMB bypass still fires exactly as
    ///     often as with ExpOnly disabled, and (c) final architectural state is identical either way.
    ///     Together these show ExpOnly's gate and SMB's bypass run in the same program without either
    ///     perturbing the other's outcome. The same-instance property ("the exact load that bypasses is
    ///     also the one ExpOnly would gate") is established separately, by <c>TryIssueSlot</c>'s own
    ///     source having no <c>lq.Bypassed</c> exemption — confirmed NOT to be dynamically exercised by
    ///     this test: deliberately injecting that exact exemption still leaves this test green.
    /// </summary>
    [Fact]
    public void ExpOnlyAndSmbBypassCoexist_ArchStateAndBypassCountUnchanged() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, TaintedReloadWithBypassEligibleLoop());
        LoadWords(memOn, 0, TaintedReloadWithBypassEligibleLoop());
        LoadWords(memOff, 300, 500);
        LoadWords(memOn, 300, 500);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        // Some reload in this run is held by ExpOnly's gate (iterations 1-2, before SMB bypass
        // becomes eligible) -- confirms the gate is genuinely active in this program, not that the
        // SAME instance that bypasses is the one held (see class doc comment).
        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.True(
            Counter(onResult, "stt_load_issue_stalls") > 0,
            "STT-ExpOnly's gate was never exercised anywhere in this run"
        );

        // SMB bypass still fires, and exactly as often with ExpOnly on as off -- proves ExpOnly's
        // gate (a pure timing/scheduling change on the load's own issue) doesn't perturb SMB's own
        // prediction/bypass behavior, i.e. neither mechanism defeats the other.
        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no SMB bypass occurred in this composition");
        Assert.Equal(Counter(offResult, "smb_bypasses"), Counter(onResult, "smb_bypasses"));
        Assert.Equal(0L, Counter(onResult, "smb_mispredicts"));
    }
}
