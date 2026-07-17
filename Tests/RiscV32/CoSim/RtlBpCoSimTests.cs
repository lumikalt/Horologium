using Mechanism.RtlFu;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using Tests.Mechanism;

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
        using var predictor = new RtlFfiBranchPredictor(RtlBpLibrary.Path!);

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
}
