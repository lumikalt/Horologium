using Mechanism;
using Mechanism.RtlFu;
using Pipeline;
using RiscV32;
using RiscV32.Execute;
using RiscV32.Memory;
using Tests.Mechanism;

namespace Tests.RiscV32.CoSim;

/// <summary>
///     Co-simulation of the verilated Chisel DivUnit against <see cref="Rv32Executor" />'s
///     C# divide model: the same instruction stream runs through both, and results must
///     be bit-identical. Also verifies the RTL unit's data-dependent cycle count actually
///     reaches the OoO pipeline as the div instruction's FU latency
///     (<see cref="ExecuteResult.LatencyOverride" />).
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlDivCoSimTests {
    /// <summary>div=4, divu=5, rem=6, remu=7 (funct3); rd=x3, rs1=x1, rs2=x2.</summary>
    private static uint Encode(uint funct3) =>
        0x02000033u | (funct3 << 12) | (3u << 7) | (1u << 15) | (2u << 20);

    [SkippableFact]
    public void DifferentialSweep_RtlMatchesCSharpExecutor() {
        Skip.If(RtlDivLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var mech = new Rv32Mechanism();
        IExecutor plain = mech.Executor;
        using var unit = new RtlFfiFunctionalUnit(RtlDivLibrary.Path);
        var rtl = new RtlBackedExecutor(plain, unit, RvRtlDiv.Select);

        IArchState state = mech.CreateArchState();
        var mem = new FlatMemory(64);

        List<uint> operands = [
            0u, 1u, 2u, 3u, 7u, 42u, 100u, 0x7FFFFFFFu, 0x80000000u, 0x80000001u,
            0xFFFFFFFFu, 0xFFFFFF9Cu /* -100 */, 0xAAAAAAAAu, 0x55555555u, 0x00010000u,
        ];
        var rng = new Random(20260717);
        for (var i = 0; i < 25; i++) operands.Add((uint)rng.Next() ^ (uint)(rng.Next() << 16));

        foreach (uint funct3 in (uint[])[4, 5, 6, 7,]) {
            ITooth tooth = mech.Decoder.Decode(0, Encode(funct3));
            foreach (uint a in operands)
            foreach (uint b in operands) {
                state.IntegerRegisters.Write(1, a);
                state.IntegerRegisters.Write(2, b);
                ExecuteResult expected = plain.Execute(tooth, state, mem);
                ExecuteResult actual = rtl.Execute(tooth, state, mem);
                Assert.True(
                    expected.RegisterResult == actual.RegisterResult,
                    $"f3={funct3} a=0x{a:X8} b=0x{b:X8}: "
                  + $"C#=0x{expected.RegisterResult.Value:X8} RTL=0x{actual.RegisterResult.Value:X8}"
                );
                Assert.InRange(actual.LatencyOverride!.Value, 1, 33);
            }
        }
    }

    [SkippableFact]
    public void NonDivInstructions_FallThroughToWrappedExecutor() {
        Skip.If(RtlDivLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var mech = new Rv32Mechanism();
        using var unit = new RtlFfiFunctionalUnit(RtlDivLibrary.Path);
        var rtl = new RtlBackedExecutor(mech.Executor, unit, RvRtlDiv.Select);

        IArchState state = mech.CreateArchState();
        state.IntegerRegisters.Write(1, 40);
        state.IntegerRegisters.Write(2, 2);
        // add x3, x1, x2 — and mul, which shares ToothClass.IntegerMulDiv with div but
        // must not be claimed by the selector.
        ExecuteResult add = rtl.Execute(mech.Decoder.Decode(0, 0x002081B3), state, new FlatMemory(64));
        ExecuteResult mul = rtl.Execute(mech.Decoder.Decode(0, 0x022081B3), state, new FlatMemory(64));
        Assert.Equal((42ul, true), add.RegisterResult);
        Assert.Null(add.LatencyOverride);
        Assert.Equal((80ul, true), mul.RegisterResult);
        Assert.Null(mul.LatencyOverride);
    }

    // ── Pipeline integration: RTL latency drives OoO timing ────────────────────

    private static (uint x3, long cycles) RunDivProgram(uint dividendImm12) {
        var mech = new Rv32Mechanism();
        using var unit = new RtlFfiFunctionalUnit(RtlDivLibrary.Path!);
        mech.Executor = new RtlBackedExecutor(mech.Executor, unit, RvRtlDiv.Select);

        var mem = new FlatMemory(4096);
        uint[] words = [
            0x00000093u | (dividendImm12 << 20), // addi x1, x0, imm (sign-extended)
            0x00200113u,                         // addi x2, x0, 2
            Encode(5),                           // divu x3, x1, x2
            0x00100073u,                         // ebreak
        ];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(0, bytes);

        var train = new OooeTrain(mech, mem);
        long cycles = train.Run().Find("ooo.pipeline")!.Counters["cycles"];
        return ((uint)train.ArchState.IntegerRegisters.Read(3), cycles);
    }

    [SkippableFact]
    public void OooeTrain_UsesRtlCycleCount_AsFuLatency() {
        Skip.If(RtlDivLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        // Same program shape, different dividend magnitude: 3/2 completes in 3 RTL
        // cycles, 0xFFFFFFFF/2 (addi sign-extends -1) needs the full 33. With the
        // static FuLatencyConfig both would cost identical MulDivLatency cycles, so
        // any cycle-count gap can only come from LatencyOverride reaching StepExecute.
        (uint smallQ, long smallCycles) = RunDivProgram(3);
        (uint bigQ, long bigCycles) = RunDivProgram(0xFFF); // imm -1 → x1 = 0xFFFFFFFF
        Assert.Equal(1u, smallQ);
        Assert.Equal(0x7FFFFFFFu, bigQ);
        Assert.True(
            bigCycles >= smallCycles + 25,
            $"Expected ~30-cycle gap between big ({bigCycles}) and small ({smallCycles}) dividends"
        );
    }
}
