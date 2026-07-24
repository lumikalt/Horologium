#region

using System.Diagnostics;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Trace;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     End-to-end gem5 TraceCPU integration tests.
///     All tests are skipped when <c>gem5</c> is not available on PATH (same
///     convention as the Spike co-simulation tests).
/// </summary>
public class Gem5IntegrationTests {
    private static readonly bool Gem5Available =
        Environment.GetEnvironmentVariable("HOROLOGIUM_GEM5_COSIM") == "1" ||
        FindOnPath("gem5") is not null;

    private static readonly string ScriptPath =
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "gem5-scripts", "trace_cpu_riscv.py"
        );

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly byte[] SmallProgram = [
        // addi x1, x0, 1   — 0x00100093
        0x93, 0x00, 0x10, 0x00,
        // addi x2, x0, 2   — 0x00200113
        0x13, 0x01, 0x20, 0x00,
        // add  x3, x1, x2  — 0x002080B3
        0xB3, 0x80, 0x20, 0x00,
        // ebreak            — 0x00100073
        0x73, 0x00, 0x10, 0x00,
    ];

    private static string? FindOnPath(string exe) {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(Path.PathSeparator).Select(dir => Path.Combine(dir, exe)).FirstOrDefault(File.Exists);
    }

    private static (string dataFile, string fetchFile) GenerateTraces(string tmpDir) {
        var mem = new FlatMemory(0x1000);
        mem.Load(0, Gem5IntegrationTests.SmallProgram);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();

        string helfPath = Path.Combine(tmpDir, "test.helf");
        string dataPath = Path.Combine(tmpDir, "test.gem5data");
        string fetchPath = Path.Combine(tmpDir, "test.gem5fetch");

        using (var helfStream = new FileStream(helfPath, FileMode.Create))
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helfStream)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run(100);
        }

        using (var inFs = new FileStream(helfPath, FileMode.Open))
        using (var outFs = new FileStream(dataPath, FileMode.Create)) {
            Gem5ElasticTraceConverter.Convert(inFs, outFs);
        }

        using (var inFs = new FileStream(helfPath, FileMode.Open))
        using (var outFs = new FileStream(fetchPath, FileMode.Create)) { Gem5FetchTraceConverter.Convert(inFs, outFs); }

        return (dataPath, fetchPath);
    }

    private static (int exitCode, string stdout, string stderr) RunGem5(
        string dataFile,
        string fetchFile
    ) {
        string script = Path.GetFullPath(Gem5IntegrationTests.ScriptPath);
        var psi = new ProcessStartInfo(
            "gem5", [
                script,
                "--data-trace-file", dataFile,
                "--inst-trace-file", fetchFile,
            ]
        ) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(TimeSpan.FromMinutes(3));
        return (proc.ExitCode, stdout, stderr);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Gem5TraceCPU_CompletesWithoutError() {
        Skip.IfNot(Gem5IntegrationTests.Gem5Available, "gem5 not available; set HOROLOGIUM_GEM5_COSIM=1 to force");
        Skip.IfNot(
            File.Exists(Path.GetFullPath(Gem5IntegrationTests.ScriptPath)), "gem5-scripts/trace_cpu_riscv.py not found"
        );

        string tmpDir = Path.Combine(Path.GetTempPath(), "horologium_gem5_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try {
            (string dataFile, string fetchFile) = GenerateTraces(tmpDir);
            (int exitCode, string stdout, string stderr) = RunGem5(dataFile, fetchFile);

            // gem5 exits 0 on normal completion
            Assert.True(
                exitCode == 0,
                $"gem5 exited with code {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}"
            );

            // Our script prints this line on successful replay
            Assert.Contains("Exiting @", stdout + stderr);
        }
        finally { Directory.Delete(tmpDir, true); }
    }
}