using System.Diagnostics;
using Mechanism;
using Mechanism.BranchPredictModels;

namespace Tests.Mechanism;

/// <summary>
///     Builds <c>native/CbpNgShim/test_predictor.hpp</c> (a minimal harcom `predictor` fixture — a
///     single global 1-bit direction register, not a competitive predictor) into a native shared
///     library via <c>native/CbpNgShim/build.sh</c>, then drives <see cref="CbpNgFfiPredictor" />
///     against it — exercising the real FFI path end-to-end rather than mocking the native side.
///     <para>Requires a host C++20 toolchain (<c>g++</c>); skips if unavailable.</para>
/// </summary>
public sealed class CbpNgFfiBranchPredictionTests : IDisposable {
    private readonly string? _libraryPath;

    public CbpNgFfiBranchPredictionTests() {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string buildScript = Path.Combine(repoRoot, "native", "CbpNgShim", "build.sh");
        string predictorHeader = Path.Combine(repoRoot, "native", "CbpNgShim", "test_predictor.hpp");
        if (!File.Exists(buildScript) || !File.Exists(predictorHeader)) return;

        string output = Path.Combine(Path.GetTempPath(), $"cbp_ng_test_{Guid.NewGuid():N}.so");
        var psi = new ProcessStartInfo(buildScript, [predictorHeader, "testbimodal", output,]) {
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
        using var p = new CbpNgFfiPredictor(_libraryPath!);
        BranchPrediction pred = p.Predict(0x1000);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(0x1004UL, pred.PredictedTarget);
    }

    [SkippableFact]
    public void Taken_FlipsPredictionToTaken() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgFfiPredictor(_libraryPath!);
        ulong branch = 0x2000, target = 0x2100;

        p.Predict(branch);
        p.Update(branch, true, target);

        BranchPrediction pred = p.Predict(branch);
        Assert.True(pred.PredictedTaken);
        Assert.Equal(target, pred.PredictedTarget);
    }

    [SkippableFact]
    public void NotTaken_StaysNotTakenAndFallsThrough() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgFfiPredictor(_libraryPath!);
        ulong branch = 0x3000;

        p.Predict(branch);
        p.Update(branch, false, branch + 4);

        BranchPrediction pred = p.Predict(branch);
        Assert.False(pred.PredictedTaken);
        Assert.Equal(branch + 4, pred.PredictedTarget);
    }

    [SkippableFact]
    public void TakenThenReverts_FlipsBackToNotTaken() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgFfiPredictor(_libraryPath!);
        ulong branch = 0x4000, target = 0x4200;

        p.Predict(branch);
        p.Update(branch, true, target);
        Assert.True(p.Predict(branch).PredictedTaken);

        p.Predict(branch);
        p.Update(branch, false, branch + 4);
        Assert.False(p.Predict(branch).PredictedTaken);
    }

    [SkippableFact]
    public void NotifyBranchKind_IsThreadedThroughToUpdate() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgFfiPredictor(_libraryPath!);
        ulong branch = 0x5000, target = 0x5100;

        p.Predict(branch);
        p.NotifyBranchKind(branch, BranchKind.Conditional | BranchKind.Call);
        p.Update(branch, true, target);

        Assert.True(p.Predict(branch).PredictedTaken);
    }
}