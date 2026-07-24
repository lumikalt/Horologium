#region

using F18A.Arbors;
using Mechanism;

#endregion

namespace F18A.Memory;

/// <summary>
///     Per-node memory for an F18A node: 512 word-addressed slots (9-bit address),
///     stored as 4-byte-aligned uint32 values in a flat byte array.
///     <para>
///         Word 0–63:    RAM (read/write).
///         Word 64–127:  ROM (read-only; initialised from program bytes).
///         Word 256–511: Port address range — reads/writes are routed through the arbor bus.
///     </para>
///     <para>
///         All addresses are byte addresses in Horologium (byte = word_address × 4).
///         The underlying capacity is 512 words = 2048 bytes.
///     </para>
/// </summary>
public sealed class F18ANodeMemory : IMemory {
    private const int WordCount = 512;
    private const int ByteCount = F18ANodeMemory.WordCount * 4;
    private const int RamWords = 64;
    public const int PortBase = 256; // first port word-address (public for node checks)

    private readonly uint[] _words = new uint[F18ANodeMemory.WordCount];

    // Wired up after construction; null slots mean "no neighbor"
    public IArborBus? ArborBus { get; init; }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        ulong end = address + (ulong)data.Length;
        if (end > F18ANodeMemory.ByteCount) throw new ArgumentOutOfRangeException(nameof(data));
        for (var i = 0; i < data.Length; i++) {
            ulong ba = address + (ulong)i;
            var wi = (uint)(ba / 4);
            int sh = (int)(ba % 4) * 8;
            _words[wi] = (_words[wi] & ~((uint)0xFF << sh)) | ((uint)data[i] << sh);
        }
    }

    public ulong Read(ulong address, int bytes) {
        var wordAddr = (uint)(address / 4);
        if (wordAddr >= F18ANodeMemory.PortBase && ArborBus is not null) {
            ArborBus.TryRead(wordAddr, out uint val);
            return val & 0x3FFFFu;
        }

        return _words[wordAddr & (F18ANodeMemory.WordCount - 1)] & 0x3FFFFu;
    }

    public void Write(ulong address, ulong value, int bytes) {
        var wordAddr = (uint)(address / 4);
        switch (wordAddr) {
            case >= F18ANodeMemory.PortBase when ArborBus is not null:
                ArborBus.TryWrite(wordAddr, (uint)value & 0x3FFFFu);
                return;
            case < F18ANodeMemory.RamWords: _words[wordAddr] = (uint)value & 0x3FFFFu; break;
        }

        // writes to ROM range (64–127) are silently ignored
    }

    /// <summary>Checks whether a port op at the given word address will succeed this tick.</summary>
    public bool IsPortReady(uint wordAddr, bool isRead) => ArborBus is null || ArborBus.IsReady(wordAddr, isRead);
}