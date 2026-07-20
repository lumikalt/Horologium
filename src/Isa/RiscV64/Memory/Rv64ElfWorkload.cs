#region

using System.Buffers.Binary;
using System.Text;
using Mechanism;
using RiscV32.Memory;

#endregion

namespace RiscV64.Memory;

/// <summary>
///     An <see cref="IWorkload" /> that loads a bare-metal ELF64 RISC-V binary.
///     Entry point and minimum memory size are derived from the ELF headers so
///     callers need only supply the file path.
/// </summary>
public sealed class Rv64ElfWorkload : IElfWorkload {
    private readonly byte[] _elfBytes;

    public Rv64ElfWorkload(string path, int? memorySizeBytes = null)
        : this(File.ReadAllBytes(path), memorySizeBytes) { }

    public Rv64ElfWorkload(byte[] elfBytes, int? memorySizeBytes = null) {
        _elfBytes = elfBytes;
        EntryPoint = ParseEntryPoint(elfBytes);
        BaseAddress = ComputeBaseAddress(elfBytes);
        MemorySize = memorySizeBytes ?? ComputeMinMemorySize(elfBytes, BaseAddress);
        HtifTohostAddress = TryFindSymbol("tohost", out ulong tohost) ? tohost : null;
        InitialBreak = ComputeInitialBreak(elfBytes);
    }

    public ulong EntryPoint { get; }
    public int MemorySize { get; }
    public int CodeSize => _elfBytes.Length;

    /// <summary>
    ///     Address just past the last PT_LOAD segment (i.e. the initial program break).
    ///     Pass to <see cref="RiscV32.Syscalls.LinuxSyscallEmulator" /> as <c>initialBreak</c>
    ///     so SYS_brk starts from the correct address.
    /// </summary>
    public ulong InitialBreak { get; }

    /// <summary>
    ///     The physical base address of the first PT_LOAD segment, e.g. 0x80000000
    ///     for Spike-compatible ELFs. Pass this to FlatMemory's constructor so that
    ///     the backing array covers only the actual code/data range.
    /// </summary>
    public ulong BaseAddress { get; }

    /// <summary>The HTIF <c>tohost</c> exit register address if the ELF exports it; null otherwise.</summary>
    public ulong? HtifTohostAddress { get; }

    public void Load(IMemory memory) => Rv64ElfLoader.Load(memory, _elfBytes);

    /// <summary>
    ///     Wraps <paramref name="memory" /> with <see cref="HtifMemory" /> when the ELF contains
    ///     a <c>tohost</c> symbol so that HTIF syscall writes are auto-acknowledged.
    ///     Without this, benchmarks that call printstr would spin forever in the fromhost
    ///     polling loop, preventing them from reaching tohost_exit.
    /// </summary>
    public IMemory WrapMemory(IMemory memory) =>
        TryFindSymbol("tohost", out ulong tohost) ? new HtifMemory(memory, tohost) : memory;

    /// <summary>
    ///     Returns the virtual address of a named ELF symbol, or throws if not found.
    /// </summary>
    public ulong FindSymbol(string name) {
        ReadOnlySpan<byte> elf = _elfBytes;
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(elf[40..]);
        ushort shentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[58..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[60..]);

        for (var i = 0; i < shnum; i++) {
            var shdr = (int)(shoff + (ulong)(i * shentsz));
            uint shType = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 4)..]);
            if (shType != 2) continue; // SHT_SYMTAB

            ulong symOff = BinaryPrimitives.ReadUInt64LittleEndian(elf[(shdr + 24)..]);
            ulong symSz = BinaryPrimitives.ReadUInt64LittleEndian(elf[(shdr + 32)..]);
            uint strtabIdx = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 40)..]);

            var strtabHdr = (int)(shoff + strtabIdx * shentsz);
            ulong strtabOff = BinaryPrimitives.ReadUInt64LittleEndian(elf[(strtabHdr + 24)..]);

            // Elf64_Sym: st_name(4) st_info(1) st_other(1) st_shndx(2) st_value(8) st_size(8) = 24 bytes
            for (ulong s = 0; s < symSz / 24; s++) {
                var sym = (int)(symOff + s * 24);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[sym..]);
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(elf[(sym + 8)..]);

                var start = (int)(strtabOff + nameOff);
                int end = start;
                while (end < _elfBytes.Length && _elfBytes[end] != 0) end++;
                if (Encoding.ASCII.GetString(_elfBytes, start, end - start) == name) return value;
            }
        }

        throw new KeyNotFoundException($"ELF symbol '{name}' not found");
    }

    /// <summary>Like <see cref="FindSymbol" /> but returns false instead of throwing.</summary>
    public bool TryFindSymbol(string name, out ulong address) {
        try {
            address = FindSymbol(name);
            return true;
        }
        catch (KeyNotFoundException) {
            address = 0;
            return false;
        }
    }

    // ── ELF header parsing ────────────────────────────────────────────────────

    private static ulong ParseEntryPoint(ReadOnlySpan<byte> elf) =>
        BinaryPrimitives.ReadUInt64LittleEndian(elf[24..]);

    private static ulong ComputeBaseAddress(ReadOnlySpan<byte> elf) {
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(elf[32..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[54..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[56..]);

        var minBase = ulong.MaxValue;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (ulong)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            ulong paddr = BinaryPrimitives.ReadUInt64LittleEndian(elf[(ph + 24)..]);
            if (paddr < minBase) minBase = paddr;
        }

        return minBase == ulong.MaxValue ? 0ul : minBase;
    }

    private static int ComputeMinMemorySize(ReadOnlySpan<byte> elf, ulong baseAddress) {
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(elf[32..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[54..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[56..]);

        ulong maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (ulong)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            ulong paddr = BinaryPrimitives.ReadUInt64LittleEndian(elf[(ph + 24)..]);
            ulong memsz = BinaryPrimitives.ReadUInt64LittleEndian(elf[(ph + 40)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Size relative to the base address, rounded to next 64 KB + 64 KB for stack/heap.
        ulong relativeEnd = maxEnd - baseAddress;
        return (int)((relativeEnd + 0xFFFF) & ~0xFFFFUL) + 0x10000;
    }

    private static ulong ComputeInitialBreak(ReadOnlySpan<byte> elf) {
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(elf[32..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[54..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[56..]);

        ulong maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (ulong)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            ulong paddr = BinaryPrimitives.ReadUInt64LittleEndian(elf[(ph + 24)..]);
            ulong memsz = BinaryPrimitives.ReadUInt64LittleEndian(elf[(ph + 40)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Round up to the next page boundary (4 KiB).
        return (maxEnd + 0xFFFUL) & ~0xFFFUL;
    }
}