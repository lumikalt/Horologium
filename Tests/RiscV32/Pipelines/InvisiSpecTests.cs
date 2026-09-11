#region

using Mechanism;
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
///     peeks its data (<see cref="IMemory.PeekRead" />, no cache-state mutation) at
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
    private const uint Ebreak = 0x00100073;

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
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

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
            Addi(1, 0, 600),        // x1 = 600
            Addi(5, 0, 1),          // x5 = 1
            Mul(6, 5, 5),           // x6 = 1 (3-cycle latency; stretches the branch's resolution window)
            Bne(6, 0, 8),           // taken (x6 != 0); mispredicts AlwaysNotTaken; target = pc+8, pc=20
            Lw(2, 1, 0),            // WRONG-PATH (only touch of address 600 before the squash): x2 = mem[600]
            InvisiSpecTests.Ebreak, // pc=20: reached both ways (not-taken falls through here; taken jumps here
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
            Addi(7, 0, 1),   // x7 = 1
            Mul(6, 7, 7),    // x6 = 1 (3-cycle latency; delays the branch below)
            Bne(6, 0, 4),    // taken (x6 != 0); targets its own fall-through (pc+4) — timing-only
            Lw(3, 1, 0),     // first load — no older load/fence: exposure
            Lw(4, 2, 0),     // second load — an older load (the first) is still in the ROB: validation
            InvisiSpecTests.Ebreak,
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
    ///     them, so every visibility-point check trivially reports "safe" the instant it's checked —
    ///     this isolates InvisiSpec's own overhead (the deferred real access) from any
    ///     branch-resolution delay. <c>StepUslResolution</c>'s deferred real access now drains through
    ///     its own per-entry countdown (<c>_pendingUslLatency</c>), the same MLP-overlapped shape
    ///     ordinary load misses get from <c>StepExecute</c>'s <c>_inFlight</c>, instead of the cache's
    ///     lump-sum stall accumulator stores use — so with no visibility delay in the way, InvisiSpec's
    ///     cycle count converges to baseline's rather than paying each miss additively. This is the
    ///     discriminating regression test for that recalibration: reverting the fix (routing the
    ///     deferred access back through the lump-sum path) reintroduces additive charging and this
    ///     assertion fails with InvisiSpec's cycles measurably higher than baseline's — confirmed
    ///     directly before finalizing this test.
    /// </summary>
    [Fact]
    public void SurvivingIndependentLoadsCostSameCyclesAsBaseline_MlpOverlapped() {
        uint[] program = [
            Lw(1, 0, 512), // mem[512] — cold miss
            Lw(2, 0, 576), // mem[576] — distinct line, cold miss
            Lw(3, 0, 640), // mem[640] — distinct line, cold miss
            InvisiSpecTests.Ebreak,
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
        Assert.Equal(Counter(offResult, "cycles"), Counter(onResult, "cycles"));
    }

    /// <summary>
    ///     InvisiSpec's genuine cost driver, isolated from the lump-sum artifact fixed above: a slow
    ///     (3x chained-<c>mul</c>, 9-cycle) branch precedes a cold USL, holding its own real access
    ///     (and therefore its retirement — <c>RobEntry.PendingUslAccess</c>) pending until the branch's
    ///     visibility point clears; the branch targets its own fall-through so it never mispredicts —
    ///     this isolates the retirement-delay cost from any squash. The USL's speculative peek pays the
    ///     same miss latency an ordinary load would (see <c>PeekRead</c>'s docs) and broadcasts its
    ///     value once that resolves, so eight independent filler <c>addi</c>s dispatched after it
    ///     complete and sit ready to retire long before the USL does — but in-order commit cannot pass
    ///     the still-pending USL at the ROB head, and the trailing <c>ebreak</c> cannot itself retire
    ///     (the self-loop halt condition) until every older instruction, including the USL, has.
    ///     Baseline has no such gate: the same load retires as soon as its data is ready, so the filler
    ///     and <c>ebreak</c> follow immediately behind it. Confirmed to fail (InvisiSpec no longer
    ///     costs more) if the deferred access is made to fire unconditionally at Execute instead of
    ///     waiting for <c>_vpTracker.IsSafe</c>.
    /// </summary>
    [Fact]
    public void RealAccessDeferredPastVisibilityPointCostsMoreThanBaseline() {
        uint[] program = [
            Addi(5, 0, 1), // x5 = 1
            Mul(6, 5, 5),  // x6 = 1
            Mul(6, 6, 6),  // still 1 (3x chained mul: ~9-cycle resolution window for the branch below)
            Mul(6, 6, 6),  // still 1
            Bne(6, 0, 4),  // taken (x6 != 0); targets its own fall-through (pc+4) — timing-only,
            // never mispredicts, but stays unresolved in the visibility tracker for the mul chain's
            // full latency, holding the USL below pending for that whole window.
            Lw(1, 0, 512), // USL: cold miss, deferred real access held by the branch above
            Addi(10, 0, 1), Addi(11, 0, 1), Addi(12, 0, 1), Addi(13, 0, 1), // 8 independent fillers:
            Addi(14, 0, 1), Addi(15, 0, 1), Addi(16, 0, 1), Addi(17, 0, 1), // complete immediately,
            // but cannot retire ahead of the still-pending USL at the ROB head.
            InvisiSpecTests.Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        var dMemConfig = new MemoryConfig(1024);
        OooTrain off = Make(memOff, false, dMemConfig);
        OooTrain on = Make(memOn, true, dMemConfig);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 512, 1);
        LoadWords(memOn, 512, 1);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(1L, Counter(onResult, "invisispec_exposures") + Counter(onResult, "invisispec_validations"));
        Assert.True(
            Counter(onResult, "cycles") > Counter(offResult, "cycles"),
            $"expected InvisiSpec ({Counter(onResult, "cycles")} cycles) to cost more than "
          + $"baseline ({Counter(offResult, "cycles")} cycles) when the deferred access is held past "
          + "a slow branch's visibility point"
        );
    }

    /// <summary>
    ///     Regression test for the (now-fixed) unphysical InvisiSpec-cheaper-than-baseline artifact:
    ///     a 2-hop dependent pointer chase (<c>lw x2,0(x1); lw x3,0(x2)</c>, no branch, so the shared
    ///     visibility point is trivially safe throughout) with both hops cold misses on distinct
    ///     lines. Before <c>PeekRead</c> charged the same miss latency an ordinary access would,
    ///     the speculative peek was free, so each hop's data became available at hit-latency
    ///     regardless of residency — on a chain where baseline pays each hop's miss latency serially
    ///     on the critical path, that made InvisiSpec measure <em>cheaper</em> than an undefended
    ///     baseline (confirmed by direct measurement before this fix: 28 cycles off vs. 18 on), which
    ///     is unphysical (a defense must never look free, let alone negative-cost). Asserts InvisiSpec
    ///     costs at least as much as baseline, not exact equality: with no visibility delay in the way
    ///     the two should now converge, but the property that actually matters here is "never
    ///     cheaper." Confirmed to fail (InvisiSpec measures cheaper) if <c>PeekRead</c>'s miss-latency
    ///     charge is reverted.
    /// </summary>
    [Fact]
    public void DependentLoadChainNeverCostsLessThanBaseline() {
        uint[] program = [
            Lw(2, 0, 512), // x2 = mem[512] (cold miss)
            Lw(3, 2, 0),   // x3 = mem[x2] (depends on x2; distinct line, cold miss)
            InvisiSpecTests.Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        var dMemConfig = new MemoryConfig(1024);
        OooTrain off = Make(memOff, false, dMemConfig);
        OooTrain on = Make(memOn, true, dMemConfig);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);
        LoadWords(memOff, 512, 800); // x2 -> 800
        LoadWords(memOn, 512, 800);
        LoadWords(memOff, 800, 0xDEAD);
        LoadWords(memOn, 800, 0xDEAD);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(2L, Counter(onResult, "invisispec_exposures") + Counter(onResult, "invisispec_validations"));
        Assert.True(
            Counter(onResult, "cycles") >= Counter(offResult, "cycles"),
            $"expected InvisiSpec ({Counter(onResult, "cycles")} cycles) to never cost less than "
          + $"baseline ({Counter(offResult, "cycles")} cycles) on a dependent load chain"
        );
    }

    /// <summary>
    ///     The optional Per-Core LLC-SB extension (Yan et al., MICRO 2018, §VI-C): reuses
    ///     <see cref="RealAccessDeferredPastVisibilityPointCostsMoreThanBaseline" />'s own program (a
    ///     single cold USL held pending by a slow, never-mispredicting branch) to isolate the SB's one
    ///     effect — its speculative peek already recorded address 512's line in the buffer well before
    ///     the branch clears; without the SB, the deferred real access that finally fires at
    ///     retirement is a genuine second miss (the base design's own accepted double payment, see
    ///     <see cref="SetAssociativeCache.PeekRead" />'s docs); with it enabled, that same deferred
    ///     access finds its own peek-time entry still resident (nothing evicted or squashed it) and is
    ///     charged the cheap buffer-hit latency instead. Confirmed to fail (no cycle difference, zero
    ///     <c>invisispec_llc_sb_hits</c>) if the LLC-SB's line-base lookup in <c>StepUslResolution</c>
    ///     is disabled.
    /// </summary>
    [Fact]
    public void LlcSbAvoidsPayingMissLatencyTwiceForTheSameUsl() {
        uint[] program = [
            Addi(5, 0, 1), // x5 = 1
            Mul(6, 5, 5),  // x6 = 1
            Mul(6, 6, 6),  // still 1 (3x chained mul: ~9-cycle resolution window for the branch below)
            Mul(6, 6, 6),  // still 1
            Bne(6, 0, 4),  // taken (x6 != 0); targets its own fall-through (pc+4) — timing-only,
            // never mispredicts, but stays unresolved in the visibility tracker for the mul chain's
            // full latency, holding the USL below pending for that whole window.
            Lw(1, 0, 512), // USL: cold miss at peek; its deferred real access is the SB's one chance
            // to avoid paying a second miss for the exact same line.
            InvisiSpecTests.Ebreak,
        ];

        var memNoSb = new FlatMemory(4096);
        var memSb = new FlatMemory(4096);
        var dMemConfig = new MemoryConfig(1024);
        var noSb = new OooTrain(
            new Rv32Mechanism(), memNoSb, dMemConfig: dMemConfig,
            enableInvisiSpec: true, enableInvisiSpecLlcSb: false
        );
        var sb = new OooTrain(
            new Rv32Mechanism(), memSb, dMemConfig: dMemConfig,
            enableInvisiSpec: true, enableInvisiSpecLlcSb: true
        );
        LoadWords(memNoSb, 0, program);
        LoadWords(memSb, 0, program);
        LoadWords(memNoSb, 512, 1);
        LoadWords(memSb, 512, 1);

        RevolutionResult noSbResult = noSb.Run();
        RevolutionResult sbResult = sb.Run();

        AssertIdenticalArchState(noSb, sb);
        Assert.Equal(0L, Counter(noSbResult, "invisispec_llc_sb_hits"));
        Assert.Equal(1L, Counter(sbResult, "invisispec_llc_sb_hits"));
        Assert.True(
            Counter(sbResult, "cycles") < Counter(noSbResult, "cycles"),
            $"expected the LLC-SB ({Counter(sbResult, "cycles")} cycles) to cost less than without it "
          + $"({Counter(noSbResult, "cycles")} cycles) by avoiding a second miss on the same line"
        );
    }
}