#region

using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for InvisiSpec (Yan et al., MICRO 2018, + 2019 Corrigendum) as wired
///     into <see cref="OooTrain" /> via <c>enableInvisiSpec</c>. Every scalar load speculatively
///     peeks its data (<see cref="Mechanism.IMemory.PeekRead" />, no cache-state mutation) at
///     Execute; its real access (expose or validate, per the TSO rule in the paper's Table 1) is
///     deferred to its own Spectre-model visibility point (<c>Pipeline.Ooo.SpectreVisibilityTracker</c>,
///     shared with STT-ExpOnly) and gates retirement (<c>RobEntry.PendingUslAccess</c>) — never the
///     register value reaching dependents, per the corrigendum's fix.
///     <para>
///         The central benefit this defense buys — a wrong-path (squashed) load never pollutes the
///         real cache, unlike this simulator's undefended baseline (confirmed by inspection of
///         <c>CapturingMemory.Read</c> before this feature existed: it forwarded straight to
///         <c>backing.Read</c> unconditionally) — is proven directly by
///         <see cref="SuppressesWrongPathCachePollution_ButBaselinePollutes" />, not merely asserted.
///     </para>
/// </summary>
public class InvisiSpecTests {
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

    private static OooTrain Make(FlatMemory mem, bool enableInvisiSpec, MemoryConfig? dMemConfig = null) =>
        new(
            new Rv32Mechanism(), mem,
            dMemConfig: dMemConfig,
            enableInvisiSpec: enableInvisiSpec
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

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    /// <summary>
    ///     Program: <c>addi x1,x0,600; addi x5,x0,1; mul x6,x5,x5</c> (x6=1, 3-cycle latency — gives
    ///     the wrong-path load below a real window to execute before the branch resolves and
    ///     squashes it) <c>; bne x6,x0,12</c> (taken — the default <c>AlwaysNotTakenPredictor</c>
    ///     mispredicts it, so the not-taken fall-through below is entirely wrong-path)
    ///     <c>; lw x2,0(x1)</c> (WRONG-PATH — squashed once the branch resolves) <c>; ebreak</c>
    ///     (wrong-path — halts the not-taken fall-through from wandering any further, so this is the
    ///     ONLY wrong-path touch of address 600) — the branch's real (taken) target lands past this,
    ///     on a genuinely fresh <c>ebreak</c> reached only after the squash. Rather than relying on
    ///     the program re-touching address 600 itself (the not-taken fall-through sequentially
    ///     re-executes <em>every</em> later PC before the squash arrives, including any "real path"
    ///     placed nearby, which confounds a same-program re-check — confirmed empirically before this
    ///     test's final form), verification is a direct out-of-band probe: <see cref="OooTrain.DCache" />
    ///     is the same live cache instance the pipeline used, so reading it directly after
    ///     <see cref="OooTrain.Run" /> returns reveals whether the wrong-path load's real access ever
    ///     happened, without the probe itself needing to go through the (now-idle) pipeline.
    /// </summary>
    [Fact]
    public void SuppressesWrongPathCachePollution_ButBaselinePollutes() {
        uint[] program = [
            Addi(1, 0, 600), // x1 = 600
            Addi(5, 0, 1), // x5 = 1
            Mul(6, 5, 5), // x6 = 1 (3-cycle latency; stretches the branch's resolution window)
            Bne(6, 0, 8), // taken (x6 != 0); mispredicts AlwaysNotTaken; target = pc+8, pc=20
            Lw(2, 1, 0), // WRONG-PATH (only touch of address 600 before the squash): x2 = mem[600]
            Ebreak, // pc=20: reached both ways (not-taken falls through here; taken jumps here
            // directly, skipping the load) — never touches address 600 either way.
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        var dMemConfig = new MemoryConfig(1024);
        OooTrain off = Make(memOff, false, dMemConfig);
        OooTrain on = Make(memOn, true, dMemConfig);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 600, 0xCAFEF00D);
        LoadWords(memOn, 600, 0xCAFEF00D);

        off.Run();
        on.Run();

        AssertIdenticalArchState(off, on);
        // x2 never survives — the load that read it was on the wrong path and got squashed.
        Assert.Equal(0UL, off.ArchState.IntegerRegisters.Read(2));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(2));

        // Baseline: the wrong-path load already installed address 600's line for real (Hits==0
        // here just confirms this is genuinely the line's first touch — the interesting bit is
        // whether the probe below now hits).
        Assert.Equal(0L, off.DCache!.Hits);
        Assert.Equal(1L, off.DCache!.Misses);

        // InvisiSpec: the wrong-path load only peeked — nothing installed anything.
        Assert.Equal(0L, on.DCache!.Hits);
        Assert.Equal(0L, on.DCache!.Misses);

        // Direct out-of-band probe of the same address, after the pipeline is idle: baseline
        // already installed the line (a real hit), InvisiSpec never touched the cache at all for
        // this address (a genuine miss) — this is the measurable "no wrong-path cache reuse" gap
        // InvisiSpec's speculative buffer exists to close.
        Assert.True(off.DCache!.Read(600, 4) is 0xCAFEF00D && off.DCache!.LastAccessWasHit);
        Assert.True(on.DCache!.Read(600, 4) is 0xCAFEF00D && !on.DCache!.LastAccessWasHit);
    }

    /// <summary>
    ///     Two back-to-back loads with nothing else between them: the first has no older load/fence
    ///     in the ROB at its own Execute time (an exposure); by the time the second executes, the
    ///     first is still in the ROB pending its own deferred access (InvisiSpec retirement gate),
    ///     so the second is classified as needing validation. A timing-only branch precedes both
    ///     loads (depends on a 3-cycle <c>mul</c>; its offset targets its own fall-through, so it
    ///     never actually mispredicts or squashes anything — its only role is to keep the shared
    ///     visibility point unresolved for a few cycles) so neither load's deferred access can fire
    ///     — and, critically, the first load cannot retire — before the second load has already
    ///     executed and been classified. Without this, the first load's real access and retirement
    ///     (nothing else gates it) can complete before the second even executes, clearing its ROB
    ///     entry and making the classification scan find no older load at all — confirmed
    ///     empirically as the actual failure mode before this test's final form.
    /// </summary>
    [Fact]
    public void ClassifiesFirstLoadAsExposureAndSecondAsValidation() {
        uint[] program = [
            Addi(1, 0, 100), // x1 = 100
            Addi(2, 0, 200), // x2 = 200
            Addi(7, 0, 1), // x7 = 1
            Mul(6, 7, 7), // x6 = 1 (3-cycle latency; delays the branch below)
            Bne(6, 0, 4), // taken (x6 != 0); targets its own fall-through (pc+4) — timing-only
            Lw(3, 1, 0), // first load — no older load/fence: exposure
            Lw(4, 2, 0), // second load — an older load (the first) is still in the ROB: validation
            Ebreak,
        ];

        var mem = new FlatMemory(4096);
        OooTrain train = Make(mem, true, new MemoryConfig(1024));
        LoadWords(mem, 0, program);
        LoadWords(mem, 100, 111);
        LoadWords(mem, 200, 222);

        RevolutionResult result = train.Run();

        Assert.Equal(1L, Counter(result, "invisispec_exposures"));
        Assert.Equal(1L, Counter(result, "invisispec_validations"));
    }

    /// <summary>
    ///     Three independent cold loads to distinct cache lines, no dependency and no branch between
    ///     them, so every visibility-point check trivially reports "safe" — this isolates InvisiSpec's
    ///     own overhead (the deferred real access) from any branch-resolution delay. Baseline overlaps
    ///     the three misses: <c>StepExecute</c> gives each load its own in-flight countdown entry, and
    ///     independent countdowns tick in parallel, so total added latency is roughly one
    ///     <c>CacheMissLatency</c>, not three. InvisiSpec's deferred accesses instead go through
    ///     <c>StepUslResolution</c>'s direct <c>DLayers.Accessor.Read</c> call, whose miss cost lands in
    ///     the cache's lump-sum stall accumulator (the same path stores use, off the load critical
    ///     path) rather than a per-load in-flight countdown — so when multiple deferred accesses
    ///     resolve in the same cycle, their miss latencies are charged additively, not overlapped. This
    ///     is a real cost of the current implementation (measurably more cycles than baseline) but the
    ///     magnitude is an implementation artifact of reusing the lump-sum stall path for what should be
    ///     a per-load latency — see the caveat in <c>README.md</c>'s InvisiSpec section and the matching
    ///     TODO.md follow-up. This test asserts only the direction (InvisiSpec costs more here) and that
    ///     all three loads were resolved as USLs, not a specific cycle count or expose/validate split
    ///     (which load in the trio is still "pending" when the next one executes is a timing detail,
    ///     not the property under test).
    /// </summary>
    [Fact]
    public void SurvivingIndependentLoadsCostMoreCyclesThanBaseline() {
        uint[] program = [
            Lw(1, 0, 512), // mem[512] — cold miss
            Lw(2, 0, 576), // mem[576] — distinct line, cold miss
            Lw(3, 0, 640), // mem[640] — distinct line, cold miss
            Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        var dMemConfig = new MemoryConfig(1024);
        OooTrain off = Make(memOff, false, dMemConfig);
        OooTrain on = Make(memOn, true, dMemConfig);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 512, 1);
        LoadWords(memOff, 576, 2);
        LoadWords(memOff, 640, 3);
        LoadWords(memOn, 512, 1);
        LoadWords(memOn, 576, 2);
        LoadWords(memOn, 640, 3);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(3L, Counter(onResult, "invisispec_exposures") + Counter(onResult, "invisispec_validations"));
        Assert.True(
            Counter(onResult, "cycles") > Counter(offResult, "cycles"),
            $"expected InvisiSpec ({Counter(onResult, "cycles")} cycles) to cost more than "
          + $"baseline ({Counter(offResult, "cycles")} cycles) on independent surviving misses"
        );
    }
}
