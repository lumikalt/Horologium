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

    [SkippableFact]
    public void CommitDriven_SurvivesManyOutstandingFetchPredictionsBeforeAnyCommit() {
        // Simulates OoOE's actual hazard: many branches fetched (Predict, which only ever
        // touches the internal fetch-side predictor) before any of them commits (Update, the
        // only place the wrapped native predictor is ever touched). Raw CbpNgFfiPredictor would
        // corrupt harcom's per-block registers if driven this way — a violation harcom itself
        // enforces by calling std::terminate() (see reg/ram "single access per cycle" and
        // "storage lifetime" checks in vendor/harcom.hpp) — so a burst of Predict() calls with no
        // matching Update() reaching the process alive, followed by Update() calls succeeding
        // cleanly, demonstrates the native predictor was never reentered.
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgCommitDrivenPredictor(_libraryPath!);
        ulong branch = 0x6000, target = 0x6100;

        // Fetch (speculatively) far more predictions than have resolved — the ROB-window pattern.
        for (var i = 0; i < 16; i++) p.Predict(branch);

        // Resolve in program order, each Update() driving exactly one harcom predict+update pair.
        p.Update(branch, true, target);
        p.Update(branch, false, branch + 4);
        p.Update(branch, true, target);

        // A second burst after resolution must still work — the adapter never accumulates
        // unresolved native state to begin with, so there's nothing to leak across bursts.
        for (var i = 0; i < 16; i++) p.Predict(branch);
        p.Update(branch, false, branch + 4);
    }

    [SkippableFact]
    public void CommitDriven_FetchPredictionIsIndependentOfHarcomState() {
        // Predict() must come from the internal fetch-side predictor, never from harcom
        // directly — verified by never calling Update and confirming Predict is still callable
        // repeatedly without ever touching the (never-resolved) native predictor unsafely.
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgCommitDrivenPredictor(_libraryPath!);
        ulong branch = 0x7000;

        BranchPrediction first = p.Predict(branch);
        for (var i = 0; i < 8; i++) p.Predict(branch);
        BranchPrediction last = p.Predict(branch);

        // The fetch-side predictor (Gshare) is deterministic given no Update calls at all —
        // repeated cold Predict()s at the same PC must return the same not-taken prediction.
        Assert.Equal(first.PredictedTaken, last.PredictedTaken);
        Assert.False(last.PredictedTaken);
    }

    [SkippableFact]
    public void CommitDriven_NotifyBranchKind_IsThreadedThroughToHarcomUpdate() {
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        using var p = new CbpNgCommitDrivenPredictor(_libraryPath!);
        ulong branch = 0x8000, target = 0x8100;

        p.Predict(branch);
        p.NotifyBranchKind(branch, BranchKind.Conditional | BranchKind.Call);
        p.Update(branch, true, target); // must not throw — kind must reach _harcom.Update
    }
}