#region

using System.Diagnostics;
using System.Security.Cryptography;
using Orrery.Cache;
using RiscV32.Memory;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Builds <c>native/RtlFu/generated/StridePf.sv</c> into a native shared library via
///     <c>build.sh ... rtl_pf_shim.cpp</c>, cached by input-content hash like the other
///     RTL fixtures. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlPfLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "StridePf.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_pf_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_stride_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "StridePf", cached, "rtl_pf_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiPrefetcher" /> against the verilated Chisel RPT stride
///     prefetcher, which mirrors <see cref="StridePrefetcher" /> bit-for-bit — the
///     differential test demands identical prefetch counts and identical target
///     addresses on an arbitrary access stream.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiPrefetcherTests {
    [SkippableFact]
    public void StrideTraining_PrefetchesAfterTwoConfirmations() {
        Skip.If(RtlPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var pf = new RtlFfiPrefetcher(RtlPfLibrary.Path);
        Span<ulong> targets = stackalloc ulong[4];

        // Access 1: initializes the entry. Access 2: learns stride 64 (confidence 0).
        // Access 3: confirms (confidence 1). Access 4: confirms again (confidence 2) → prefetch.
        Assert.Equal(0, pf.OnAccess(0x400, 0x1000, false, targets));
        Assert.Equal(0, pf.OnAccess(0x400, 0x1040, false, targets));
        Assert.Equal(0, pf.OnAccess(0x400, 0x1080, false, targets));
        Assert.Equal(1, pf.OnAccess(0x400, 0x10C0, false, targets));
        Assert.Equal(0x1100UL, targets[0]);
    }

    [SkippableFact]
    public void NegativeStride_PrefetchesBackward() {
        Skip.If(RtlPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var pf = new RtlFfiPrefetcher(RtlPfLibrary.Path);
        Span<ulong> targets = stackalloc ulong[4];

        pf.OnAccess(0x800, 0x2000, false, targets);
        pf.OnAccess(0x800, 0x1FC0, false, targets);
        pf.OnAccess(0x800, 0x1F80, false, targets);
        Assert.Equal(1, pf.OnAccess(0x800, 0x1F40, false, targets));
        Assert.Equal(0x1F00UL, targets[0]);
    }

    [SkippableFact]
    public void DifferentialStream_MatchesCSharpStridePrefetcher() {
        Skip.If(RtlPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiPrefetcher(RtlPfLibrary.Path);
        var reference = new StridePrefetcher(); // tableSize = 64 = the Chisel default

        // Mixed access patterns over more PCs than the table has entries (aliasing
        // included): strided walks, stride changes, and random scatter.
        var rng = new Random(20260717);
        ulong[] pcs = [.. Enumerable.Range(0, 96).Select(i => 0x1000UL + (ulong)(i * 4)),];
        var walkAddr = new ulong[96];
        Span<ulong> expBuf = stackalloc ulong[4];
        Span<ulong> gotBuf = stackalloc ulong[4];

        for (var i = 0; i < 5000; i++) {
            int p = rng.Next(pcs.Length);
            ulong addr = (p % 4) switch {
                0 => walkAddr[p] += 64,                        // steady forward stride
                1 => walkAddr[p] -= 32,                        // steady backward stride
                2 => walkAddr[p] += (ulong)(rng.Next(3) * 64), // stride churn (incl. 0)
                _ => (ulong)rng.Next(1 << 20),                 // scatter
            };

            int expected = reference.OnAccess(pcs[p], addr, true, expBuf);
            int got = rtl.OnAccess(pcs[p], addr, true, gotBuf);
            Assert.True(
                expected == got && (expected == 0 || expBuf[0] == gotBuf[0]),
                $"step {i} pc=0x{pcs[p]:X} addr=0x{addr:X}: "
              + $"C#=({expected},0x{(expected > 0 ? expBuf[0] : 0):X}) RTL=({got},0x{(got > 0 ? gotBuf[0] : 0):X})"
            );
        }
    }

    [SkippableFact]
    public void MemoryConfigFactory_WiresRtlPrefetcherIntoLayers() {
        Skip.If(RtlPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var cfg = new MemoryConfig(
            8192,
            PrefetcherFactory: () => new RtlFfiPrefetcher(RtlPfLibrary.Path)
        );
        var layers = MemoryLayers.Build(new FlatMemory(1 << 16), cfg);
        Assert.IsType<RtlFfiPrefetcher>(layers.Prefetcher);
        (layers.Prefetcher as IDisposable)?.Dispose();
    }
}