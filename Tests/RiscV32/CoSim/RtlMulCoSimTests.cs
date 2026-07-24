#region

using System.Diagnostics;
using System.Security.Cryptography;
using Mechanism;
using Mechanism.RtlFu;
using Pipeline;
using RiscV32;
using RiscV32.Execute;
using RiscV32.Memory;
using Tests.Mechanism;

#endregion

namespace Tests.RiscV32.CoSim;

/// <summary>
///     Builds <c>native/RtlFu/generated/MulUnit.sv</c> into a native shared library via
///     <c>build.sh</c> (the standard FU shim — a fully pipelined unit is just
///     constantly ready under the same port contract), cached by input-content hash.
///     Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlMulLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "MulUnit.sv");
        string shim = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_fu_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(), $"horologium_rtl_mul_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "MulUnit", cached,]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Co-simulation of the verilated pipelined Chisel multiplier against
///     <see cref="Rv32Executor" />'s C# multiply model: bit-identical results across all
///     four RV32M multiply ops, constant 3-cycle pipeline latency, and — since 3
///     equals the static <c>MulDivLatency</c> default — cycle-identical OoO runs.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlMulCoSimTests {
    /// <summary>mul=0, mulh=1, mulhsu=2, mulhu=3 (funct3); rd=x3, rs1=x1, rs2=x2.</summary>
    private static uint Encode(uint funct3) =>
        0x02000033u | (funct3 << 12) | (3u << 7) | (1u << 15) | (2u << 20);

    [SkippableFact]
    public void DifferentialSweep_RtlMatchesCSharpExecutor_AtConstantLatency() {
        Skip.If(RtlMulLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var mech = new Rv32Mechanism();
        IExecutor plain = mech.Executor;
        using var unit = new RtlFfiFunctionalUnit(RtlMulLibrary.Path);
        var rtl = new RtlBackedExecutor(plain, unit, RvRtlMul.Select);

        IArchState state = mech.CreateArchState();
        var mem = new FlatMemory(64);

        List<uint> operands = [
            0u, 1u, 2u, 3u, 7u, 42u, 100u, 0x7FFFFFFFu, 0x80000000u, 0x80000001u,
            0xFFFFFFFFu, 0xFFFFFF9Cu /* -100 */, 0xAAAAAAAAu, 0x55555555u, 0x00010000u,
        ];
        var rng = new Random(20260718);
        for (var i = 0; i < 25; i++) operands.Add((uint)rng.Next() ^ (uint)(rng.Next() << 16));

        foreach (uint funct3 in (uint[])[0, 1, 2, 3,]) {
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
                Assert.Equal(3, actual.LatencyOverride); // pipeline depth, every op
            }
        }
    }

    [SkippableFact]
    public void OooeTrain_FullMExtension_RtlMulAndDivChained() {
        Skip.If(
            RtlMulLibrary.Path is null || RtlDivLibrary.Path is null,
            "verilator toolchain unavailable — skipping."
        );

        // x3 = (6 * 7) computed by RTL mul; x4 = x3 / 5 by RTL div; x5 = x3 % 5.
        uint[] words = [
            0x00600093, // addi x1, x0, 6
            0x00700113, // addi x2, x0, 7
            Encode(0),  // mul  x3, x1, x2       → 42
            0x00500293, // addi x5, x0, 5
            0x0251C233, // div  x4, x3, x5       → 8
            0x0251E333, // rem  x6, x3, x5       → 2
            0x00100073, // ebreak
        ];
        var mem = new FlatMemory(4096);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(0, bytes);

        var mech = new Rv32Mechanism();
        using var divUnit = new RtlFfiFunctionalUnit(RtlDivLibrary.Path);
        using var mulUnit = new RtlFfiFunctionalUnit(RtlMulLibrary.Path);
        mech.Executor = new RtlBackedExecutor(mech.Executor, divUnit, RvRtlDiv.Select);
        mech.Executor = new RtlBackedExecutor(mech.Executor, mulUnit, RvRtlMul.Select);

        var train = new OooTrain(mech, mem);
        train.Run();

        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(8u, (uint)train.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(2u, (uint)train.ArchState.IntegerRegisters.Read(6));
    }

    [SkippableFact]
    public void OooeTrain_MulProgram_RtlLatencyMatchesStaticDefault() {
        Skip.If(RtlMulLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        // The pipeline reports 3 cycles per multiply operation — exactly FuLatencyConfig's default
        // MulDivLatency — so an RTL-mul run must be cycle-identical to the static model.
        uint[] words = [
            0x00600093, // addi x1, x0, 6
            0x00700113, // addi x2, x0, 7
            Encode(0),  // mul  x3, x1, x2
            Encode(1),  // mulh x3, x1, x2 (overwrites; keeps the port busy)
            Encode(3),  // mulhu x3, x1, x2
            0x00100073, // ebreak
        ];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);

        Assert.Equal(Run(false), Run(true));
        return;

        long Run(bool rtl) {
            var mem = new FlatMemory(4096);
            mem.Load(0, bytes);
            var mech = new Rv32Mechanism();
            if (rtl)
                mech.Executor = new RtlBackedExecutor(
                    mech.Executor, new RtlFfiFunctionalUnit(RtlMulLibrary.Path!), RvRtlMul.Select
                );
            return new OooTrain(mech, mem).Run().Find("ooo.pipeline")!.Counters["cycles"];
        }
    }
}