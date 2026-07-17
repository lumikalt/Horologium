using System.Diagnostics;
using System.Security.Cryptography;
using Orrery.Cache;

namespace Tests.Orrery;

/// <summary>
///     Builds <c>native/RtlFu/generated/StreamPf.sv</c> into a native shared library via
///     <c>build.sh ... rtl_mpf_shim.cpp</c> (the multi-degree drain-queue shim; same
///     rtl_pf_* C ABI as the stride prefetcher), cached by input-content hash.
///     Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlStreamPfLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "StreamPf.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_mpf_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_stream_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "StreamPf", cached, "rtl_mpf_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiPrefetcher" /> against the verilated Chisel stream-buffer
///     prefetcher (4 streams × depth 8), which mirrors <see cref="StreamPrefetcher" />
///     bit-for-bit — including the multi-target allocation burst that the drain-queue
///     shim carries across the FFI one address per clock.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiStreamPfTests {
    [SkippableFact]
    public void AllocationBurst_IssuesDepthLinesAtOnce() {
        Skip.If(RtlStreamPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var pf = new RtlFfiPrefetcher(RtlStreamPfLibrary.Path);
        Span<ulong> targets = stackalloc ulong[16];

        // Cold miss allocates a stream and bursts 8 sequential lines.
        int n = pf.OnAccess(0x100, 0x8000, wasHit: false, targets);
        Assert.Equal(8, n);
        for (var k = 0; k < 8; k++) Assert.Equal(0x8020UL + (ulong)(k * 32), targets[k]);

        // Sequential advance issues exactly one line to hold the frontier depth ahead.
        n = pf.OnAccess(0x100, 0x8020, wasHit: true, targets);
        Assert.Equal(1, n);
        Assert.Equal(0x8120UL, targets[0]); // front = lineBase(0x8020) + 8*32
    }

    [SkippableFact]
    public void NonSequentialHit_IssuesNothing() {
        Skip.If(RtlStreamPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var pf = new RtlFfiPrefetcher(RtlStreamPfLibrary.Path);
        Span<ulong> targets = stackalloc ulong[16];

        pf.OnAccess(0, 0x8000, wasHit: false, targets);
        Assert.Equal(0, pf.OnAccess(0, 0xF000, wasHit: true, targets)); // no match, hit → nothing
    }

    [SkippableFact]
    public void DifferentialStream_MatchesCSharpStreamPrefetcher() {
        Skip.If(RtlStreamPfLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiPrefetcher(RtlStreamPfLibrary.Path);
        var reference = new StreamPrefetcher(); // 4 streams × depth 8 × 32 B = the Chisel default

        // Six interleaved sequential walkers (more than the 4 stream buffers, forcing
        // LRU evictions) plus scattered accesses; hit flags randomized but identical
        // for both sides.
        var rng = new Random(20260717);
        var walkers = new ulong[6];
        for (var w = 0; w < walkers.Length; w++) walkers[w] = (ulong)(0x10000 * (w + 1));

        Span<ulong> expBuf = stackalloc ulong[16];
        Span<ulong> gotBuf = stackalloc ulong[16];
        for (var i = 0; i < 5000; i++) {
            ulong addr;
            if (rng.Next(5) == 0) { addr = (ulong)rng.Next(1 << 22) & ~31UL; }
            else {
                int w = rng.Next(walkers.Length);
                walkers[w] += 32;
                addr = walkers[w];
            }

            bool wasHit = rng.Next(3) != 0;
            int expected = reference.OnAccess(0, addr, wasHit, expBuf);
            int got = rtl.OnAccess(0, addr, wasHit, gotBuf);
            Assert.True(expected == got, $"step {i} addr=0x{addr:X}: C# count {expected}, RTL count {got}");
            for (var k = 0; k < expected; k++)
                Assert.True(
                    expBuf[k] == gotBuf[k],
                    $"step {i} addr=0x{addr:X} target[{k}]: C#=0x{expBuf[k]:X} RTL=0x{gotBuf[k]:X}"
                );
        }
    }
}
