#region

using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable InconsistentNaming

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     LoopPoint's loop-header region-boundary detection (<see cref="LoopHeaderTracker" />) on
///     hand-assembled RV32I programs with known backward-branch structure.
/// </summary>
public class LoopHeaderTrackerTests {
    private const uint BneX1X0Minus12 = 0xFE009AE3;
    private const uint Ebreak = 0x0010_0073;

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

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Jal(int rd, int immOffset) {
        var imm = (uint)immOffset;
        uint bit20 = (imm >> 20) & 1;
        uint bits10_1 = (imm >> 1) & 0x3FF;
        uint bit11 = (imm >> 11) & 1;
        uint bits19_12 = (imm >> 12) & 0xFF;
        return (bit20 << 31) | (bits10_1 << 21) | (bit11 << 20) | (bits19_12 << 12) | ((uint)rd << 7) | 0b1101111u;
    }

    private static uint Jalr(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b1100111);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    [Fact]
    public void TwoLoopProgram_DetectsBothHeadersWithCorrectIterationCounts() {
        // Same fixture as SimPointTests.ProfileTwoLoopProgram: two 4-instruction loops, 600
        // iterations each, back-to-back — loopA's header at 0x04, loopB's at 0x18.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(1, 0, 600),                       // 0x00: addi x1, x0, 600
            Addi(2, 2, 1),                         // 0x04: loopA: addi x2, x2, 1
            Addi(2, 2, 1),                         // 0x08
            Addi(1, 1, -1),                        // 0x0C
            LoopHeaderTrackerTests.BneX1X0Minus12, // 0x10: bne x1, x0, loopA
            Addi(1, 0, 600),                       // 0x14: addi x1, x0, 600
            Addi(3, 3, 3),                         // 0x18: loopB: addi x3, x3, 3
            Addi(3, 3, 3),                         // 0x1C
            Addi(1, 1, -1),                        // 0x20
            LoopHeaderTrackerTests.BneX1X0Minus12, // 0x24: bne x1, x0, loopB
            LoopHeaderTrackerTests.Ebreak          // 0x28
        );

        var mechanism = new Rv32Mechanism();
        var tracker = new LoopHeaderTracker(mechanism.Decoder, 0, 4096);
        new SingleCycleTrain(mechanism, mem, commitObserver: tracker).Run();

        // 599, not 600: the loop header's first visit is a fall-through from the setup code (not a
        // discontinuous transfer, so not observable here) — only the 599 backward-taken re-entries
        // are counted. See LoopHeaderTracker's class doc comment.
        Assert.Equal(599, tracker.HeaderIterationCounts[0x04]);
        Assert.Equal(599, tracker.HeaderIterationCounts[0x18]);
        Assert.Equal(1198, tracker.Markers.Count);

        // Markers are emitted in execution order: all of loopA's before any of loopB's, each
        // header's own count strictly increasing 1..599.
        (ulong Pc, long Count)[] loopAMarkers = [.. tracker.Markers.Take(599),];
        (ulong Pc, long Count)[] loopBMarkers = [.. tracker.Markers.Skip(599),];
        Assert.All(loopAMarkers, m => Assert.Equal(0x04UL, m.Pc));
        Assert.All(loopBMarkers, m => Assert.Equal(0x18UL, m.Pc));
        Assert.Equal(Enumerable.Range(1, 599).Select(i => (long)i), loopAMarkers.Select(m => m.Count));
        Assert.Equal(Enumerable.Range(1, 599).Select(i => (long)i), loopBMarkers.Select(m => m.Count));
    }

    [Fact]
    public void BackwardCall_IsNotCountedAsALoopHeader() {
        // helper (0x00-0x04) is defined before main (0x08-0x0C), so main's call to it is a
        // backward control transfer by address alone — but it's a call (jal ra, ...), not a
        // loop, and must not register a header hit.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(5, 0, 1),                // 0x00: helper: addi x5, x0, 1
            Jalr(0, 1, 0),                // 0x04: helper: jalr x0, 0(ra)  (ret)
            Jal(1, -8),                   // 0x08: main: jal ra, helper (backward call)
            LoopHeaderTrackerTests.Ebreak // 0x0C: main halts after return
        );

        var mechanism = new Rv32Mechanism();
        var tracker = new LoopHeaderTracker(mechanism.Decoder, 0, 4096);
        new SingleCycleTrain(mechanism, mem, 0x08, commitObserver: tracker).Run();

        Assert.Empty(tracker.Markers);
        Assert.Empty(tracker.HeaderIterationCounts);
    }

    [Fact]
    public void BackwardReturn_ToACallSiteBelowAHigherAddressCallee_IsNotCountedAsALoopHeader() {
        // The discriminating case a backward-CALL-only exclusion would miss: helper (0x18) is
        // placed AFTER its caller's loop (link order), so the direct call at 0x04 is forward (never
        // a backward candidate at all) — but the indirect `ret` at 0x1C jumps backward to 0x08,
        // every iteration, which would look identical to a real loop header if only calls were
        // excluded. The genuine back-edge (bne at 0x0C -> 0x04) must still be counted normally.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(6, 0, 100),               // 0x00: main: addi x6, x0, 100
            Jal(1, 0x14),                  // 0x04: loop: jal ra, helper (forward call)
            Addi(6, 6, -1),                // 0x08: addi x6, x6, -1
            Bne(6, 0, -8),                 // 0x0C: bne x6, x0, loop
            LoopHeaderTrackerTests.Ebreak, // 0x10: main halts
            Addi(5, 5, 1),                 // 0x18: helper: addi x5, x5, 1
            Jalr(0, 1, 0)                  // 0x1C: helper: jalr x0, 0(ra) (ret -> 0x08)
        );

        var mechanism = new Rv32Mechanism();
        var tracker = new LoopHeaderTracker(mechanism.Decoder, 0, 4096);
        new SingleCycleTrain(mechanism, mem, commitObserver: tracker).Run();

        // 99, not 100: same fall-through-first-visit accounting as the two-loop test above.
        Assert.Equal(99, tracker.HeaderIterationCounts[0x04]);
        Assert.False(tracker.HeaderIterationCounts.ContainsKey(0x08)); // the ret's landing site — not a header
        Assert.All(tracker.Markers, m => Assert.Equal(0x04UL, m.Pc));
    }

    [Fact]
    public void ExcludedRange_SuppressesThatHeader_ButNotOthersOutsideIt() {
        // Same two-loop fixture as the first test: loopA's header (0x04) falls inside an excluded
        // range (mimicking a synchronization-library function's address span); loopB's (0x18) does
        // not. loopA must produce no markers/counts at all; loopB is unaffected.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(1, 0, 600),                       // 0x00: addi x1, x0, 600
            Addi(2, 2, 1),                         // 0x04: loopA: addi x2, x2, 1
            Addi(2, 2, 1),                         // 0x08
            Addi(1, 1, -1),                        // 0x0C
            LoopHeaderTrackerTests.BneX1X0Minus12, // 0x10: bne x1, x0, loopA
            Addi(1, 0, 600),                       // 0x14: addi x1, x0, 600
            Addi(3, 3, 3),                         // 0x18: loopB: addi x3, x3, 3
            Addi(3, 3, 3),                         // 0x1C
            Addi(1, 1, -1),                        // 0x20
            LoopHeaderTrackerTests.BneX1X0Minus12, // 0x24: bne x1, x0, loopB
            LoopHeaderTrackerTests.Ebreak          // 0x28
        );

        var mechanism = new Rv32Mechanism();
        var tracker = new LoopHeaderTracker(mechanism.Decoder, 0, 4096, [(0x00UL, 0x14UL),]);
        new SingleCycleTrain(mechanism, mem, commitObserver: tracker).Run();

        Assert.False(tracker.HeaderIterationCounts.ContainsKey(0x04));
        Assert.Equal(599, tracker.HeaderIterationCounts[0x18]);
        Assert.All(tracker.Markers, m => Assert.Equal(0x18UL, m.Pc));
    }

    [Fact]
    public void UnconditionalBackwardJump_IsCountedAsALoopHeader() {
        // A "goto"-compiled loop back-edge (jal x0, ...) — an unconditional jump with a
        // discarded destination register, not a call — must still register as a loop header.
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Addi(1, 1, 1), // 0x00: loop: addi x1, x1, 1
            Jal(0, -4)     // 0x04: jal x0, loop (unconditional backward jump)
        );

        var mechanism = new Rv32Mechanism();
        var tracker = new LoopHeaderTracker(mechanism.Decoder, 0, 4096);
        var train = new SingleCycleTrain(mechanism, mem, commitObserver: tracker);
        train.Run(50);

        Assert.True(tracker.HeaderIterationCounts[0x00] >= 20);
    }
}