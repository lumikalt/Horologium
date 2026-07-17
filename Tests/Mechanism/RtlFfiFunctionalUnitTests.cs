using System.Diagnostics;
using System.Security.Cryptography;
using Mechanism.RtlFu;

namespace Tests.Mechanism;

/// <summary>
///     Builds <c>native/RtlFu/generated/DivUnit.sv</c> (the Chisel divider) into a native
///     shared library once per input-content hash, caching the result in the temp
///     directory — verilation takes tens of seconds, far too slow to repeat per test or
///     even per session. Returns null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlDivLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "DivUnit.sv");
        string shim = System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_fu_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"horologium_rtl_div_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "DivUnit", cached,]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Drives <see cref="RtlFfiFunctionalUnit" /> against the verilated Chisel DivUnit —
///     the real FFI path end-to-end, mirroring <see cref="CbpFfiBranchPredictionTests" />.
///     Ops follow the DivUnit encoding: 0=DIV, 1=DIVU, 2=REM, 3=REMU.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFfiFunctionalUnitTests {
    private const uint Div = 0, Divu = 1, Rem = 2, Remu = 3;

    private static RtlFfiFunctionalUnit Open() {
        Skip.If(RtlDivLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        return new RtlFfiFunctionalUnit(RtlDivLibrary.Path!);
    }

    [SkippableTheory]
    [InlineData(Div, 100u, 7u, 14u)]
    [InlineData(Rem, 100u, 7u, 2u)]
    [InlineData(Div, unchecked((uint)-100), 7u, unchecked((uint)-14))]
    [InlineData(Rem, unchecked((uint)-100), 7u, unchecked((uint)-2))]
    [InlineData(Div, 100u, unchecked((uint)-7), unchecked((uint)-14))]
    [InlineData(Rem, 100u, unchecked((uint)-7), 2u)]
    [InlineData(Div, unchecked((uint)-100), unchecked((uint)-7), 14u)]
    [InlineData(Rem, unchecked((uint)-100), unchecked((uint)-7), unchecked((uint)-2))]
    [InlineData(Divu, 0x0FFFFFFFu, 3u, 0x05555555u)]
    [InlineData(Remu, 10u, 3u, 1u)]
    public void SignedAndUnsignedResults(uint op, uint a, uint b, uint expected) {
        using RtlFfiFunctionalUnit fu = Open();
        (uint result, int cycles) = fu.Execute(op, a, b);
        Assert.Equal(expected, result);
        Assert.InRange(cycles, 1, 33);
    }

    [SkippableTheory]
    [InlineData(Div, 7u, 0u, 0xFFFFFFFFu)]  // div-by-zero → -1
    [InlineData(Divu, 7u, 0u, 0xFFFFFFFFu)] // div-by-zero → 2^32-1
    [InlineData(Rem, 7u, 0u, 7u)]           // div-by-zero → dividend
    [InlineData(Remu, 7u, 0u, 7u)]
    [InlineData(Div, 0x80000000u, 0xFFFFFFFFu, 0x80000000u)] // INT_MIN/-1 → INT_MIN
    [InlineData(Rem, 0x80000000u, 0xFFFFFFFFu, 0u)]          // INT_MIN%-1 → 0
    public void RiscVSpecialCases_ResolveInOneCycle(uint op, uint a, uint b, uint expected) {
        using RtlFfiFunctionalUnit fu = Open();
        (uint result, int cycles) = fu.Execute(op, a, b);
        Assert.Equal(expected, result);
        Assert.Equal(1, cycles);
    }

    [SkippableFact]
    public void Latency_IsDataDependent_OnDividendMagnitude() {
        using RtlFfiFunctionalUnit fu = Open();
        // significant-bits(|dividend|) + 1: early termination skips leading zeros.
        (_, int small) = fu.Execute(Divu, 3, 2);
        (_, int mid) = fu.Execute(Divu, 0xFFFF, 2);
        (_, int big) = fu.Execute(Divu, 0xFFFFFFFF, 2);
        Assert.Equal(3, small);
        Assert.Equal(17, mid);
        Assert.Equal(33, big);
    }

    [SkippableFact]
    public void BackToBackOperations_AreIndependent() {
        using RtlFfiFunctionalUnit fu = Open();
        (uint first, _) = fu.Execute(Div, 100, 7);
        (uint second, _) = fu.Execute(Remu, 100, 7);
        (uint third, _) = fu.Execute(Div, 100, 7);
        Assert.Equal(14u, first);
        Assert.Equal(2u, second);
        Assert.Equal(first, third); // unit returns to idle: same inputs, same result
    }
}
