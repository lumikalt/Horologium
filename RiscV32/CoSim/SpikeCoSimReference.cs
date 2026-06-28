using System.Diagnostics;
using System.Text.RegularExpressions;
using Mechanism;

namespace RiscV32.CoSim;

/// <summary>
/// Online lock-step co-verification against Spike.
///
/// Launches Spike with <c>--log-commits</c> and keeps it running as a child
/// process. Each call to <see cref="ICommitObserver.OnCommit"/> reads the
/// next commit record from Spike's live stderr stream, blocks until Spike
/// has produced the matching instruction, and immediately compares PC, raw
/// encoding, and any integer register write.
///
/// Divergence (PC out-of-order, encoding mismatch, wrong register value) is
/// detected at the exact failing instruction and reported via
/// <see cref="CoSimDivergenceException"/>. Boot-ROM commits (PC below
/// <paramref name="baseAddress"/>) are skipped transparently.
///
/// Implements <see cref="IDisposable"/> — the caller must dispose to kill
/// Spike when the simulation ends.
/// </summary>
public sealed class SpikeCoSimReference : ICommitObserver, IDisposable {
    private readonly record struct SpikeEntry(ulong Pc, uint RawEncoding, int RegIndex, uint RegValue);

    private static readonly Regex CommitLine = new(
        @"core\s+\d+:\s+\d+\s+(0x[0-9a-f]+)\s+\((0x[0-9a-f]+)\)(?:\s+x(\d+)\s+(0x[0-9a-f]+))?",
        RegexOptions.Compiled
    );

    private readonly Process _proc;
    private readonly StreamReader _log;
    private readonly ulong _baseAddress;
    private int _committed;

    /// <param name="elfPath">Path to the ELF binary to run under Spike.</param>
    /// <param name="baseAddress">ELF base address; Spike boot-ROM commits below this are skipped.</param>
    /// <param name="memorySizeBytes">Spike <c>-m</c> region size in bytes.</param>
    /// <param name="isa">ISA string passed to Spike's <c>--isa=</c>.</param>
    public SpikeCoSimReference(
        string elfPath,
        ulong baseAddress = 0x80000000UL,
        int memorySizeBytes = 0x400000,
        string isa = "rv32imafcv"
    ) {
        _baseAddress = baseAddress;

        var psi = new ProcessStartInfo {
            FileName = "spike",
            Arguments =
                $"--log-commits --isa={isa} -m0x{baseAddress:x}:0x{memorySizeBytes:x} {elfPath}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = BuildSpikeEnvPath();

        _proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start spike. Is it on PATH?");
        _log = _proc.StandardError;
    }

    /// <summary>
    /// Called by the pipeline for each committed instruction. Reads the next
    /// ELF-range commit from Spike's live output (blocking until available),
    /// then compares PC, encoding, and any integer register write.
    /// </summary>
    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        var entry = ReadNextElfEntry(pc);
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
            var horologium = (uint)state.IntegerRegisters.Read(entry.RegIndex);
            if (horologium != entry.RegValue)
                throw new CoSimDivergenceException(
                    $"x{entry.RegIndex} mismatch at 0x{pc:x8} (0x{rawEncoding:x8}): " +
                    $"Horologium=0x{horologium:x8}, Spike=0x{entry.RegValue:x8}"
                );
        }
    }

    public void Dispose() {
        if (!_proc.HasExited) {
            _proc.Kill();
            _proc.WaitForExit(1000);
        }
        _proc.Dispose();
    }

    // Reads lines from Spike's live commit log, discarding warning lines and
    // boot-ROM commits, until it finds the next ELF-range commit record.
    private SpikeEntry ReadNextElfEntry(ulong expectedPc) {
        while (true) {
            var line = _log.ReadLine();
            if (line is null)
                throw new CoSimDivergenceException(
                    $"Spike commit log ended unexpectedly at commit #{_committed + 1} " +
                    $"(Horologium about to commit 0x{expectedPc:x8})"
                );

            var m = CommitLine.Match(line);
            if (!m.Success) continue;

            var pc = Convert.ToUInt64(m.Groups[1].Value, 16);
            if (pc < _baseAddress) continue;

            return new SpikeEntry(
                pc,
                Convert.ToUInt32(m.Groups[2].Value, 16),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0,
                m.Groups[4].Success ? (uint)Convert.ToUInt64(m.Groups[4].Value, 16) : 0u
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
        var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (FindOnPath("dtc") is not null) return existing;

        // dtc not in PATH — search the nix store (dev-shell may not be reloaded).
        string? dtcBin = NixDtcBin();
        return dtcBin is not null ? $"{dtcBin}:{existing}" : existing;
    }

    // Returns the directory containing the named executable on PATH, or null.
    private static string? FindOnPath(string exe) {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
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
        } catch {
            return null;
        }
    }
}
