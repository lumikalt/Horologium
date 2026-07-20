#region

using System.Diagnostics;
using Mechanism.BranchPred;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Full pipeline-integration coverage for <see cref="CbpNgCommitDrivenBp" />: wires a
///     built harcom submission directly into a real <c>OooeTrain</c> — exercising the actual
///     <c>Predict</c> call sites (main fetch and shadow/runahead fetch) and ROB-based commit-order
///     <c>Update</c> — rather than the isolated-adapter-unit calls in
///     <see cref="Tests.Mechanism.CbpNgFfiBranchPredictionTests" />.
///     <para>Requires a host C++20 toolchain (<c>g++</c>); skips if unavailable.</para>
/// </summary>
public sealed class CbpNgOoOeIntegrationTests : IDisposable {
    private readonly string? _libraryPath;

    public CbpNgOoOeIntegrationTests() {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string buildScript = Path.Combine(repoRoot, "native", "CbpNgShim", "build.sh");
        string predictorHeader = Path.Combine(repoRoot, "native", "CbpNgShim", "test_predictor.hpp");
        if (!File.Exists(buildScript) || !File.Exists(predictorHeader)) return;

        string output = Path.Combine(Path.GetTempPath(), $"cbp_ng_ooo_test_{Guid.NewGuid():N}.so");
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
    public void CommitDriven_DrivesRealOooeTrain_ToCorrectResultWithoutNativeCrash() {
        // Same call/loop/return program as OoO_TrueOraclePredictor_ZeroBranchMisses_WithCallReturn
        // (JAL call, BLT loop 5x, JALR return) — but with the default OoOE settings (robCapacity=16,
        // issueWidth=2), several loop iterations' branches are fetched (Predict) into the ROB well
        // before the earliest of them commits (Update), including via TryShadowStep's runahead
        // fetch past the head. A raw CbpNgFfiBp would corrupt harcom's per-block registers
        // under this pattern — enforced by harcom itself via std::terminate() on a same-cycle
        // reentrant reg/ram access (vendor/harcom.hpp). Running to completion, with correct final
        // register values, demonstrates the adapter holds under genuine pipeline-driven concurrency,
        // not just the hand-driven Predict/Update bursts in CbpNgFfiBranchPredictionTests.
        Skip.If(_libraryPath is null, "g++ unavailable or native shim failed to build — skipping.");
        uint[] program = [
            0x010000EF, // addr  0: jal  x1, 16        -- call subroutine; x1 = return addr 4
            0x00100073, // addr  4: ebreak              -- end of main (reached after return)
            0x00000013, // addr  8: nop                 -- padding
            0x00000013, // addr 12: nop                 -- padding
            0x00000113, // addr 16: addi x2, x0, 0     -- x2 = 0
            0x00500193, // addr 20: addi x3, x0, 5     -- x3 = 5
            0x00110113, // addr 24: addi x2, x2, 1     -- loop body
            0xFE314EE3, // addr 28: blt  x2, x3, -4    -- if x2 < 5, jump to addr 24
            0x00008067, // addr 32: jalr x0, x1, 0     -- return
        ];

        var mem = new FlatMemory(4096);
        LoadWords(mem, program);
        var mechanism = new Rv32Mechanism();
        using var predictor = new CbpNgCommitDrivenBp(_libraryPath!);
        var train = new OooeTrain(mechanism, mem, predictor: predictor);

        RevolutionResult result = train.Run();

        Assert.Equal(5u, (uint)train.ArchState.IntegerRegisters.Read(2)); // loop ran to completion
        Assert.Equal(4u, (uint)train.ArchState.IntegerRegisters.Read(1)); // x1 still holds return addr

        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        Assert.True(snap.Counters["branch_misses"] >= 0L); // present and finite — no crash mid-run
    }

    private static void LoadWords(FlatMemory mem, uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }
}