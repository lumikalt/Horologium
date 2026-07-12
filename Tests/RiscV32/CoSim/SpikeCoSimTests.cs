using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.CoSim;
using RiscV32.Memory;

namespace Tests.RiscV32.CoSim;

/// <summary>
/// Lock-step co-simulation tests against Spike — the project's correctness
/// contract for the ISA datapath (see the "Co-simulation contract" section of
/// the README).
/// <para>
/// These tests require the <c>spike</c> simulator and the <c>dtc</c> device-tree
/// compiler. The Nix dev-shell provides both; outside the shell run
/// <c>direnv reload</c> first. When the toolchain is absent the tests
/// <em>skip</em> (via <see cref="SkippableFactAttribute"/>) rather than fail —
/// unless <c>HOROLOGIUM_REQUIRE_COSIM</c> is set, in which case a missing
/// toolchain is a hard failure so a CI job that enforces the contract cannot
/// silently pass with the checks skipped.
/// </para>
/// <para>
/// Each test runs one of the three trains against an ELF with Spike attached
/// as a live <see cref="ICommitObserver"/>; a <see cref="CoSimDivergenceException"/>
/// is thrown at the first commit that disagrees with Spike. <c>test.elf</c> is
/// the simple RV32I golden path; <c>rich.elf</c> (RV32IM) adds multiply/divide,
/// an insertion sort, and heavy data-dependent branching to exercise the
/// multi-cycle functional units, store-to-load forwarding, and flush paths.
/// </para>
/// </summary>
public class SpikeCoSimTests {
    /// <summary>Set to 1/true to turn a missing Spike toolchain into a hard failure.</summary>
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_COSIM";

    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    // Skips the test when the Spike toolchain is unavailable, unless the run is
    // configured to require co-sim (HOROLOGIUM_REQUIRE_COSIM) — then it fails
    // loudly, so an enforcing CI cannot pass with the contract silently skipped.
    private static void RequireSpikeOrSkip() {
        if (SpikeCoSimReference.IsAvailable()) return;

        bool required = Environment.GetEnvironmentVariable(SpikeCoSimTests.RequireEnvVar) is "1" or "true";
        if (required)
            throw new InvalidOperationException(
                $"{SpikeCoSimTests.RequireEnvVar} is set but the Spike co-sim toolchain (spike + dtc) was not found. " +
                "Run inside the Nix dev-shell, which provides both, or unset the variable to allow skipping."
            );

        Skip.If(true, "spike/dtc not available — skipping co-sim. Set HOROLOGIUM_REQUIRE_COSIM=1 to require it.");
    }

    // Runs the ELF through the train produced by trainFactory with Spike attached.
    // Spike runs as a live child process; dispose kills it when the run ends.
    // memorySize overrides the ELF-derived sizing (the HTIF fixture reserves a
    // stack beyond its tiny load extent, so it needs an explicit region size).
    // isa overrides the Spike --isa= string (default rv32imafcv covers I/M/A/F/C/V).
    private static void RunCoSim(
        string elfName,
        Func<IMechanism, IMemory, ulong, ICommitObserver, object> trainFactory,
        int? memorySize = null,
        string isa = "rv32imafcv"
    ) {
        RequireSpikeOrSkip();

        string elfPath = ElfPath(elfName);
        var workload = new Rv32ElfWorkload(elfPath, memorySize);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(elfPath, workload.BaseAddress, workload.MemorySize, isa);

        // When the ELF exits via HTIF, give the mechanism the tohost address so
        // the exit store terminates the run at the write itself (first-class
        // RequestHalt). ELFs without tohost (EBREAK-terminated) get null.
        ulong? tohost = workload.TryFindSymbol("tohost", out ulong tohostAddr) ? tohostAddr : null;

        // CoSimDivergenceException is thrown on the first mismatch during Run().
        switch (trainFactory(new Rv32Mechanism(tohost), mem, workload.EntryPoint, cosim)) {
            case SingleCycleTrain t: t.Run(); break;
            case FiveStageTrain t:   t.Run(); break;
            case OooeTrain t:        t.Run(); break;
            default:                 throw new InvalidOperationException("unknown train");
        }
    }

    private static SingleCycleTrain SingleCycle(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static FiveStageTrain FiveStage(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    private static OooeTrain Oooe(IMechanism m, IMemory mem, ulong pc, ICommitObserver o) =>
        new(m, mem, pc, commitObserver: o);

    // ── test.elf: simple RV32I golden path ──────────────────────────────────────

    [SkippableFact]
    public void SingleCycle_TestElf_MatchesSpike() => RunCoSim("test.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_TestElf_MatchesSpike() => RunCoSim("test.elf", FiveStage);

    [SkippableFact]
    public void Oooe_TestElf_MatchesSpike() => RunCoSim("test.elf", Oooe);

    // ── rich.elf: RV32IM mul/div + sort + heavy branching ───────────────────────

    [SkippableFact]
    public void SingleCycle_RichElf_MatchesSpike() => RunCoSim("rich.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_RichElf_MatchesSpike() => RunCoSim("rich.elf", FiveStage);

    [SkippableFact]
    public void Oooe_RichElf_MatchesSpike() => RunCoSim("rich.elf", Oooe);

    // ── htif.elf: RV32IM workload that exits via the HTIF tohost register ────────
    //
    // Spike exits cleanly (no EBREAK debug-stub hang) and logs the post-exit
    // self-loop an indeterminate number of times; each train commits the exit
    // store and a single self-loop jump, then halts, so its stream is a clean
    // prefix of Spike's. A 1 MB region covers the fixture's reserved stack.

    private const int HtifMemoryBytes = 0x100000;

    [SkippableFact]
    public void SingleCycle_HtifElf_MatchesSpike() =>
        RunCoSim("htif.elf", SingleCycle, SpikeCoSimTests.HtifMemoryBytes);

    [SkippableFact]
    public void FiveStage_HtifElf_MatchesSpike() => RunCoSim("htif.elf", FiveStage, SpikeCoSimTests.HtifMemoryBytes);

    [SkippableFact]
    public void Oooe_HtifElf_MatchesSpike() => RunCoSim("htif.elf", Oooe, SpikeCoSimTests.HtifMemoryBytes);

    // ── Official riscv-tests conformance suite ────────────────────────────────
    //
    // Co-simulates every conformance ELF under TestBinaries/isa/.  Run on the
    // single-cycle train: this validates the decoder/executor against Spike
    // across the whole suite; the per-train datapaths are covered by the
    // test/rich/htif fixtures on all three trains.  rv32si (supervisor) tests
    // are excluded — their trap-handler control flow is a separate concern.
    //
    // ma_data is excluded by design: Spike traps misaligned data access while
    // Horologium's FlatMemory permits it directly, so co-sim cannot apply.
    //
    // rv32uz* (bit-manipulation, Zicond) need extended ISA strings — handled by
    // separate theories below.

    private static readonly string IsaDir = Path.Combine(AppContext.BaseDirectory, "isa");

    // rv32ui / rv32um / rv32ua / rv32uc / rv32uf — all covered by rv32imafcv.
    public static IEnumerable<object[]> ConformanceElfs() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv32u*.elf")
                 .Where(p => !p.Contains("ma_data")
                          && !Path.GetFileName(p).StartsWith("rv32uz")
                  )
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfs))]
    public void SingleCycle_Conformance_MatchesSpike(string elf) => RunCoSim(elf, SingleCycle);

    // rv32uzba / rv32uzbb / rv32uzbc / rv32uzbs — need Zba/Zbb/Zbc/Zbs in ISA string.
    public static IEnumerable<object[]> ConformanceElfsZb() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv32uzb*.elf")
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfsZb))]
    public void SingleCycle_ConformanceZb_MatchesSpike(string elf) =>
        RunCoSim(elf, SingleCycle, isa: "rv32imafcv_zba_zbb_zbc_zbs");

    // rv32uzicond — needs Zicond in ISA string.
    public static IEnumerable<object[]> ConformanceElfsZicond() =>
        Directory.EnumerateFiles(SpikeCoSimTests.IsaDir, "rv32uzicond-p-*.elf")
                 .OrderBy(p => p)
                 .Select(p => new object[] { Path.Combine("isa", Path.GetFileName(p)), });

    [SkippableTheory]
    [MemberData(nameof(ConformanceElfsZicond))]
    public void SingleCycle_ConformanceZicond_MatchesSpike(string elf) =>
        RunCoSim(elf, SingleCycle, isa: "rv32imafcv_zicond");

    // ── zcmop.elf: Zcmop c.mop.N hint NOPs on all three trains ─────────────────
    //
    // Requires Spike with Zcmop support (available since 1.1.0-unstable-2024-09-21
    // with --isa=..._zcmop). Verifies that all 8 c.mop.N instructions commit as
    // hint NOPs (no register write) and that PC advances agree between Horologium
    // and Spike.

    private static void RunCoSimZcmop(Func<IMechanism, IMemory, ulong, ICommitObserver, object> trainFactory) {
        RequireSpikeOrSkip();

        string elfPath = ElfPath("zcmop.elf");
        var workload = new Rv32ElfWorkload(elfPath);

        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        using var cosim = new SpikeCoSimReference(
            elfPath,
            workload.BaseAddress,
            workload.MemorySize,
            "rv32gc_zcmop"
        );

        switch (trainFactory(new Rv32Mechanism(), mem, workload.EntryPoint, cosim)) {
            case SingleCycleTrain t: t.Run(); break;
            case FiveStageTrain t:   t.Run(); break;
            case OooeTrain t:        t.Run(); break;
            default:                 throw new InvalidOperationException("unknown train");
        }
    }

    [SkippableFact]
    public void SingleCycle_ZcmopElf_MatchesSpike() => RunCoSimZcmop(SingleCycle);

    [SkippableFact]
    public void FiveStage_ZcmopElf_MatchesSpike() => RunCoSimZcmop(FiveStage);

    [SkippableFact]
    public void Oooe_ZcmopElf_MatchesSpike() => RunCoSimZcmop(Oooe);

    // ── zimop.elf: Zimop mop.r.N / mop.rr.N hint NOPs ────────────────────────

    [SkippableFact]
    public void SingleCycle_ZimopElf_MatchesSpike() =>
        RunCoSim("zimop.elf", SingleCycle, isa: "rv32imafcv_zimop");

    [SkippableFact]
    public void FiveStage_ZimopElf_MatchesSpike() =>
        RunCoSim("zimop.elf", FiveStage, isa: "rv32imafcv_zimop");

    [SkippableFact]
    public void Oooe_ZimopElf_MatchesSpike() =>
        RunCoSim("zimop.elf", Oooe, isa: "rv32imafcv_zimop");

    // ── zabha.elf: Zabha byte / halfword AMOs ─────────────────────────────────

    [SkippableFact]
    public void SingleCycle_ZabhaElf_MatchesSpike() =>
        RunCoSim("zabha.elf", SingleCycle, isa: "rv32imafcv_zabha");

    [SkippableFact]
    public void FiveStage_ZabhaElf_MatchesSpike() =>
        RunCoSim("zabha.elf", FiveStage, isa: "rv32imafcv_zabha");

    [SkippableFact]
    public void Oooe_ZabhaElf_MatchesSpike() =>
        RunCoSim("zabha.elf", Oooe, isa: "rv32imafcv_zabha");

    // ── zawrs.elf: Zawrs wrs.nto / wrs.sto hint NOPs ─────────────────────────

    [SkippableFact]
    public void SingleCycle_ZawrsElf_MatchesSpike() =>
        RunCoSim("zawrs.elf", SingleCycle, isa: "rv32imafcv_zawrs");

    [SkippableFact]
    public void FiveStage_ZawrsElf_MatchesSpike() =>
        RunCoSim("zawrs.elf", FiveStage, isa: "rv32imafcv_zawrs");

    [SkippableFact]
    public void Oooe_ZawrsElf_MatchesSpike() =>
        RunCoSim("zawrs.elf", Oooe, isa: "rv32imafcv_zawrs");

    // ── cbo.elf: Zicbom / Zicboz cache block operations ──────────────────────

    [SkippableFact]
    public void SingleCycle_CboElf_MatchesSpike() =>
        RunCoSim("cbo.elf", SingleCycle, isa: "rv32imafcv_zicbom_zicboz");

    [SkippableFact]
    public void FiveStage_CboElf_MatchesSpike() =>
        RunCoSim("cbo.elf", FiveStage, isa: "rv32imafcv_zicbom_zicboz");

    [SkippableFact]
    public void Oooe_CboElf_MatchesSpike() =>
        RunCoSim("cbo.elf", Oooe, isa: "rv32imafcv_zicbom_zicboz");

    // ── vector.elf: RVV vsetvli / vmv.v.x / vadd.vv / vmv.x.s ───────────────

    [SkippableFact]
    public void SingleCycle_VectorElf_MatchesSpike() => RunCoSim("vector.elf", SingleCycle);

    [SkippableFact]
    public void FiveStage_VectorElf_MatchesSpike() => RunCoSim("vector.elf", FiveStage);

    [SkippableFact]
    public void Oooe_VectorElf_MatchesSpike() => RunCoSim("vector.elf", Oooe);

    // ── ReadLine watchdog: a stalled or ended Spike must fail cleanly, not hang ──
    //
    // These use a stub in place of the spike binary, so they run (and matter)
    // even where the real toolchain is absent.

    private static string WriteStubSpike(string body) {
        string path = Path.Combine(Path.GetTempPath(), $"stub-spike-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Watchdog_SilentSpike_TimesOutInsteadOfHanging() {
        string stub = WriteStubSpike("exec sleep 300");
        try {
            using var cosim = new SpikeCoSimReference(
                "/dev/null",
                readTimeout: TimeSpan.FromMilliseconds(500),
                spikeExecutable: stub
            );
            var ex = Assert.Throws<CoSimDivergenceException>(() => cosim.OnCommit(0x80000000UL, 0x00000013u, null!)
            );
            Assert.Contains("No Spike commit record", ex.Message);
        }
        finally { File.Delete(stub); }
    }

    [Fact]
    public void Watchdog_ExitingSpike_ReportsLogEnd() {
        string stub = WriteStubSpike("exit 0");
        try {
            using var cosim = new SpikeCoSimReference(
                "/dev/null",
                readTimeout: TimeSpan.FromSeconds(5),
                spikeExecutable: stub
            );
            var ex = Assert.Throws<CoSimDivergenceException>(() => cosim.OnCommit(0x80000000UL, 0x00000013u, null!)
            );
            Assert.Contains("ended unexpectedly", ex.Message);
        }
        finally { File.Delete(stub); }
    }
}