using System.Diagnostics;
using System.Text.RegularExpressions;
using Mechanism;

namespace RiscV32.CoSim;

/// <summary>
/// Lock-step co-verification against Spike. Runs Spike with <c>--log-commits</c>
/// at construction time, parses its commit log, and checks each Horologium commit
/// against the corresponding Spike entry via <see cref="ICommitObserver.OnCommit"/>.
///
/// Only integer register writes are compared; stores and branches are verified
/// implicitly via PC ordering. Boot-ROM instructions (PC below
/// <paramref name="baseAddress"/>) are skipped from Spike's log automatically.
///
/// Throws <see cref="CoSimDivergenceException"/> on the first mismatch.
/// </summary>
public sealed class SpikeCoSimReference : ICommitObserver {
    private readonly record struct SpikeEntry(ulong Pc, uint RawEncoding, int RegIndex, uint RegValue);

    private static readonly Regex CommitLine = new(
        @"core\s+\d+:\s+\d+\s+(0x[0-9a-f]+)\s+\((0x[0-9a-f]+)\)(?:\s+x(\d+)\s+(0x[0-9a-f]+))?",
        RegexOptions.Compiled
    );

    private readonly List<SpikeEntry> _log;
    private int _index;

    /// <param name="elfPath">Path to the ELF binary to run under Spike.</param>
    /// <param name="baseAddress">ELF base address; Spike boot-ROM commits below this are skipped.</param>
    /// <param name="memorySizeBytes">Spike <c>-m</c> region size.</param>
    /// <param name="isa">ISA string passed to <c>--isa=</c>.</param>
    public SpikeCoSimReference(
        string elfPath,
        ulong baseAddress = 0x80000000UL,
        int memorySizeBytes = 0x400000,
        string isa = "rv32imafcv"
    ) {
        _log = CollectSpikeLog(elfPath, baseAddress, memorySizeBytes, isa);
    }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        if (_index >= _log.Count)
            throw new CoSimDivergenceException(
                $"Horologium committed instruction at 0x{pc:x8} but Spike log is exhausted after {_log.Count} entries"
            );

        var entry = _log[_index++];

        if (entry.Pc != pc)
            throw new CoSimDivergenceException(
                $"PC mismatch at commit #{_index}: Horologium=0x{pc:x8}, Spike=0x{entry.Pc:x8}"
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

    private static List<SpikeEntry> CollectSpikeLog(
        string elfPath,
        ulong baseAddress,
        int memorySizeBytes,
        string isa
    ) {
        var psi = new ProcessStartInfo {
            FileName = "spike",
            Arguments =
                $"--log-commits --isa={isa} -m0x{baseAddress:x}:0x{memorySizeBytes:x} {elfPath}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = BuildSpikeEnvPath();

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start spike");

        // Read commit log from stderr. Spike stalls in a fault loop after EBREAK
        // (never generates more output but never exits), so we kill it once the
        // stream has gone quiet for 500 ms.
        var lines = new List<string>(1024);
        var readTask = Task.Run(() => {
            string? line;
            while ((line = proc.StandardError.ReadLine()) != null)
                lines.Add(line);
        });

        // Wait up to 3 s for the log to settle, then kill Spike. For typical test
        // binaries all output arrives in well under 1 s.
        readTask.Wait(TimeSpan.FromSeconds(3));
        if (!proc.HasExited) {
            proc.Kill();
            proc.WaitForExit(1000);
        }
        // Let the background reader drain EOF after the kill.
        readTask.Wait(500);

        return ParseLog(lines, baseAddress);
    }

    private static List<SpikeEntry> ParseLog(List<string> lines, ulong baseAddress) {
        var entries = new List<SpikeEntry>(lines.Count);
        foreach (var line in lines) {
            var m = CommitLine.Match(line);
            if (!m.Success) continue;

            var pc = Convert.ToUInt64(m.Groups[1].Value, 16);
            if (pc < baseAddress) continue;

            var rawEncoding = Convert.ToUInt32(m.Groups[2].Value, 16);
            var regIndex = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            var regValue = m.Groups[4].Success ? (uint)Convert.ToUInt64(m.Groups[4].Value, 16) : 0u;

            entries.Add(new SpikeEntry(pc, rawEncoding, regIndex, regValue));
        }
        return entries;
    }

    private static string BuildSpikeEnvPath() {
        var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (existing.Split(':').Any(d => File.Exists(Path.Combine(d, "dtc"))))
            return existing;

        // dtc not in PATH — search the nix store (dev-shell may not be active).
        try {
            var nixStore = new DirectoryInfo("/nix/store");
            if (nixStore.Exists) {
                var dtcBin = nixStore.GetDirectories("*-dtc-*")
                    .Select(d => Path.Combine(d.FullName, "bin"))
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "dtc")));
                if (dtcBin is not null)
                    return $"{dtcBin}:{existing}";
            }
        } catch { }

        return existing;
    }
}
