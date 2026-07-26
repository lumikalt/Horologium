#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     <see cref="MultiHartLoopPointProfiler" /> against small, hand-assembled multi-hart RV32I
///     programs with known instruction-by-instruction behavior, so region-boundary timing,
///     per-thread normalization, and namespacing can all be checked against exactly-predicted values
///     rather than just "it produced something".
/// </summary>
public class MultiHartLoopPointProfilerTests {
    private const uint AddiX1X1Plus1 = 0x00108093; // addi x1, x1, 1
    private const long NormalizationScale = 1_000_000;

    private static uint Jal(int rd, int immOffset) {
        var imm = (uint)immOffset;
        uint bit20 = (imm >> 20) & 1;
        uint bits10_1 = (imm >> 1) & 0x3FF;
        uint bit11 = (imm >> 11) & 1;
        uint bits19_12 = (imm >> 12) & 0xFF;
        return (bit20 << 31) | (bits10_1 << 21) | (bit11 << 20) | (bits19_12 << 12) | ((uint)rd << 7) | 0b1101111u;
    }

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    [Fact]
    public void TwoIdenticalHarts_CloseRegionAtNextMarkerAfterGlobalTargetReached_EquallySplit() {
        // Both harts run the same 2-instruction infinite counting loop (addi x1,x1,1; jal x0,-4) at
        // different addresses, entirely in lockstep (proven by the flow-control tests). Every loop
        // iteration commits exactly 2 instructions, both credited to the single block key at the
        // loop's own start address (see BbvProfiler: the jal ends the block every iteration, so a new
        // one immediately reopens at the same PC next commit).
        //
        // Global target = 6. Global instruction count after each tick (2 harts x 1 commit each):
        //   tick1: 2   tick2: 4   tick3: 6 -- reached mid-tick3, but only a *marker* hit can close a
        // region. Hart-local commit #3 (an `addi`, i.e. the start of that hart's own iteration 2) is
        // exactly where each hart's own LoopHeaderTracker registers a marker (the first backward-taken
        // re-entry) -- and tick 3 is precisely each hart's own local commit #3. So the region closes
        // during tick 3, at hart 1's marker (hart 0's own marker at the same tick doesn't yet reach the
        // target: global count is 5 there, not 6).
        //
        // At closure each hart has committed exactly 3 instructions this region (iter1's 2 + iter2's
        // in-progress addi) -- all credited to that hart's own loop-start PC. Complete() flushes the
        // in-progress partial block too, so no instructions are lost off either hart's tally.
        var mem = new FlatMemory(0x200);
        mem.Load(0x00, ToBytes(AddiX1X1Plus1, Jal(0, -4)));
        mem.Load(0x40, ToBytes(AddiX1X1Plus1, Jal(0, -4)));

        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
        var kernel = new MultiHartKernel(mem, mech0, mech1);
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        var profiler = new MultiHartLoopPointProfiler([mech0.Decoder, mech1.Decoder,], 0, 0x200, 6);
        kernel.SetObserver(0, profiler.HartObserver(0));
        kernel.SetObserver(1, profiler.HartObserver(1));

        kernel.Step();
        kernel.Step();
        Assert.Empty(profiler.RegionBbvs); // target (6) not yet reached after 2 ticks (4 global instructions)

        kernel.Step(); // tick 3: global count reaches 6 exactly when hart 1's own marker fires
        Assert.Single(profiler.RegionBbvs);

        IReadOnlyDictionary<ulong, long> region0 = profiler.RegionBbvs[0];
        Assert.Equal(2, region0.Count); // one key per hart, no collision
        Assert.Equal(NormalizationScale, region0[NamespaceKey(0, 0x00)]);
        Assert.Equal(NormalizationScale, region0[NamespaceKey(1, 0x40)]); // hart 1's own loop lives at 0x40
    }

    [Fact]
    public void MultipleRegions_EachHartsContributionAlwaysNormalizesToExactlyOneScale() {
        // Same fixture as above, run for enough ticks to close several regions back-to-back. Region
        // *lengths* are not guaranteed equal (a region ends at the next marker after the target is
        // reached, and target vs. marker-spacing alignment can drift — e.g. the very first region
        // above closes mid-instruction, shifting where later regions' basic-block keys fall). What
        // must always hold, regardless of that drift or of a mid-block cut splitting a block's
        // weight across two regions (BbvProfiler.CutInterval's continuation semantics): every
        // active hart's own contribution to every region normalizes to exactly NormalizationScale,
        // and hart 0's own loop-start key (0x00) is not accidentally reused for hart 1's namespaced
        // contribution.
        var mem = new FlatMemory(0x200);
        mem.Load(0x00, ToBytes(AddiX1X1Plus1, Jal(0, -4)));
        mem.Load(0x40, ToBytes(AddiX1X1Plus1, Jal(0, -4)));

        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
        var kernel = new MultiHartKernel(mem, mech0, mech1);
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);

        var profiler = new MultiHartLoopPointProfiler([mech0.Decoder, mech1.Decoder,], 0, 0x200, 6);
        kernel.SetObserver(0, profiler.HartObserver(0));
        kernel.SetObserver(1, profiler.HartObserver(1));

        for (var i = 0; i < 30; i++) kernel.Step();

        Assert.True(profiler.RegionBbvs.Count >= 3);
        foreach (IReadOnlyDictionary<ulong, long> region in profiler.RegionBbvs) {
            long hart0Total = region.Where(kv => (kv.Key >> 48) == 0).Sum(kv => kv.Value);
            long hart1Total = region.Where(kv => (kv.Key >> 48) == 1).Sum(kv => kv.Value);
            Assert.Equal(NormalizationScale, hart0Total);
            Assert.Equal(NormalizationScale, hart1Total);
        }
    }

    [Fact]
    public void ExcludedRangeInstructions_DontCountTowardBbvOrGlobalTarget() {
        // Single hart: a "spin-loop" (backward branch, entirely inside an excluded range) that spins
        // a fixed number of times before falling into real, non-excluded work with its own loop
        // header. If exclusion is respected, (a) the spin PC never appears in the BBV, (b) the global
        // target counter only starts advancing once real work begins, so the region closes at the
        // real loop's marker timing, not skewed earlier by the (uncounted) spin iterations.
        //
        // Layout:
        //   0x00: spin: addi x2, x2, 1        (excluded range: [0x00, 0x08))
        //   0x04: bne  x2, x3, spin            (x3 preset so this taken-branches exactly 4 times)
        //   0x08: real: addi x1, x1, 1        (excluded range ends here; real work loop)
        //   0x0C: jal  x0, -4 (-> 0x08)
        const uint addiX2 = 0x00110113; // addi x2, x2, 1
        uint bneSpin = Bne(2, 3, -4); // bne x2, x3, -4 (-> 0x00)

        var mem = new FlatMemory(0x100);
        mem.Load(0x00, ToBytes(addiX2, bneSpin, AddiX1X1Plus1, Jal(0, -4)));

        var mech = new Rv32Mechanism();
        var kernel = new MultiHartKernel(mem, mech);
        kernel.SetEntryPoint(0, 0x00);
        kernel.StateOf(0).IntegerRegisters.Write(3, 4); // spin runs while x2 != 4 -> 4 taken iterations

        // Target small enough to close on the *first* real-loop marker once spinning is done.
        var profiler = new MultiHartLoopPointProfiler([mech.Decoder,], 0, 0x100, 1, [(0x00UL, 0x08UL),]);
        kernel.SetObserver(0, profiler.HartObserver(0));

        // Run past the spin (4 iterations x 2 instructions = 8 commits) plus a couple of real-loop
        // iterations, well within budget.
        for (var i = 0; i < 20; i++) kernel.Step();

        Assert.NotEmpty(profiler.RegionBbvs);
        IReadOnlyDictionary<ulong, long> region0 = profiler.RegionBbvs[0];
        Assert.DoesNotContain(NamespaceKey(0, 0x00), region0.Keys); // spin PC never enters the BBV
        Assert.Contains(NamespaceKey(0, 0x08), region0.Keys); // real loop's start PC does
    }

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static ulong NamespaceKey(int hartId, ulong pc) => ((ulong)hartId << 48) | pc;
}
