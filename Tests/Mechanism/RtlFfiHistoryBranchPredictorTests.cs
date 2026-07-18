using System.Diagnostics;
using System.Security.Cryptography;
using Mechanism;
using Mechanism.BranchPredictModels;
using Mechanism.RtlFu;

namespace Tests.Mechanism;

/// <summary>
///     Builds <c>native/RtlFu/generated/LTageBp.sv</c> into a native shared library via
///     <c>build.sh ... rtl_hbp_shim.cpp</c>, cached by input-content hash like the other
///     RTL fixtures. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlTageLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "LTageBp.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_hbp_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_ltage_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "LTageBp", cached, "rtl_hbp_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiHistoryBranchPredictor" /> against the verilated Chisel
///     L-TAGE, which mirrors <see cref="LTagePredictor" /> bit-for-bit. The differential
///     test replays an out-of-order pipeline's driving pattern — speculative fetch with
///     history capture, in-order commit, partial squash on mispredict, occasional full
///     flush — and demands identical predictions and identical history checkpoints at
///     every step.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiHistoryBranchPredictorTests {
    [SkippableFact]
    public void Loader_DetectsShimAbi() {
        Skip.If(
            RtlTageLibrary.Path is null || RtlBpLibrary.Path is null,
            "verilator toolchain unavailable — skipping."
        );
        IBranchPredictor tage = RtlBranchPredictorLoader.Load(RtlTageLibrary.Path);
        IBranchPredictor gshare = RtlBranchPredictorLoader.Load(RtlBpLibrary.Path);
        Assert.IsType<RtlFfiHistoryBranchPredictor>(tage);
        Assert.IsType<RtlFfiBranchPredictor>(gshare);
        (tage as IDisposable)!.Dispose();
        (gshare as IDisposable)!.Dispose();
    }

    [SkippableFact]
    public void LoopBranch_LearnedByLoopPredictor() {
        Skip.If(RtlTageLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var p = new RtlFfiHistoryBranchPredictor(RtlTageLibrary.Path);
        const ulong pc = 0x1000, target = 0x0F00;

        // Period-4 loop: taken ×3, not-taken ×1. After the loop predictor gains
        // confidence (4 consistent exits), every prediction in the pattern is exact.
        var warm = 0;
        for (var round = 0; round < 12; round++)
        for (var i = 0; i < 4; i++) {
            bool actual = i < 3;
            BranchPrediction pred = p.Predict(pc);
            if (round >= 8 && pred.PredictedTaken == actual) warm++;
            p.Update(pc, actual, actual ? target : pc + 4);
        }

        Assert.Equal(16, warm); // rounds 8..11 fully correct
    }

    [SkippableFact]
    public void DifferentialOooSequence_MatchesCSharpLTage() {
        Skip.If(RtlTageLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiHistoryBranchPredictor(RtlTageLibrary.Path);
        var reference = new LTagePredictor();

        var rng = new Random(20260717);
        ulong[] pcs = [.. Enumerable.Range(0, 24).Select(i => 0x4000UL + (ulong)(i * 4)),];
        var occurrence = new int[pcs.Length];

        var inflight = new Queue<(int P, bool Predicted, BranchHistoryCheckpoint RefChk,
            BranchHistoryCheckpoint RtlChk)>();

        for (var step = 0; step < 6000; step++) {
            int op = rng.Next(10);
            switch (op) {
                case < 6 when inflight.Count < 12: {
                    // Fetch: predict, checkpoint, fold predicted direction into spec history.
                    int p = rng.Next(pcs.Length);
                    BranchPrediction expected = reference.Predict(pcs[p]);
                    BranchPrediction got = rtl.Predict(pcs[p]);
                    Assert.True(
                        expected.PredictedTaken == got.PredictedTaken
                     && expected.PredictedTarget == got.PredictedTarget,
                        $"step {step} pc=0x{pcs[p]:X}: C#=({expected.PredictedTaken},0x{expected.PredictedTarget:X}) "
                      + $"RTL=({got.PredictedTaken},0x{got.PredictedTarget:X})"
                    );

                    BranchHistoryCheckpoint refChk = reference.CaptureHistory(pcs[p]);
                    BranchHistoryCheckpoint rtlChk = rtl.CaptureHistory(pcs[p]);
                    Assert.True(
                        refChk.Global == rtlChk.Global,
                        $"step {step}: checkpoint mismatch C#=0x{refChk.Global:X} RTL=0x{rtlChk.Global:X}"
                    );

                    reference.SpeculativeHistoryUpdate(pcs[p], expected.PredictedTaken);
                    rtl.SpeculativeHistoryUpdate(pcs[p], got.PredictedTaken);
                    inflight.Enqueue((p, expected.PredictedTaken, refChk, rtlChk));
                    break;
                }
                case < 9 when inflight.Count > 0: {
                    // Commit oldest; on mispredict, partial-squash: restore the branch's
                    // checkpoint folding the resolved direction, and discard younger.
                    (int p, bool predicted, BranchHistoryCheckpoint refChk, BranchHistoryCheckpoint rtlChk)
                        = inflight.Dequeue();
                    bool actual = Outcome(p);
                    reference.Update(pcs[p], actual, actual ? pcs[p] + 0x40 : pcs[p] + 4);
                    rtl.Update(pcs[p], actual, actual ? pcs[p] + 0x40 : pcs[p] + 4);
                    if (predicted != actual) {
                        reference.RestoreHistory(in refChk, pcs[p], actual);
                        rtl.RestoreHistory(in rtlChk, pcs[p], actual);
                        inflight.Clear();
                    }

                    break;
                }
                default: {
                    if (inflight.Count > 0) {
                        // Full flush: drop wrong-path history, discard in-flight branches.
                        reference.RecoverSpeculativeHistory();
                        rtl.RecoverSpeculativeHistory();
                        inflight.Clear();
                    }

                    break;
                }
            }
        }

        return;

        // Mixed behaviors: loops of differing trip counts, biased-random, history-correlated.
        bool Outcome(int p) {
            occurrence[p]++;
            return (p % 4) switch {
                0 => occurrence[p] % 5 != 0,   // loop, trip count 5
                1 => occurrence[p] % 3 != 0,   // loop, trip count 3
                2 => rng.Next(8) != 0,         // strongly biased taken
                _ => (occurrence[p] & 2) != 0, // period-4 alternating
            };
        }
    }
}