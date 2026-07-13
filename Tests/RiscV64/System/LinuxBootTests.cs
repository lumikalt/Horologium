using Mechanism;
using Orrery.Devices;
using Pipeline;
using RiscV32.Memory;
using RiscV64;
using RiscV64.Memory;
using Xunit.Abstractions;

namespace Tests.RiscV64.System;

/// <summary>
///     RV64 counterpart of <see cref="Tests.RiscV32.System.LinuxBootTests" /> — boots Linux
///     6.12 RV64 NOMMU on a SingleCycleTrain and verifies that the kernel version banner
///     appears on the ns16550a UART output.
///     Prerequisites:
///     nix build .#linux-rv64 -o result-linux-rv64   → result-linux-rv64/share/linux/Image
///     The out-link name must differ from the RV32 test's `result-linux`/`result` — otherwise
///     building one kernel clobbers the other test's discovery path (same collision class as
///     the OpenSBI RV32/RV64 firmware images; see <see cref="OpenSbiBannerTests" />).
///     The kernel is CONFIG_RISCV_M_MODE=y (nommu_virt_defconfig, no 32-bit.config fragment),
///     so it runs entirely in M-mode. We boot it directly — no OpenSBI — by setting the
///     initial PC to KernelAddr. The simulator already starts in M-mode.
///     Memory layout (base 0x80000000, 128 MiB RAM — matches Rv64VirtDtb memory node):
///     0x80000000  Linux Image         — NOMMU kernel (PAGE_OFFSET = 0x80000000)
///     0x80400000  Rv64VirtDtb.Bytes   — device tree blob (after ~2.5 MiB kernel)
///     Peripheral bus:
///     CLINT   0x02000000   64 KiB
///     PLIC    0x0C000000   64 MiB
///     UART    0x10000000  256 B   (ns16550a, output captured in StringWriter)
///     The kernel cmdline (compiled-in, CMDLINE_FORCE) is:
///     "earlycon=uart8250,mmio,0x10000000,115200n8 console=ttyS0"
///     so UART output starts before any driver init.  The test catches
///     "Linux version" which appears in the very first dmesg line.
/// </summary>
public class LinuxBootTests(ITestOutputHelper testOutputHelper) {
    private const string RequireEnvVar = "HOROLOGIUM_REQUIRE_LINUX";

    private const ulong RamBase = 0x80000000UL;
    private const ulong KernelAddr = 0x80000000UL; // PAGE_OFFSET=0x80000000; kernel must load at RAM base
    private const ulong DtbAddr = 0x80400000UL;    // after kernel (~2.5 MiB), well within 128 MiB RAM
    private const int RamSize = 128 * 1024 * 1024; // 128 MiB — matches Rv64VirtDtb memory node
    private static readonly string[] SourceArray = ["result-linux-rv64", "result-linux", "result",];

    // ── Image discovery ───────────────────────────────────────────────────────

    private static string? FindLinuxImage() {
        string? env = Environment.GetEnvironmentVariable("LINUX64_IMAGE");
        if (env is not null && File.Exists(env)) return env;
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")
        );
        return LinuxBootTests.SourceArray.Select(prefix => Path.Combine(repoRoot, prefix, "share", "linux", "Image"))
                             .FirstOrDefault(File.Exists);
    }

    private static void RequireImagesOrSkip() {
        bool required = Environment.GetEnvironmentVariable(LinuxBootTests.RequireEnvVar) is "1" or "true";
        bool hasKernel = FindLinuxImage() is not null;

        if (hasKernel) return;

        if (required)
            throw new InvalidOperationException(
                $"Linux kernel image not found and {LinuxBootTests.RequireEnvVar}=1. " +
                "Build with: nix build .#linux-rv64 -o result-linux-rv64"
            );
        Skip.If(
            true,
            "Linux kernel image not found — skipping Linux boot test. " +
            "Build with 'nix build .#linux-rv64 -o result-linux-rv64', then re-run. " +
            $"Set {LinuxBootTests.RequireEnvVar}=1 to require it."
        );
    }

    // ── Test ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void SingleCycle_Linux_PrintsBanner() {
        RequireImagesOrSkip();

        byte[] kernelBytes = File.ReadAllBytes(FindLinuxImage()!);

        // ── Memory ─────────────────────────────────────────────────────────
        var mem = new FlatMemory(LinuxBootTests.RamSize, LinuxBootTests.RamBase);
        mem.Load(LinuxBootTests.DtbAddr, Rv64VirtDtb.Bytes);
        mem.Load(LinuxBootTests.KernelAddr, kernelBytes);

        // ── Peripherals ────────────────────────────────────────────────────
        var clint = new ClintDevice();
        var plic = new PlicDevice();
        var uart = new Ns16550AUart(new StringWriter());
        IMemory bus = new PeripheralBus(
            mem,
            [
                (clint, ClintDevice.DefaultBase, ClintDevice.RegionSize),
                (plic, PlicDevice.DefaultBase, PlicDevice.RegionSize),
                (uart, Ns16550AUart.DefaultBase, Ns16550AUart.RegionSize),
            ]
        );

        // ── Boot directly into M-mode kernel (no OpenSBI) ──────────────────
        var mechanism = new Rv64Mechanism(clint: clint, plic: plic);
        var train = new SingleCycleTrain(mechanism, bus, LinuxBootTests.KernelAddr);

        // RISC-V boot protocol: a0 = hartid = 0, a1 = DTB physical address
        train.ArchState.IntegerRegisters.Write(10, 0);
        train.ArchState.IntegerRegisters.Write(11, LinuxBootTests.DtbAddr);

        train.Run(1_000_000);
        var uartOut = ((StringWriter)uart.Output).ToString();
        testOutputHelper.WriteLine(
            $"[LinuxBoot] uart={uartOut.Length} bytes  pc=0x{train.ArchState.Pc:X16}"
        );

        Assert.Contains("Linux version", uartOut);
    }
}