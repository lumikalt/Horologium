using System.Diagnostics;
using System.Security.Cryptography;
using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
///     Builds <c>native/RtlFu/generated/SrripRp.sv</c> into a native shared library via
///     <c>build.sh ... rtl_rp_shim.cpp</c>, cached by input-content hash like the other
///     RTL fixtures. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlRpLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "SrripRp.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_rp_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_srrip_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "SrripRp", cached, "rtl_rp_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiReplacementPolicy" /> against the verilated Chisel SRRIP
///     (elaborated 64 sets × 4 ways). The RTL mirrors <see cref="SrripPolicy" />
///     bit-for-bit, so the differential test demands identical victims and identical
///     per-way RRPV metadata on an arbitrary hit/victim/install stream, and the
///     cache-level test demands identical hit/miss counts from
///     <see cref="SetAssociativeCache" /> under each policy.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiReplacementPolicyTests {
    [SkippableFact]
    public void Geometry_IsExposedAndValidated() {
        Skip.If(RtlRpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var policy = new RtlFfiReplacementPolicy(RtlRpLibrary.Path!);
        Assert.Equal(64, policy.Sets);
        Assert.Equal(4, policy.Ways);

        Assert.Throws<ArgumentException>(() => new RtlFfiReplacementPolicy(RtlRpLibrary.Path!, 128, 8));
        Assert.Null(RtlFfiReplacementPolicy.TryCreate(RtlRpLibrary.Path!, 128, 8));
        RtlFfiReplacementPolicy.TryCreate(RtlRpLibrary.Path!, 64, 4)?.Dispose();
    }

    [SkippableFact]
    public void SrripSemantics_InsertLong_HitPromote_VictimDistant() {
        Skip.If(RtlRpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var policy = new RtlFfiReplacementPolicy(RtlRpLibrary.Path!);

        // All ways start at RRPV 3 (distant) → first victim is way 0, no aging needed.
        Assert.Equal(0, policy.ChooseVictim(5));
        policy.RecordInstall(5, 0);
        Assert.Equal(2, policy.GetMetadata(5, 0)); // long re-reference

        policy.RecordHit(5, 0);
        Assert.Equal(0, policy.GetMetadata(5, 0)); // RRIP-HP promotion

        // Ways 1..3 still distant → next victim is way 1.
        Assert.Equal(1, policy.ChooseVictim(5));
    }

    [SkippableFact]
    public void DifferentialStream_MatchesCSharpSrrip() {
        Skip.If(RtlRpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiReplacementPolicy(RtlRpLibrary.Path!);
        var reference = new SrripPolicy(64, 4);

        var rng = new Random(20260717);
        for (var i = 0; i < 5000; i++) {
            int set = rng.Next(64);
            if (rng.Next(3) == 0) {
                int way = rng.Next(4);
                reference.RecordHit(set, way);
                rtl.RecordHit(set, way);
            }
            else {
                int expected = reference.ChooseVictim(set);
                int got = rtl.ChooseVictim(set);
                Assert.True(expected == got, $"step {i} set {set}: C# victim {expected}, RTL victim {got}");
                reference.RecordInstall(set, expected);
                rtl.RecordInstall(set, expected);
            }
        }

        for (var s = 0; s < 64; s++)
        for (var w = 0; w < 4; w++)
            Assert.Equal(reference.GetMetadata(s, w), rtl.GetMetadata(s, w));
    }

    [SkippableFact]
    public void Cache_HitMissStream_MatchesCSharpSrrip() {
        Skip.If(RtlRpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        // 8 KiB / 4-way / 32 B lines = 64 sets — the RTL model's elaborated geometry.
        var csCache = new SetAssociativeCache(
            new FlatMemory(1 << 20), 8192, 4, 32, 10, replacementPolicy: ReplacementPolicyKind.Srrip
        );
        var rtlCache = new SetAssociativeCache(
            new FlatMemory(1 << 20), 8192, 4, 32, 10,
            customPolicy: new RtlFfiReplacementPolicy(RtlRpLibrary.Path!, 64, 4)
        );

        // Mixed working sets: sequential scans (thrash) over reused hot lines.
        var rng = new Random(42);
        ulong[] hot = [.. Enumerable.Range(0, 96).Select(i => (ulong)(i * 32)),];
        for (var i = 0; i < 20000; i++) {
            ulong addr = rng.Next(4) == 0
                ? (ulong)(rng.Next(1 << 14)) & ~31UL // scan over 16 KiB (2× capacity)
                : hot[rng.Next(hot.Length)];
            csCache.Read(addr, 4);
            rtlCache.Read(addr, 4);
        }

        Assert.Equal(csCache.Hits, rtlCache.Hits);
        Assert.Equal(csCache.Misses, rtlCache.Misses);
        Assert.True(csCache.Hits > 0 && csCache.Misses > 0);
    }
}
