using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Config;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Regression coverage for the out-of-order memory-level-parallelism (MLP) load model.
///
/// The OoO pipeline gives each missed load its own in-flight latency countdown so
/// independent misses overlap (instead of freezing the clock lump-sum). The danger is
/// that a load now sits in flight for many cycles before it broadcasts its value, which
/// widens the store-to-load memory-disambiguation window: an older store can resolve and
/// commit while the load is still in flight. Load disambiguation state is therefore
/// registered at EXECUTE time (not at CDB-broadcast time) so the in-flight load stays
/// visible to CheckLoadViolations for its whole life.
///
/// An earlier MLP attempt (reverted commit cb91a56) registered at broadcast time and
/// shipped green against the whole suite + Spike co-sim, because none of those exercise
/// load-miss + store-to-same-address + an L1 cache together. memcpy is store-heavy and
/// copies a buffer it then verifies; on an L1 (so loads actually miss) the broken model
/// read a stale value, jumped through a corrupted return address, and livelocked.
///
/// The ground-truth correctness signal is the benchmark's own HTIF exit verdict: memcpy
/// checks the copied buffer and writes PASS (tohost low word == 1) or a FAIL code. This
/// test runs memcpy on an 8-wide OoO with a 16 KB L1 and asserts that PASS — which catches
/// BOTH the livelock (tohost stays 0 → "hit maxTicks") and a terminate-but-wrong stale-load
/// (tohost carries a FAIL code). It is verified to fail on the broken model. A no-cache run
/// is included as a reference: the race cannot arise there (loads never miss), so it isolates
/// the cache path as the variable.
/// </summary>
public class OoOMemoryParallelismTests {
    // memcpy at the "big_core" calibration point (8-wide, ROB 128) — the config the broken
    // MLP model livelocked on. Split 16 KB I/D L1, 10-cycle miss penalty (the harness config).
    private const long MaxTicks = 4_000_000;

    private static (ulong TohostLow, long Retired, long Ticks) RunMemcpy(bool withL1) {
        var workload = new Rv32ElfWorkload(
            Path.Combine(AppContext.BaseDirectory, "benchmarks", "memcpy.elf"), 4 * 1024 * 1024
        );
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        IMemory runMem = workload.WrapMemory(mem);
        ulong tohost = workload.HtifTohostAddress!.Value;

        CacheHardwareConfig? l1 = withL1 ? new CacheHardwareConfig(16384, 4, 64, 10) : null;
        var cfg = new TrainConfig("ooo", ICache: l1, DCache: l1);
        MemoryConfig iMem = cfg.ToIMemoryConfig();
        // The HTIF tohost/fromhost registers must bypass the cache (HtifMemory ACKs below it).
        MemoryConfig dMem = cfg.ToDMemoryConfig() with { UncacheableBase = tohost, UncacheableSize = 16, };

        var train = new OooeTrain(
            new Rv32Mechanism(workload.HtifTohostAddress), runMem, workload.EntryPoint,
            8, 128, 64,
            predictor: BranchPredictorConfig.NBit().Build(),
            iMemConfig: iMem, dMemConfig: dMem
        );

        RevolutionResult r = train.Run(OoOMemoryParallelismTests.MaxTicks);
        long retired = r.Find("ooo.pipeline")!.Counters["retired"];
        return (mem.Read(tohost, 4), retired, r.TotalTicks);
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
        (ulong cachedTohost, long cachedRetired, long cachedTicks) = RunMemcpy(true);
        (ulong refTohost, long refRetired, long refTicks) = RunMemcpy(false);

        // The trusted reference: no cache → loads always hit → the miss/disambiguation race
        // cannot occur. It must self-check PASS.
        AssertHtifPass(refTohost, refTicks, "memcpy OoO no-cache");

        // The bug-exposing config: the broken MLP model livelocked here (tohost stays 0) or, in
        // a milder corruption, would write a FAIL code. Both are caught by the PASS assertion.
        AssertHtifPass(cachedTohost, cachedTicks, "memcpy OoO + 16 KB L1");

        // Sanity: the L1 run committed a comparable amount of work to the reference (not a few
        // hundred poll iterations). HTIF poll-loop jitter aside, the two stay close.
        Assert.True(refRetired > 10_000, $"memcpy retired implausibly few: {refRetired}");
        Assert.InRange(cachedRetired, refRetired - 2_000, refRetired + 2_000);
    }
}