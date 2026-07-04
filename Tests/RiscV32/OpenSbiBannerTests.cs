using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Boots OpenSBI generic platform (RV32, fw_jump) on a SingleCycleTrain and
/// verifies that the banner "OpenSBI" appears on the ns16550a UART output.
/// <para>
/// Requires a pre-built <c>fw_jump.bin</c>. Build it with:
/// <code>nix build .#opensbi-rv32</code>
/// This places the binary at <c>result/share/opensbi/fw_jump.bin</c> relative
/// to the repository root. The test also accepts a path via the environment
/// variable <c>OPENSBI_FW_JUMP_BIN</c>.
/// </para>
/// <para>
/// Memory layout (base 0x80000000):
///   0x80000000  OpenSBI fw_jump.bin (~265 KB)
///   0x80100000  virt.dtb (embedded in RiscV32 assembly)
///   0x80200000  ebreak — stops simulation after OpenSBI drops to S-mode
///   Total RAM:  6 MiB (enough for the above plus OpenSBI scratch space)
/// </para>
/// <para>
/// Peripheral bus:
///   CLINT  0x02000000  64 KiB  — mtime, mtimecmp, msip
///   UART   0x10000000  256 B   — ns16550a, output captured in a StringWriter
/// </para>
/// </summary>
public class OpenSbiBannerTests {
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_OPENSBI";

    private const ulong RamBase      = 0x80000000UL;
    private const ulong DtbAddr      = 0x80100000UL;
    private const ulong JumpTarget   = 0x80200000UL;
    private const int   RamSize      = 64 * 1024 * 1024; // 64 MiB (DTS declares 128 MiB; covers OpenSBI scratch area ~34 MiB in)

    private static string? FindFwJump() {
        // 1. Explicit env var
        string? env = Environment.GetEnvironmentVariable("OPENSBI_FW_JUMP_BIN");
        if (env is not null && File.Exists(env)) return env;

        // 2. result/share/opensbi/fw_jump.bin relative to repo root (after nix build .#opensbi-rv32)
        // AppContext.BaseDirectory = .../Tests/bin/Debug/net11.0/ → 4 levels up = repo root
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string candidate = Path.Combine(repoRoot, "result", "share", "opensbi", "fw_jump.bin");
        if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static void RequireFwJumpOrSkip() {
        bool required = Environment.GetEnvironmentVariable(RequireEnvVar) is "1" or "true";
        if (FindFwJump() is not null) return;
        if (required)
            throw new InvalidOperationException(
                $"fw_jump.bin not found and {RequireEnvVar}=1. " +
                "Build it with: nix build .#opensbi-rv32");
        Skip.If(true,
            "fw_jump.bin not found — skipping OpenSBI boot test. " +
            "Build with 'nix build .#opensbi-rv32', then re-run. " +
            $"Set {RequireEnvVar}=1 to require it.");
    }

    [SkippableFact]
    public void SingleCycle_OpenSBI_PrintsBanner() {
        RequireFwJumpOrSkip();
        string fwPath = FindFwJump()!;
        byte[] fwBytes = File.ReadAllBytes(fwPath);

        // ── Memory ─────────────────────────────────────────────────────────────
        var mem = new FlatMemory(RamSize, RamBase);

        // Load OpenSBI at RAM base
        mem.Load(RamBase, fwBytes);

        // Load DTB at 0x80100000
        mem.Load(DtbAddr, VirtDtb.Bytes);

        // Plant ebreak at the jump target (0x80200000) so execution stops cleanly
        // after OpenSBI hands off to S-mode.  ebreak = 0x00100073 (little-endian).
        mem.Write(JumpTarget, 0x10500073u, 4); // wfi: halts simulation when OpenSBI jumps to S-mode

        // ── Peripherals ────────────────────────────────────────────────────────
        var clint = new ClintDevice();
        var plic  = new PlicDevice();
        var uart  = new Ns16550aUart(new StringWriter());
        IMemory bus = new PeripheralBus(
            mem,
            [
                (clint, ClintDevice.DefaultBase,  ClintDevice.RegionSize),
                (plic,  PlicDevice.DefaultBase,   PlicDevice.RegionSize),
                (uart,  Ns16550aUart.DefaultBase,  Ns16550aUart.RegionSize),
            ]
        );

        // ── Mechanism + train ──────────────────────────────────────────────────
        var mechanism = new Rv32Mechanism(clint: clint, plic: plic);
        var train = new SingleCycleTrain(mechanism, bus, RamBase);

        // Set RISC-V boot protocol registers (prior firmware stage sets these):
        //   a0 = hartid = 0
        //   a1 = physical DTB address
        train.ArchState.IntegerRegisters.Write(10, 0);
        train.ArchState.IntegerRegisters.Write(11, DtbAddr);

        // Run until ebreak halts execution or the tick budget expires.
        // OpenSBI initialises quickly; 50M ticks is generous headroom.
        train.Run(50_000_000);

        // ── Verify ─────────────────────────────────────────────────────────────
        string uartOut = ((StringWriter)uart.Output).ToString();
        Assert.Contains("OpenSBI", uartOut);
    }
}
