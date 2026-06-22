using System.Buffers.Binary;
using Mechanism;

namespace RiscV.Memory;

/// <summary>
/// An <see cref="IWorkload"/> that loads a bare-metal ELF32 RISC-V binary.
/// Entry point and minimum memory size are derived from the ELF headers so
/// callers need only supply the file path.
/// </summary>
public sealed class ElfWorkload : IWorkload {
    private readonly byte[] _elfBytes;

    public ulong EntryPoint { get; }
    public int MemorySize { get; }

    public ElfWorkload(string path, int? memorySizeBytes = null)
        : this(File.ReadAllBytes(path), memorySizeBytes) { }

    public ElfWorkload(byte[] elfBytes, int? memorySizeBytes = null) {
        _elfBytes = elfBytes;
        EntryPoint = ParseEntryPoint(elfBytes);
        MemorySize = memorySizeBytes ?? ComputeMinMemorySize(elfBytes);
    }

    public void Load(IMemory memory) => ElfLoader.Load(memory, _elfBytes);

    // ── ELF header parsing ────────────────────────────────────────────────────

    private static ulong ParseEntryPoint(ReadOnlySpan<byte> elf) =>
        BinaryPrimitives.ReadUInt32LittleEndian(elf[24..]);

    private static int ComputeMinMemorySize(ReadOnlySpan<byte> elf) {
        uint phoff     = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum   = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        uint maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            int ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            uint memsz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 20)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Round to next 64 KB boundary and add 64 KB for stack/heap.
        return (int)((maxEnd + 0xFFFF) & ~0xFFFFU) + 0x10000;
    }
}
