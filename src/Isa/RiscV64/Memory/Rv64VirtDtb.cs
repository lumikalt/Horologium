using System.Reflection;

namespace RiscV64.Memory;

/// <summary>
/// Provides the precompiled Device Tree Blob for the RISC-V virt machine layout,
/// compatible with QEMU's <c>-machine virt</c> for a single RV64 hart.
/// The blob is compiled from <c>Memory/virt64.dts</c> via <c>dtc</c> and embedded
/// as a resource in the <c>RiscV64</c> assembly.
/// <para>
/// Same memory map as <see cref="RiscV32.Memory.VirtDtb"/>, but with
/// <c>#address-cells = 2</c>/<c>#size-cells = 2</c> (RV64 convention),
/// <c>riscv,isa = "rv64imafdc"</c>, and <c>mmu-type = "riscv,sv39"</c>:
///   RAM           0x80000000  128 MiB
///   CLINT         0x02000000    64 KiB
///   PLIC          0x0C000000    64 MiB
///   UART          0x10000000   256 bytes  (ns16550a, PLIC irq 10)
///   VirtIO MMIO   0x10001000     4 KiB   (block device, PLIC irq 1)
/// </para>
/// </summary>
public static class Rv64VirtDtb {
    private static byte[]? _bytes;

    /// <summary>Returns the raw DTB bytes (lazy-loaded from the embedded resource).</summary>
    public static byte[] Bytes {
        get {
            if (Rv64VirtDtb._bytes is null) {
                Assembly asm = typeof(Rv64VirtDtb).Assembly;
                using Stream stream = asm.GetManifestResourceStream("RiscV64.Memory.virt64.dtb")
                                   ?? throw new InvalidOperationException(
                                          "Embedded resource 'RiscV64.Memory.virt64.dtb' not found."
                                      );
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Rv64VirtDtb._bytes = ms.ToArray();
            }

            return Rv64VirtDtb._bytes;
        }
    }
}