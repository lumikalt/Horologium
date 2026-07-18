using System.Diagnostics;
using System.Security.Cryptography;
using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
///     Builds <c>native/RtlFu/generated/DrripRp.sv</c> into a native shared library via
///     <c>build.sh ... rtl_rp_shim.cpp</c> (the same shim ABI as SRRIP), cached by
///     input-content hash. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlDrripLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "DrripRp.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_rp_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_drrip_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "DrripRp", cached, "rtl_rp_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiReplacementPolicy" /> against the verilated Chisel DRRIP,
///     which mirrors <see cref="DrripPolicy" /> bit-for-bit — including the global
///     cross-set state (10-bit PSEL duel, shared BRRIP bimodal counter) that
///     distinguishes it from the per-set-only SRRIP. Wrong PSEL bookkeeping shows up
///     immediately in follower-set insertion RRPVs, so the differential stream mixes
///     SDM and follower sets with thrash phases that swing the duel both ways.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiDrripTests {
    [SkippableFact]
    public void SetDuelingInsertion_SdmSetsFollowTheirPolicy() {
        Skip.If(RtlDrripLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var policy = new RtlFfiReplacementPolicy(RtlDrripLibrary.Path);
        Assert.Equal(64, policy.Sets);
        Assert.Equal(4, policy.Ways);

        // 64 sets → sdmSets = min(32, 64/4) = 16: set 0 is an SRRIP SDM, set 16 a BRRIP SDM.
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        Assert.Equal(2, policy.GetMetadata(0, 0)); // SRRIP SDM inserts long

        // BRRIP SDM: distant for the first 31 inserts, long on the 32nd (1/32 bimodal).
        for (var i = 0; i < 31; i++) {
            policy.RecordInstall(16, 0);
            Assert.Equal(3, policy.GetMetadata(16, 0));
        }

        policy.RecordInstall(16, 0);
        Assert.Equal(2, policy.GetMetadata(16, 0));
    }

    [SkippableFact]
    public void DifferentialStream_MatchesCSharpDrrip() {
        Skip.If(RtlDrripLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var rtl = new RtlFfiReplacementPolicy(RtlDrripLibrary.Path);
        var reference = new DrripPolicy(64, 4);

        var rng = new Random(20260717);
        for (var i = 0; i < 8000; i++) {
            // Phase-based set bias: alternate between SDM-heavy phases (drives the PSEL
            // duel hard in each direction) and follower-heavy phases (exposes whichever
            // insertion policy PSEL currently selects).
            int set = (i / 1000 % 3) switch {
                0 => rng.Next(16),      // SRRIP SDM sets → PSEL rises
                1 => 16 + rng.Next(16), // BRRIP SDM sets → PSEL falls
                _ => 32 + rng.Next(32), // followers → insertion depends on PSEL
            };

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
    public void Cache_HitMissStream_MatchesCSharpDrrip() {
        Skip.If(RtlDrripLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var csCache = new SetAssociativeCache(
            new FlatMemory(1 << 20), 8192, 4, 32, 10, replacementPolicy: ReplacementPolicyKind.Drrip
        );
        var rtlCache = new SetAssociativeCache(
            new FlatMemory(1 << 20), 8192, 4, 32, 10,
            customPolicy: new RtlFfiReplacementPolicy(RtlDrripLibrary.Path, 64, 4)
        );

        // Thrash phases (working set > capacity, where BRRIP should win the duel)
        // interleaved with reuse phases (where SRRIP should).
        var rng = new Random(42);
        ulong[] hot = [.. Enumerable.Range(0, 96).Select(i => (ulong)(i * 32)),];
        for (var i = 0; i < 20000; i++) {
            bool thrash = i / 2500 % 2 == 0;
            ulong addr = thrash
                ? (ulong)rng.Next(1 << 15) & ~31UL // 32 KiB working set (4× capacity)
                : hot[rng.Next(hot.Length)];
            csCache.Read(addr, 4);
            rtlCache.Read(addr, 4);
        }

        Assert.Equal(csCache.Hits, rtlCache.Hits);
        Assert.Equal(csCache.Misses, rtlCache.Misses);
        Assert.True(csCache is { Hits: > 0, Misses: > 0, });
    }
}