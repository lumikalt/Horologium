#region

using Mechanism;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Config;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Regression coverage for the out-of-order memory-level-parallelism (MLP) load model
///     and the store-side write-buffer MLP model.
///     <para>
///         Load-side MLP: the OoO pipeline gives each missed load its own in-flight latency
///         countdown so independent misses overlap (instead of freezing the clock lump-sum). The
///         danger is that a load now sits in flight for many cycles before it broadcasts its value,
///         which widens the store-to-load memory-disambiguation window: an older store can resolve
///         and commit while the load is still in flight. Load disambiguation state is therefore
///         registered at EXECUTE time (not at CDB-broadcast time) so the in-flight load stays
///         visible to CheckLoadViolations for its whole life.
///     </para>
///     <para>
///         An earlier MLP attempt (reverted commit cb91a56) registered at broadcast time and
///         shipped green against the whole suite + Spike co-sim, because none of those exercise
///         load-miss + store-to-same-address + an L1 cache together. memcpy is store-heavy and
///         copies a buffer it then verifies; on an L1 (so loads actually miss) the broken model
///         read a stale value, jumped through a corrupted return address, and livelocked.
///     </para>
///     <para>
///         Store-side MLP: committed stores write through the cache immediately (write-through /
///         no-write-allocate), but their write-miss penalty is absorbed into a bounded write buffer
///         rather than lump-summed against the pipeline clock. Subsequent instructions keep
///         executing while the write bus drains in the background. The write buffer is enabled by
///         passing writeBufferCapacity > 0 to OooTrain; the default (0) restores the lump-sum path.
///     </para>
///     <para>
///         MSHR capacity: limits the number of simultaneously outstanding load-miss countdowns in
///         _inFlight. When all slots are occupied, loads and atomics are held in the IQ until a
///         slot frees. This models finite miss-status-holding registers and is the mechanism that
///         prevents unbounded load-level parallelism in real hardware.
///     </para>
///     <para>
///         The ground-truth correctness signal is the benchmark's own HTIF exit verdict: memcpy
///         checks the copied buffer and writes PASS (tohost low word == 1) or a FAIL code. This
///         test runs memcpy on an 8-wide OoO with a 16 KB L1 and asserts that PASS — which catches
///         BOTH the livelock (tohost stays 0 → "hit maxTicks") and a terminate-but-wrong stale-load
///         (tohost carries a FAIL code). It is verified to fail on the broken model. A no-cache run
///         is included as a reference: the race cannot arise there (loads never miss), so it isolates
///         the cache path as the variable.
///     </para>
/// </summary>
public class OoOMemoryParallelismTests {
    // memcpy at the "big_core" calibration point (8-wide, ROB 128) — the config the broken
    // MLP model livelocked on. Split 16 KB I/D L1, 10-cycle miss penalty (the harness config).
    private const long MaxTicks = 4_000_000;

    private static (ulong TohostLow, long Retired, long CpuCycles, long WbAbsorbed, long MshrStalls) RunMemcpy(
        bool withL1,
        int writeBufferCapacity = 0,
        int mshrCapacity = 0
    ) {
        var workload = new Rv32ElfWorkload(
            Path.Combine(AppContext.BaseDirectory, "benchmarks", "memcpy.elf"), 4 * 1024 * 1024
        );
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        IMemory runMem = workload.WrapMemory(mem);
        ulong tohost = workload.HtifTohostAddress!.Value;

        CacheHardwareConfig? l1 = withL1 ? new CacheHardwareConfig(16384, 4, 64) : null;
        var cfg = new TrainConfig("ooo", ICache: l1, DCache: l1);
        MemoryConfig iMem = cfg.ToIMemoryConfig();
        // The HTIF tohost/fromhost registers must bypass the cache (HtifMemory ACKs below it).
        MemoryConfig dMem = cfg.ToDMemoryConfig() with { UncacheableBase = tohost, UncacheableSize = 16, };

        var train = new OooTrain(
            new Rv32Mechanism(workload.HtifTohostAddress), runMem, workload.EntryPoint,
            8, 128, 64,
            predictor: BranchPredictorConfig.NBit().Build(),
            iMemConfig: iMem, dMemConfig: dMem,
            writeBufferCapacity: writeBufferCapacity,
            mshrCapacity: mshrCapacity
        );

        RevolutionResult r = train.Run(OoOMemoryParallelismTests.MaxTicks);
        IReadOnlyDictionary<string, long> counters = r.Find("ooo.pipeline")!.Counters;
        long retired = counters["retired"];
        // "cycles" is the simulated CPU cycle count including stall cycles charged via ChargeStallCycles.
        long cycles = counters["cycles"];
        long absorbed = counters.GetValueOrDefault("wb_absorbed_stalls");
        long mshrStalls = counters.GetValueOrDefault("mshr_stalls");
        return (mem.Read(tohost, 4), retired, cycles, absorbed, mshrStalls);
    }

    // tohost low word: 1 = exit(0) PASS, 0 = never halted, else FAIL code (N<<1)|1.
    private static void AssertHtifPass(ulong tohostLow, long ticks, string label) {
        Assert.False(tohostLow == 0, $"{label}: hit maxTicks ({ticks}) without an HTIF exit — livelock");
        Assert.True(
            tohostLow == 1,
            $"{label}: benchmark self-check FAILED (tohost=0x{tohostLow:X}, exit {tohostLow >> 1}) — stale-load corruption"
        );
    }

    [Fact]
    public void OoO_Memcpy_WithL1_SelfChecksPass_AndDoesNotLivelock() {
        (ulong cachedTohost, long cachedRetired, long cachedCycles, _, _) = RunMemcpy(true);
        (ulong refTohost, long refRetired, _, _, _) = RunMemcpy(false);

        // The trusted reference: no cache → loads always hit → the miss/disambiguation race
        // cannot occur. It must self-check PASS.
        AssertHtifPass(refTohost, 0, "memcpy OoO no-cache");

        // The bug-exposing config: the broken MLP model livelocked here (tohost stays 0) or, in
        // a milder corruption, would write a FAIL code. Both are caught by the PASS assertion.
        AssertHtifPass(cachedTohost, cachedCycles, "memcpy OoO + 16 KB L1");

        // Sanity: the L1 run committed a comparable amount of work to the reference (not a few
        // hundred poll iterations). HTIF poll-loop jitter aside, the two stay close.
        Assert.True(refRetired > 10_000, $"memcpy retired implausibly few: {refRetired}");
        Assert.InRange(cachedRetired, refRetired - 2_000, refRetired + 2_000);
    }

    /// <summary>
    ///     Store-side MLP: a bounded write buffer absorbs the write-miss stall for each committed
    ///     store so the pipeline can keep running while the write bus drains. Asserts both
    ///     correctness (HTIF PASS) and performance (fewer CPU cycles than the lump-sum baseline).
    /// </summary>
    [Fact]
    public void OoO_Memcpy_WriteBuffer_ReducesCycles_AndSelfChecksPass() {
        // Baseline: write buffer disabled → store-commit write misses charged lump-sum.
        (ulong baseTohost, _, long baseCycles, long baseAbsorbed, _) = RunMemcpy(true);

        // Write buffer enabled with 16 slots (enough to cover burst commit width of 8).
        (ulong wbTohost, _, long wbCycles, long wbAbsorbed, _) = RunMemcpy(true, 16);

        AssertHtifPass(baseTohost, baseCycles, "memcpy OoO + L1, no write buffer");
        AssertHtifPass(wbTohost, wbCycles, "memcpy OoO + L1, write buffer");

        // The write buffer should have absorbed some miss stalls.
        Assert.True(wbAbsorbed > 0, $"write buffer absorbed no stalls (absorbed={wbAbsorbed})");
        Assert.Equal(0L, baseAbsorbed);

        // The write buffer run should have fewer (or equal) CPU cycles.
        Assert.True(
            wbCycles <= baseCycles,
            $"write buffer did not reduce cycles: wb={wbCycles} >= base={baseCycles}"
        );
    }

    /// <summary>
    ///     MSHR capacity cap: when the cap is set to 1, only one load miss can be outstanding
    ///     at a time. Subsequent loads are held in the IQ until the slot frees.
    ///     <para>
    ///         Correctness: the benchmark must still PASS (the gate is a timing-only resource
    ///         constraint — loads still execute and produce correct values). Performance: the
    ///         constrained run accumulates mshr_stalls > 0 (backpressure was exercised) and takes
    ///         at least as many cycles as the unlimited run (serialising misses can only hurt or be
    ///         neutral vs. overlapping them).
    ///     </para>
    /// </summary>
    [Fact]
    public void OoO_Memcpy_MshrCap_ExercisesBackpressure_AndSelfChecksPass() {
        // Unlimited MSHRs: baseline for cycle comparison.
        (ulong baseTohost, _, long baseCycles, _, long baseMshrStalls) = RunMemcpy(true, mshrCapacity: 0);

        // Cap at 1: at most one load miss in-flight → serialises all misses.
        (ulong capTohost, _, long capCycles, _, long capMshrStalls) = RunMemcpy(true, mshrCapacity: 1);

        AssertHtifPass(baseTohost, baseCycles, "memcpy OoO + L1, unlimited MSHR");
        AssertHtifPass(capTohost, capCycles, "memcpy OoO + L1, MSHR cap 1");

        // Unlimited path must not report any MSHR stalls (counter not even registered).
        Assert.Equal(0L, baseMshrStalls);

        // Constrained path must have exercised the backpressure at least once.
        Assert.True(capMshrStalls > 0, $"MSHR cap did not generate any stalls (capMshrStalls={capMshrStalls})");

        // Serialising misses can only be equal-to or worse than overlapping them.
        Assert.True(
            capCycles >= baseCycles,
            $"MSHR cap mysteriously reduced cycles: cap={capCycles} < base={baseCycles}"
        );
    }

    /// <summary>
    ///     Prefetcher correctness: a next-line prefetcher on the D-cache must not corrupt
    ///     HTIF MMIO registers. The uncacheable guard in MemoryLayers.TryPrefetch prevents
    ///     a prefetch landing on the tohost/fromhost line from re-caching stale ACK values,
    ///     which would re-introduce the stale-fromhost livelock fixed earlier.
    ///     <para>
    ///         The test runs memcpy with a next-line prefetcher, asserts HTIF PASS (correctness)
    ///         and dcache_prefetches > 0 (prefetcher actually fired). It is the MMIO-safety
    ///         counterpart to the base L1 test above.
    ///     </para>
    /// </summary>
    [Fact]
    public void OoO_Memcpy_NextLinePrefetcher_SelfChecksPass_AndPrefetchesFired() {
        var workload = new Rv32ElfWorkload(
            Path.Combine(AppContext.BaseDirectory, "benchmarks", "memcpy.elf"), 4 * 1024 * 1024
        );
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        IMemory runMem = workload.WrapMemory(mem);
        ulong tohost = workload.HtifTohostAddress!.Value;

        var l1 = new CacheHardwareConfig(16384, 4, 64);
        var cfg = new TrainConfig("ooo", ICache: l1, DCache: l1, DPrefetcher: "next_line");
        MemoryConfig iMem = cfg.ToIMemoryConfig();
        MemoryConfig dMem = cfg.ToDMemoryConfig() with { UncacheableBase = tohost, UncacheableSize = 16, };

        var train = new OooTrain(
            new Rv32Mechanism(workload.HtifTohostAddress), runMem, workload.EntryPoint,
            8, 128, 64,
            predictor: BranchPredictorConfig.NBit().Build(),
            iMemConfig: iMem, dMemConfig: dMem
        );

        RevolutionResult r = train.Run(OoOMemoryParallelismTests.MaxTicks);
        IReadOnlyDictionary<string, long> counters = r.Find("ooo.pipeline")!.Counters;
        ulong tohostLow = mem.Read(tohost, 4);

        AssertHtifPass(tohostLow, counters["cycles"], "memcpy OoO + L1 + next-line prefetcher");
        Assert.True(
            counters.GetValueOrDefault("dcache_prefetches") > 0,
            "next-line prefetcher did not fire any prefetches"
        );
    }

    /// <summary>
    ///     Realistic prefetch latency: with DPrefetchLatency > 0 a prefetched line is in
    ///     flight for that many cycles, and a demand access arriving earlier pays the
    ///     remaining countdown instead of zero (the idealized free model).
    ///     <para>
    ///         Correctness: the timing model must not change architectural results — HTIF PASS
    ///         on both runs. Timing: memcpy streams sequentially, so next-line prefetches are
    ///         demanded within a few cycles of being issued; with a 10-cycle prefetch latency
    ///         the run must record late-prefetch hits and take at least as many cycles as the
    ///         free-prefetch run (paying a remainder can only hurt or be neutral).
    ///     </para>
    /// </summary>
    [Fact]
    public void OoO_Memcpy_RealisticPrefetchLatency_SelfChecksPass_AndPaysRemainder() {
        (ulong freeTohost, long freeCycles, long freePrefetches, long freeLateHits) = Run(0);
        (ulong realTohost, long realCycles, long realPrefetches, long realLateHits) = Run(10);

        AssertHtifPass(freeTohost, freeCycles, "memcpy OoO + free prefetcher");
        AssertHtifPass(realTohost, realCycles, "memcpy OoO + 10-cycle prefetch latency");

        Assert.True(freePrefetches > 0 && realPrefetches > 0, "prefetcher did not fire in both runs");

        // The free model never registers the counter; the realistic model must have been
        // caught in flight at least once on a sequential stream.
        Assert.Equal(0L, freeLateHits);
        Assert.True(realLateHits > 0, "no demand access ever caught a prefetch in flight");

        Assert.True(
            realCycles >= freeCycles,
            $"realistic prefetch latency reduced cycles: real={realCycles} < free={freeCycles}"
        );
        return;

        (ulong tohostLow, long cycles, long prefetches, long lateHits) Run(int prefetchLatency) {
            var workload = new Rv32ElfWorkload(
                Path.Combine(AppContext.BaseDirectory, "benchmarks", "memcpy.elf"), 4 * 1024 * 1024
            );
            var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
            workload.Load(mem);
            IMemory runMem = workload.WrapMemory(mem);
            ulong tohost = workload.HtifTohostAddress!.Value;

            var l1 = new CacheHardwareConfig(16384, 4, 64);
            var cfg = new TrainConfig(
                "ooo", ICache: l1, DCache: l1,
                DPrefetcher: "next_line", DPrefetchLatency: prefetchLatency
            );
            MemoryConfig iMem = cfg.ToIMemoryConfig();
            MemoryConfig dMem = cfg.ToDMemoryConfig() with { UncacheableBase = tohost, UncacheableSize = 16, };

            var train = new OooTrain(
                new Rv32Mechanism(workload.HtifTohostAddress), runMem, workload.EntryPoint,
                8, 128, 64,
                predictor: BranchPredictorConfig.NBit().Build(),
                iMemConfig: iMem, dMemConfig: dMem
            );

            RevolutionResult r = train.Run(OoOMemoryParallelismTests.MaxTicks);
            IReadOnlyDictionary<string, long> counters = r.Find("ooo.pipeline")!.Counters;
            return (
                mem.Read(tohost, 4),
                counters["cycles"],
                counters.GetValueOrDefault("dcache_prefetches"),
                counters.GetValueOrDefault("dcache_late_prefetch_hits")
            );
        }
    }
}