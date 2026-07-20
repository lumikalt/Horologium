#region

using System.Buffers.Binary;
using Mechanism;
using RiscV32.Memory;

#endregion

namespace RiscV64.Memory;

/// <summary>
///     Minimal ELF64 loader for bare-metal RV64 executables.
///     Loads all PT_LOAD segments into an IMemory and returns the entry point.
///     Only little-endian ELF64 with machine type EM_RISCV (0xF3) is accepted.
/// </summary>
public static class Rv64ElfLoader {
    private const uint PtLoad = 1;
    private const ushort EmRiscv = 0xF3;
    private const byte ElfClass64 = 2;
    private const byte ElfData2Lsb = 1;

    // ELF64 header field offsets
    private const int EhdrClass = 4;
    private const int EhdrData = 5;
    private const int EhdrMachine = 18;
    private const int EhdrEntry = 24;
    private const int EhdrPhoff = 32;
    private const int EhdrPhentsize = 54;
    private const int EhdrPhnum = 56;

    // ELF64 program header field offsets (relative to phdr start) — note p_flags
    // sits right after p_type in ELF64, unlike ELF32 where it trails p_paddr/p_filesz/p_memsz.
    private const int PhdrType = 0;
    private const int PhdrOffset = 8;
    private const int PhdrPaddr = 24;
    private const int PhdrFilesz = 32;
    private const int PhdrMemsz = 40;

    public static ulong LoadFile(IMemory memory, string path) =>
        Load(memory, File.ReadAllBytes(path));

    public static ulong Load(IMemory memory, ReadOnlySpan<byte> elf) {
        if (elf.Length < 64) throw new ElfException("File too small to be a valid ELF.");

        ValidateMagic(elf);

        if (elf[Rv64ElfLoader.EhdrClass] != Rv64ElfLoader.ElfClass64)
            throw new ElfException($"Only ELF64 is supported (class byte = {elf[Rv64ElfLoader.EhdrClass]}).");
        if (elf[Rv64ElfLoader.EhdrData] != Rv64ElfLoader.ElfData2Lsb)
            throw new ElfException("Only little-endian ELF is supported.");

        ushort machine = U16(elf, Rv64ElfLoader.EhdrMachine);
        if (machine != Rv64ElfLoader.EmRiscv)
            throw new ElfException(
                $"Unexpected e_machine 0x{machine:X2}, expected EM_RISCV (0x{Rv64ElfLoader.EmRiscv:X2})."
            );

        ulong entry = U64(elf, Rv64ElfLoader.EhdrEntry);
        ulong phoff = U64(elf, Rv64ElfLoader.EhdrPhoff);
        ushort phentsize = U16(elf, Rv64ElfLoader.EhdrPhentsize);
        ushort phnum = U16(elf, Rv64ElfLoader.EhdrPhnum);

        for (var i = 0; i < phnum; i++) {
            var phdrStart = (int)(phoff + (ulong)(i * phentsize));
            uint type = U32(elf, phdrStart + Rv64ElfLoader.PhdrType);
            if (type != Rv64ElfLoader.PtLoad) continue;

            ulong fileOffset = U64(elf, phdrStart + Rv64ElfLoader.PhdrOffset);
            ulong paddr = U64(elf, phdrStart + Rv64ElfLoader.PhdrPaddr);
            ulong filesz = U64(elf, phdrStart + Rv64ElfLoader.PhdrFilesz);
            ulong memsz = U64(elf, phdrStart + Rv64ElfLoader.PhdrMemsz);

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

    private static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}