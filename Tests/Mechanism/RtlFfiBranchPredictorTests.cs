#region

using System.Diagnostics;
using System.Security.Cryptography;
using Mechanism;
using Mechanism.BranchPredictModels;
using Mechanism.RtlFu;

#endregion

namespace Tests.Mechanism;

/// <summary>
///     Builds <c>native/RtlFu/generated/GshareBp.sv</c> into a native shared library via
///     <c>build.sh ... rtl_bp_shim.cpp</c>, cached by input-content hash like
///     <see cref="RtlDivLibrary" />. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlBpLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "GshareBp.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_bp_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_gshare_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "GshareBp", cached, "rtl_bp_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiBranchPredictor" /> against the verilated Chisel gshare.
///     The RTL module mirrors <see cref="GsharePredictor" /> bit-for-bit, so beyond basic
///     train/predict behavior the differential test demands identical predictions —
///     direction and target — on an arbitrary branch stream.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiBranchPredictorTests {
    private static RtlFfiBranchPredictor Open() {
        Skip.If(RtlBpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        return new RtlFfiBranchPredictor(RtlBpLibrary.Path);
    }

    [SkippableFact]
    public void ColdMiss_PredictsFallThrough() {
        using RtlFfiBranchPredictor p = Open();
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [SkippableFact]
    public void RepeatedTaken_TrainsToPredictTakenWithTarget() {
        using RtlFfiBranchPredictor p = Open();
        const ulong branch = 0x2000, target = 0x2100;
        // Repeated same-direction updates saturate every (pc ^ ghr) entry the loop touches.
        for (var i = 0; i < 20; i++) {
            p.Predict(branch);
            p.Update(branch, true, target);
        }

        BranchPrediction pred = p.Predict(branch);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(target, pred.PredictedTarget);
    }

    [SkippableFact]
    public void DifferentialStream_MatchesCSharpGshare() {
        Skip.If(RtlBpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiBranchPredictor(RtlBpLibrary.Path);
        var reference = new GsharePredictor(); // historyBits = 8 = the Chisel default

        // Branch pool with mixed biases: always-taken, always-not, loop-periodic,
        // and pattern-based, over more iterations than the tables have entries.
        var rng = new Random(20260717);
        ulong[] pcs = [.. Enumerable.Range(0, 24).Select(i => 0x1000UL + (ulong)(i * 4)),];
        for (var i = 0; i < 4000; i++) {
            ulong pc = pcs[rng.Next(pcs.Length)];
            bool actual = ((pc >> 2) % 3) switch {
                0 => true,
                1 => i % 5 != 0,
                _ => rng.Next(4) == 0,
            };
            ulong target = pc + 0x40;

            BranchPrediction expected = reference.Predict(pc);
            BranchPrediction got = rtl.Predict(pc);
            Assert.True(
                expected.PredictedTaken == got.PredictedTaken
             && expected.PredictedTarget == got.PredictedTarget,
                $"step {i} pc=0x{pc:X}: C#=({expected.PredictedTaken},0x{expected.PredictedTarget:X}) "
              + $"RTL=({got.PredictedTaken},0x{got.PredictedTarget:X})"
            );

            reference.Update(pc, actual, target);
            rtl.Update(pc, actual, target);
        }
    }
}