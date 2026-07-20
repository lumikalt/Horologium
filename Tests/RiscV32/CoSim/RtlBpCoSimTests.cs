#region

using Mechanism;
using Mechanism.BranchPred;
using Mechanism.RtlFu;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using Tests.Mechanism;

#endregion

namespace Tests.RiscV32.CoSim;

/// <summary>
///     Runs the verilated Chisel gshare as the live branch predictor of an
///     <see cref="OooeTrain" />: speculative fetch is steered by RTL predictions
///     (mispredictions squash and redirect as usual), and commit-time updates train the
///     RTL tables. Architectural results must be unaffected by prediction quality.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlBpCoSimTests {
    [SkippableFact]
    public void OooeTrain_LoopProgram_CorrectWithRtlPredictor() {
        Skip.If(RtlBpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var predictor = new RtlFfiBp(RtlBpLibrary.Path);

        var mem = new FlatMemory(4096);
        uint[] words = [
            0x00500093, // addi x1, x0, 5
            0xFFF08093, // loop: addi x1, x1, -1
            0xFE009EE3, // bne  x1, x0, loop (-4)
            0x02A00193, // addi x3, x0, 42
            0x00100073, // ebreak
        ];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(0, bytes);

        var train = new OooeTrain(new Rv32Mechanism(), mem, predictor: predictor);
        train.Run();

        Assert.Equal(0u, (uint)train.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(3));
    }

    [SkippableFact]
    public void OooeTrain_NestedLoops_RtlTageMatchesCSharpLTageCycleForCycle() {
        Skip.If(RtlTageLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        // Nested loops give the speculative-history machinery real work: inner-loop
        // back-edges mispredict at every exit, driving capture/partial-squash/recover.
        // The RTL L-TAGE mirrors the C# LTageBp bit-for-bit, and OooeTrain drives
        // both through the same call sites — so cycles and results must match exactly.
        uint[] words = [
            0x00600093, // addi x1, x0, 6        (outer counter)
            0x00000113, // addi x2, x0, 0        (accumulator)
            0x00400193, // outer: addi x3, x0, 4 (inner counter)
            0x00110113, // inner: addi x2, x2, 1
            0xFFF18193, // addi x3, x3, -1
            0xFE019CE3, // bne  x3, x0, inner (-8)
            0xFFF08093, // addi x1, x1, -1
            0xFE0096E3, // bne  x1, x0, outer (-20)
            0x00100073, // ebreak
        ];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);

        long Run(Func<IBranchPredictor> predictor, out uint x2) {
            var mem = new FlatMemory(4096);
            mem.Load(0, bytes);
            var train = new OooeTrain(new Rv32Mechanism(), mem, predictor: predictor());
            long cycles = train.Run().Find("ooo.pipeline")!.Counters["cycles"];
            x2 = (uint)train.ArchState.IntegerRegisters.Read(2);
            return cycles;
        }

        long csCycles = Run(() => new LTageBp(), out uint csX2);
        long rtlCycles = Run(() => new RtlFfiHistoryBp(RtlTageLibrary.Path), out uint rtlX2);

        Assert.Equal(24u, csX2); // 6 × 4 inner iterations
        Assert.Equal(csX2, rtlX2);
        Assert.Equal(csCycles, rtlCycles);
    }
}