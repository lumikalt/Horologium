#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for the full DelayExecute+STT explicit-branch slice (Yu et al., MICRO
///     2019, §6.4.1) on <see cref="OooTrain" />'s <c>enableSttImplicitBranches</c> constructor
///     parameter. Closes the resolution-based implicit channel through explicit branches: a
///     mispredicted branch whose own resolution is tainted (<c>RobEntry.SourceYrot</c> not yet safe)
///     has its execute-time squash deferred (queued in <c>_pendingTaintedMispredicts</c>, re-checked
///     every cycle by <c>StepSttMispredictResolution</c>) rather than armed immediately, so the
///     squash's timing is no longer a function of tainted data. Predictor training and the value/
///     bypass-mispredict squashes already fire only at commit (strictly later than any visibility
///     point) and so are already safe by construction — untouched by this flag; see the field comment
///     on <c>_enableSttImplicitBranches</c> in OooTrain.cs.
/// </summary>
public class SttImplicitBranchTests {
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Mul(int rd, int rs1, int rs2) =>
        (uint)((0b0000001 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint Beq(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b000u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private const uint Ebreak = 0x00100073;

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static OooTrain Make(FlatMemory mem, bool enableSttImplicitBranches) =>
        new(
            new Rv32Mechanism(), mem,
            enableSttImplicitBranches: enableSttImplicitBranches
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
    ///     Program: an older branch B1 (<c>beq x6,x0,4</c>) depends on a chain of three dependent
    ///     3-cycle <c>mul</c>s, so it resolves many cycles after dispatch even though it is always
    ///     correctly predicted (targets its own fall-through either way — a timing-only branch, same
    ///     trick as SttExpOnlyTests). Between B1 and B2 sits a load L1 (<c>lw x2,0(x1)</c>) whose
    ///     result (0) both is B2's branch condition and roots B2's <c>SourceYrot</c> at L1's own
    ///     InstrId (a load is itself an access instruction). L1 depends only on <c>x1</c> (ready from
    ///     the very first instruction), so B2 completes long before B1's mul chain does — confirmed
    ///     empirically via PEventLog (a single 3-cycle mul was not enough of a gap; B1 still resolved
    ///     one cycle before B2 completed, so the chain was widened to three to give real margin). B2
    ///     (<c>beq x2,x0,...</c>) is TAKEN (x2==0) against the default
    ///     <see cref="AlwaysNotTakenPredictor" />'s not-taken prediction — a genuine misprediction
    ///     whose squash must discard eight wrong-path filler instructions before reaching the real
    ///     target. With the defense off, this squash fires the instant B2 resolves. With it on, B2's
    ///     own taint (rooted at L1) isn't safe until B1 — still unresolved on the slow mul chain —
    ///     finally resolves, so the squash (and therefore the halt) is measurably delayed even though
    ///     final architectural state is identical either way.
    /// </summary>
    private static uint[] TaintedMispredictAfterSlowOlderBranch() => [
        Addi(1, 0, 100), // x1 = 100
        Addi(5, 0, 1), // x5 = 1
        Mul(6, 5, 5), // x6 = 1 (3-cycle latency)
        Mul(6, 6, 5), // x6 = 1 (dependent; another 3 cycles)
        Mul(6, 6, 5), // x6 = 1 (dependent; another 3 cycles — stretches B1's resolution window)
        Beq(6, 0, 4), // B1: x6==0? false -> falls through either way (timing-only)
        Lw(2, 1, 0), // L1: x2 = mem[100] = 0 -- roots B2's taint at L1's own InstrId
        Beq(2, 0, 36), // B2: x2==0 -> TAKEN; predicted not-taken -> mispredicts
        Addi(7, 0, 111), Addi(7, 0, 222), Addi(7, 0, 333), Addi(7, 0, 444), // wrong-path filler,
        Addi(7, 0, 555), Addi(7, 0, 666), Addi(7, 0, 777), Addi(7, 0, 888), // must never survive
        Ebreak, // the real (taken) target
    ];

    [Fact]
    public void DefersMispredictSquashUntilOwnTaintClears_MeasurableCostAndIdenticalArchState() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, TaintedMispredictAfterSlowOlderBranch());
        LoadWords(memOn, 0, TaintedMispredictAfterSlowOlderBranch());
        LoadWords(memOff, 100, 0);
        LoadWords(memOn, 100, 0);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        // A pure timing/scheduling defense must never change what the program computes, only when —
        // including that the wrong-path fillers (x7) never survive in either run.
        AssertIdenticalArchState(off, on);
        Assert.Equal(0UL, off.ArchState.IntegerRegisters.Read(7));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(7));

        Assert.Equal(0L, Counter(offResult, "stt_mispredict_deferrals"));
        Assert.True(
            Counter(onResult, "stt_mispredict_deferrals") > 0,
            "the mispredicted branch's squash was never actually deferred by the STT gate"
        );

        // The paper's own framing of this cost (§1.1): "the later we delay issuing [the dependent
        // instruction], the greater the chance it delays instruction retirement" — here, delaying the
        // squash itself measurably delays the halt.
        Assert.True(
            Counter(onResult, "cycles") > Counter(offResult, "cycles"),
            $"expected the deferred squash to cost more cycles (off={Counter(offResult, "cycles")}, "
          + $"on={Counter(onResult, "cycles")})"
        );
    }

    /// <summary>
    ///     Same shape (an older slow branch, a load, then a second branch) but the second branch's
    ///     condition comes from an independent <c>addi</c>, not from the load's result — no taint root
    ///     reaches it, so the gate must never fire and cycle count must be identical to the
    ///     feature-off run. Mirrors SttExpOnlyTests' own no-op baseline check.
    /// </summary>
    [Fact]
    public void UntaintedMispredict_ProducesZeroDeferralsAndIdenticalCycleCount() {
        uint[] program = [
            Addi(1, 0, 100), // x1 = 100
            Addi(5, 0, 1), // x5 = 1
            Mul(6, 5, 5), // x6 = 1 (3-cycle latency; timing only)
            Mul(6, 6, 5), // x6 = 1 (dependent; timing only)
            Mul(6, 6, 5), // x6 = 1 (dependent; timing only)
            Beq(6, 0, 4), // B1: timing-only, as above
            Lw(2, 1, 0), // x2 = mem[100] = 0 (never consumed as B2's condition)
            Addi(3, 0, 0), // x3 = 0, independently of x2
            Beq(3, 0, 20), // B2: x3==0 -> TAKEN (mispredict), but untainted
            Addi(7, 0, 111), Addi(7, 0, 222), Addi(7, 0, 333), Addi(7, 0, 444), // wrong-path filler
            Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 100, 0);
        LoadWords(memOn, 100, 0);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(0UL, off.ArchState.IntegerRegisters.Read(7));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(7));
        Assert.Equal(0L, Counter(offResult, "stt_mispredict_deferrals"));
        Assert.Equal(0L, Counter(onResult, "stt_mispredict_deferrals"));
        Assert.Equal(Counter(offResult, "cycles"), Counter(onResult, "cycles"));
    }

    /// <summary>
    ///     Liveness/multi-entry test for <c>_pendingTaintedMispredicts</c>: two mispredicted branches
    ///     (OuterB, then InnerB, dispatched speculatively past OuterB's still-deferred squash) both
    ///     root their taint through a load gated by the SAME older slow branch (RootB), so both end up
    ///     queued simultaneously and become safe in the very same cycle once RootB finally resolves —
    ///     exactly the "pick the oldest of several now-safe candidates" path in
    ///     <c>StepSttMispredictResolution</c>, and the case where firing OuterB's squash must discard
    ///     the still-pending, now-moot InnerB entry (<c>StepPartialSquash</c>'s
    ///     <c>_pendingTaintedMispredicts.RemoveAll(id &gt; bId)</c>). A prior "trace it and it's fine"
    ///     confidence level has been wrong before on this codebase (three CPR/CFP deadlocks + a
    ///     store-sets livelock, all first found on a real run) — bounded <c>maxTicks</c> both confirms
    ///     no livelock and, via <see cref="AssertIdenticalArchState" /> against a feature-off run,
    ///     that the discarded InnerB subtree never corrupts final state.
    /// </summary>
    [Fact]
    public void NestedTaintedMispredictsSharingRootBranch_NoLivelockAndIdenticalArchState() {
        uint[] program = [
            Addi(1, 0, 100), // x1 = 100 (L1 pointer)
            Addi(4, 0, 104), // x4 = 104 (L2 pointer)
            Addi(5, 0, 1), // x5 = 1
            Mul(6, 5, 5), Mul(6, 6, 5), Mul(6, 6, 5), // RootB depends on this chain (9-cycle latency)
            Beq(6, 0, 4), // RootB: timing-only, correctly predicted, resolves late
            Lw(2, 1, 0), // L1: x2 = mem[100] = 0 -- roots OuterB's taint
            Beq(2, 0, 28), // OuterB: x2==0 -> TAKEN; mispredict, tainted via L1 (gated by RootB)
            Lw(3, 4, 0), // L2: x3 = mem[104] = 0 -- roots InnerB's taint (still gated by RootB)
            Beq(3, 0, 20), // InnerB: x3==0 -> TAKEN; mispredict, ALSO tainted via RootB (shared gate)
            Addi(7, 0, 111), Addi(7, 0, 222), Addi(7, 0, 333), Addi(7, 0, 444), // wrong-path filler
            Ebreak, // the only correct outcome: OuterB's taken target
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 100, 0);
        LoadWords(memOff, 104, 0);
        LoadWords(memOn, 100, 0);
        LoadWords(memOn, 104, 0);

        const long maxTicks = 10_000;
        RevolutionResult offResult = off.Run(maxTicks);
        RevolutionResult onResult = on.Run(maxTicks);

        Assert.True(offResult.TotalTicks < maxTicks, "baseline run should halt normally well under the cap");
        Assert.True(
            onResult.TotalTicks < maxTicks,
            "STT-implicit-branches run never halted (hit maxTicks) -- the nested tainted-mispredict "
          + "chain sharing one root branch likely livelocked"
        );

        AssertIdenticalArchState(off, on);
        Assert.Equal(0UL, off.ArchState.IntegerRegisters.Read(7));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(7));
        Assert.True(Counter(onResult, "stt_mispredict_deferrals") > 0);
    }

    /// <summary>
    ///     The paper's actual main proposal is DelayExecute+STT with BOTH the explicit-channel load
    ///     gate (STT-ExpOnly) and the explicit-branch resolution gate (this slice) active together —
    ///     each in isolation is a half-scheme. The shared <c>SpectreVisibilityTracker</c> now has two
    ///     independent consumers (ExpOnly's load-issue gate reads <c>IsSafe</c>; this slice's deferral
    ///     both reads <c>IsSafe</c> and controls when <c>OnBranchResolved</c> fires), so this proves
    ///     they compose rather than merely type-checking together. Program: RootB1 gates L0→L1's
    ///     pointer-chase (ExpOnly holds L1 at Issue until RootB1 resolves); MidB is a SEPARATE slow
    ///     branch dispatched between L0 and L1 — older than L1 (so it matters to L1's own taint-root
    ///     safety) but younger than L0 (so it is invisible to ExpOnly's own gate, which only checks
    ///     L0's safety) — meaning by the time OuterB (fed by L1's loaded value) mispredicts, MidB can
    ///     still be genuinely unresolved even though ExpOnly already released L1. Confirmed empirically
    ///     (via a temporary probe) that both <c>stt_load_issue_stalls</c> and
    ///     <c>stt_mispredict_deferrals</c> are nonzero in this single run before finalizing the test.
    /// </summary>
    [Fact]
    public void SttExpOnlyAndImplicitBranches_ComposeWithoutBreakingEachOther() {
        uint[] program = [
            Addi(9, 0, 100), // x9 = 100 (L0 pointer)
            Addi(5, 0, 1), Mul(6, 5, 5), Mul(6, 6, 5), Mul(6, 6, 5), Beq(6, 0, 4), // RootB1 (9-cycle)
            Lw(1, 9, 0), // L0: x1 = mem[100] = 200 (a pointer value)
            Addi(8, 0, 1), Mul(10, 8, 8), Mul(10, 10, 8), Mul(10, 10, 8), Mul(10, 10, 8), Mul(10, 10, 8),
            Beq(10, 0, 4), // MidB: independent 15-cycle chain, older than L1 but younger than L0
            Lw(2, 1, 0), // L1: x2 = mem[200] = 0 -- ExpOnly holds this at Issue until L0 (RootB1) is safe
            Beq(2, 0, 20), // OuterB: x2==0 -> TAKEN; tainted via L1, gated by MidB (still unresolved)
            Addi(7, 0, 111), Addi(7, 0, 222), Addi(7, 0, 333), Addi(7, 0, 444), // wrong-path filler
            Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = new(new Rv32Mechanism(), memOff);
        OooTrain on = new(new Rv32Mechanism(), memOn, enableSttExpOnly: true, enableSttImplicitBranches: true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 100, 200);
        LoadWords(memOff, 200, 0);
        LoadWords(memOn, 100, 200);
        LoadWords(memOn, 200, 0);

        const long maxTicks = 10_000;
        RevolutionResult offResult = off.Run(maxTicks);
        RevolutionResult onResult = on.Run(maxTicks);

        Assert.True(offResult.TotalTicks < maxTicks);
        Assert.True(onResult.TotalTicks < maxTicks, "combined STT-ExpOnly + implicit-branches run never halted");

        AssertIdenticalArchState(off, on);
        Assert.Equal(0UL, off.ArchState.IntegerRegisters.Read(7));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(7));
        Assert.True(Counter(onResult, "stt_load_issue_stalls") > 0, "ExpOnly's own gate never engaged");
        Assert.True(Counter(onResult, "stt_mispredict_deferrals") > 0, "the implicit-branch gate never engaged");
    }
}
