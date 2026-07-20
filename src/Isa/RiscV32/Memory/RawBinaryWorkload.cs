#region

using Mechanism;

#endregion

namespace RiscV32.Memory;

/// <summary>
///     An <see cref="IWorkload" /> that loads a flat binary (e.g., an OpenSBI fw_payload.bin)
///     at a fixed physical address. Optionally embeds a Device Tree Blob (DTB) at a separate
///     physical address so that M-mode firmware can locate the hardware description.
///     <para>
///         RISC-V boot convention: the previous firmware stage (the simulator) passes
///         <c>a0 = hartid</c> and <c>a1 = DTB physical address</c> in registers before jumping
///         to the firmware entry point. These registers are NOT set by this workload — the caller
///         must write them to the train's <see cref="IArchState.IntegerRegisters" /> after construction
///         and before calling <c>Run()</c>:
///     </para>
///     <code>
/// var train = new SingleCycleTrain(mechanism, bus, workload.EntryPoint);
/// train.ArchState.IntegerRegisters.Write(10, 0);                 // a0 = hartid
/// train.ArchState.IntegerRegisters.Write(11, workload.DtbAddress); // a1 = DTB
/// train.Run(maxTicks);
/// </code>
///     <para>
///         This works because the train's <c>Wind()</c> method only overwrites <c>Pc</c>,
///         leaving all other registers intact.
///     </para>
/// </summary>
public sealed class RawBinaryWorkload : IWorkload {
    private readonly byte[] _binary;
    private readonly byte[]? _dtb;

    /// <param name="binary">The raw binary image to load (e.g., OpenSBI fw_payload.bin).</param>
    /// <param name="baseAddress">Physical address to load <paramref name="binary" /> at (default 0x80000000).</param>
    /// <param name="entryPoint">Initial PC; defaults to <paramref name="baseAddress" />.</param>
    /// <param name="dtb">Optional Device Tree Blob bytes to embed in memory.</param>
    /// <param name="dtbAddress">
    ///     Physical address for the DTB. Defaults to the next 4 KiB-aligned address after
    ///     the binary image, leaving 4 KiB of guard space.
    /// </param>
    /// <param name="memorySizeBytes">
    ///     Explicit memory size. Defaults to enough to hold binary + DTB plus 256 KiB headroom.
    /// </param>
    public RawBinaryWorkload(
        byte[] binary,
        ulong baseAddress = 0x80000000UL,
        ulong? entryPoint = null,
        byte[]? dtb = null,
        ulong? dtbAddress = null,
        int? memorySizeBytes = null
    ) {
        _binary = binary;
        _dtb = dtb;
        BaseAddress = baseAddress;
        EntryPoint = entryPoint ?? baseAddress;
        DtbAddress = dtb is not null
            ? dtbAddress ?? AlignedDtbAddress(baseAddress, binary.Length)
            : 0UL;
        MemorySize = memorySizeBytes ?? ComputeMemorySize(baseAddress, binary.Length, DtbAddress, dtb?.Length ?? 0);
    }

    /// <summary>
    ///     Physical address where the DTB is loaded, or 0 when no DTB was provided.
    ///     Write this to <c>a1</c> (register 11) before calling <c>Run()</c>.
    /// </summary>
    public ulong DtbAddress { get; }

    /// <inheritdoc />
    public ulong EntryPoint { get; }

    /// <inheritdoc />
    public ulong BaseAddress { get; }

    /// <inheritdoc />
    public int MemorySize { get; }

    /// <inheritdoc />
    public void Load(IMemory memory) {
        memory.Load(BaseAddress, _binary);
        if (_dtb is not null) memory.Load(DtbAddress, _dtb);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // DTB placed 4 KiB above the next page-aligned boundary after the binary.
    private static ulong AlignedDtbAddress(ulong baseAddr, int binarySize) =>
        ((baseAddr + (ulong)binarySize + 0xFFFUL) & ~0xFFFUL) + 0x1000UL;

    private static int ComputeMemorySize(ulong baseAddr, int binarySize, ulong dtbAddr, int dtbSize) {
        ulong top = Math.Max(baseAddr + (ulong)binarySize, dtbSize > 0 ? dtbAddr + (ulong)dtbSize : 0UL);
        ulong span = top - baseAddr;
        // Round to next 64 KiB + 256 KiB headroom for stack / OpenSBI scratch
        return (int)(((span + 0xFFFFUL) & ~0xFFFFUL) + 0x40000UL);
    }
}