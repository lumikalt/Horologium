#region

using System.Diagnostics;
using Mechanism;
using Mechanism.BranchPredictModels;

#endregion

namespace Tests.Mechanism;

/// <summary>
///     Builds <c>native/CbpShim/test_predictor.h</c> (a minimal 2-bit-counter <c>class PREDICTOR</c>
///     fixture) into a native shared library via <c>native/CbpShim/build.sh</c>, then drives
///     <see cref="CbpFfiPredictor" /> against it — exercising the real FFI path end-to-end rather
///     than mocking the native side.
///     <para>Requires a host C++ toolchain (<c>g++</c>); skips if unavailable.</para>
/// </summary>
public sealed class CbpFfiBranchPredictionTests : IDisposable {
    private readonly string? _libraryPath;

    public CbpFfiBranchPredictionTests() {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string buildScript = Path.Combine(repoRoot, "native", "CbpShim", "build.sh");
        string predictorHeader = Path.Combine(repoRoot, "native", "CbpShim", "test_predictor.h");
        if (!File.Exists(buildScript) || !File.Exists(predictorHeader)) return;

        string output = Path.Combine(Path.GetTempPath(), $"cbp_test_{Guid.NewGuid():N}.so");
        var psi = new ProcessStartInfo(buildScript, [predictorHeader, output,]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        if (proc is { ExitCode: 0, } && File.Exists(output)) _libraryPath = output;
    }

    public void Dispose() {
        if (_libraryPath is not null && File.Exists(_libraryPath)) File.Delete(_libraryPath);
    }

    [SkippableFact]
    public void ColdMiss_PredictsFallThrough() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpFfiPredictor(_libraryPath!);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [SkippableFact]
    public void RepeatedTaken_TrainsCounterToPredictTaken() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpFfiPredictor(_libraryPath!);
        ulong branch = 0x2000, target = 0x2100;

        for (var i = 0; i < 3; i++) {
            p.Predict(branch);
            p.Update(branch, true, target);
        }

        BranchPrediction pred = p.Predict(branch);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(target, pred.PredictedTarget);
    }

    [SkippableFact]
    public void RepeatedNotTaken_StaysNotTakenAndFallsThrough() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpFfiPredictor(_libraryPath!);
        ulong branch = 0x3000;

        for (var i = 0; i < 3; i++) {
            p.Predict(branch);
            p.Update(branch, false, branch + 4);
        }

        BranchPrediction pred = p.Predict(branch);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(branch + 4, pred.PredictedTarget);
    }

    [SkippableFact]
    public void TakenThenReverts_CounterSaturatesBackToNotTaken() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpFfiPredictor(_libraryPath!);
        ulong branch = 0x4000, target = 0x4200;

        for (var i = 0; i < 3; i++) {
            p.Predict(branch);
            p.Update(branch, true, target);
        }

        Assert.True(p.Predict(branch).PredictedTaken);

        for (var i = 0; i < 3; i++) {
            p.Predict(branch);
            p.Update(branch, false, branch + 4);
        }

        Assert.False(p.Predict(branch).PredictedTaken);
    }
}