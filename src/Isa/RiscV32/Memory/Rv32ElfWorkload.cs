#region

using System.Buffers.Binary;
using System.Text;
using Mechanism;

#endregion

namespace RiscV32.Memory;

/// <summary>
///     An <see cref="IWorkload" /> that loads a bare-metal ELF32 RISC-V binary.
///     Entry point and minimum memory size are derived from the ELF headers so
///     callers need only supply the file path.
/// </summary>
public sealed class Rv32ElfWorkload : IElfWorkload {
    private readonly byte[] _elfBytes;

    public Rv32ElfWorkload(string path, int? memorySizeBytes = null)
        : this(File.ReadAllBytes(path), memorySizeBytes) { }

    private Rv32ElfWorkload(byte[] elfBytes, int? memorySizeBytes = null) {
        _elfBytes = elfBytes;
        EntryPoint = ParseEntryPoint(elfBytes);
        BaseAddress = ComputeBaseAddress(elfBytes);
        MemorySize = memorySizeBytes ?? ComputeMinMemorySize(elfBytes, BaseAddress);
        HtifTohostAddress = TryFindSymbol("tohost", out ulong tohost) ? tohost : null;
        InitialBreak = ComputeInitialBreak(elfBytes);
        (PhdrAddress, PhEntrySize, PhNum) = ComputePhdrInfo(elfBytes);
    }

    /// <summary>
    ///     Address just past the last PT_LOAD segment (i.e. the initial program break).
    ///     Pass to <see cref="RiscV32.Syscalls.LinuxSyscallEmulator" /> as <c>initialBreak</c>
    ///     so SYS_brk starts from the correct address.
    /// </summary>
    public ulong InitialBreak { get; }

    public ulong EntryPoint { get; }
    public int MemorySize { get; }
    public int CodeSize => _elfBytes.Length;

    /// <inheritdoc />
    public ulong PhdrAddress { get; }

    /// <inheritdoc />
    public ulong PhEntrySize { get; }

    /// <inheritdoc />
    public ulong PhNum { get; }

    /// <summary>
    ///     The physical base address of the first PT_LOAD segment, e.g. 0x80000000
    ///     for Spike-compatible ELFs. Pass this to FlatMemory's constructor so that
    ///     the backing array covers only the actual code/data range.
    /// </summary>
    public ulong BaseAddress { get; }

    /// <summary>The HTIF <c>tohost</c> exit register address if the ELF exports it; null otherwise.</summary>
    public ulong? HtifTohostAddress { get; }

    public void Load(IMemory memory) => Rv32ElfLoader.Load(memory, _elfBytes);

    // The interface method has no output parameter; route it through the concrete overload.
    IMemory IWorkload.WrapMemory(IMemory memory) => WrapMemory(memory);

    /// <summary>
    ///     Returns the virtual address of a named ELF symbol, or throws if not found.
    /// </summary>
    public ulong FindSymbol(string name) {
        foreach ((string symName, uint value, uint _) in EnumerateSymbolEntries())
            if (symName == name)
                return value;

        throw new KeyNotFoundException($"ELF symbol '{name}' not found");
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Name, ulong Address, ulong Size)> EnumerateSymbols() => [
        .. EnumerateSymbolEntries().Where(e => e.Name.Length > 0 && e.Size > 0)
                                   .Select(e => (e.Name, (ulong)e.Value, (ulong)e.Size)),
    ];

    // Reads through _elfBytes.AsSpan(offset) per call rather than holding one ReadOnlySpan<byte>
    // local across the method — a span (ref struct) can't be preserved across a yield boundary.
    private IEnumerable<(string Name, uint Value, uint Size)> EnumerateSymbolEntries() {
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(32));
        ushort shentsz = BinaryPrimitives.ReadUInt16LittleEndian(_elfBytes.AsSpan(46));
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(_elfBytes.AsSpan(48));

        for (var i = 0; i < shnum; i++) {
            var shdr = (int)(shoff + (uint)(i * shentsz));
            uint shType = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(shdr + 4));
            if (shType != 2) continue; // SHT_SYMTAB

            uint symOff = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(shdr + 16));
            uint symSz = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(shdr + 20));
            uint strtabIdx = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(shdr + 24));

            var strtabHdr = (int)(shoff + strtabIdx * shentsz);
            uint strtabOff = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(strtabHdr + 16));

            // Elf32_Sym: st_name(4) st_value(4) st_size(4) st_info(1) st_other(1) st_shndx(2) = 16 bytes
            for (uint s = 0; s < symSz / 16; s++) {
                var sym = (int)(symOff + s * 16);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(sym));
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(sym + 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(_elfBytes.AsSpan(sym + 8));

                var start = (int)(strtabOff + nameOff);
                int end = start;
                while (end < _elfBytes.Length && _elfBytes[end] != 0) end++;
                yield return (Encoding.ASCII.GetString(_elfBytes, start, end - start), value, size);
            }
        }
    }

    /// <summary>
    ///     Wraps <paramref name="memory" /> with <see cref="HtifMemory" /> when the ELF contains
    ///     a <c>tohost</c> symbol, executing fesvr magic-mem syscalls and ACK-ing fromhost.
    ///     <paramref name="output" /> receives SYS_write output; when null, output is discarded.
    /// </summary>
    public IMemory WrapMemory(IMemory memory, TextWriter? output = null) =>
        TryFindSymbol("tohost", out ulong tohost) ? new HtifMemory(memory, tohost, output) : memory;

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
        BinaryPrimitives.ReadUInt32LittleEndian(elf[24..]);

    private static ulong ComputeBaseAddress(ReadOnlySpan<byte> elf) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        var minBase = uint.MaxValue;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            if (paddr < minBase) minBase = paddr;
        }

        return minBase == uint.MaxValue ? 0u : minBase;
    }

    private static int ComputeMinMemorySize(ReadOnlySpan<byte> elf, ulong baseAddress) {
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

        // Size relative to the base address, rounded to next 64 KB + 64 KB for stack/heap.
        uint relativeEnd = maxEnd - (uint)baseAddress;
        return (int)((relativeEnd + 0xFFFF) & ~0xFFFFU) + 0x10000;
    }

    // See Rv64ElfWorkload.ComputePhdrInfo's doc comment: translates the phdr table's file offset
    // (e_phoff) to the address a real libc's own PT_TLS/PT_GNU_STACK walk will dereference, using
    // p_paddr (not p_vaddr) to match Rv32ElfLoader.Load and this class's other addresses.
    private static (ulong PhdrAddress, ulong PhEntrySize, ulong PhNum) ComputePhdrInfo(ReadOnlySpan<byte> elf) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 4)..]);
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            uint filesz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 16)..]);
            if (phoff >= fileOffset && phoff < fileOffset + filesz)
                return (paddr + (phoff - fileOffset), phentsz, phnum);
        }

        return (0, 0, 0);
    }

    private static ulong ComputeInitialBreak(ReadOnlySpan<byte> elf) {
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

        // Round up to the next page boundary (4 KiB).
        return (maxEnd + 0xFFFU) & ~0xFFFUL;
    }
}