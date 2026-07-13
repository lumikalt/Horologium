using System.Reflection;

namespace RiscV32.Memory;

/// <summary>
///     Provides the precompiled Device Tree Blob for the RISC-V virt machine layout,
///     compatible with QEMU's <c>-machine virt</c> for a single RV32 hart.
///     The blob is compiled from <c>Memory/virt.dts</c> via <c>dtc</c> and embedded
///     as a resource in the <c>RiscV32</c> assembly.
///     <para>
///         Memory map described by the DTB:
///         RAM           0x80000000  128 MiB
///         CLINT         0x02000000    64 KiB
///         PLIC          0x0C000000    64 MiB
///         UART          0x10000000   256 bytes  (ns16550a, PLIC irq 10)
///         VirtIO MMIO   0x10001000     4 KiB   (block device, PLIC irq 1)
///     </para>
/// </summary>
public static class VirtDtb {
    private static byte[]? _bytes;

    /// <summary>Returns the raw DTB bytes (lazy-loaded from the embedded resource).</summary>
    public static byte[] Bytes {
        get {
            if (VirtDtb._bytes is null) {
                Assembly asm = typeof(VirtDtb).Assembly;
                using Stream stream = asm.GetManifestResourceStream("RiscV32.Memory.virt.dtb")
                                   ?? throw new InvalidOperationException(
                                          "Embedded resource 'RiscV32.Memory.virt.dtb' not found."
                                      );
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                VirtDtb._bytes = ms.ToArray();
            }

            return VirtDtb._bytes;
        }
    }
}