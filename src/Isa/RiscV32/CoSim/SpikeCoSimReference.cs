using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Mechanism;

namespace RiscV32.CoSim;

/// <summary>
/// Online lock-step co-verification against Spike.
/// 
/// Launches Spike with <c>--log-commits</c> and keeps it running as a child
/// process. Each call to <see cref="ICommitObserver.OnCommit"/> reads the
/// next commit record from Spike's live stderr stream, waits (bounded by a
/// watchdog timeout) until Spike has produced the matching instruction, and
/// immediately compares PC, raw encoding, and any integer register write.
/// 
/// Divergence (PC out-of-order, encoding mismatch, wrong register value) is
/// detected at the exact failing instruction and reported via
/// <see cref="CoSimDivergenceException"/>. Boot-ROM commits (PC below
/// <paramref>
///     <name>baseAddress</name>
/// </paramref>
/// ) are skipped transparently.
/// 
/// Implements <see cref="IDisposable"/> — the caller must dispose to kill
/// Spike when the simulation ends.
/// </summary>
public sealed partial class SpikeCoSimReference : ICommitObserver, IDisposable {
    private readonly record struct SpikeEntry(ulong Pc, uint RawEncoding, int RegIndex, ulong RegValue);

    [GeneratedRegex(
        @"core\s+\d+:\s+\d+\s+(0x[0-9a-f]+)\s+\((0x[0-9a-f]+)\)(?:\s+x(\d+)\s+(0x[0-9a-f]+))?", RegexOptions.Compiled
    )]
    private static partial Regex CommitLine { get; }

    private readonly Process _proc;
    private readonly ulong _baseAddress;
    private readonly TimeSpan _readTimeout;
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _reader;
    private int _committed;

    /// <param name="elfPath">Path to the ELF binary to run under Spike.</param>
    /// <param name="baseAddress">ELF base address; Spike boot-ROM commits below this are skipped.</param>
    /// <param name="memorySizeBytes">Spike <c>-m</c> region size in bytes.</param>
    /// <param name="isa">ISA string passed to Spike's <c>--isa=</c>.</param>
    /// <param name="readTimeout">
    /// Watchdog limit on waiting for the next Spike commit record (default 30 s).
    /// If Spike produces nothing within it — over-run past the workload's end, or a
    /// stall — the wait fails with <see cref="CoSimDivergenceException"/> instead
    /// of blocking forever.
    /// </param>
    /// <param name="spikeExecutable">Spike binary to launch (name on PATH or explicit path).</param>
    public SpikeCoSimReference(
        string elfPath,
        ulong baseAddress = 0x80000000UL,
        int memorySizeBytes = 0x400000,
        string isa = "rv32imafcv",
        TimeSpan? readTimeout = null,
        string spikeExecutable = "spike"
    ) {
        _baseAddress = baseAddress;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);

        var psi = new ProcessStartInfo {
            FileName = spikeExecutable,
            Arguments =
                $"--log-commits --isa={isa} -m0x{baseAddress:x}:0x{memorySizeBytes:x} {elfPath}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = {
                ["PATH"] = BuildSpikeEnvPath(),
            },
        };

        _proc = Process.Start(psi)
             ?? throw new InvalidOperationException("Failed to start spike. Is it on PATH?");
        StreamReader log = _proc.StandardError;

        // Pump Spike's stderr on a dedicated thread so the consumer side can
        // wait with a timeout. Ends (completing the collection) when the pipe
        // closes — normal Spike exit or Dispose killing the process.
        _reader = new Thread(() => {
                try {
                    while (log.ReadLine() is { } line) _lines.Add(line);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                finally { _lines.CompleteAdding(); }
            }
        ) { IsBackground = true, Name = "spike-cosim-log-reader", };
        _reader.Start();
    }

    /// <summary>
    /// Called by the pipeline for each committed instruction. Reads the next
    /// ELF-range commit from Spike's live output (blocking until available),
    /// then compares PC, encoding, and any integer register write.
    /// </summary>
    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        SpikeEntry entry = ReadNextElfEntry(pc);
        _committed++;

        if (entry.Pc != pc)
            throw new CoSimDivergenceException(
                $"PC mismatch at commit #{_committed}: Horologium=0x{pc:x8}, Spike=0x{entry.Pc:x8}"
            );

        if (entry.RawEncoding != rawEncoding)
            throw new CoSimDivergenceException(
                $"Encoding mismatch at 0x{pc:x8}: Horologium=0x{rawEncoding:x8}, Spike=0x{entry.RawEncoding:x8}"
            );

        if (entry.RegIndex > 0) {
            ulong horologium = state.IntegerRegisters.Read(entry.RegIndex);
            if (horologium != entry.RegValue)
                throw new CoSimDivergenceException(
                    $"x{entry.RegIndex} mismatch at 0x{pc:x8} (0x{rawEncoding:x8}): " +
                    $"Horologium=0x{horologium:x16}, Spike=0x{entry.RegValue:x16}"
                );
        }
    }

    public void Dispose() {
        if (!_proc.HasExited) {
            _proc.Kill();
            _proc.WaitForExit(1000);
        }

        // Killing the process closes the stderr pipe, which unblocks the
        // reader thread and completes the collection.
        _reader.Join(1000);
        _proc.Dispose();
        _lines.Dispose();
    }

    // Reads lines from Spike's live commit log, discarding warning lines and
    // boot-ROM commits, until it finds the next ELF-range commit record.
    private SpikeEntry ReadNextElfEntry(ulong expectedPc) {
        while (true) {
            if (!_lines.TryTake(out string? line, _readTimeout)) {
                if (_lines.IsCompleted)
                    throw new CoSimDivergenceException(
                        $"Spike commit log ended unexpectedly at commit #{_committed + 1} " +
                        $"(Horologium about to commit 0x{expectedPc:x8})"
                    );
                throw new CoSimDivergenceException(
                    $"No Spike commit record within {_readTimeout.TotalSeconds:0.#}s at commit " +
                    $"#{_committed + 1} (Horologium about to commit 0x{expectedPc:x8}) — " +
                    "Horologium has over-run Spike's instruction stream or Spike has stalled"
                );
            }

            Match m = CommitLine.Match(line);
            if (!m.Success) continue;

            var pc = Convert.ToUInt64(m.Groups[1].Value, 16);
            if (pc < _baseAddress) continue;

            return new SpikeEntry(
                pc,
                Convert.ToUInt32(m.Groups[2].Value, 16),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0,
                m.Groups[4].Success ? Convert.ToUInt64(m.Groups[4].Value, 16) : 0UL
            );
        }
    }

    /// <summary>
    /// True when the Spike co-simulation toolchain can be located: the
    /// <c>spike</c> simulator on PATH and the <c>dtc</c> device-tree compiler it
    /// needs (on PATH or in the Nix store). Lets callers skip the co-sim tests
    /// gracefully where the toolchain is absent. The Nix dev-shell provides both.
    /// </summary>
    public static bool IsAvailable() =>
        FindOnPath("spike") is not null && (FindOnPath("dtc") is not null || NixDtcBin() is not null);

    private static string BuildSpikeEnvPath() {
        string existing = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (FindOnPath("dtc") is not null) return existing;

        // dtc not in PATH — search the nix store (dev-shell may not be reloaded).
        string? dtcBin = NixDtcBin();
        return dtcBin is not null ? $"{dtcBin}:{existing}" : existing;
    }

    // Returns the directory containing the named executable on PATH, or null.
    private static string? FindOnPath(string exe) {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
                   .FirstOrDefault(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));
    }

    // Returns a nix-store bin directory containing dtc, or null.
    private static string? NixDtcBin() {
        try {
            var nixStore = new DirectoryInfo("/nix/store");
            if (!nixStore.Exists) return null;
            return nixStore.GetDirectories("*-dtc-*")
                           .Select(d => Path.Combine(d.FullName, "bin"))
                           .FirstOrDefault(d => File.Exists(Path.Combine(d, "dtc")));
        }
        catch { return null; }
    }
}