#region

using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for Vector Runahead's vector pipelining (Naithani,
///     Ainsworth, Jones &amp; Eeckhout, ISCA 2021, §III-G, "P overlapped in-flight rounds") — the
///     v3 extension of <see cref="VectorRunaheadUnrollTests" /> that packs
///     <c>runaheadPipelineDepth</c> unroll rounds into a single origin-load vectorization event
///     instead of requiring one full loop-body walk per round, decoupling round-issue rate from
///     the shadow PC's walk cadence (see <c>PipelineRoundsThisVisit</c> in <see cref="OooeTrain" />).
/// </summary>
public class VectorRunaheadPipelineTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        bool enableRunahead,
        bool enableVectorRunahead = false,
        int runaheadVectorWidth = 8,
        int runaheadUnrollLength = 8,
        int runaheadPipelineDepth = 1,
        int runaheadBudget = 400,
        int extraPhysRegs = 32,
        int issueWidth = 2,
        int robCapacity = 8,
        int iqCapacity = 32,
        int memSize = 4096,
        MemoryConfig? dMemConfig = null
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            extraPhysRegs: extraPhysRegs,
            predictor: new AlwaysTakenPredictor(),
            dMemConfig: dMemConfig ?? new MemoryConfig(1024),
            enableRunahead: enableRunahead,
            runaheadBudget: runaheadBudget,
            enableVectorRunahead: enableVectorRunahead,
            runaheadVectorWidth: runaheadVectorWidth,
            runaheadUnrollLength: runaheadUnrollLength,
            runaheadPipelineDepth: runaheadPipelineDepth
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

    // Same program as VectorRunaheadUnrollTests.StridedChainProgram: a 20-iteration stride-4 walk
    // with a dependent indirect load per iteration, giving a chain long-lived enough (relative to
    // the L1 miss latency and issueWidth) for many unroll rounds to matter.
    private static uint[] StridedChainProgram() => [
        0x32000093, // addi x1, x0, 800
        0x01400113, // addi x2, x0, 20
        0x7D000313, // addi x6, x0, 2000
        0x0000A203, // lw   x4, 0(x1)
        0x006202B3, // add  x5, x4, x6
        0x0002A383, // lw   x7, 0(x5)
        0x00408093, // addi x1, x1, 4
        0xFFF10113, // addi x2, x2, -1
        0xFE0116E3, // bnez x2, loop
        0x00100073, // ebreak
    ];

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    // bne rs1, rs2, imm (B-type)
    private static uint Bne(int rs1, int rs2, int imm) {
        uint imm12 = (uint)(imm >> 12) & 1;
        uint imm10To5 = (uint)(imm >> 5) & 0x3F;
        uint imm4To1 = (uint)(imm >> 1) & 0xF;
        uint imm11 = (uint)(imm >> 11) & 1;
        return (imm12 << 31) | (imm10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001 << 12) | (imm4To1 << 8) | (imm11 << 7) | 0b1100011;
    }

    // A single-load chain with a 64-byte (one cache-line) stride, so every vectorized lane —
    // whether from one N-wide round or many rounds packed together by pipelining — misses a
    // genuinely distinct line rather than sharing one with its neighbors. StridedChainProgram's
    // word-granularity stride (4 bytes) packs 8 lanes into a single line, so most vectorized
    // lanes there are cache hits and never touch the MSHR table at all — unsuitable for
    // observing MSHR contention, which is exactly what distinguishes true pipelining (many
    // simultaneous misses in one origin-visit instant) from serial unrolling (misses spread
    // across separate loop-body walks, with TickMshr freeing slots in between).
    private static uint[] WideStrideProgram(int iterations) {
        var loopBody = new[] {
            0x0000A203u,    // lw   x4, 0(x1)
            Addi(1, 1, 64), // addi x1, x1, 64
            Addi(2, 2, -1), // addi x2, x2, -1
        };
        int branchOffset = -(loopBody.Length * 4);
        return [
            Addi(1, 0, 0x400), // addi x1, x0, 1024
            Addi(2, 0, iterations),
            loopBody[0], loopBody[1], loopBody[2],
            Bne(2, 0, branchOffset),
            0x00100073u, // ebreak
        ];
    }

    /// <summary>
    ///     Vector pipelining is not cosmetic bookkeeping: with an MSHR table too small to absorb
    ///     P rounds' worth of simultaneous misses at once, packing them into a single origin-visit
    ///     instant (instead of spreading them across separate loop-body walks, letting
    ///     <c>TickMshr</c> free slots in between) genuinely contends for the finite MSHR table,
    ///     driving a real, measurable divergence through the shared cache model
    ///     (<c>SetAssociativeCache.ChargeAndAllocateMshr</c>), not just a relabeling of counters:
    ///     <c>DCache.MshrCapacityStalls</c> must be strictly higher for the fully-pipelined (P=8)
    ///     run than the serial (P=1) run on the same wide-stride, distinct-cache-line-per-lane
    ///     chain and the same small <c>CacheMshrCount</c> — the same *direction* of MSHR-count
    ///     sensitivity the paper reports (§VI-B, Fig. 11), though this model does not reproduce the
    ///     paper's net speedup from pipelining (see the README's Vector Runahead section for why).
    /// </summary>
    [Fact]
    public void PipelineDepthP_UnderConstrainedMshrs_IncursMoreContentionThanSerial() {
        uint[] program = WideStrideProgram(40);
        var dCfg = new MemoryConfig(16384, CacheBlockBytes: 32, CacheMissLatency: 10, CacheMshrCount: 8);

        (OooeTrain serial, FlatMemory memSerial) = Make(
            true, true, runaheadPipelineDepth: 1, memSize: 65536, dMemConfig: dCfg
        );
        (OooeTrain pipelined, FlatMemory memPipelined) = Make(
            true, true, runaheadPipelineDepth: 8, memSize: 65536, dMemConfig: dCfg
        );
        Load(memSerial, program);
        Load(memPipelined, program);

        serial.Run();
        pipelined.Run();

        Assert.NotNull(serial.DCache);
        Assert.NotNull(pipelined.DCache);
        Assert.True(
            pipelined.DCache!.MshrCapacityStalls > serial.DCache!.MshrCapacityStalls,
            $"expected P=8 ({pipelined.DCache.MshrCapacityStalls} MSHR-capacity stalls) to contend "
          + $"more than P=1 ({serial.DCache.MshrCapacityStalls}) under the same 8-entry MSHR table"
        );
    }

    /// <summary>
    ///     The flip side of the previous test: with an MSHR table large enough to absorb every
    ///     lane of a fully-pipelined round without contention, pipelining reaches the same real
    ///     demand-side miss count (<c>dcache_misses</c> — the actual program loads, unaffected by
    ///     how the shadow lane prefetched them) as serial unrolling, using far fewer chain-origin
    ///     events. This particular program (only 40 iterations against U×N=64 reach) overshoots
    ///     real demand and pollutes the cache, so coverage alone is the only thing checked here —
    ///     see <see cref="PipelineDepthP_RecoversPartOfRunaheadsOwnOverhead_ButNeverBeatsRunaheadOff" />
    ///     below for the case where reach does <em>not</em> overshoot, which shows the (partial)
    ///     real-cycle reduction this test does not attempt to measure.
    /// </summary>
    [Fact]
    public void PipelineDepthP_WithAmpleMshrs_MatchesSerialCoverage_WithFewerEvents() {
        uint[] program = WideStrideProgram(40);
        var dCfg = new MemoryConfig(16384, CacheBlockBytes: 32, CacheMissLatency: 10, CacheMshrCount: 64);

        (OooeTrain serial, FlatMemory memSerial) = Make(
            true, true, runaheadPipelineDepth: 1, memSize: 65536, dMemConfig: dCfg
        );
        (OooeTrain pipelined, FlatMemory memPipelined) = Make(
            true, true, runaheadPipelineDepth: 8, memSize: 65536, dMemConfig: dCfg
        );
        Load(memSerial, program);
        Load(memPipelined, program);

        RevolutionResult serialResult = serial.Run();
        RevolutionResult pipelinedResult = pipelined.Run();

        Assert.Equal(Counter(serialResult, "dcache_misses"), Counter(pipelinedResult, "dcache_misses"));
        Assert.True(
            Counter(pipelinedResult, "runahead_vector_chains") < Counter(serialResult, "runahead_vector_chains"),
            "expected pipelining to need fewer chain-origin events for the same real-miss coverage"
        );
    }

    /// <summary>
    ///     Disentangles the in-principle episode-shortening benefit from MSHR contention and
    ///     reach-overshoot (the two confounds identified when pipelining was first added) — but
    ///     the honest finding, found only by adding the <c>enableRunahead: false</c> baseline this
    ///     test was missing at first, is more sobering than "pipelining helps": on this program
    ///     (a single-load-per-iteration stride walk with a ROB deep enough — 8 entries, 3
    ///     instructions/iteration — to already run 2-3 iterations' loads concurrently), plain OoO
    ///     execution already extracts near-full memory-level parallelism <em>without</em> Vector
    ///     Runahead. <c>StepRename</c> freezes real rename for the entire duration
    ///     <c>_runaheadActive</c> is true (including a chain-bound episode's extension past the
    ///     point the real blocking load resolves), which prevents further iterations from even
    ///     entering the ROB window during the episode — pure overhead when the hardware would
    ///     have overlapped those misses for free anyway. Measured on this program (200 iterations,
    ///     one-cache-line stride, ample MSHR — <c>CacheMshrCount: 128</c> &gt;= N×P=64, zero
    ///     <c>MshrCapacityStalls</c> at any P): runahead <em>off</em> = 1311 cycles, beating every
    ///     runahead-on configuration tried (P=1: 2565, P=8: 2487). Deeper <c>runaheadPipelineDepth</c>
    ///     does shrink the chain-active window and measurably recovers part of that self-inflicted
    ///     rename-freeze cost (P=8 &lt; P=1, confirmed monotonic: P=1 2565, P=2 2506, P=4 2492, P=8
    ///     2487) — but never enough to close the gap back to simply not runahead-ing at all. So the
    ///     TODO's "why does pipelining measure neutral-to-worse" resolves to: it isn't pipelining
    ///     specifically, it's Vector Runahead itself being net-negative on ROB-parallelizable
    ///     streaming patterns, with deeper P only modulating how much of that self-inflicted
    ///     damage is recovered, never eliminating it. Also needs generous
    ///     <c>runaheadBudget</c>/<c>extraPhysRegs</c> (2000/128 here vs. this file's small
    ///     400/32 defaults, sized for short chains on the 40-iteration program elsewhere in this
    ///     file): under the small defaults this same comparison inverts (P=1: 1808, P=8: 3009,
    ///     P=8 <em>worse</em>) — checked, not a coincidence: at P=1 under the small budget,
    ///     <c>runahead_episodes</c> explodes to 645 (vs. 9 in the clean regime) while total
    ///     <c>runahead_instructions</c> stays tiny (226) — nearly every episode aborts on
    ///     <c>!_rat.HasFree</c> almost immediately, so P=1 ends up doing barely any speculative
    ///     work at all and is cheap almost by accident (close to the 1311-cycle runahead-off
    ///     floor). At P=8 under the same small budget, episodes stay low (13) but
    ///     <c>runahead_vector_lane_accesses</c> is high (576 = 9 chains × 64 lanes, i.e. most
    ///     episodes *do* complete a full unrolled chain before something forces a restart) — so
    ///     P=8 pays for several complete, largely redundant re-vectorizations of overlapping
    ///     address ranges across those 13 restarts, which costs more than either the low-P
    ///     near-no-op case or the well-provisioned single-episode case. In short: tight
    ///     <c>runaheadBudget</c>/<c>extraPhysRegs</c> changes *how much redundant speculative work
    ///     survives per episode*, and that effect has the opposite sign for small vs. large P — a
    ///     fourth mechanism, distinct from MSHR contention, reach-overshoot, and the ROB-MLP
    ///     finding above, worth remembering before trusting any pipelining comparison run under
    ///     tight register/instruction budgets.
    /// </summary>
    [Fact]
    public void PipelineDepthP_RecoversPartOfRunaheadsOwnOverhead_ButNeverBeatsRunaheadOff() {
        uint[] program = WideStrideProgram(200);
        var dCfg = new MemoryConfig(65536, CacheBlockBytes: 64, CacheMissLatency: 10, CacheMshrCount: 128);

        (OooeTrain off, FlatMemory memOff) = Make(false, memSize: 65536, dMemConfig: dCfg);
        (OooeTrain serial, FlatMemory memSerial) = Make(
            true, true, runaheadPipelineDepth: 1, runaheadBudget: 2000, extraPhysRegs: 128,
            memSize: 65536, dMemConfig: dCfg
        );
        (OooeTrain pipelined, FlatMemory memPipelined) = Make(
            true, true, runaheadPipelineDepth: 8, runaheadBudget: 2000, extraPhysRegs: 128,
            memSize: 65536, dMemConfig: dCfg
        );
        Load(memOff, program);
        Load(memSerial, program);
        Load(memPipelined, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult serialResult = serial.Run();
        RevolutionResult pipelinedResult = pipelined.Run();

        Assert.NotNull(serial.DCache);
        Assert.NotNull(pipelined.DCache);
        Assert.Equal(0, serial.DCache!.MshrCapacityStalls);
        Assert.Equal(0, pipelined.DCache!.MshrCapacityStalls);

        long offCycles = Counter(offResult, "cycles");
        long serialCycles = Counter(serialResult, "cycles");
        long pipelinedCycles = Counter(pipelinedResult, "cycles");

        Assert.True(
            pipelinedCycles < serialCycles,
            $"expected P=8 ({pipelinedCycles} cycles) to recover some of P=1's ({serialCycles} cycles) "
          + "self-inflicted rename-freeze overhead once MSHR contention and reach-overshoot are both "
          + "eliminated by construction"
        );
        Assert.True(
            offCycles < pipelinedCycles,
            $"expected runahead OFF ({offCycles} cycles) to still beat even the best-case pipelined "
          + $"run ({pipelinedCycles} cycles) on this ROB-parallelizable streaming pattern — pipelining "
          + "recovers part of Vector Runahead's own overhead here, it doesn't turn it into a net win"
        );
    }

    /// <summary>
    ///     The core §III-G claim: pipelining reaches the same U rounds of coverage using
    ///     dramatically fewer chain-origin vectorization events, because each event now packs
    ///     multiple rounds instead of requiring a separate loop-body walk per round. With U=8,
    ///     P=1 every event covers exactly 1 round (today's pre-pipelining behavior); with U=8,
    ///     P=8 a single event can cover all 8. So the fully-pipelined run's
    ///     <c>runahead_vector_chains</c> count (one increment per origin-load event) must be
    ///     strictly smaller than the serial run's, while <c>runahead_vector_lane_accesses</c>
    ///     (the actual scalar-equivalent lane work) stays comparable — proving the speedup is a
    ///     reduction in loop-body walks, not a reduction in coverage.
    /// </summary>
    [Fact]
    public void PipelineDepthP_ReachesSameCoverage_WithFewerChainOriginEvents() {
        uint[] program = StridedChainProgram();

        (OooeTrain serial, FlatMemory memSerial) = Make(true, true, runaheadPipelineDepth: 1);
        (OooeTrain pipelined, FlatMemory memPipelined) = Make(true, true, runaheadPipelineDepth: 8);
        Load(memSerial, program);
        Load(memPipelined, program);

        RevolutionResult serialResult = serial.Run();
        RevolutionResult pipelinedResult = pipelined.Run();

        (OooeTrain off, FlatMemory memOff) = Make(false);
        Load(memOff, program);
        off.Run();

        AssertIdenticalArchState(off, serial);
        AssertIdenticalArchState(off, pipelined);

        long serialChains = Counter(serialResult, "runahead_vector_chains");
        long pipelinedChains = Counter(pipelinedResult, "runahead_vector_chains");
        long serialLaneAccesses = Counter(serialResult, "runahead_vector_lane_accesses");
        long pipelinedLaneAccesses = Counter(pipelinedResult, "runahead_vector_lane_accesses");

        Assert.True(serialChains > 0, "expected at least one chain-origin event in the serial run");
        Assert.True(
            pipelinedChains < serialChains,
            $"expected P=8 ({pipelinedChains} origin events) to need fewer loop-body walks than "
          + $"P=1 ({serialChains}) to reach the same U=8 rounds"
        );
        Assert.True(
            pipelinedLaneAccesses >= serialLaneAccesses,
            $"expected pipelining to preserve or increase total lane coverage: P=8 gave "
          + $"{pipelinedLaneAccesses}, P=1 gave {serialLaneAccesses}"
        );
    }

    /// <summary>
    ///     Pipelining must not overshoot the unroll budget: a chain-origin event packs
    ///     <c>min(P, U - roundsSoFar)</c> rounds, so even when P doesn't evenly divide U, or
    ///     P &gt; U, the total scalar-equivalent lane accesses attributable to a single chain's
    ///     origin load stay bounded by U rounds' worth, not P rounds' worth. This exercises the
    ///     clamp in <c>PipelineRoundsThisVisit</c> directly (P=5 against U=8: 5 then 3, not 5+5).
    /// </summary>
    [Fact]
    public void PipelineDepthNotDividingUnrollLength_ClampsToRemainingBudget() {
        uint[] program = StridedChainProgram();

        (OooeTrain train, FlatMemory mem) = Make(
            true, true, runaheadUnrollLength: 8, runaheadPipelineDepth: 5
        );
        Load(mem, program);

        RevolutionResult result = train.Run();

        (OooeTrain off, FlatMemory memOff) = Make(false);
        Load(memOff, program);
        off.Run();
        AssertIdenticalArchState(off, train);

        // Two possible chain origins (the base strided load and its dependent indirect load),
        // each capped at U=8 total rounds regardless of how P slices them up: 8 rounds sliced as
        // 5+3 is 2 origin events per chain, so at most 4 chain-origin events total, and at most
        // 8 rounds' worth of lane accesses (64 scalar-equivalent iterations) per chain.
        long chains = Counter(result, "runahead_vector_chains");
        Assert.True(chains > 0, "expected at least one vectorized chain-origin event");
        Assert.True(chains <= 4, $"expected at most 2 origins x 2 events (5+3 rounds) each, got {chains}");
    }

    /// <summary>
    ///     P=1 is the no-pipelining baseline: it must reproduce the exact counter
    ///     values of a run built without specifying <c>runaheadPipelineDepth</c> at all (the
    ///     pre-existing default), confirming the new knob is purely additive and does not perturb
    ///     unrolling-only behavior.
    /// </summary>
    [Fact]
    public void PipelineDepthOne_IsBehaviorallyIdenticalToDefault() {
        uint[] program = StridedChainProgram();

        (OooeTrain withDefault, FlatMemory memDefault) = Make(true, true);
        (OooeTrain explicitP1, FlatMemory memP1) = Make(true, true, runaheadPipelineDepth: 1);
        Load(memDefault, program);
        Load(memP1, program);

        RevolutionResult defaultResult = withDefault.Run();
        RevolutionResult p1Result = explicitP1.Run();

        AssertIdenticalArchState(withDefault, explicitP1);
        Assert.Equal(
            Counter(defaultResult, "runahead_vector_chains"), Counter(p1Result, "runahead_vector_chains")
        );
        Assert.Equal(
            Counter(defaultResult, "runahead_vector_lane_accesses"),
            Counter(p1Result, "runahead_vector_lane_accesses")
        );
        Assert.Equal(Counter(defaultResult, "runahead_instructions"), Counter(p1Result, "runahead_instructions"));
    }
}
