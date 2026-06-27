using System.Buffers.Binary;
using System.Text;
using Mechanism;

namespace RiscV32.Memory;

/// <summary>
/// An <see cref="IWorkload"/> that loads a bare-metal ELF32 RISC-V binary.
/// Entry point and minimum memory size are derived from the ELF headers so
/// callers need only supply the file path.
/// </summary>
public sealed class Rv32ElfWorkload : IWorkload {
    private readonly byte[] _elfBytes;

    public ulong EntryPoint { get; }
    public int MemorySize { get; }
    public int CodeSize => _elfBytes.Length;

    public Rv32ElfWorkload(string path, int? memorySizeBytes = null)
        : this(File.ReadAllBytes(path), memorySizeBytes) { }

    public Rv32ElfWorkload(byte[] elfBytes, int? memorySizeBytes = null) {
        _elfBytes = elfBytes;
        EntryPoint = ParseEntryPoint(elfBytes);
        MemorySize = memorySizeBytes ?? ComputeMinMemorySize(elfBytes);
    }

    public void Load(IMemory memory) => Rv32ElfLoader.Load(memory, _elfBytes);

    /// <summary>
    /// Returns the virtual address of a named ELF symbol, or throws if not found.
    /// </summary>
    public ulong FindSymbol(string name) {
        ReadOnlySpan<byte> elf = _elfBytes;
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[32..]);
        ushort shentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[46..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[48..]);

        for (var i = 0; i < shnum; i++) {
            var shdr = (int)(shoff + (uint)(i * shentsz));
            uint shType = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 4)..]);
            if (shType != 2) continue; // SHT_SYMTAB

            uint symOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 16)..]);
            uint symSz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 20)..]);
            uint strtabIdx = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 24)..]);

            var strtabHdr = (int)(shoff + strtabIdx * shentsz);
            uint strtabOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[(strtabHdr + 16)..]);

            for (uint s = 0; s < symSz / 16; s++) {
                var sym = (int)(symOff + s * 16);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[sym..]);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(elf[(sym + 4)..]);

                var start = (int)(strtabOff + nameOff);
                int end = start;
                while (end < _elfBytes.Length && _elfBytes[end] != 0) end++;
                if (Encoding.ASCII.GetString(_elfBytes, start, end - start) == name) return value;
            }
        }

        throw new KeyNotFoundException($"ELF symbol '{name}' not found");
    }

    // ── ELF header parsing ────────────────────────────────────────────────────

    private static ulong ParseEntryPoint(ReadOnlySpan<byte> elf) =>
        BinaryPrimitives.ReadUInt32LittleEndian(elf[24..]);

    private static int ComputeMinMemorySize(ReadOnlySpan<byte> elf) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        uint maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            uint memsz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 20)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Round to next 64 KB boundary and add 64 KB for stack/heap.
        return (int)((maxEnd + 0xFFFF) & ~0xFFFFU) + 0x10000;
    }
}