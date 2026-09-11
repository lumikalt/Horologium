#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for STT-ExpOnly (Yu et al., MICRO 2019), the "DelayExecute+STT-ExpOnly"
///     variant the paper itself evaluates: only loads are treated as transmitters (explicit-channel
///     protection only — no implicit-branch/prediction-based taint tracking), and the Spectre-model
///     visibility point (Yan et al., MICRO 2018, Table 1: all older branches resolved) gates a
///     load's Issue whenever its address depends on another load's not-yet-visible data. See
///     <see cref="OooTrain" />'s <c>enableSttExpOnly</c> constructor parameter,
///     <c>RobEntry.SourceYrot</c>, <c>PhysicalRegisterFile.Yrot</c>/<c>SetYrot</c>, and
///     <c>Pipeline.Ooo.SpectreVisibilityTracker</c>.
///     <para>
///         Every test compares the same program run twice (feature off vs on) rather than asserting
///         absolute cycle counts, so the assertions track the mechanism's actual effect (a real,
///         measurable Issue-cycle delay on the tainted load only) rather than incidental pipeline
///         timing that would break under unrelated latency-model changes.
///     </para>
/// </summary>
public class SttExpOnlyTests {
    private const uint Ebreak = 0x00100073;

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
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b000u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static uint Jal(int rd, int immOffset) {
        var imm = (uint)immOffset;
        uint bit20 = (imm >> 20) & 0x1;
        uint bits10To1 = (imm >> 1) & 0x3FF;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits19To12 = (imm >> 12) & 0xFF;
        return (bit20 << 31) | (bits10To1 << 21) | (bit11 << 20) | (bits19To12 << 12) | ((uint)rd << 7) | 0b1101111u;
    }

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static OooTrain Make(FlatMemory mem, bool enableSttExpOnly, PEventLog? pEventLog = null) =>
        new(
            new Rv32Mechanism(), mem,
            pEventLog: pEventLog,
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
    ///     Program:
    ///     <c>
    ///         addi x1,x0,400; mul x6,x0,x0; beq x6,x0,4 (targets pc+4, the fall-through,
    ///         regardless of direction — a timing-only branch); lw x2,0(x1); lw x4,0(x2); ebreak
    ///     </c>
    ///     .
    ///     Memory[400] holds the pointer value 800 so the second load's address is genuinely
    ///     data-dependent on the first. The branch depends on <c>mul</c>'s 3-cycle-latency result
    ///     (<see cref="FuLatencyConfig.Default" />), so it resolves several cycles after it dispatches
    ///     — stretching a real "older unresolved branch" window the second load's taint chain roots
    ///     through the first load can be checked against.
    /// </summary>
    private static uint[] PointerChaseAfterSlowBranch() => [
        Addi(1, 0, 400), // x1 = 400
        Mul(6, 0, 0),    // x6 = 0 (3-cycle latency; only its timing matters)
        Beq(6, 0, 4),    // depends on x6; targets pc+4 (fall-through) either way
        Lw(2, 1, 0),     // x2 = mem[x1] = mem[400] = 800
        Lw(4, 2, 0),     // x4 = mem[x2] = mem[800]  — address depends on the first load
        SttExpOnlyTests.Ebreak,
    ];

    [Fact]
    public void DelaysDependentLoadUntilVisibilityPoint_ButNotTheIndependentFirstLoad() {
        var logOff = new PEventLog();
        var logOn = new PEventLog();
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false, logOff);
        OooTrain on = Make(memOn, true, logOn);
        LoadWords(memOff, 0, PointerChaseAfterSlowBranch());
        LoadWords(memOn, 0, PointerChaseAfterSlowBranch());
        LoadWords(memOff, 400, 800);
        LoadWords(memOn, 400, 800);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        // STT-ExpOnly is a pure timing/scheduling transform: it must never change what the
        // program actually computes, only when. A gate that corrupted issue order (e.g. skipped
        // re-checking a slot, or a taint chain feeding the wrong register) would show up here as
        // a final-state divergence even if the cycle-count assertions below happened to pass.
        AssertIdenticalArchState(off, on);

        long offL1Issue = IssueCycle(logOff, 12);
        long offL2Issue = IssueCycle(logOff, 16);
        long onL1Issue = IssueCycle(logOn, 12);
        long onL2Issue = IssueCycle(logOn, 16);

        // The first load (independent of the branch/taint) issues at the same cycle regardless —
        // the defense is selective, not a blanket slowdown.
        Assert.Equal(offL1Issue, onL1Issue);

        // The second (pointer-chasing) load issues strictly later with the defense on: it is held
        // until the branch's Spectre-model visibility point clears, not just until x2 is ready.
        Assert.True(
            onL2Issue > offL2Issue,
            $"expected STT-ExpOnly to delay the dependent load's Issue (off={offL2Issue}, on={onL2Issue})"
        );

        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.True(
            Counter(onResult, "stt_load_issue_stalls") > 0,
            "the dependent load was never actually held at Issue by the STT-ExpOnly gate"
        );
        return;

        long IssueCycle(PEventLog l, ulong pc) =>
            l.Events.Single(e => e.Kind == PEventKind.Issue && e.Pc == pc).Cycle;
    }

    /// <summary>
    ///     Same shape of program (a branch followed by two loads) but the second load's address
    ///     comes from an independent <c>addi</c>, not from the first load's result — no register
    ///     ever carries a taint root, so the gate must never fire and cycle count must be identical
    ///     to the feature-off run. Proves the mechanism doesn't stall untainted loads it happens to
    ///     sit near, and doubles as the harness's own "no-op" baseline check.
    /// </summary>
    [Fact]
    public void NoTaintedChain_ProducesZeroStallsAndIdenticalCycleCount() {
        uint[] program = [
            Addi(1, 0, 400), // x1 = 400
            Mul(6, 0, 0),    // x6 = 0
            Beq(6, 0, 4),    // timing-only branch, as above
            Lw(2, 1, 0),     // x2 = mem[400] = 800 (never consumed as an address below)
            Addi(3, 0, 800), // x3 = 800, independently of x2
            Lw(4, 3, 0),     // x4 = mem[x3] — untainted, ordinary independent load
            SttExpOnlyTests.Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 400, 800);
        LoadWords(memOn, 400, 800);
        LoadWords(memOff, 800, 0xDEADBEEF);
        LoadWords(memOn, 800, 0xDEADBEEF);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.Equal(0L, Counter(onResult, "stt_load_issue_stalls"));
        Assert.Equal(Counter(offResult, "cycles"), Counter(onResult, "cycles"));
    }

    /// <summary>
    ///     Direct JAL (<c>ToothClass.Branch</c>) sits in the same <see cref="SpectreVisibilityTracker" />
    ///     FIFO as a conditional branch — dispatched via the same <c>OnDispatchBranch</c> call — but its
    ///     removal (<c>OnBranchResolved</c>, gated on <c>ExecResult.ResolvedNextPc.HasValue</c> in
    ///     StepComplete) depends on the executor actually setting that field for an unconditional jump,
    ///     not just a conditional one. If it didn't, the tracker would leak: <c>OldestUnresolvedBranchInstrId</c>
    ///     would pin at the JAL forever and every younger tainted load would stall permanently — a
    ///     correctness bug (the run would never finish issuing) far worse than a timing-only defense
    ///     should ever cause. <c>jal x0, 4</c> targets its own fall-through either way, so this is a
    ///     timing-only jump like <see cref="PointerChaseAfterSlowBranch" />'s branch, isolating the
    ///     JAL-resolution question from unrelated control-flow correctness.
    /// </summary>
    [Fact]
    public void DirectJalDoesNotWedgeTheVisibilityTracker() {
        uint[] program = [
            Jal(0, 4),       // jal x0, pc+4 (fall-through either way) — must still resolve in StepComplete
            Addi(1, 0, 400), // x1 = 400
            Lw(2, 1, 0),     // x2 = mem[400] = 800
            Lw(4, 2, 0),     // x4 = mem[x2] = mem[800] — tainted, must not wait on the JAL forever
            SttExpOnlyTests.Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 400, 800);
        LoadWords(memOn, 400, 800);
        LoadWords(memOff, 800, 0xDEADBEEF);
        LoadWords(memOn, 800, 0xDEADBEEF);

        const long maxTicks = 10_000;
        RevolutionResult offResult = off.Run(maxTicks);
        RevolutionResult onResult = on.Run(maxTicks);

        Assert.True(offResult.TotalTicks < maxTicks, "baseline run should halt normally on ebreak well under the cap");
        Assert.True(
            onResult.TotalTicks < maxTicks,
            "STT-ExpOnly run never halted (hit maxTicks) — the second load likely never issued " +
            "(visibility tracker wedged by the JAL)"
        );
        AssertIdenticalArchState(off, on);
        Assert.Equal(0xDEADBEEFUL, off.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(0xDEADBEEFUL, on.ArchState.IntegerRegisters.Read(4));
    }
}