using System.Diagnostics;
using System.Security.Cryptography;
using Mechanism;
using Mechanism.RtlFu;
using Pipeline;
using RiscV32;
using RiscV32.Execute;
using RiscV32.Memory;

namespace Tests.RiscV32.CoSim;

/// <summary>
///     Builds <c>native/RtlFu/generated/FDivSqrtUnit.sv</c> into a native shared library
///     via <c>build.sh ... rtl_fpu_shim.cpp</c> (the flags-carrying FU shim), cached by
///     input-content hash. Null (→ tests skip) when the toolchain is unavailable.
/// </summary>
public static class RtlFpLibrary {
    public static string? Path { get; } = Build();

    private static string? Build() {
        string repoRoot = global::System.IO.Path.GetFullPath(
            global::System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        string buildScript = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "build.sh");
        string sv = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "generated", "FDivSqrtUnit.sv");
        string shim = global::System.IO.Path.Combine(repoRoot, "native", "RtlFu", "rtl_fpu_shim.cpp");
        if (!File.Exists(buildScript) || !File.Exists(sv) || !File.Exists(shim)) return null;

        byte[] hash = SHA256.HashData(
            [.. File.ReadAllBytes(sv), .. File.ReadAllBytes(shim), .. File.ReadAllBytes(buildScript),]
        );
        string cached = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(), $"horologium_rtl_fdiv_{Convert.ToHexString(hash)[..16]}.so"
        );
        if (File.Exists(cached)) return cached;

        var psi = new ProcessStartInfo(buildScript, [sv, "FDivSqrtUnit", cached, "rtl_fpu_shim.cpp",]) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc is { ExitCode: 0, } && File.Exists(cached) ? cached : null;
    }
}

/// <summary>
///     Co-simulation of the verilated Chisel FDIV.S/FSQRT.S unit against
///     <see cref="Rv32Executor" />'s C# soft-float model: bit-identical NaN-boxed
///     results AND bit-identical fflags across IEEE special values, subnormals,
///     rounding edges, and random bit patterns — the differential drives both
///     executors and compares the register write plus the fflags each SideEffect
///     leaves behind.
///     <para>Requires verilator + g++ (via native/RtlFu/build.sh); skips if unavailable.</para>
/// </summary>
public sealed class RtlFpCoSimTests {
    // fdiv.s f3, f1, f2 (rm=0) and fsqrt.s f3, f1 (rm=0); f-regs are unified indices.
    private const uint FdivEnc = 0x182081D3;
    private const uint FsqrtEnc = 0x580081D3;

    private static readonly uint[] Corpus = [
        0x00000000, 0x80000000,             // ±0
        0x3F800000, 0xBF800000,             // ±1
        0x40000000, 0x40400000, 0x41100000, // 2, 3, 9
        0x3EAAAAAB,                         // ~1/3 (inexact everywhere)
        0x40490FDB,                         // π
        0x7F800000, 0xFF800000,             // ±Inf
        0x7FC00000, 0xFFC00001,             // quiet NaNs
        0x7FA00000,                         // signaling NaN
        0x00000001, 0x80000001,             // ±min subnormal
        0x007FFFFF, 0x00400000,             // subnormals
        0x00800000, 0x80800000,             // ±min normal
        0x7F7FFFFF, 0xFF7FFFFF,             // ±max normal
        0x3F7FFFFF, 0x3F800001,             // 1∓ulp
        0x0B800000,                         // tiny normal (drives underflow in division)
        0x7E800000,                         // huge normal (drives overflow)
    ];

    private static (ulong Bits, uint Fflags) Run(IExecutor ex, IMechanism mech, uint enc, uint aBits, uint bBits) {
        IArchState state = mech.CreateArchState();
        IRegisterFile regs = state.IntegerRegisters;
        regs.Write(33, 0xFFFFFFFF00000000UL | aBits); // f1, NaN-boxed
        regs.Write(34, 0xFFFFFFFF00000000UL | bBits); // f2
        ITooth tooth = mech.Decoder.Decode(0, enc);
        ExecuteResult r = ex.Execute(tooth, state, new FlatMemory(64));
        r.SideEffect?.Invoke(state);
        // Read fflags architecturally: csrr x6, fflags through the plain executor.
        ExecuteResult csrr = mech.Executor.Execute(
            mech.Decoder.Decode(0, 0x00102373), state, new FlatMemory(64)
        );
        return (r.RegisterResult.Value, (uint)csrr.RegisterResult.Value);
    }

    [SkippableFact]
    public void DifferentialSweep_ResultsAndFlagsMatchCSharpSoftFloat() {
        Skip.If(RtlFpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        var mech = new Rv32Mechanism();
        IExecutor plain = mech.Executor;
        using var unit = new RtlFfiFunctionalUnit(RtlFpLibrary.Path!);
        var rtl = new RvRtlFpExecutor(plain, unit);

        List<uint> ops = [.. RtlFpCoSimTests.Corpus,];
        var rng = new Random(20260718);
        for (var i = 0; i < 40; i++) ops.Add((uint)rng.Next() ^ (uint)(rng.Next() << 16)); // any bits

        foreach (uint a in ops) {
            // sqrt over the whole corpus
            (ulong expBits, uint expFlags) = RtlFpCoSimTests.Run(plain, mech, RtlFpCoSimTests.FsqrtEnc, a, 0);
            (ulong gotBits, uint gotFlags) = RtlFpCoSimTests.Run(rtl, mech, RtlFpCoSimTests.FsqrtEnc, a, 0);
            Assert.True(
                expBits == gotBits && expFlags == gotFlags,
                $"fsqrt(0x{a:X8}): C#=0x{expBits:X16}/fl={expFlags:X} RTL=0x{gotBits:X16}/fl={gotFlags:X}"
            );

            foreach (uint b in ops) {
                (expBits, expFlags) = RtlFpCoSimTests.Run(plain, mech, RtlFpCoSimTests.FdivEnc, a, b);
                (gotBits, gotFlags) = RtlFpCoSimTests.Run(rtl, mech, RtlFpCoSimTests.FdivEnc, a, b);
                Assert.True(
                    expBits == gotBits && expFlags == gotFlags,
                    $"fdiv(0x{a:X8}, 0x{b:X8}): C#=0x{expBits:X16}/fl={expFlags:X} "
                  + $"RTL=0x{gotBits:X16}/fl={gotFlags:X}"
                );
            }
        }
    }

    [SkippableFact]
    public void Latency_SpecialCasesFast_IterativeSlow() {
        Skip.If(RtlFpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        using var unit = new RtlFfiFunctionalUnit(RtlFpLibrary.Path!);
        (_, _, int special) = unit.ExecuteWithFlags(0, 0x3F800000, 0x00000000); // 1/0 → Inf+DZ
        (_, _, int iterative) = unit.ExecuteWithFlags(0, 0x3F800000, 0x40400000); // 1/3
        Assert.Equal(1, special);
        Assert.InRange(iterative, 28, 31);
    }

    [SkippableFact]
    public void OooeTrain_FpDivideProgram_CorrectThroughRtl() {
        Skip.If(RtlFpLibrary.Path is null, "verilator toolchain unavailable — skipping.");
        // x1=84 → f1; x2=2 → f2; f3 = f1/f2 = 42.0; x5 = (int)f3.
        uint[] words = [
            0x05400093, // addi x1, x0, 84
            0x00200113, // addi x2, x0, 2
            0xD00080D3, // fcvt.s.w f1, x1
            0xD0010153, // fcvt.s.w f2, x2
            RtlFpCoSimTests.FdivEnc, // fdiv.s f3, f1, f2
            0xC00192D3, // fcvt.w.s x5, f3, rtz
            0x00100073, // ebreak
        ];
        var mem = new FlatMemory(4096);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(0, bytes);

        var mech = new Rv32Mechanism();
        using var unit = new RtlFfiFunctionalUnit(RtlFpLibrary.Path!);
        mech.Executor = new RvRtlFpExecutor(mech.Executor, unit);

        var train = new OooeTrain(mech, mem);
        train.Run();
        Assert.Equal(42u, (uint)train.ArchState.IntegerRegisters.Read(5));
    }
}
