using System.Buffers.Binary;
using Mechanism;

namespace RiscV32.Memory;

/// <summary>
///     Minimal ELF32 loader for bare-metal RISC-V executables.
///     Loads all PT_LOAD segments into an IMemory and returns the entry point.
///     Only little-endian ELF32 with machine type EM_RISCV (0xF3) is accepted.
/// </summary>
public static class Rv32ElfLoader {
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

        if (elf[Rv32ElfLoader.EhdrClass] != Rv32ElfLoader.ElfClass32)
            throw new ElfException($"Only ELF32 is supported (class byte = {elf[Rv32ElfLoader.EhdrClass]}).");
        if (elf[Rv32ElfLoader.EhdrData] != Rv32ElfLoader.ElfData2Lsb)
            throw new ElfException("Only little-endian ELF is supported.");

        ushort machine = U16(elf, Rv32ElfLoader.EhdrMachine);
        if (machine != Rv32ElfLoader.EmRiscv)
            throw new ElfException(
                $"Unexpected e_machine 0x{machine:X2}, expected EM_RISCV (0x{Rv32ElfLoader.EmRiscv:X2})."
            );

        ulong entry = U32(elf, Rv32ElfLoader.EhdrEntry);
        uint phoff = U32(elf, Rv32ElfLoader.EhdrPhoff);
        ushort phentsize = U16(elf, Rv32ElfLoader.EhdrPhentsize);
        ushort phnum = U16(elf, Rv32ElfLoader.EhdrPhnum);

        for (var i = 0; i < phnum; i++) {
            var phdrStart = (int)(phoff + (uint)(i * phentsize));
            uint type = U32(elf, phdrStart + Rv32ElfLoader.PhdrType);
            if (type != Rv32ElfLoader.PtLoad) continue;

            uint fileOffset = U32(elf, phdrStart + Rv32ElfLoader.PhdrOffset);
            uint paddr = U32(elf, phdrStart + Rv32ElfLoader.PhdrPaddr);
            uint filesz = U32(elf, phdrStart + Rv32ElfLoader.PhdrFilesz);
            uint memsz = U32(elf, phdrStart + Rv32ElfLoader.PhdrMemsz);

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