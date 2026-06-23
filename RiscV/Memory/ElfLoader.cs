using System.Buffers.Binary;
using Mechanism;

namespace RiscV.Memory;

/// <summary>
/// Minimal ELF32 loader for bare-metal RISC-V executables.
/// Loads all PT_LOAD segments into an IMemory and returns the entry point.
/// Only little-endian ELF32 with machine type EM_RISCV (0xF3) is accepted.
/// </summary>
public static class ElfLoader {
    private const uint PtLoad = 1;
    private const ushort EmRiscv = 0xF3;
    private const byte ElfClass32 = 1;
    private const byte ElfData2Lsb = 1;

    // ELF32 header field offsets
    private const int EhdrClass = 4;
    private const int EhdrData = 5;
    private const int EhdrMachine = 18;
    private const int EhdrEntry = 24;
    private const int EhdrPhoff = 28;
    private const int EhdrPhentsize = 42;
    private const int EhdrPhnum = 44;

    // ELF32 program header field offsets (relative to phdr start)
    private const int PhdrType = 0;
    private const int PhdrOffset = 4;
    private const int PhdrPaddr = 12;
    private const int PhdrFilesz = 16;
    private const int PhdrMemsz = 20;

    public static ulong LoadFile(IMemory memory, string path) =>
        Load(memory, File.ReadAllBytes(path));

    public static ulong Load(IMemory memory, ReadOnlySpan<byte> elf) {
        if (elf.Length < 52) throw new ElfException("File too small to be a valid ELF.");

        ValidateMagic(elf);

        if (elf[ElfLoader.EhdrClass] != ElfLoader.ElfClass32)
            throw new ElfException($"Only ELF32 is supported (class byte = {elf[ElfLoader.EhdrClass]}).");
        if (elf[ElfLoader.EhdrData] != ElfLoader.ElfData2Lsb)
            throw new ElfException("Only little-endian ELF is supported.");

        ushort machine = U16(elf, ElfLoader.EhdrMachine);
        if (machine != ElfLoader.EmRiscv)
            throw new ElfException(
                $"Unexpected e_machine 0x{machine:X2}, expected EM_RISCV (0x{ElfLoader.EmRiscv:X2})."
            );

        ulong entry = U32(elf, ElfLoader.EhdrEntry);
        uint phoff = U32(elf, ElfLoader.EhdrPhoff);
        ushort phentsize = U16(elf, ElfLoader.EhdrPhentsize);
        ushort phnum = U16(elf, ElfLoader.EhdrPhnum);

        for (var i = 0; i < phnum; i++) {
            var phdrStart = (int)(phoff + (uint)(i * phentsize));
            uint type = U32(elf, phdrStart + ElfLoader.PhdrType);
            if (type != ElfLoader.PtLoad) continue;

            uint fileOffset = U32(elf, phdrStart + ElfLoader.PhdrOffset);
            uint paddr = U32(elf, phdrStart + ElfLoader.PhdrPaddr);
            uint filesz = U32(elf, phdrStart + ElfLoader.PhdrFilesz);
            uint memsz = U32(elf, phdrStart + ElfLoader.PhdrMemsz);

            // Load file bytes
            if (filesz > 0) memory.Load(paddr, elf.Slice((int)fileOffset, (int)filesz));

            // Zero BSS region (memsz > filesz)
            if (memsz > filesz) {
                var zeros = new byte[memsz - filesz];
                memory.Load(paddr + filesz, zeros);
            }
        }

        return entry;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void ValidateMagic(ReadOnlySpan<byte> elf) {
        if (elf[0] != 0x7F || elf[1] != 'E' || elf[2] != 'L' || elf[3] != 'F')
            throw new ElfException("Not an ELF file (bad magic bytes).");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}

public sealed class ElfException(string message) : Exception(message);