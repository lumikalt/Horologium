#region

using System.Diagnostics;
using RiscV64.Memory;

#endregion

namespace Face.Models;

/// <summary>
///     Compiles a pasted UVE (RV64) C source string into a statically-linked musl ELF via the UVE
///     author's patched clang+lld toolchain (github.com/lumicrespo/UVEcompiler — not part of
///     <c>flake.nix</c>, so its path is user-supplied), then wraps the result as a loadable
///     <see cref="Rv64ElfWorkload" />.
///     <para>
///         GNU <c>ld</c> cannot handle the custom <c>R_RISCV_UVE_STREAM_BRANCH</c> relocation UVE
///         branches emit ("unsupported relocation" internal error), so linking must go through the
///         same patched <c>ld.lld</c> that ships alongside the patched clang — reached via
///         <c>-fuse-ld=lld -B &lt;clang's own bin dir&gt;</c>. The link *driver* is still the flake's
///         <c>riscv64-unknown-linux-musl-gcc</c>, not clang itself: it already knows where musl's
///         crt objects/libc live, which the standalone patched clang build (no runtimes, no sysroot
///         baked in) does not.
///     </para>
/// </summary>
public static class CompiledSourceWorkload {
    private const string MuslTarget = "riscv64-unknown-linux-musl";
    private const string MArch = "rv64imafdc_xuve0p1";

    /// <summary>
    ///     Compiles and links <paramref name="source" />, returning a ready-to-run workload. Throws
    ///     <see cref="InvalidOperationException" /> with the compiler/linker's own diagnostic text on
    ///     failure, or <see cref="FileNotFoundException" /> if <paramref name="clangPath" /> doesn't exist.
    /// </summary>
    /// <param name="clangPath">Path to the patched clang binary.</param>
    /// <param name="source">C source, typically with inline UVE <c>asm volatile</c> blocks.</param>
    /// <param name="memoryBytes">Guest memory size for the returned workload.</param>
    public static async Task<Rv64ElfWorkload> CompileAsync(string clangPath, string source, int memoryBytes) {
        if (!File.Exists(clangPath)) throw new FileNotFoundException($"UVE clang not found at '{clangPath}'.");
        string clangDir = Path.GetDirectoryName(clangPath)!;

        string? linkerGcc = FindOnPath($"{CompiledSourceWorkload.MuslTarget}-gcc");
        if (linkerGcc is null)
            throw new InvalidOperationException(
                $"{CompiledSourceWorkload.MuslTarget}-gcc not found in PATH. Run inside the dev shell: nix develop"
            );

        string muslInclude = await FindMuslSysIncludeDir(linkerGcc);

        string tmpDir = Path.GetTempPath();
        var tag = Guid.NewGuid().ToString("N");
        string srcFile = Path.Combine(tmpDir, $"horologium_uve_{tag}.c");
        string objFile = Path.Combine(tmpDir, $"horologium_uve_{tag}.o");
        string elfFile = Path.Combine(tmpDir, $"horologium_uve_{tag}.elf");

        try {
            await File.WriteAllTextAsync(srcFile, source);

            (int ccExit, _, string ccErr) = await RunProcess(
                clangPath,
                $"--target={CompiledSourceWorkload.MuslTarget} -march={CompiledSourceWorkload.MArch} "
              + $"-mabi=lp64d -menable-experimental-extensions -isystem \"{muslInclude}\" -O2 "
              + $"-c -o \"{objFile}\" \"{srcFile}\""
            );
            if (ccExit != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(ccErr) ? $"Compiler exited {ccExit}" : ccErr.Trim()
                );

            (int ldExit, _, string ldErr) = await RunProcess(
                linkerGcc,
                $"-static -fuse-ld=lld -B \"{clangDir}\" -o \"{elfFile}\" \"{objFile}\""
            );
            if (ldExit != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(ldErr) ? $"Linker exited {ldExit}" : ldErr.Trim()
                );

            return new Rv64ElfWorkload(elfFile, memoryBytes);
        }
        finally {
            TryDelete(srcFile);
            TryDelete(objFile);
            TryDelete(elfFile);
        }
    }

    // gcc's own musl sysroot is baked in at build time (--with-native-system-header-dir), not exposed
    // via a stable -print-* flag — but "-Wp,-v" (forwarded to cc1) dumps the real #include search
    // list to stderr, always with the musl libc's own "…musl-riscv64-unknown-linux-musl…/include"
    // entry present (distinct from the adjacent gcc-internal "…/sys-include" entry). Filtering for
    // that substring is stable across nix store hash churn and any unrelated -isystem/CPATH pollution
    // this dev shell's global environment happens to carry.
    private static async Task<string> FindMuslSysIncludeDir(string gcc) {
        (_, string stdout, string stderr) = await RunProcess(gcc, "-E -Wp,-v -xc /dev/null");
        string? match = (stdout + "\n" + stderr)
                       .Split('\n')
                       .Select(l => l.Trim())
                       .FirstOrDefault(l => l.Contains("musl-riscv64-unknown-linux-musl") && l.EndsWith("/include")
                        );
        if (match is null)
            throw new InvalidOperationException($"Could not locate the musl libc header directory from '{gcc}'.");
        return match;
    }

    private static string? FindOnPath(string exeName) {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv.Split(':')
                      .Select(dir => Path.Combine(dir, exeName))
                      .FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(string exe, string args) {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(exe, args) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        proc.Start();
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static void TryDelete(string path) {
        try { File.Delete(path); }
        catch {
            // ignored
        }
    }
}