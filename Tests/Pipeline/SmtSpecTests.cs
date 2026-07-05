using Orrery.Train;
using Pipeline;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

namespace Tests.Pipeline;

public class SmtSpecTests {
    private static byte[] Encode(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    private static FlatMemory MakeMemory(params uint[] program) {
        var mem = new FlatMemory(0x400);
        mem.Load(0, Encode(program));
        return mem;
    }

    private static RevolutionResult Run(ISteppableTrain train, long maxTicks = 10_000) {
        train.BeginStepping();
        long ticks = 0;
        while (ticks++ < maxTicks && train.StepCycle()) { }

        return train.FinishStepping();
    }

    // ── Base contract (single-hart via PipelineSpec.Build) ─────────────────────

    [Fact]
    public void SmtSpec_SingleHart_BaseContract_ProducesCorrectResult() {
        // addi x1, x0, 42 / sw x1, 128(x0) / ebreak
        FlatMemory mem = MakeMemory(0x02a00093, 0x08102023, 0x00100073);
        ISteppableTrain train = new SmtSpec().Build(new Rv32Mechanism(), mem);
        Run(train);
        Assert.Equal(42uL, mem.Read(128, 4));
    }

    [Fact]
    public void SmtSpec_IsAssignableAs_PipelineSpec() {
        PipelineSpec spec = new SmtSpec();
        Assert.IsType<SmtSpec>(spec);
    }

    // ── N-hart build ──────────────────────────────────────────────────────────

    [Fact]
    public void SmtSpec_TwoHarts_BothProduceCorrectResult() {
        FlatMemory mem0 = MakeMemory(0x02a00093, 0x00100073); // addi x1, x0, 42; ebreak
        FlatMemory mem1 = MakeMemory(0x06300093, 0x00100073); // addi x1, x0, 99; ebreak

        SmtTrain smt = new SmtSpec().Build(
            [new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem0, mem1,]
        );
        smt.Run(1_000);

        Assert.Equal(42uL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(99uL, smt.StateOf(1).IntegerRegisters.Read(1));
    }

    [Fact]
    public void SmtSpec_ThreeHarts_AllProduceCorrectResult() {
        FlatMemory mem0 = MakeMemory(0x00a00093, 0x00100073); // addi x1, x0, 10
        FlatMemory mem1 = MakeMemory(0x01400093, 0x00100073); // addi x1, x0, 20
        FlatMemory mem2 = MakeMemory(0x01e00093, 0x00100073); // addi x1, x0, 30

        SmtTrain smt = new SmtSpec(3).Build(
            [new Rv32Mechanism(), new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem0, mem1, mem2,]
        );
        smt.Run(1_000);

        Assert.Equal(10uL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(20uL, smt.StateOf(1).IntegerRegisters.Read(1));
        Assert.Equal(30uL, smt.StateOf(2).IntegerRegisters.Read(1));
    }

    [Fact]
    public void SmtSpec_NHartBuild_HartCountMatchesInput() {
        FlatMemory mem = MakeMemory(0x00100073);
        SmtTrain smt = new SmtSpec().Build(
            [new Rv32Mechanism(), new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem, mem, mem,]
        );
        Assert.Equal(3, smt.HartCount);
    }

    [Fact]
    public void SmtSpec_WithEntryPoints_EachHartStartsAtCorrectAddress() {
        // Two programs at address 0 and 8.
        // Address 0: addi x1, x0, 11; ebreak
        // Address 8: addi x1, x0, 22; ebreak
        var mem = new FlatMemory(0x400);
        mem.Load(
            0, Encode(
                0x00b00093, 0x00100073, // hart 0: addi x1,x0,11; ebreak
                0x01600093, 0x00100073
            )
        ); // hart 1: addi x1,x0,22; ebreak

        SmtTrain smt = new SmtSpec().Build(
            [new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem, mem,],
            [0uL, 8uL,]
        );
        smt.Run(1_000);

        Assert.Equal(11uL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(22uL, smt.StateOf(1).IntegerRegisters.Read(1));
    }
}